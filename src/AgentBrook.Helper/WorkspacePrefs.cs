using System.Text.Json;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Helper;

/// <summary>
/// 主工作空间覆盖配置：持久化在应用数据目录（与工作区本身解耦，切换工作区不影响该文件）。
/// 读取方：AssistantController 初始化（BrookAgent 工作区）与 UiPrefs（settings.json 落点）。
/// </summary>
internal static class WorkspacePrefs
{
    private static string OverrideFile() => Path.Combine(Workspace.LocateAppRoot(), "workspace-override.json");

    /// <summary>读取覆盖路径；未设置返回 null。</summary>
    public static string? LoadOverride()
    {
        try
        {
            var f = OverrideFile();
            if (!File.Exists(f))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("path", out var p) &&
                p.GetString() is { Length: > 0 } v)
            {
                return v;
            }
        }
        catch
        {
            // 配置损坏按未设置处理
        }
        return null;
    }

    /// <summary>保存覆盖路径（null/空 = 恢复默认）。返回 null 表示成功，否则为失败原因。</summary>
    public static string? SaveOverride(string? path)
    {
        try
        {
            var f = OverrideFile();
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            if (string.IsNullOrWhiteSpace(path))
            {
                if (File.Exists(f))
                {
                    File.Delete(f);
                }
            }
            else
            {
                File.WriteAllText(f, JsonSerializer.Serialize(new { path },
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            UiPrefs.InvalidateWorkspaceDirCache();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
