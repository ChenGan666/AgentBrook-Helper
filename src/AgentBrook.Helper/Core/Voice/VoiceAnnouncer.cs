using System.Text;
using AgentBrook.Agent;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Helper.Core.Voice;
using AgentBrook.Helper.Support;
using AgentBrook.Plugin.Abstractions;

namespace AgentBrook.Helper.Core;

/// <summary>
/// 语音播报装配（doc/Voice/07 §6）：回合真正结束后，旁路生成 ≤60 字口语摘要并经
/// ITtsCapability（本地 TTS 插件）合成播放。挂在 AssistantController.SendAsync 的 finally 块。
/// 评审定案（N1/N2）：同步门卫段 TryBeginAnnounce 全内存判定返回 willAttempt（提示音据此让位）；
/// 仅用户中断不播——崩溃回合播"出错了"（failed），语义失败由摘要内容表达（normal）。
/// 语音链路任何异常不得影响回合与文字回复：后台链 fire-and-forget + 超时 + 全程 try/catch。
/// 隐私：日志只记长度/耗时/错误码，不落播报文本（TtsLog 同款纪律）。
/// </summary>
internal sealed class VoiceAnnouncer
{
    private const int LruCap = 256;

    private readonly SpeechSummaryService _summary = new();

    private readonly object _gate = new();
    private readonly Queue<string> _announcedOrder = new();
    private readonly HashSet<string> _announcedKeys = new(StringComparer.Ordinal);

    // ── 主界面播报芯片（doc/Voice/07）：状态与事件 ──────────────────────────

    /// <summary>播报链进行中（摘要+合成+播放全程）。后台线程触发事件，订阅方自行调度 UI。</summary>
    public bool AnnounceActive { get; private set; }

    /// <summary>播报链开始/结束（后台线程触发；bool=是否进行中）。</summary>
    public event Action<bool>? AnnounceActiveChanged;

