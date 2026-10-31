using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentBrook.Privacy;
using Microsoft.Extensions.AI;

namespace AgentBrook.Plugin.PrivacyMasking;

/// <summary>单回合脱敏统计（T5.3 徽标数据源）。</summary>
public readonly record struct MaskingTurnStats(int MaskedCount, int DataContentCount, bool PlaceholderNoteInjected);

/// <summary>
/// 敏感信息脱敏中间件壳（doc/15 §7，L4）——唯一挂载点：包在模型客户端外侧、
/// MAF 自动添加的 FunctionInvokingChatClient 内侧（工具拿真实值执行，模型只见占位符）。
///
/// 会话状态由本壳持有（引擎无会话集合状态，17 号 v4/v5）：emittedMap（tag→canonical，含高位临时段）
/// 与 escapeMap（全角中性形）跨回合持续（1.0.7 起经宿主持久化到 workspace/privacy/session-maps.json，
/// 重启/重装后由构造参数注入恢复——工作区文件里的字面占位符跨进程仍可还原）。
///
/// v1.0.9 设计修订（doc/15 §7.3）：出站文本不再逐 chunk 流式还原（原 StreamingRestorer 已删）——
/// 文本占位符原样流式输出（分片边界不再影响正确性），回合结束后显示层经 IDisplayTextRestorer
/// 一次性还原；管线内仅保留工具调用实参的即时还原（EgressPolicy：本地工具拿真实值执行）。
/// </summary>
public sealed class SensitiveMaskingChatClient : DelegatingChatClient
{
    private const int SessionMapCap = 50_000;   // 会话映射软上限（超限清空 = 简化 LRU，残留按 fail-open）

    private const string PlaceholderNoteZh =
        "【脱敏说明】为保护隐私，用户内容中的真实姓名、机构、电话、地址等敏感信息已被系统替换为脱敏标记" +
        "（方括号内为英文类别名加数字编号，每个标记的类别与编号各不相同）。标记与真实值一一对应，" +
        "回复在展示给用户前，系统会把标记完整替换还原为真实值。因此：1) 把你在上下文中看到的标记当作真实数据正常使用，" +
        "不要称其为虚构、示例或占位符，不要猜测、询问或编造真实值；2) 只能原样引用上下文中实际出现的标记，" +
        "保留其中的每一个字符，不要翻译、改写、拆分或套用任何其他标记；3) 不要自行编造新标记，无需向用户解释脱敏机制。";

    private const string PlaceholderNoteEn =
        "[Sanitization note] For privacy, real names, organizations, phone numbers, addresses, etc. in the user's content " +
        "have been replaced by the system with sanitization tokens (a bracketed English category name plus a numeric id; " +
        "the category and id differ per token). Each token maps 1:1 to a real value; the system fully restores the tokens " +
        "before the reply is shown to the user. Therefore: 1) treat the tokens you see in context as real data and use them " +
        "naturally - never call them fictional, sample or placeholder values, never guess, ask about, or invent real values, " +
        "and never alter any character inside a token; 2) only reference tokens that actually appear in the context - do not " +
        "copy from any example and do not invent new tokens; 3) do not explain this mechanism to the user.";

    private const string PlaceholderNoteId =
        "[Catatan sanitasi] Demi privasi, nama, organisasi, nomor telepon, alamat, dll. asli dalam konten pengguna telah " +
        "diganti sistem dengan token sanitasi (kurung berisi nama kategori Inggris plus id angka; kategori dan id berbeda " +
        "tiap token). Setiap token berkorespondensi 1:1 dengan nilai asli; sistem mengembalikannya secara penuh sebelum " +
        "jawaban ditampilkan. Karena itu: 1) perlakukan token yang Anda lihat dalam konteks sebagai data nyata dan gunakan " +
        "secara wajar - jangan sebut fiktif/contoh/placeholder, jangan menebak, menanyakan, atau mengarang nilai asli, dan " +
        "jangan mengubah karakter apa pun di dalam token; 2) hanya kutip token yang benar-benar muncul dalam konteks - jangan " +
        "meniru contoh apa pun dan jangan mengarang token baru; 3) tidak perlu menjelaskan mekanisme ini ke pengguna.";

    private readonly IPrivacyEngine _engine;
    private readonly string _language;
    private readonly Action<int, string?, bool>? _turnEndCallback;
    private readonly Action? _persistCallback;
    private volatile string? _lastMaskedUserText;
    // 会话映射 = 插件级共享（v1.0.11 架构修订）：主客户端/子 Agent/管线重建后的各壳实例
    // 共用同一映射集——否则标签分散在多壳各自的映射里，还原时"部分标签没有替换"（真机案例）
    private readonly ConcurrentDictionary<string, string> _emitted;
    private readonly ConcurrentDictionary<string, string> _escapes;
    private readonly ConditionalWeakTable<ChatMessage, CacheEntry> _cache = new();
    private int _sessionVersion;

