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
            _dir = Path.Combine(appRoot, config.Agent.WorkspaceRoot);
            Directory.CreateDirectory(_dir);
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

/// <summary>个性化设置：助手名字与气泡图标。</summary>
internal sealed record AssistantSettings(string AssistantName, string BubbleIcon, string Language, string CustomPrompt);


/// <summary>助手个性化的运行时状态（启动加载、设置窗口保存后更新）。</summary>
internal static class AssistantIdentity
{
    public static string Current { get; private set; } = "Brook";
    public static string BubbleIcon { get; private set; } = "Icon.Sparkle";
    public static string Language { get; private set; } = "zh";
    public static string CustomPrompt { get; private set; } = "";

    public static void Load()
    {
        var s = UiPrefs.Load<AssistantSettings>("settings.json");
        if (s is not null)
        {
            if (!string.IsNullOrWhiteSpace(s.AssistantName)) Current = s.AssistantName.Trim();
            if (!string.IsNullOrWhiteSpace(s.BubbleIcon)) BubbleIcon = s.BubbleIcon;
            if (!string.IsNullOrWhiteSpace(s.Language)) Language = s.Language;
            CustomPrompt = s.CustomPrompt ?? "";
        }
    }

    public static void Apply(string name, string bubbleIcon, string language, string customPrompt)
    {
        if (!string.IsNullOrWhiteSpace(name)) Current = name.Trim();
        if (!string.IsNullOrWhiteSpace(bubbleIcon)) BubbleIcon = bubbleIcon;
        if (!string.IsNullOrWhiteSpace(language)) Language = language;
        CustomPrompt = customPrompt ?? "";
        UiPrefs.Save("settings.json", new AssistantSettings(Current, BubbleIcon, Language, CustomPrompt));
    }
}
