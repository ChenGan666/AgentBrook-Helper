namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// Core 层面向用户日志的多语支持：宿主启动时设置 Lang（"zh"/"en"/"id"），
/// 日志文案用 L(中文, English, Bahasa?) 按当前语言取值。默认中文。
/// </summary>
public static class CoreStrings
{
    /// <summary>当前 UI 语言（"zh"/"en"/"id"）。由宿主在启动时设置。</summary>
    public static string Lang { get; set; } = "zh";

    public static string L(string zh, string en, string? id = null) => Lang switch
    {
        "en" => en,
        "id" => id ?? en,
        _ => zh,
    };
}