    /// <summary>芯片状态（UI 线程调用）：null=插件未装/未启用；(ready, enabled)=模型就绪与开关。</summary>
    public (bool Ready, bool Enabled)? GetChipState(BrookAgent? agent)
    {
        try
        {
            if (agent?.PluginManager?.Registry.GetCapability<ITtsCapability>() is not { } cap)
                return null;
            return (cap.IsReady, cap.AutoAnnounceEnabled);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 同步门卫段（微秒级，全内存判定；N2-a）：通过返回 true（调用方跳过本回合提示音），
    /// 同时启动后台摘要/播报链。任何分支都不抛异常、不阻塞。
    /// 每回合的首行判定与所有拒绝原因都落 voice-log（Windows GUI 无控制台——排障唯一痕迹）。
    /// </summary>
    public bool TryBeginAnnounce(BrookAgent? agent, int turnSeq, string userText, string finalAnswer,
        TaskArtifacts? artifacts, bool sawTurnCompleted, bool turnCancelled, bool turnHadError, bool convActive)
    {
        try
        {
            ITtsCapability? cap = null;
            try
            {
                cap = agent?.PluginManager?.Registry.GetCapability<ITtsCapability>();
            }
            catch { /* 插件热插拔竞态：按未安装处理 */ }

            bool Reject(string reason)
            {
                VLog($"turn {turnSeq}: 跳过播报——{reason}");
                return false;
            }

            VLog($"turn {turnSeq}: 回合结束（完成={sawTurnCompleted} 取消={turnCancelled} 出错={turnHadError} 前台={convActive}，" +
                 $"回答长度={finalAnswer.Length}，tts={(cap is null ? "未装" : cap.IsReady ? "就绪" : "未就绪")}）");

            // 0a 用户中断：唯一不播的完成态（需求 §5.5）
            if (turnCancelled) return Reject("用户中断");

            // 0e 插件门卫：未装/停用/总开关关/模型未就绪
            if (cap is null) return Reject("tts 插件未安装或未启用");
            if (!cap.AutoAnnounceEnabled) return Reject("自动播报开关为关");
            if (!cap.IsReady) return Reject("模型未就绪");

            // 语言门卫：播报语言 ∉ {zh,en}（如 id 语 UI + 跟随界面）→ 跳过（引擎 v1 只支持中英）
            var lang = cap.AnnounceLanguage switch
            {
                TtsAnnounceLanguage.Chinese => "zh",
                TtsAnnounceLanguage.English => "en",
                _ => I18n.Lang,
            };
            if (lang is not ("zh" or "en")) return Reject($"播报语言 {lang} 引擎不支持");

            // 0b statusHint：仅用户中断不播（上面已排除）；崩溃回合播"出错了"（N1）
            var statusHint =
                !sawTurnCompleted ? "failed" :                       // 崩溃路径（catch 置 turnHadError）
                turnHadError ? "failed" :                            // 罕见：TurnCompleted 已发后收尾再抛
                SpeechSummaryService.LooksLikeQuestionTail(finalAnswer) ? "awaiting_user_answer" :
                "normal";

            // 0c IM 会话排除已移除（2026-11-15 真机）：手机发起的 IM 回合走 HelperImTurnExecutor、
            //    不经 SendAsync，天然到不了本链路；会被拦的只有主窗口主动打开的 im- 会话回合——
            //    那正是用户人在桌面期待播报的场景（真机日志实证误拦）

            // 0d 播报时机=仅窗口后台（默认，B1）
            if (convActive && cap.AnnounceOnlyWhenInactive) return Reject("仅窗口后台模式且对话窗在前台");

            // 内容门卫：没有任何可播依据
            if (string.IsNullOrWhiteSpace(userText) && string.IsNullOrWhiteSpace(finalAnswer))
                return Reject("用户输入与回答均为空");

            // 0f 去重（B2：TaskId 可空 → turnSeq 降级键；登记于同步段，后台失败不回滚——本就无重试语义）
            var key = artifacts?.TaskId is { Length: > 0 } taskId ? taskId : $"seq:{turnSeq}";
            lock (_gate)
            {
                if (!_announcedKeys.Add(key)) return Reject($"去重命中（{key}）");
                _announcedOrder.Enqueue(key);
                while (_announcedOrder.Count > LruCap && _announcedOrder.TryDequeue(out var old))
                {
                    _announcedKeys.Remove(old);
                }
            }

            VLog($"turn {turnSeq}: 门卫通过（statusHint={statusHint} lang={lang}），启动后台播报链");
            BeginAnnounceChain(agent, cap, turnSeq, userText, finalAnswer, artifacts, statusHint, lang);
            return true;
        }
        catch (Exception ex)
        {
            VLog($"turn {turnSeq}: 门卫异常 {ex.GetType().Name}，回退纯提示音");
            return false;   // 门卫任何异常 → 回退纯提示音行为
        }
    }

    /// <summary>新回合开始（SendAsync 开头）：打断仍在进行的播报（需求 §7.5）。fire-and-forget，不 await。</summary>
    public void OnTurnStarted(BrookAgent? agent)
    {
        try
        {
            ITtsCapability? cap = null;
            try
            {
                cap = agent?.PluginManager?.Registry.GetCapability<ITtsCapability>();
            }
            catch { }
            if (cap is null) return;
            _ = cap.StopAsync();   // 不 await：SendAsync 在 UI 线程（N1/C2）
        }
        catch
        {
            // 打断失败静默（不影响新回合）
        }
    }

    /// <summary>后台链：摘要 LLM（20s 闸）→ 清洗 → 合成播放（30s 闸）。失败静默让位，不重试。</summary>
    private async void BeginAnnounceChain(BrookAgent agent, ITtsCapability cap, int turnSeq,
        string userText, string finalAnswer, TaskArtifacts? artifacts, string statusHint, string lang)
    {
        AnnounceActive = true;
        try
        {
            AnnounceActiveChanged?.Invoke(true);
        }
        catch { }
        try
        {
            using var summaryCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var artifactFiles = artifacts?.Files
                .Select(f => (Name: f.FileName, MediaType: f.FileType)).ToList();
            var summary = await _summary.SummarizeAsync(agent, new SpeechSummaryService.SummaryInput(
                userText, finalAnswer, artifactFiles, statusHint), lang, summaryCts.Token).ConfigureAwait(false);
            if (summary is null)
            {
                VLog($"turn {turnSeq}: 摘要为空，跳过播报");
                return;
            }
            VLog($"turn {turnSeq}: 摘要生成成功（{summary.Length} 字），开始合成播放");

            using var speakCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await cap.SpeakAsync(new TtsSpeakRequest { Text = summary }, speakCts.Token).ConfigureAwait(false);
            VLog($"turn {turnSeq}: 播报 {(result.Success ? $"完成（音频 {result.AudioMs}ms，合成 {result.SynthMs}ms）" : $"失败（{result.ErrorCode}）")}，摘要长度 {summary.Length}");
        }
        catch (OperationCanceledException)
        {
            VLog($"turn {turnSeq}: 摘要/播报超时或被取消，静默让位（N2-a 代价）");
        }
        catch (Exception ex)
        {
            VLog($"turn {turnSeq}: 链路异常 {ex.GetType().Name}：{ex.Message}，静默（不视为任务失败）");
        }
        finally
        {
            AnnounceActive = false;
            try
            {
                AnnounceActiveChanged?.Invoke(false);
            }
            catch { }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>播报链诊断落盘（voice-log.txt，Helper 内部同款 256KB 纪律）——Windows GUI 无控制台的唯一痕迹。</summary>
    private static void VLog(string message)
    {
        VoiceLog.Write("[tts-announcer] " + message);
        Console.Error.WriteLine("[tts-announcer] " + message);
    }
}
