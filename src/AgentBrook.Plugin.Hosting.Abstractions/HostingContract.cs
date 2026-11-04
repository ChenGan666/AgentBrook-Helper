namespace AgentBrook.Plugin.Hosting.Abstractions;

/// <summary>新宿主扩展契约版本（预览版：只承诺同版本宿主兼容；设计 §15.5）。</summary>
public static class HostingContract
{
    public const string Version = "0.1.0-preview";

    /// <summary>版本兼容判定：主版本.次版本一致即兼容（preview 修订号不承诺）。</summary>
    public static bool IsCompatible(string? hostVersion, string? requiredVersion)
    {
        if (string.IsNullOrWhiteSpace(hostVersion) || string.IsNullOrWhiteSpace(requiredVersion)) return false;
        var hv = ParsePrefix(hostVersion);
        var rv = ParsePrefix(requiredVersion);
        return hv is not null && rv is not null && hv.Equals(rv);
    }

    private static Version? ParsePrefix(string version)
    {
        var dash = version.IndexOf('-');
        var core = dash < 0 ? version : version[..dash];
        var parts = core.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        return new Version(major, minor);
    }
}

/// <summary>常驻生命周期状态机（设计 §15.4）：Loaded → Starting → Active → Stopping → Disabled / Failed。</summary>
public enum PluginRunState
{
    Loaded = 1,
    Starting = 2,
    Active = 3,
    Stopping = 4,
    Disabled = 5,
    Failed = 6,
}
