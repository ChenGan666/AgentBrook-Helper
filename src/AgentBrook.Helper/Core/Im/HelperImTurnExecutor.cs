using System.Text;
using AgentBrook.Agent.IM;
using AgentBrook.Messaging;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper.Core.Im;

/// <summary>IM 回合过程钩子（UI 桥接用：气泡状态 + 选中会话的实时流式；IM 侧只收最终结果）。</summary>
public sealed record ImTurnHooks(
    Action<string>? OnTurnStart,          // (sessionId)
    Action<string, string>? OnDelta,      // (sessionId, textDelta)
    Action<string, bool>? OnTurnEnd);     // (sessionId, ok)

/// <summary>
/// Helper 宿主的 IM 回合执行器：真实 MAF（HelperImAgentHost，同供应商/模型），
/// 从 CURRENT 载入最新会话 → RunStreamingAsync(context.UserText) → 快照随 Outcome 回传（S7 契约）。
/// 过程经 ImTurnHooks 外报（执行过程与主 Agent 一样可见）；快照镜像写 legacy session.json
/// （会话界面点开可见历史；版本化存储仍是权威，镜像只服务 UI 渲染）。
/// </summary>
public sealed class HelperImTurnExecutor : ITurnExecutor
{
    private readonly VersionedSessionStore _store;
    private readonly HelperImAgentHost _agentHost;
    private readonly ImTurnHooks? _hooks;
    private readonly string? _legacySessionsDir;   // workspace/sessions（镜像落盘；null=不镜像）
    private readonly Func<AgentBrook.Agent.BrookAgent?>? _brookAccessor;   // 主 Agent 包装（同权执行：完整回合循环）
    private readonly HelperBlobService? _blobs;                             // 产物回传写 Blob（附件互传出站）
    private readonly Func<TurnExecutionAttachment, CancellationToken, Task<TurnExecutionAttachmentBlob?>>? _materialize;  // 统一管道物化（通道插件下载）
    private readonly Func<string, bool>? _sendImmediate;                    // 回合中即时出站（授权/提问提示）
    private readonly bool _interactiveApprovals;                            // true=对话确认；false=自动放行（auto 模式）
    private readonly Func<bool>? _fullAccessAccessor;                       // 完全访问：IM 审批同样自动通过

    public HelperImTurnExecutor(VersionedSessionStore store, HelperImAgentHost agentHost,
        ImTurnHooks? hooks = null, string? legacySessionsDir = null,
        Func<AgentBrook.Agent.BrookAgent?>? brookAccessor = null,
        HelperBlobService? blobs = null,
        Func<TurnExecutionAttachment, CancellationToken, Task<TurnExecutionAttachmentBlob?>>? materializeAttachment = null,
        Func<string, bool>? sendImmediate = null,
        bool interactiveApprovals = false,
        Func<bool>? fullAccessAccessor = null)
    {
        _store = store;
        _agentHost = agentHost;
        _hooks = hooks;
        _legacySessionsDir = legacySessionsDir;
        _brookAccessor = brookAccessor;
        _blobs = blobs;
        _materialize = materializeAttachment;
        _sendImmediate = sendImmediate;
        _interactiveApprovals = interactiveApprovals;
        _fullAccessAccessor = fullAccessAccessor;
    }