    private sealed record CacheEntry(long SnapshotVersion, int SessionVersion, ChatMessage Masked);

    public SensitiveMaskingChatClient(IChatClient inner, IPrivacyEngine engine, string language = "zh",
        Action<int, string?, bool>? turnEndCallback = null,
        ConcurrentDictionary<string, string>? sharedEmitted = null,
        ConcurrentDictionary<string, string>? sharedEscapes = null,
        Action? persistCallback = null) : base(inner)
    {
        _engine = engine;
        _language = language;
        _turnEndCallback = turnEndCallback;
        _persistCallback = persistCallback;
        _emitted = sharedEmitted ?? new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        _escapes = sharedEscapes ?? new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>emitted 映射快照（宿主落盘用）。</summary>
    public IReadOnlyDictionary<string, string> EmittedSnapshot() =>
        _emitted.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>escapes 映射快照（宿主落盘用）。</summary>
    public IReadOnlyDictionary<string, string> EscapesSnapshot() =>
        _escapes.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>诊断：当前 emitted 映射条数。</summary>
    public int EmittedCount => _emitted.Count;

    private string Note() => _language switch
    {
        "en" => PlaceholderNoteEn,
        "id" => PlaceholderNoteId,
        _ => PlaceholderNoteZh,
    };

    /// <summary>上一请求的脱敏统计。</summary>
    public MaskingTurnStats LastTurnStats { get; private set; }

    /// <summary>总开关（privacy.enabled）。关闭时一次布尔检查直通，零开销（D8）。</summary>
    private bool Enabled => _engine.Snapshot.MasterEnabled;

    // ── 非流式 ───────────────────────────────────────────────────────────

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // 强制离开调用线程（常为 UI）：Task.Yield 只会排回同一 SyncContext（无效），
        // 必须 Task.Run——PrepareRequest 冷缓存全历史脱敏可达秒级（Windows 实测"发送后卡死"）
        if (!Enabled)
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        _engine.BeginTurn();
        List<ChatMessage> outMessages;
        ChatOptions? outOptions;
        MaskingTurnStats stats;
        try
        {
            (outMessages, outOptions, stats) = await Task.Run(() => PrepareRequest(messages, options),
                cancellationToken);
        }
        catch (Exception ex) when (!_engine.Snapshot.FailOpen)
        {
            // fail-closed（D7）：绝不在脱敏失败时发送原文
            Console.Error.WriteLine($"[privacy] 出站脱敏失败，已中止本请求（fail-closed；可在设置中临时关闭）：{ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[privacy] 出站脱敏失败（fail_open=1，放行原文）：{ex.Message}");
            outMessages = messages.ToList();
            outOptions = options;
            stats = default;
        }
        var response = await base.GetResponseAsync(outMessages, outOptions, cancellationToken);
        foreach (var message in response.Messages)
        {
            for (var i = 0; i < message.Contents.Count; i++)
            {
                message.Contents[i] = RestoreToolContent(message.Contents[i]);
            }
        }
        LastTurnStats = stats;
        NotifyTurnEnd(stats);
        await SafeFlushAsync();
        return response;
    }

    // ── 流式 ─────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }
        _engine.BeginTurn();

        List<ChatMessage> outMessages;
        ChatOptions? outOptions;
        MaskingTurnStats stats;
        try
        {
            (outMessages, outOptions, stats) = await Task.Run(() => PrepareRequest(messages, options),
                cancellationToken);
        }
        catch (Exception ex) when (!_engine.Snapshot.FailOpen)
        {
            // fail-closed（D7）：绝不在脱敏失败时发送原文
            Console.Error.WriteLine($"[privacy] 出站脱敏失败，已中止本请求（fail-closed；可在设置中临时关闭）：{ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[privacy] 出站脱敏失败（fail_open=1，放行原文）：{ex.Message}");
            outMessages = messages.ToList();
            outOptions = options;
            stats = default;
        }
        LastTurnStats = stats;

