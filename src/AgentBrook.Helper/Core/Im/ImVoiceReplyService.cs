using System.Collections.Concurrent;
using AgentBrook.Agent;
using AgentBrook.Agent.Plugins;
using AgentBrook.Messaging;
using AgentBrook.Helper.Support;
using AgentBrook.Plugin.Hosting.Abstractions;
using System.Text.Json;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Abstractions;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Helper.Core.Im;

/// <summary>语音回复五态（doc/Voice/08 v12 §4.1——快照触发点派生，优先级序：Cancelled→TimedOut→Failed→Awaiting→Succeeded）。</summary>
public enum TurnVoiceState
{
    Succeeded = 0,
    TimedOut = 1,
    Failed = 2,
    AwaitingInput = 3,
    Cancelled = 4,
}

/// <summary>
/// 语音回复快照（不可变；触发点同步捕获，后台链只读——评审三#4；字段=doc/Voice/08 v12 §4.2）。
/// </summary>
public sealed record SpeechTaskSnapshot(
    string SessionId,
    string TurnId,                    // 来源 A=回合 turnId；来源 B=收据 LogicTurnId
    string BizKey,                    // A 正常: speech-{turnId}；A 失败: speech-{ns}-{dedupKey}；B: speech-{receipt.LogicTurnId}
    string Source,                    // "im-remote" | "im-local"
    TurnVoiceState State,
    string StatusHint,                // normal / timed_out / failed / awaiting_user_answer
    string? UserText,                 // 需求原文（截 2000 字）
    string AnswerText,                // 最终回答（截 4000 字）
    IReadOnlyList<(string Name, string MediaType)> Files,   // 产物摘要（不含字节）
    string RouteNamespace,            // channelId+instanceId
    long AccountEpoch,
    string AccountId,
    string ChatId,
    string Lang,
    string Voice,
    float Speed);

