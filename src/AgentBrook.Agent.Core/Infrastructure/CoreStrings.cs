namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// Core 层面向用户日志的双语支持：宿主启动时设置 UiLang（"zh"/"en"），
/// 日志文案用 L(中文, English) 按当前语言取值。默认中文。
/// </summary>
public static class CoreStrings
{
    /// <summary>当前 UI 语言（"zh"/"en"）。由宿主在启动时设置。</summary>
    public static string Lang { get; set; } = "zh";

    public static string L(string zh, string en) => Lang == "en" ? en : zh;
}
