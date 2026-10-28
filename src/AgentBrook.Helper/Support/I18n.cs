using AgentBrook.Helper.Windows;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using System.Globalization;

namespace AgentBrook.Helper.Support;

/// <summary>
/// 界面多语言支持：统一语义化 key（如 "status.starting"），每种语言一个独立字典。
/// 字典定义见 I18n.Zh.cs / I18n.En.cs / I18n.Id.cs（partial）。
/// 语言设置：auto（跟随系统，默认）/ zh / en / id，持久化于 settings.json 的 UiLanguage。
/// </summary>
internal static partial class I18n
{
    public static string Setting { get; private set; } = "auto";   // 用户设置值
    public static string Lang { get; private set; } = "zh";        // 解析后的实际语言

    /// <summary>语言切换后触发（窗口订阅以刷新已渲染的静态文案）。</summary>
    public static event Action? LanguageChanged;

    // 字典按语言拆分在 I18n.{Zh,En,Id}.cs 分部文件中。
    private static readonly Dictionary<string, string> Zh = ZhCore();
    private static readonly Dictionary<string, string> En = EnCore();
    private static readonly Dictionary<string, string> Id = IdCore();

    /// <summary>当前语言对应字典；未知语言回退中文。</summary>
    private static Dictionary<string, string> Active => Lang switch
    {
        "en" => En,
        "id" => Id,
        _ => Zh,
    };

    /// <summary>应用语言设置（auto/zh/en/id）。auto 跟随系统 UI 文化：zh→中文，id→印尼语，其余→英文。</summary>
    public static void SetLanguage(string setting)
    {
        Setting = string.IsNullOrWhiteSpace(setting) ? "auto" : setting;
        var prev = Lang;
        Lang = Setting == "auto"
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
            {
                "zh" => "zh",
                "id" => "id",
                _ => "en",
            }
            : Setting;
        if (Lang != prev)
        {
            AgentBrook.Agent.Infrastructure.CoreStrings.Lang = Lang;   // Core 日志语言同步
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>翻译：按 key 查当前语言字典，缺失时依次回退中文 → key 本身；args 填充 {0}… 占位。</summary>
    public static string T(string key, params object[] args)
    {
        var s = Active.GetValueOrDefault(key) ?? Zh.GetValueOrDefault(key) ?? key;
        return args.Length > 0 ? string.Format(s, args) : s;
    }
}
