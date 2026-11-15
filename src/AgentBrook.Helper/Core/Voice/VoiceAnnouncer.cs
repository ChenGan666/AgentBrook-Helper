using System.Text;
using AgentBrook.Agent;
using AgentBrook.Agent.Infrastructure;
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

    /// <summary>摘要系统提示词（doc/Voice/07 §6.3 定稿 + 第 8 条 N1 崩溃表述）。</summary>
    private const string SummarySystemPrompt =
        """
        你是 AgentBrook 的语音播报摘要器。主任务刚执行完毕，请根据输入生成一段将被朗读给用户听的简短播报文本。

        输入包含：①用户原始需求 ②Agent 最终文字回答（可能很长，仅供提炼） ③本轮产物清单（类型/名称/是否被打开） ④状态提示（正常完成/执行失败/等待用户输入或授权）。

        要求：
        1. 先说结果（完成/失败/等待用户操作），再用一句话说明交付了什么。
        2. 产物如实描述：仅生成→说"可以查看/打开查看"；只有清单标注"已打开"才可说"已经帮你打开"；绝不虚构未执行的动作。
        3. 状态为"等待用户输入或授权"时，明确请用户继续下一步。
        4. 不复述最终回答全文；不念代码、Markdown、JSON、链接、文件正文。
        5. 自然、亲切的口语，1~3 句，一般不超过 60 个汉字；避免"任务已完成"这类机械话术堆叠。
        6. 没有值得建议的下一步时，简洁告知结果即可。
        7. 只输出要朗读的纯文本：无标题、无引号、无表情、无任何格式标记；文本将被直接送入本地 TTS 引擎。
        8. 状态提示为「执行失败」（程序级崩溃）时，直接告知用户任务执行中遇到了错误，简要说明已知原因并建议重试或查看回复；此时最终回答可能为空或残缺，绝不虚构已完成的步骤。
        9. 第一句控制在 15 字以内（语音合成按句流水线播放——第一句越短，用户越早听到声音）。
        """;

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

            // 0a 用户中断：唯一不播的完成态（需求 §5.5）
            if (turnCancelled) return false;

            // 0e 插件门卫：未装/停用/总开关关/模型未就绪
            if (cap is null || !cap.AutoAnnounceEnabled || !cap.IsReady) return false;

            // 语言门卫：播报语言 ∉ {zh,en}（如 id 语 UI + 跟随界面）→ 跳过（引擎 v1 只支持中英）
            var lang = cap.AnnounceLanguage switch
            {
                TtsAnnounceLanguage.Chinese => "zh",
                TtsAnnounceLanguage.English => "en",
                _ => I18n.Lang,
            };
            if (lang is not ("zh" or "en")) return false;

            // 0b statusHint：仅用户中断不播（上面已排除）；崩溃回合播"出错了"（N1）
            var statusHint =
                !sawTurnCompleted ? "failed" :                       // 崩溃路径（catch 置 turnHadError）
                turnHadError ? "failed" :                            // 罕见：TurnCompleted 已发后收尾再抛
                LooksLikeQuestionTail(finalAnswer) ? "awaiting_user_answer" :
                "normal";

            // 0c IM 会话不播（v1 范围；:566 先例）
            if (agent?.CurrentSessionId.StartsWith("im-", StringComparison.Ordinal) == true) return false;

            // 0d 播报时机=仅窗口后台（默认，B1）
            if (convActive && cap.AnnounceOnlyWhenInactive) return false;

            // 内容门卫：没有任何可播依据
            if (string.IsNullOrWhiteSpace(userText) && string.IsNullOrWhiteSpace(finalAnswer)) return false;

            // 0f 去重（B2：TaskId 可空 → turnSeq 降级键；登记于同步段，后台失败不回滚——本就无重试语义）
            var key = artifacts?.TaskId is { Length: > 0 } taskId ? taskId : $"seq:{turnSeq}";
            lock (_gate)
            {
                if (!_announcedKeys.Add(key)) return false;
                _announcedOrder.Enqueue(key);
                while (_announcedOrder.Count > LruCap && _announcedOrder.TryDequeue(out var old))
                {
                    _announcedKeys.Remove(old);
                }
            }

            BeginAnnounceChain(agent, cap, turnSeq, userText, finalAnswer, artifacts, statusHint, lang);
            return true;
        }
        catch
        {
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
            var summary = await agent.QuickCompleteAsync(
                SummarySystemPrompt,
                BuildUserPrompt(userText, finalAnswer, artifacts, statusHint, lang),
                summaryCts.Token).ConfigureAwait(false);
            summary = Sanitize(summary);
            if (string.IsNullOrWhiteSpace(summary))
            {
                Log($"[tts-announcer] turn {turnSeq}: 摘要为空，跳过播报");
                return;
            }

            using var speakCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await cap.SpeakAsync(new TtsSpeakRequest { Text = summary }, speakCts.Token).ConfigureAwait(false);
            Log($"[tts-announcer] turn {turnSeq}: 播报 {(result.Success ? $"完成（音频 {result.AudioMs}ms，合成 {result.SynthMs}ms）" : $"失败（{result.ErrorCode}）")}，摘要长度 {summary.Length}");
        }
        catch (OperationCanceledException)
        {
            Log($"[tts-announcer] turn {turnSeq}: 摘要/播报超时或被取消，静默让位（N2-a 代价）");
        }
        catch (Exception ex)
        {
            Log($"[tts-announcer] turn {turnSeq}: 链路异常 {ex.GetType().Name}，静默（不视为任务失败）");
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

    /// <summary>摘要 user prompt 组装（需求原文 + 最终回答 + 产物清单 + 状态 + 语言指令）。</summary>
    private static string BuildUserPrompt(string userText, string finalAnswer, TaskArtifacts? artifacts,
        string statusHint, string lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【输出语言】{lang switch { "en" => "English", _ => "中文" }}");
        sb.AppendLine($"【状态提示】{statusHint switch
        {
            "failed" => "执行失败（程序级异常，最终回答可能为空或残缺）",
            "awaiting_user_answer" => "等待用户输入或授权（最终回答以提问结尾）",
            _ => "正常完成",
        }}");
        sb.AppendLine($"【用户原始需求】{(string.IsNullOrWhiteSpace(userText) ? "（空）" : Truncate(userText, 2000))}");
        sb.AppendLine($"【Agent 最终文字回答】{(string.IsNullOrWhiteSpace(finalAnswer) ? "（空）" : Truncate(finalAnswer, 4000))}");
        if (artifacts is { Files.Count: > 0 })
        {
            sb.AppendLine("【本轮产物清单】（类型 | 名称 | 状态）");
            foreach (var f in artifacts.Files.Take(10))
            {
                sb.AppendLine($"- {f.FileType} | {f.FileName} | {f.Kind}");
            }
        }
        return sb.ToString();
    }

    /// <summary>摘要清洗：剥代码栅栏/Markdown 符号/链接 + 长度闸（>120 字截断加提示）。</summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r", "").Split('\n');
        var kept = lines.Where(l => !l.TrimStart().StartsWith("```")).ToList();
        var s = string.Join(' ', kept);
        // 剥常见 Markdown 修饰与链接（摘要必须纯文本可读）
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[*_#`>]+", "");
        s = s.Trim();
        if (s.Length > 120)
        {
            s = s[..120].TrimEnd() + (lang_zh(s) ? "…详情请看回复。" : "... see the full reply.");
        }
        return s;

        static bool lang_zh(string t) => t.Count(c => c >= 0x4E00 && c <= 0x9FFF) > t.Length / 4;
    }

    /// <summary>收尾反问判定（"？"结尾——核心 DetectIncomplete 同族语义的轻量近似）。</summary>
    private static bool LooksLikeQuestionTail(string answer)
    {
        var t = answer.TrimEnd();
        return t.EndsWith('？') || t.EndsWith('?');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void Log(string message) => Console.Error.WriteLine(message);
}
