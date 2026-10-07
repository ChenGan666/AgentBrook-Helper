using System.Text.Json;
using Microsoft.Extensions.Configuration;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Helper;

/// <summary>UI 偏好持久化（存工作区目录，随会话数据一起保留）。</summary>
internal static class UiPrefs
{
    private static string? _dir;

    public static string WorkspaceDir()
    {
        if (_dir is null)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();
            var config = configuration.Get<AppConfig>() ?? new AppConfig();
            var appRoot = Workspace.LocateAppRoot();
            // 与宿主初始化共用同一解析规则：全新安装回退到数据目录内的 workspace
            _dir = Workspace.ResolveWorkspaceDir(appRoot, config.Agent.WorkspaceRoot);
        }
        return _dir;
    }

    public static T? Load<T>(string file) where T : class
    {
        try
        {
            var path = Path.Combine(WorkspaceDir(), file);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
        }
        catch
        {
            // 偏好文件损坏时回退默认值
        }
        return null;
    }

    public static void Delete(string file)
    {
        try
        {
            var path = Path.Combine(WorkspaceDir(), file);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }

    public static void Save<T>(string file, T value)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(WorkspaceDir(), file),
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 偏好保存失败不影响主流程
        }
    }
}

/// <summary>悬浮气泡位置（屏幕逻辑坐标）。</summary>
internal sealed record BubblePos(int X, int Y);

/// <summary>对话窗口位置与尺寸（PixelPoint 全局像素坐标 + DIP 宽高）。</summary>
internal sealed record ConvWinBounds(int X, int Y, double Width, double Height);

/// <summary>个性化设置：助手名字与气泡图标（提示音偏好带默认值，旧配置文件缺字段时自动补全）。</summary>
internal sealed record AssistantSettings(
    string AssistantName, string BubbleIcon, string Language, string CustomPrompt, string UiLanguage,
    bool NotifySoundOn = true, string NotifySoundId = "ding");


/// <summary>助手个性化的运行时状态（启动加载、设置窗口保存后更新）。</summary>
internal static class AssistantIdentity
{
    public static string Current { get; private set; } = "Brook";
    public static string BubbleIcon { get; private set; } = "Icon.Sparkle";
    public static string Language { get; private set; } = "zh";        // 模型回复语言
    public static string UiLanguage { get; private set; } = "auto";    // 界面语言（auto/zh/en）
    public static string CustomPrompt { get; private set; } = "";
    public static bool NotifySoundOn { get; private set; } = true;      // 任务完成提示音（会话窗非活动时）
    public static string NotifySoundId { get; private set; } = "ding";  // 音色

    public static void Load()
    {
        var s = UiPrefs.Load<AssistantSettings>("settings.json");
        if (s is not null)
        {
            if (!string.IsNullOrWhiteSpace(s.AssistantName)) Current = s.AssistantName.Trim();
            if (!string.IsNullOrWhiteSpace(s.BubbleIcon)) BubbleIcon = s.BubbleIcon;
            if (!string.IsNullOrWhiteSpace(s.Language)) Language = s.Language;
            if (!string.IsNullOrWhiteSpace(s.UiLanguage)) UiLanguage = s.UiLanguage;
            CustomPrompt = s.CustomPrompt ?? "";
            NotifySoundOn = s.NotifySoundOn;
            if (NotifySound.IsValidId(s.NotifySoundId)) NotifySoundId = s.NotifySoundId;
        }
        I18n.SetLanguage(UiLanguage);
        ApplyToProcessEnv();
    }

    /// <summary>把助手名注入进程环境变量：工作区生成脚本读取它作为水印/角标品牌（子进程自动继承，改名即生效）。</summary>
    public static void ApplyToProcessEnv()
        => Environment.SetEnvironmentVariable("AGENTBROOK_ASSISTANT_NAME", Current, EnvironmentVariableTarget.Process);

    /// <summary>保存提示音偏好（即时生效，独立于"保存常规设置"）。</summary>
    public static void SaveNotifyPrefs(bool on, string soundId)
    {
        NotifySoundOn = on;
        if (NotifySound.IsValidId(soundId))
        {
            NotifySoundId = soundId;
        }
        UiPrefs.Save("settings.json", new AssistantSettings(Current, BubbleIcon, Language, CustomPrompt, UiLanguage, NotifySoundOn, NotifySoundId));
    }

    public static void Apply(string name, string bubbleIcon, string language, string customPrompt, string uiLanguage)
    {
        if (!string.IsNullOrWhiteSpace(name)) Current = name.Trim();
        if (!string.IsNullOrWhiteSpace(bubbleIcon)) BubbleIcon = bubbleIcon;
        if (!string.IsNullOrWhiteSpace(language)) Language = language;
        if (!string.IsNullOrWhiteSpace(uiLanguage)) UiLanguage = uiLanguage;
        CustomPrompt = customPrompt ?? "";
        UiPrefs.Save("settings.json", new AssistantSettings(Current, BubbleIcon, Language, CustomPrompt, UiLanguage));
        I18n.SetLanguage(UiLanguage);
        ApplyToProcessEnv();
    }
}