        // v1.0.9 设计修订（doc/15 §7.3）：文本原样流式输出（占位符短暂可见），
        // 回合结束后由显示层经 IDisplayTextRestorer 一次性还原——
        // 删除逐 chunk 流式回扣（StreamingRestorer），分片边界类 bug 连根消除。
        // 仅工具调用实参仍需即时还原（EgressPolicy：本地工具拿真实值执行，不能等回合结束）。
        await foreach (var update in base.GetStreamingResponseAsync(outMessages, outOptions, cancellationToken))
        {
            var changed = false;
            var contents = new List<AIContent>(update.Contents.Count);
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fc:
                    {
                        // D15：流中的工具调用已是完整对象（MEAI OpenAI 适配器聚合）——统一走 EgressPolicy
                        var restoredArgs = _engine.RestoreToolArgs(fc.Arguments, fc.Name, _emitted, _escapes);
                        var args = restoredArgs as IDictionary<string, object?> ?? fc.Arguments;
                        changed |= !ReferenceEquals(args, fc.Arguments);
                        contents.Add(new FunctionCallContent(fc.CallId, fc.Name, args));
                        break;
                    }
                    default:
                        contents.Add(content);
                        break;
                }
            }
            if (changed)
            {
                var clone = new ChatResponseUpdate(update.Role, contents)
                {
                    AuthorName = update.AuthorName,
                    ResponseId = update.ResponseId,
                    MessageId = update.MessageId,
                    CreatedAt = update.CreatedAt,
                    AdditionalProperties = update.AdditionalProperties,
                };
                yield return clone;
            }
            else
            {
                yield return update;
            }
        }

        NotifyTurnEnd(stats);
        await SafeFlushAsync();
    }

    /// <summary>回合结束回调：脱敏计数 / 模型实际收到的最后一条用户文本 / 是否含图片（T5.3 徽标数据源）；
    /// 顺带触发会话映射落盘（此时本回合的 mask 已全部入表）。</summary>
    private void NotifyTurnEnd(MaskingTurnStats stats)
    {
        _turnEndCallback?.Invoke(stats.MaskedCount, _lastMaskedUserText, stats.DataContentCount > 0);
        _persistCallback?.Invoke();
    }

    // ── 出站（Prepare：副本 + 掩码 + 说明注入） ──────────────────────────

    private (List<ChatMessage> Messages, ChatOptions? Options, MaskingTurnStats Stats) PrepareRequest(
        IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        // 缓存版本键取"本轮开始时"的版本（§7.1：本轮首现 tag 不可能以半角形存在于历史）
        var snapshotVersion = _engine.Snapshot.Version;
        var sessionVersion = Volatile.Read(ref _sessionVersion);

        var source = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var outMessages = new List<ChatMessage>(source.Count);
        var maskedTotal = 0;
        var dataContentCount = 0;

        foreach (var message in source)
        {
            if (_cache.TryGetValue(message, out var entry)
                && entry.SnapshotVersion == snapshotVersion && entry.SessionVersion == sessionVersion)
            {
                outMessages.Add(entry.Masked);
                if (message.Role == ChatRole.User)
                {
                    _lastMaskedUserText = string.Concat(entry.Masked.Contents.OfType<TextContent>().Select(t => t.Text));
                }
                continue;
            }
            var masked = MaskMessage(message, ref maskedTotal, ref dataContentCount);
            _cache.Remove(message);
            _cache.Add(message, new CacheEntry(snapshotVersion, sessionVersion, masked));
            outMessages.Add(masked);
            if (message.Role == ChatRole.User)
            {
                _lastMaskedUserText = string.Concat(masked.Contents.OfType<TextContent>().Select(t => t.Text));
            }
        }

        // ChatOptions 请求级副本（D17）：Instructions 先脱敏、后拼说明
        ChatOptions? outOptions = null;
        var instructions = "";
        var hadInstructions = false;
        if (options is not null)
        {
            outOptions = options.Clone();
            instructions = outOptions.Instructions ?? "";
            hadInstructions = instructions.Length > 0;
            if (hadInstructions)
            {
                var outcome = MaskWithSession(instructions);
                maskedTotal += outcome.MaskedCount;
                instructions = outcome.Text;
                outOptions.Instructions = instructions;
            }
        }

        // 占位符系统说明（D12 合并注入）：本轮有脱敏或会话已有占位符时追加
        var noteInjected = false;
        if (_engine.Snapshot.MaskNote && (maskedTotal > 0 || !_emitted.IsEmpty))
        {
            var note = Note();
            if (hadInstructions && outOptions is not null)
            {
                outOptions.Instructions = instructions + "\n\n" + note;
                noteInjected = true;
            }
            else
            {
                outOptions = options?.Clone() ?? new ChatOptions();
                var prefix = outOptions.Instructions;
                outOptions.Instructions = string.IsNullOrEmpty(prefix) ? note : prefix + "\n\n" + note;
                noteInjected = true;
            }
        }

        return (outMessages, outOptions, new MaskingTurnStats(maskedTotal, dataContentCount, noteInjected));
    }

    private ChatMessage MaskMessage(ChatMessage message, ref int maskedTotal, ref int dataContentCount)
    {
        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent tc:
                {
                    var outcome = MaskWithSession(tc.Text ?? "");
                    maskedTotal += outcome.MaskedCount;
                    contents.Add(new TextContent(outcome.Text));
                    break;
                }
                case TextReasoningContent rc:
                {
                    var outcome = MaskWithSession(rc.Text ?? "");
                    maskedTotal += outcome.MaskedCount;
                    contents.Add(new TextReasoningContent(outcome.Text));
                    break;
                }
                case FunctionCallContent fc:
                {
                    // 历史中的工具调用实参：先还原（上回合展示用真实值）再重新脱敏（D2/D15）
                    var restored = _engine.RestoreToolArgs(fc.Arguments, fc.Name, _emitted, _escapes);
                    var args = restored as IDictionary<string, object?> ?? fc.Arguments;
                    var maskedArgs = _engine.MaskToolArgs(args, _emitted, _escapes);
                    maskedTotal += maskedArgs.Emitted.Count;
                    contents.Add(new FunctionCallContent(fc.CallId, fc.Name,
                        maskedArgs.Args as IDictionary<string, object?> ?? fc.Arguments));
                    break;
                }
                case ToolApprovalRequestContent approval when approval.ToolCall is FunctionCallContent afc:
                {
                    var restored = _engine.RestoreToolArgs(afc.Arguments, afc.Name, _emitted, _escapes);
                    var args = restored as IDictionary<string, object?> ?? afc.Arguments;
                    var maskedArgs = _engine.MaskToolArgs(args, _emitted, _escapes);
                    maskedTotal += maskedArgs.Emitted.Count;
                    contents.Add(new ToolApprovalRequestContent(afc.Name, new FunctionCallContent(
                        afc.CallId, afc.Name, maskedArgs.Args as IDictionary<string, object?> ?? afc.Arguments))
                    {
#pragma warning disable MEAI001
                        RequiresConfirmation = approval.RequiresConfirmation,
#pragma warning restore MEAI001
                    });
                    break;
                }
                case DataContent dc:
                    // D6：图片等二进制内容不脱敏——透传并计数（徽标"图片内容未脱敏"提示）
                    dataContentCount++;
                    contents.Add(dc);
                    break;
                default:
                    contents.Add(content);
                    break;
            }
        }
        return new ChatMessage(message.Role, contents);
    }

    private MaskOutcome MaskWithSession(string text)
    {
        var outcome = _engine.MaskText(text, _emitted, _escapes);
        MergeDeltas(outcome);
        return outcome;
    }

    private void MergeDeltas(MaskOutcome outcome)
    {
        foreach (var (tag, canonical) in outcome.Emitted)
        {
            if (_emitted.TryAdd(tag, canonical))
            {
                SessionVersionBump();
            }
        }
        foreach (var (escaped, original) in outcome.Escapes)
        {
            if (_escapes.TryAdd(escaped, original))
            {
                SessionVersionBump();
            }
        }
    }

    private void SessionVersionBump()
    {
        // 软上限（doc/15 §7.1）：超限清空 = fail-open（旧映射占位符按原样显示）
        if (_emitted.Count > SessionMapCap || _escapes.Count > SessionMapCap)
        {
            _emitted.Clear();
            _escapes.Clear();
        }
        Interlocked.Increment(ref _sessionVersion);
    }

    /// <summary>回合结束时排空 overlay/统计落库（幂等；失败不阻断——数据在下回合重试）。</summary>
    private async Task SafeFlushAsync()
    {
        try
        {
            await _engine.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[privacy] flush 失败（下回合重试）：{ex.Message}");
        }
    }

    /// <summary>非流式响应中的工具实参/审批还原（文本内容由显示层一次性还原，此处不处理文本）。</summary>
    private AIContent RestoreToolContent(AIContent content)
    {
        switch (content)
        {
            case FunctionCallContent fc:
            {
                var restored = _engine.RestoreToolArgs(fc.Arguments, fc.Name, _emitted, _escapes);
                return new FunctionCallContent(fc.CallId, fc.Name,
                    restored as IDictionary<string, object?> ?? fc.Arguments);
            }
            case ToolApprovalRequestContent approval when approval.ToolCall is FunctionCallContent afc:
            {
                var restored = _engine.RestoreToolArgs(afc.Arguments, afc.Name, _emitted, _escapes);
                return new ToolApprovalRequestContent(afc.Name, new FunctionCallContent(
                    afc.CallId, afc.Name, restored as IDictionary<string, object?> ?? afc.Arguments))
                {
#pragma warning disable MEAI001
                    RequiresConfirmation = approval.RequiresConfirmation,
#pragma warning restore MEAI001
                };
            }
            default:
                return content;
        }
    }
}
