using System.Text;
using AgentBrook.Agent;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Helper.Core;

/// <summary>
/// 共享语音摘要服务（doc/Voice/08 §4.2）：桌面播报（VoiceAnnouncer）与 IM 语音消息（ImVoiceReplyService）
/// 消费同一实例——摘要内容、风格、语义一致，不是"提示词相似"。无状态纯函数集，唯一外部依赖
/// `BrookAgent.QuickCompleteAsync`（走摘要档模型、出口脱敏还原；失败回退主模型）。
/// </summary>
internal sealed class SpeechSummaryService
{
    /// <summary>摘要系统提示词（doc/Voice/07 §6.3 定稿 + 第 8 条 N1 崩溃表述 + 第 9 条首句短句化）。</summary>
    public const string SummarySystemPrompt =
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

    /// <summary>摘要输入（触发点同步捕获的不可变数据；Files=产物摘要元组——桌面由 TaskArtifacts 转换）。</summary>
    public sealed record SummaryInput(string UserText, string FinalAnswer,
        IReadOnlyList<(string Name, string MediaType)>? Files, string StatusHint);

    /// <summary>
    /// 生成摘要：QuickCompleteAsync（摘要档）→ 失败回退主模型（同一 20s 预算内）→ Sanitize。
    /// 返回 null = 摘要为空（调用方跳过语音）。
    /// </summary>
    public async Task<string?> SummarizeAsync(BrookAgent agent, SummaryInput input, string lang, CancellationToken ct)
    {
        var prompt = BuildUserPrompt(input.UserText, input.FinalAnswer, input.Files, input.StatusHint, lang);
        string summary;
        try
        {
            summary = await agent.QuickCompleteAsync(SummarySystemPrompt, prompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 摘要档模型不可用（Windows 工作区常未配置摘要档）→ 回退主模型重试（同一预算内）
            VLog($"摘要档调用失败（{ex.GetType().Name}），回退主模型重试");
            summary = await agent.QuickCompleteAsync(SummarySystemPrompt, prompt, ct, useMainModel: true).ConfigureAwait(false);
        }
        summary = Sanitize(summary);
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    /// <summary>摘要 user prompt 组装（需求原文 + 最终回答 + 产物清单 + 状态 + 语言指令）。</summary>
    public static string BuildUserPrompt(string userText, string finalAnswer,
        IReadOnlyList<(string Name, string MediaType)>? files,
        string statusHint, string lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【输出语言】{lang switch { "en" => "English", _ => "中文" }}");
        sb.AppendLine($"【状态提示】{statusHint switch
        {
            "failed" => "执行失败（程序级异常，最终回答可能为空或残缺）",
            "timed_out" => "处理超时（本次执行超时，回答可能为兜底文案）",
            "awaiting_user_answer" => "等待用户输入或授权（最终回答以提问结尾）",
            _ => "正常完成",
        }}");
        sb.AppendLine($"【用户原始需求】{(string.IsNullOrWhiteSpace(userText) ? "（空）" : Truncate(userText, 2000))}");
        sb.AppendLine($"【Agent 最终文字回答】{(string.IsNullOrWhiteSpace(finalAnswer) ? "（空）" : Truncate(finalAnswer, 4000))}");
        if (files is { Count: > 0 })
        {
            sb.AppendLine("【本轮产物清单】（名称 | 媒体类型）");
            foreach (var f in files.Take(10))
            {
                sb.AppendLine($"- {f.Name} | {f.MediaType}");
            }
        }
        return sb.ToString();
    }

    /// <summary>摘要清洗：剥代码栅栏/Markdown 符号/链接 + 长度闸（>120 字截断加提示）。</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r", "").Split('\n');
        var kept = lines.Where(l => !l.TrimStart().StartsWith("```")).ToList();
        var s = string.Join(' ', kept);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[*_#`>]+", "");
        s = s.Trim();
        if (s.Length > 120)
        {
            s = s[..120].TrimEnd() + (IsZhDominant(s) ? "…详情请看回复。" : "... see the full reply.");
        }
        return s;

        static bool IsZhDominant(string t) => t.Count(c => c >= 0x4E00 && c <= 0x9FFF) > t.Length / 4;
    }

    /// <summary>收尾反问判定（"？"结尾——核心 DetectIncomplete 同族语义的轻量近似，非强契约）。</summary>
    public static bool LooksLikeQuestionTail(string answer)
    {
        var t = answer.TrimEnd();
        return t.EndsWith('？') || t.EndsWith('?');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void VLog(string message) => Console.Error.WriteLine("[speech-summary] " + message);
}