/// <summary>
/// IM 语音回复编排（doc/Voice/08 v12 §4.2-4.4，09-S2a B6/B7）：消费不可变快照，
/// 门卫 → 摘要（Succeeded 走 LLM；timed_out/failed 确定性话术）→ 纯合成 → 按通道格式
/// （Telegram=ogg-opus 语音条；微信/Bridge=wav 文件——MP3 跨平台不支持，D3）→ blob → outbox 追加
/// （Text=概要、Files=[音频 ContentRef]、IdempotencyKey=BizKey）。
/// 全链 fire-and-forget（20s 摘要 + 30s 合成闸）；同步入口与后台链各自异常边界（评审四#1）；
/// 持久幂等 = IdempotencyKey=BizKey（唯一索引原子领取）+ in-flight 并发去重；有界等待 ≤16。
/// </summary>
internal sealed class ImVoiceReplyService
{
    private readonly SpeechSummaryService _summary = new();
    private readonly Func<BrookAgent?> _agentAccessor;        // 摘要 LLM 入口（QuickCompleteAsync）
    private readonly Func<MessagingStore?> _storeAccessor;    // 出站落库（Gate 内）
    private readonly Func<IBlobService?> _blobAccessor;       // 音频附件存储

    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);   // BizKey 并发去重
    private int _waiting;                                     // 有界等待（B7：≤16，超限放弃不堆积）
    private const int MaxWaiting = 16;

    public ImVoiceReplyService(Func<BrookAgent?> agentAccessor,
        Func<MessagingStore?> storeAccessor, Func<IBlobService?> blobAccessor)
    {
        _agentAccessor = agentAccessor;
        _storeAccessor = storeAccessor;
        _blobAccessor = blobAccessor;
    }

    /// <summary>
    /// 入口（来源 A/B 各自构建快照后调用）。同步段（门卫/去重/入队决策）异常不得外溢（评审四#1）；
    /// 返回 true = 已领取并异步执行（调用方可用于让位提示音等既有逻辑）。
    /// </summary>
    public bool TryEnqueue(SpeechTaskSnapshot snapshot, Func<ITtsSynthesis?> synthesis)
    {
        try
        {
            // 并发/重复通知去重：BizKey 原子领取（持久层唯一索引是第二道闸）
            if (!_inFlight.TryAdd(snapshot.BizKey, 0)) return false;
            if (!Precheck(snapshot, synthesis()))
            {
                _inFlight.TryRemove(snapshot.BizKey, out _);
                return false;
            }
            if (Interlocked.Increment(ref _waiting) > MaxWaiting)
            {
                Interlocked.Decrement(ref _waiting);
                _inFlight.TryRemove(snapshot.BizKey, out _);
                VLog($"{snapshot.BizKey}: 等待队列已满（{MaxWaiting}）——放弃语音");
                return false;
            }
            _ = RunPipelineAsync(snapshot, synthesis);
            return true;
        }
        catch (Exception ex)
        {
            VLog($"{snapshot.BizKey}: 同步入口异常 {ex.GetType().Name}——静默跳过（评审四#1）");
            return false;
        }
    }

    /// <summary>门卫（doc/Voice/08 v12 §4.1/§4.3）。</summary>
    private static bool Precheck(SpeechTaskSnapshot snapshot, ITtsSynthesis? cap)
    {
        if (cap is null) { VLog($"{snapshot.BizKey}: tts 插件未装/未启用——跳过"); return false; }
        if (!cap.IsReady) { VLog($"{snapshot.BizKey}: 模型未就绪——跳过"); return false; }
        if (snapshot.Lang is not ("zh" or "en"))
        {
            VLog($"{snapshot.BizKey}: 语言 {snapshot.Lang} 引擎不支持——跳过");
            return false;
        }
        if (string.IsNullOrWhiteSpace(snapshot.UserText) && string.IsNullOrWhiteSpace(snapshot.AnswerText))
        {
            VLog($"{snapshot.BizKey}: 摘要输入为空——跳过");
            return false;
        }
        return true;
    }

    private async Task RunPipelineAsync(SpeechTaskSnapshot snapshot, Func<ITtsSynthesis?> synthesis)
    {
        try
        {
            var cap = synthesis();
            if (cap is null || !cap.IsReady) return;

            // ① 摘要：Succeeded 走 LLM（摘要档→主模型回退）；timed_out/failed 确定性三语话术（不走 LLM）
            string summary;
            if (snapshot.State is TurnVoiceState.Succeeded)
            {
                var agent = _agentAccessor();
                if (agent is null) { VLog($"{snapshot.BizKey}: agent 不可用——跳过"); return; }
                using var summaryCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                summary = await _summary.SummarizeAsync(agent, new SpeechSummaryService.SummaryInput(
                    snapshot.UserText, snapshot.AnswerText, snapshot.Files, MapHint(snapshot.State)),
                    snapshot.Lang, summaryCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    VLog($"{snapshot.BizKey}: 摘要为空——跳过播报");
                    return;
                }
            }
            else
            {
                // timed_out/failed 确定性三语话术（评审七#5：超时=终态，不说"仍在处理"）
                summary = DeterministicTemplate(snapshot.State, snapshot.Lang);
            }
            VLog($"{snapshot.BizKey}: 摘要就绪（{summary.Length} 字），开始纯合成");

            // ② 纯合成（30s 闸；整段一次生成不分段；租约/drain 由引擎 v9 语义兜住）
            using var synthCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var synth = await cap.SynthesizeAsync(
                new TtsSynthesisRequest { Text = summary, Voice = snapshot.Voice, Speed = snapshot.Speed },
                synthCts.Token).ConfigureAwait(false);
            if (!synth.Success || synth.WavBytes is null)
            {
                VLog($"{snapshot.BizKey}: 纯合成失败（{synth.ErrorCode}）");
                return;
            }

            // ③ 封装（目标通道固定策略，评审五#3：Telegram=ogg-opus 语音条；微信/Bridge=wav 文件——MP3 跨平台不支持，D3）
            var channelId = snapshot.RouteNamespace.Split('+')[0];
            var isTelegram = channelId.Contains("telegram", StringComparison.OrdinalIgnoreCase);
            var fileName = isTelegram
                ? $"AgentBrook语音概要_{snapshot.TurnId}.ogg"
                : $"AgentBrook语音概要_{snapshot.TurnId}.wav";
            var mediaType = isTelegram ? "audio/ogg" : "audio/wav";

            // ④ blob（原子写+失败自清，评审三#3；HelperBlobService v9 原子写）
            var blobSvc = _blobAccessor();
            if (blobSvc is null) return;
            await using var audioStream = new MemoryStream(synth.WavBytes);
            var content = await blobSvc.WriteAsync("im-voice", audioStream, synth.WavBytes.Length,
                fileName, mediaType, CancellationToken.None).ConfigureAwait(false);

            // ⑤ outbox 追加：Text=概要、Files=[音频 ContentRef]；IdempotencyKey=BizKey（唯一索引原子领取，
            //    评审二#3/三#2）；turn_id=BizKey 逻辑关联原任务
            var store = _storeAccessor();
            if (store is null) { VLog($"{snapshot.BizKey}: store 不可用"); return; }
            var row = new OutboxRow(
                $"ob-{Guid.NewGuid():N}"[0..16], snapshot.RouteNamespace, snapshot.SessionId, snapshot.BizKey,
                snapshot.AccountEpoch, snapshot.AccountId, snapshot.ChatId,
                summary, ImJson.Serialize(new List<ContentRef>
                {
                    new(content.BlobId, content.Sha256, content.Length, fileName, mediaType),
                }),
                snapshot.BizKey, null, "Pending", 0, null, false, null, null, null,
                DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
            var (ok, already, _) = store.TryInsertVoiceOutbox(row);
            if (!ok)
            {
                VLog($"{snapshot.BizKey}: 语音 outbox 唯一领取失败（重复通知）");
                return;
            }
            VLog($"{snapshot.BizKey}: 语音 outbox 已入队（{fileName} {synth.WavBytes.Length}B {mediaType}）");
        }
        catch (OperationCanceledException)
        {
            VLog($"{snapshot.BizKey}: 语音链超时/取消——静默放弃");
        }
        catch (Exception ex)
        {
            VLog($"{snapshot.BizKey}: 语音链异常 {ex.GetType().Name}——静默（不视为任务失败）");
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
            _inFlight.TryRemove(snapshot.BizKey, out _);
        }
    }

    // ── 双来源入口（doc/Voice/08 v12 §4.1/§4.2）────────────────────────────

    /// <summary>
    /// 来源 A 入口（ImComposition.TurnCompleted 订阅调用；语音异常不得外溢——评审四#1）。
    /// 快照由 notice additive 字段构建；BizKey 按分支派生（永久失败 turnId 空 → 入站身份，评审五#1）。
    /// </summary>
    public void OnTurnCompletedRemote(AgentBrook.Messaging.TurnCompletedNotice notice,
        Func<BrookAgent?> agentAccessor, Func<IBlobService?> blobsAccessor)
    {
        try
        {
            var agent = agentAccessor();
            var cap = agent?.PluginManager?.Registry.GetCapability<ITtsSynthesis>();
            if (cap is null)
            {
                VLog($"{notice.TurnId ?? "-"}: tts 插件未装——跳过");
                return;
            }
            var settings = agent.PluginManager?.GetPluginSettingsJson("tts");
            if (!ImVoiceReplyService.IsImVoiceEnabled(settings))
            {
                VLog($"{notice.TurnId ?? "-"}: im_voice_enabled 关——跳过");
                return;
            }
            var lang = I18n.Lang;
            var state = notice.TimedOut ? TurnVoiceState.TimedOut
                : !notice.Completed ? TurnVoiceState.Failed
                : SpeechSummaryService.LooksLikeQuestionTail(notice.AnswerText ?? "")
                    ? TurnVoiceState.AwaitingInput : TurnVoiceState.Succeeded;
            if (state is TurnVoiceState.AwaitingInput)
            {
                VLog($"{notice.TurnId ?? "-"}: AwaitingInput 不播（D7）");
                return;
            }

            // BizKey：永久失败通知 turnId 为空（F23）→ 入站身份 dedupKey（评审五#1）
            var bizKey = string.IsNullOrWhiteSpace(notice.TurnId)
                ? $"speech-{notice.RouteNamespace}-{notice.InboxDedupKey}"
                : $"speech-{notice.TurnId}";
            var snapshot = new SpeechTaskSnapshot(
                notice.SessionId, notice.TurnId, bizKey, "im-remote", state, MapHint(state),
                notice.UserText, notice.AnswerText ?? "",
                notice.Files ?? Array.Empty<(string Name, string MediaType)>(),
                notice.RouteNamespace, notice.AccountEpoch, notice.AccountId, notice.ChatId,
                lang, ImVoiceReplyService.VoiceOf(settings), ImVoiceReplyService.SpeedOf(settings));
            TryEnqueue(snapshot, () => agent.PluginManager?.Registry.GetCapability<ITtsSynthesis>());
        }
        catch (Exception ex)
        {
            VLog($"来源 A 入口异常 {ex.GetType().Name}——静默（不得外溢）");
        }
    }

    /// <summary>
    /// 来源 B 入口（AssistantController.SendAsync finally；收据非空才接入——评审三#1 来源 B 失败不播）。
    /// </summary>
    public void OnTurnCompletedLocal(AgentBrook.Messaging.MessagingService.OutboundReceipt receipt,
        int turnSeq, string? userText, string answer,
        IReadOnlyList<(string Name, string MediaType)>? files,
        string sessionId, string routeNamespace, long accountEpoch, string accountId, string chatId)
    {
        try
        {
            var agent = _agentAccessor();
            var cap = agent?.PluginManager?.Registry.GetCapability<ITtsSynthesis>();
            if (cap is null) return;
            var settings = agent.PluginManager?.GetPluginSettingsJson("tts");
            if (!ImVoiceReplyService.IsImVoiceEnabled(settings)) return;
            var lang = I18n.Lang;
            if (lang is not ("zh" or "en")) return;
            var state = SpeechSummaryService.LooksLikeQuestionTail(answer)
                ? TurnVoiceState.AwaitingInput : TurnVoiceState.Succeeded;
            if (state is TurnVoiceState.AwaitingInput) return;   // D7：来源 B 也不播追问

            var bizKey = $"speech-local-{receipt.LogicTurnId}";
            var snapshot = new SpeechTaskSnapshot(
                sessionId, receipt.LogicTurnId, bizKey, "im-local", state, MapHint(state),
                userText, answer,
                files ?? Array.Empty<(string Name, string MediaType)>(),
                routeNamespace, accountEpoch, accountId, chatId,
                lang, ImVoiceReplyService.VoiceOf(settings), ImVoiceReplyService.SpeedOf(settings));
            TryEnqueue(snapshot, () => agent.PluginManager?.Registry.GetCapability<ITtsSynthesis>());
        }
        catch (Exception ex)
        {
            VLog($"来源 B 入口异常 {ex.GetType().Name}——静默");
        }
    }

    /// <summary>D2：im_voice_enabled 独立开关、默认关、缺键关（09 §0；宿主经 GetPluginSettingsJson 直读）。</summary>
    public static bool IsImVoiceEnabled(string? ttsSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(ttsSettingsJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ttsSettingsJson);
            return doc.RootElement.TryGetProperty("tts.im_voice_enabled", out var v)
                && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                && v.GetBoolean();
        }
        catch { return false; }
    }

    private static string VoiceOf(string? ttsSettingsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ttsSettingsJson ?? "{}");
            return doc.RootElement.TryGetProperty("tts.voice", out var v) ? v.GetString() ?? "zf_024" : "zf_024";
        }
        catch { return "zf_024"; }
    }

    private static float SpeedOf(string? ttsSettingsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ttsSettingsJson ?? "{}");
            return doc.RootElement.TryGetProperty("tts.speed", out var v) && v.TryGetSingle(out var f)
                ? Math.Clamp(f, 0.7f, 1.5f) : 1.0f;
        }
        catch { return 1.0f; }
    }

    /// <summary>五态→确定性三语话术（timed_out/failed；评审七#5：超时=终态，不说"仍在处理"）。</summary>
    private static string DeterministicTemplate(TurnVoiceState state, string lang) => state switch
    {
        TurnVoiceState.TimedOut => lang == "en"
            ? "This run timed out — check the text reply, then retry or split the task."
            : "本次处理超时了，请查看文字回复，稍后重试或拆分任务。",
        TurnVoiceState.Failed => lang == "en"
            ? "This task hit a problem — please retry later, or check Helper for details."
            : "这项任务处理时遇到了问题，请稍后重试，或回到 Helper 查看详情。",
        _ => "",
    };

    private static string MapHint(TurnVoiceState state) => state switch
    {
        TurnVoiceState.TimedOut => "timed_out",
        TurnVoiceState.Failed => "failed",
        TurnVoiceState.AwaitingInput => "awaiting_user_answer",
        _ => "normal",
    };

    private static void VLog(string message)
    {
        Console.Error.WriteLine("[im-voice] " + message);
        ImDiag.Log("[im-voice] " + message);   // GUI 无控制台——host-log.txt 是真机排障唯一痕迹
    }
}