    public async Task<TurnOutcome> ExecuteAsync(TurnExecutionContext context, CancellationToken cancellationToken)
    {
        var userText = context.UserText ?? throw new InvalidOperationException(
            "[BadPayload] 回合上下文缺少 UserText（调度器必须从 Inbox 行传入）。");
        var hasAttachments = context.Attachments is { Count: > 0 };
        ImDiag.Log($"[im] 回合开始：session={context.SessionId} text={TruncateLog(userText)} attachments={context.Attachments?.Count ?? 0}");
        _hooks?.OnTurnStart?.Invoke(context.SessionId);

        // 回合超时（模型/工具挂起时给用户确定性回执）。
        // 对话确认模式会把 5 分钟审批等待计入回合 → 总上限提到 10 分钟；auto 模式保持 5 分钟。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(_interactiveApprovals ? 10 : 5));

        // ── 同权执行：走主 Agent 完整回合循环（RunAsync：多轮工具执行 + 审批弹窗与主界面同流程）──
        var brook = _brookAccessor?.Invoke();
        Console.Error.WriteLine($"[im] 同权执行：{(brook is { InnerAgent: not null } ? "主 Agent 循环（全工具）" : "回退 host（无工具纯对话）")}，主回合进行中={brook?.TurnInProgress}");
        if (brook is { InnerAgent: not null })
        {
            // 与主回合互斥（共享 TurnAnswer/_turnCts 等回合态字段）：等待主回合空闲，有界自愈
            var waitDeadline = DateTime.UtcNow.AddSeconds(120);
            while (brook.TurnInProgress && DateTime.UtcNow < waitDeadline
                   && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            if (!brook.TurnInProgress)
            {
                try
                {
                    // 交互路由（chat 模式 + 可即时出站）：授权/提问经 IM 对话完成（手机回复），
                    // 不弹本地卡片。AsyncLocal 作用域只覆盖本回合异步流——本地回合读不到，互不影响。
                    // auto 模式/无出站通道：保持原行为（全自动放行）。
                    var router = _interactiveApprovals && _sendImmediate is not null
                                 && context.ReplyTarget is { } replyTarget
                                 && context.Principal is { } principal
                        ? new ImTurnInteractionRouter(context.SessionId, replyTarget, principal.PrincipalId,
                            _sendImmediate, fullAccess: _fullAccessAccessor)
                        : null;
                    using var interactionScope = router is null ? null : brook.BeginExternalInteraction(router);
                    brook.ExternalTurnActive = true;              // 转写门控（必须先于 SwitchSession 的 Reconcile）
                    brook.AutoApproveExternalTurns = router is null;   // 无交互能力时的兜底策略
                    // IM 会话就位：**同会话也必须从 CURRENT 重载**——上一回合的提交由调度器推进，
                    // 进程内载入基线会落后一版（SwitchSessionAsync 同会话早返回不重载），
                    // 后续保存会以陈旧 revision 撞 [RevisionConflict]，调度器重试→模型重跑→无限循环
                    if (brook.CurrentSessionId == context.SessionId)
                    {
                        if (!await brook.ReloadCurrentSessionAsync().ConfigureAwait(false))
                            ImDiag.Log($"[im] 同会话重载失败（非版本化/无快照），继续使用内存态：{context.SessionId}");
                    }
                    else
                    {
                        await brook.SwitchSessionAsync(context.SessionId).ConfigureAwait(false);
                    }
                    var (modelName, imageInput, videoInput) = _agentHost.GetModelCapabilities(brook.CurrentModel);   // 解析模型=主界面当前选中

                    // 附件能力检查：不支持→反馈请求方（不进会话、不调模型），提示切换多模态模型
                    var unsupported = "";
                    if (hasAttachments)
                    {
                        var kinds = context.Attachments!
                            .Select(a => a.Kind switch { 2 => "图片", 4 => "视频", 3 => "语音", _ => "文件" })
                            .Distinct().ToList();
                        var kindText = string.Join("、", kinds);
                        if (context.Attachments.Any(a => a.Kind == 2) && !imageInput)
                            unsupported = "不支持图片理解";
                        if (context.Attachments.Any(a => a.Kind == 4) && !videoInput)
                            unsupported += (unsupported.Length > 0 ? "、" : "") + "不支持视频理解";
                        if (unsupported.Length > 0)
                            unsupported = $"当前模型 {modelName} {unsupported}";
                        var _ = kindText;   // kindText 用于反馈文案
                        if (unsupported.Length > 0)
                        {
                            var feedback = $"已收到你发送的附件（{kindText}）。{unsupported}——"
                                + "请在 Helper 切换到多模态模型（如 deepseek-v4-flash / glm-5.3）后重新发送，或直接以文字描述需求。";
                            var feedbackEvents = new List<TurnEvent> { new(context.TurnId, 0, "assistant", feedback, null) };
                            _hooks?.OnTurnEnd?.Invoke(context.SessionId, true);
                            return new TurnOutcome(true, feedback, feedbackEvents, [], null, null);
                        }
                    }

                    // ── 回合输入：附件=DataContent（会话历史承载图片，指令回合模型带图上下文）；文字=用户指令 ──
                    Microsoft.Extensions.AI.ChatMessage turnInput;
                    var userEventText = userText;
                    var materialized = new List<TurnExecutionAttachmentBlob>();
                    if (hasAttachments)
                    {
                        var parts = new List<Microsoft.Extensions.AI.AIContent>();
                        foreach (var att in context.Attachments!)
                        {
                            TurnExecutionAttachmentBlob? blob = null;
                            try { blob = _materialize?.Invoke(att, cancellationToken).GetAwaiter().GetResult(); }
                            catch (Exception ex) { Console.Error.WriteLine($"[im] 附件物化失败 {att.RemoteId}：{ex.Message}"); continue; }
                            if (blob is null) { Console.Error.WriteLine($"[im] 附件物化失败 {att.RemoteId}"); continue; }
                            materialized.Add(blob);
                            parts.Add(new Microsoft.Extensions.AI.DataContent(blob.Bytes, blob.MediaType ?? "image/jpeg"));
                        }
                        // 全部附件物化失败：明确告知（不静默走空输入，否则用户以为附件已收到）
                        if (materialized.Count == 0)
                        {
                            var failed = "附件接收失败（可能超过大小限制、平台下载失败或文件已过期）。请重新发送一次。";
                            var failedEvents = new List<TurnEvent> { new(context.TurnId, 0, "assistant", failed, null) };
                            _hooks?.OnTurnEnd?.Invoke(context.SessionId, true);
                            return new TurnOutcome(true, failed, failedEvents, [], null, null);
                        }
                        // 附件消息（无文字）：host 指令强约束——只回一句话（不描述/不分析/不执行任何工具）；
                        // 会话历史保留图片，待用户发出具体指令后模型带图执行
                        if (parts.Count > 0 && string.IsNullOrWhiteSpace(userText))
                            parts.Add(new Microsoft.Extensions.AI.TextContent(
                                "（用户发来了附件。请只回复：已收到附件，等待具体指令。——不要描述或分析附件内容，不要执行任何工具，不要提出建议。）"));
                        turnInput = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, parts);
                        userEventText = string.IsNullOrWhiteSpace(userEventText) ? "[附件]" : userEventText;
                    }
                    else
                    {
                        turnInput = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, userText);
                    }

                    // 累积全程增量（多轮工具调用的每轮正文）——IM 收到的与主窗口显示的一致；
                    // brook.TurnAnswer 只保留末轮，单用它会让 Telegram 缺失中间轮次正文（用户感知"回复没收到"）
                    // 同时按"轮"切分：每轮模型输出=MAF 历史里的一条 assistant 消息。投影行与 MAF 一一对应后，
                    // 提交后重载的对账（turnId 直配或 role+text 去重）才能命中——此前单条合并行对不上每轮多条，
                    // 重载时会把整轮正文再追加一遍（主窗口重复回复，微信侧只有一份）。
                    var streamed = new StringBuilder();
                    var roundTexts = new List<string>();
                    var currentRound = new StringBuilder();
                    await foreach (var ev in brook.RunAsync(turnInput).WithCancellation(timeoutCts.Token).ConfigureAwait(false))
                    {
                        if (ev is AgentBrook.Agent.BrookEvent.TextDelta delta && !string.IsNullOrEmpty(delta.Text))
                        {
                            streamed.Append(delta.Text);
                            currentRound.Append(delta.Text);
                        }
                        else if (ev is AgentBrook.Agent.BrookEvent.ToolStarted)
                        {
                            // 工具调用意味着本轮模型输出已完整：收口本轮正文，后续增量属于下一轮
                            if (currentRound.Length > 0)
                            {
                                roundTexts.Add(currentRound.ToString());
                                currentRound.Clear();
                            }
                        }
                    }
                    if (currentRound.Length > 0) roundTexts.Add(currentRound.ToString());

                    var answerText = streamed.Length > 0 ? streamed.ToString() : (brook.TurnAnswer ?? "");
                    var finalAnswer = StripToolCallMarkup(answerText);
                    if (string.IsNullOrWhiteSpace(finalAnswer)) finalAnswer = "（本轮没有产出文字结果）";
                    ImDiag.Log($"[im] 回合完成：session={context.SessionId} 回答 {finalAnswer.Length} 字符（{roundTexts.Count} 轮正文）");

                    // user 事件携带附件 artifacts（会话界面用户侧内嵌图片/文件行的唯一数据源——
                    // MAF 侧的图片消息在转写构建时被跳过，见 BuildTranscriptTextList）
                    var userArtifactsJson = materialized.Count > 0
                        ? SerializeArtifactsJson($"attach-{context.TurnId}", materialized.Select(b =>
                        {
                            var ext = b.MediaType switch
                            {
                                "image/jpeg" => "jpg", "image/png" => "png", "image/webp" => "webp",
                                "image/gif" => "gif", "video/mp4" => "mp4", "application/pdf" => "pdf",
                                _ => "file",
                            };
                            return (b.FileName ?? $"附件-{(b.BlobId ?? "x")[..Math.Min(8, (b.BlobId ?? "x").Length)]}",
                                b.LocalPath ?? "", (string?)"附件", ext);
                        }))
                        : null;
                    // 投影事件：user 一条 + assistant **按轮一条**（与 MAF 历史一一对应，供重载对账命中）；
                    // IM 回复本身仍是全部轮次拼接（answerText/finalAnswer 不变）
                    var commitEvents = new List<TurnEvent>
                    {
                        new(context.TurnId, 0, "user", userEventText, userArtifactsJson),
                    };
                    var roundIndex = 1;
                    foreach (var round in roundTexts)
                    {
                        var text = round.Trim();   // 与 BuildTranscriptTextList 的 trim 对齐（签名可比）
                        if (text.Length == 0) continue;
                        commitEvents.Add(new(context.TurnId, roundIndex++, "assistant", text, null));
                    }
                    if (roundIndex == 1)   // 无任何轮次正文（空回/仅工具标记）：回退整条最终回答
                        commitEvents.Add(new(context.TurnId, 1, "assistant", finalAnswer, null));
                    var snapshot = brook.InnerSession is null || brook.InnerAgent is null
                        ? null
                        : MafSnapshotCodec.Encode(brook.InnerAgent, brook.InnerSession);

                    // 产物回传（附件互传出站方向）：任务工件文件 → 宿主 Blob → outbox files_json → 插件上传
                    List<TurnExecutionFile>? files = null;
                    if (_blobs is not null && brook.TurnArtifacts is { HasFiles: true } artifacts)
                    {
                        foreach (var f in artifacts.Files)
                        {
                            if (f.FileType == "link" || !File.Exists(f.FilePath)) continue;
                            try
                            {
                                await using var fs = File.OpenRead(f.FilePath);
                                var mediaType = GuessMediaType(f.FileType);
                                var content = await _blobs.WriteAsync("im-reply", fs,
                                    50L * 1024 * 1024, f.FileName, mediaType, CancellationToken.None).ConfigureAwait(false);
                                (files ??= []).Add(new TurnExecutionFile(f.FileName, content));
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"[im] 产物回传失败 {f.FileName}：{ex.Message}"); }
                        }
                        if (files is { Count: 0 }) files = null;
                    }

                    MirrorLegacySnapshot(context.SessionId, snapshot ?? []);
                    _hooks?.OnTurnEnd?.Invoke(context.SessionId, true);
                    return new TurnOutcome(true, finalAnswer, commitEvents, [], null, snapshot, files);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _hooks?.OnTurnEnd?.Invoke(context.SessionId, true);
                    var timeoutAnswer = "这条消息处理超时了（可能是任务较重或网络较慢）。请稍后再试一次，或把任务拆小一点。";
                    var events = new List<TurnEvent> { new(context.TurnId, 0, "assistant", timeoutAnswer, null) };
                    return new TurnOutcome(true, timeoutAnswer, events, [], null, null);
                }
                catch (Exception ex)
                {
                    ImDiag.Log($"[im] 回合失败：session={context.SessionId} {ex.GetType().Name}: {ex.Message}");
                    // 丢弃未提交的内存回合（失败=未提交）：否则重试会累积重复往来、
                    // 后续重载对账还会把未提交内容写进 transcript（界面出现幻影回复）
                    try { await brook.ReloadCurrentSessionAsync().ConfigureAwait(false); } catch { }
                    _hooks?.OnTurnEnd?.Invoke(context.SessionId, false);
                    throw;
                }
                finally
                {
                    brook.AutoApproveExternalTurns = false;
                    brook.ExternalTurnActive = false;
                }
            }
            // 主回合长时间未结束：降级走回退路径（不丢消息）
        }

        // ── 回退路径：自有 host 单轮（无工具纯对话；测试/降级场景）──
        var agent = _agentHost.GetOrCreateAgent();
        var payload = _store.ReadCurrentPayload(context.SessionId);
        var session = MafSnapshotCodec.Decode(agent, payload);

        // 降级路径同样要处理附件（此前完全不物化 → 该回合 user 事件无 artifacts、图片在会话里永久缺失）
        var fallbackUserEventText = userText;
        var fallbackArtifactsJson = (string?)null;
        Microsoft.Extensions.AI.ChatMessage fallbackInput;
        if (hasAttachments)
        {
            var parts = new List<Microsoft.Extensions.AI.AIContent>();
            var materialized = new List<TurnExecutionAttachmentBlob>();
            foreach (var att in context.Attachments!)
            {
                try
                {
                    var blob = _materialize?.Invoke(att, cancellationToken).GetAwaiter().GetResult();
                    if (blob is null) continue;
                    materialized.Add(blob);
                    parts.Add(new Microsoft.Extensions.AI.DataContent(blob.Bytes, blob.MediaType ?? "image/jpeg"));
                }
                catch (Exception ex) { Console.Error.WriteLine($"[im] 附件物化失败（降级路径）{att.RemoteId}：{ex.Message}"); }
            }
            if (materialized.Count == 0)
            {
                // 全部物化失败：明确告知（不静默当作普通消息，否则用户以为已收到）
                _hooks?.OnTurnEnd?.Invoke(context.SessionId, true);
                var failed = "附件接收失败（可能超过大小限制、平台下载失败或文件已过期）。请重新发送一次。";
                var failedEvents = new List<TurnEvent> { new(context.TurnId, 0, "assistant", failed, null) };
                return new TurnOutcome(true, failed, failedEvents, [], null, null);
            }
            if (string.IsNullOrWhiteSpace(userText))
                parts.Add(new Microsoft.Extensions.AI.TextContent(
                    "（用户发来了附件。请只回复：已收到附件，等待具体指令。——不要描述或分析附件内容，不要执行任何工具，不要提出建议。）"));
            fallbackInput = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, parts);
            fallbackUserEventText = string.IsNullOrWhiteSpace(userText) ? "[附件]" : userText;
            fallbackArtifactsJson = SerializeArtifactsJson($"attach-{context.TurnId}", materialized.Select(b =>
            {
                var ext = b.MediaType switch
                {
                    "image/jpeg" => "jpg", "image/png" => "png", "image/webp" => "webp",
                    "image/gif" => "gif", "video/mp4" => "mp4", "application/pdf" => "pdf",
                    _ => "file",
                };
                return (b.FileName ?? $"附件-{(b.BlobId ?? "x")[..Math.Min(8, (b.BlobId ?? "x").Length)]}",
                    b.LocalPath ?? "", (string?)"附件", ext);
            }));
        }
        else
        {
            fallbackInput = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, userText);
        }

        var fallbackAnswer = new StringBuilder();
        try
        {
            await foreach (var update in agent.RunStreamingAsync(
                new List<Microsoft.Extensions.AI.ChatMessage> { fallbackInput }, session, null)
                .WithCancellation(timeoutCts.Token).ConfigureAwait(false))
            {
                var delta = update.Text ?? "";
                if (delta.Length == 0) continue;
                fallbackAnswer.Append(delta);
            }
            var finalAnswer = StripToolCallMarkup(fallbackAnswer.ToString());
            var events = new List<TurnEvent>
            {
                new(context.TurnId, 0, "user", fallbackUserEventText, fallbackArtifactsJson),
                new(context.TurnId, 1, "assistant", finalAnswer, null),
            };
            var snapshot = MafSnapshotCodec.Encode(agent, session);
            MirrorLegacySnapshot(context.SessionId, snapshot);
            _hooks?.OnTurnEnd?.Invoke(context.SessionId, true);
            return new TurnOutcome(true, finalAnswer, events, [], null, snapshot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _hooks?.OnTurnEnd?.Invoke(context.SessionId, true);
            var timeoutAnswer = "这条消息处理超时了（可能是任务较重或网络较慢）。请稍后再试一次，或把任务拆小一点。";
            var events = new List<TurnEvent> { new(context.TurnId, 0, "assistant", timeoutAnswer, null) };
            return new TurnOutcome(true, timeoutAnswer, events, [], null, null);
        }
        catch
        {
            _hooks?.OnTurnEnd?.Invoke(context.SessionId, false);
            throw;
        }
    }

    /// <summary>序列化附件/产物为 artifacts JSON（camelCase TaskArtifacts 形态——ResetTranscript 与会话界面渲染共用）。</summary>
    private static string SerializeArtifactsJson(string taskId,
        IEnumerable<(string FileName, string FilePath, string? Description, string FileType)> files)
    {
        var payload = new
        {
            taskId,
            files = files.Select(f => new
            {
                fileName = f.FileName,
                filePath = f.FilePath,
                fileType = f.FileType,
                description = f.Description ?? "",
            }),
        };
        return System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    }

    /// <summary>产物媒体类型（按扩展名；小映射够 P0——未知扩展按文档流处理）。</summary>
    private static string GuessMediaType(string fileType) => fileType.ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "mp4" => "video/mp4",
        "mp3" or "wav" => "audio/mpeg",
        "pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    /// <summary>剥离模型误发的工具调用标记（DSML 块等）：按关键词定位所在行，整行起截断——只保留正文。
    /// 兼容全角/半角竖线与重复竖线变体（<｜｜DSML｜｜、<|DSML|> 等）。</summary>
    private static string StripToolCallMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var idx = text.IndexOf("DSML", StringComparison.Ordinal);
        if (idx < 0)
        {
            idx = text.IndexOf("<｜", StringComparison.Ordinal);
            if (idx < 0) idx = text.IndexOf("<|", StringComparison.Ordinal);
            if (idx < 0) return text;
            return idx == 0 ? "" : text[..idx].TrimEnd();
        }
        // 回退到 DSML 所在行的行首（保留标记前的正文行）
        var lineStart = text.LastIndexOf('\n', idx);
        var head = lineStart >= 0 ? text[..lineStart] : "";
        return head.TrimEnd();
    }

    private void MirrorLegacySnapshot(string sessionId, byte[] snapshot)
    {
        if (_legacySessionsDir is null) return;
        try
        {
            var dir = Path.Combine(_legacySessionsDir, sessionId);
            Directory.CreateDirectory(dir);
            // 只镜像 session.json（点开会话可读 MAF 快照）；transcript.jsonl 由提交投影统一写入
            // （事件=user/assistant 全文各一条——流式增量不落盘，见 ExecuteAsync 注释）
            File.WriteAllBytes(Path.Combine(dir, "session.json"), snapshot);
            // 列表时间戳由提交后钩子（Dispatcher.TurnCompleted → Touch）推进——执行器阶段触碰会"假活跃"
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[im] legacy 镜像写失败 {sessionId}：{ex.Message}");
        }
    }

    private static string TruncateLog(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var t = text.Replace('\n', ' ');
        return t.Length > 40 ? t[..40] + "…" : t;
    }
}
