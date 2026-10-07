using System.Globalization;

namespace AgentBrook.Agent.Team;

/// <summary>
/// 工作 Agent 的授予权能：shell 命令执行与/或白名单内的 MCP 服务器。
/// 由主 Agent 在 spawn_worker 的 capabilities 参数中请求，经用户批准（或完全访问模式）后生效。
/// </summary>
public sealed record WorkerCaps(bool Shell, List<string> McpServers)
{
    public bool Any => Shell || McpServers.Count > 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (Shell)
        {
            parts.Add("shell");
        }
        if (McpServers.Count > 0)
        {
            parts.Add($"MCP[{string.Join(",", McpServers)}]");
        }
        return string.Join("、", parts);
    }

    /// <summary>
    /// 解析 capabilities 参数并对照天花板校验。
    /// 支持："shell"、"mcp"（全部已配置服务器）、"mcp:服务器名"（可多个）。
    /// 返回 caps 为 null 且 error 为 null 表示未请求任何能力；error 非 null 表示请求非法。
    /// </summary>
    public static (WorkerCaps? Caps, string? Error) Parse(
        IEnumerable<string>? specs, string ceiling, IReadOnlyList<string> configuredServers)
    {
        if (specs is null || specs.All(string.IsNullOrWhiteSpace))
        {
            return (null, null);
        }

        var c = (ceiling ?? "all").Trim().ToLowerInvariant();
        var allowShell = c is "all" or "shell";
        var allowMcp = c is "all" or "mcp";

        var wantShell = false;
        var mcp = new List<string>();
        foreach (var raw in specs)
        {
            var s = raw.Trim();
            if (s.Length == 0)
            {
                continue;
            }
            if (s.Equals("shell", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowShell)
                {
                    return (null, $"能力天花板为「{ceiling}」，不允许向 worker 授予 shell");
                }
                wantShell = true;
            }
            else if (s.StartsWith("mcp", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowMcp)
                {
                    return (null, $"能力天花板为「{ceiling}」，不允许向 worker 授予 MCP");
                }
                if (s.Equals("mcp", StringComparison.OrdinalIgnoreCase))
                {
                    if (configuredServers.Count == 0)
                    {
                        return (null, "当前未配置任何 MCP 服务器，无法授予 mcp 能力");
                    }
                    foreach (var sv in configuredServers)
                    {
                        if (!mcp.Contains(sv, StringComparer.OrdinalIgnoreCase))
                        {
                            mcp.Add(sv);
                        }
                    }
                }
                else if (s.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var name in s["mcp:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        var server = configuredServers.FirstOrDefault(sv =>
                            sv.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (server is null)
                        {
                            return (null, $"未配置名为「{name}」的 MCP 服务器（可用：{string.Join("、", configuredServers)}）");
                        }
                        if (!mcp.Contains(server, StringComparer.OrdinalIgnoreCase))
                        {
                            mcp.Add(server);
                        }
                    }
                }
                else
                {
                    return (null, $"无法识别的能力「{s}」（支持 shell / mcp / mcp:服务器名）");
                }
            }
            else
            {
                return (null, $"无法识别的能力「{s}」（支持 shell / mcp / mcp:服务器名）");
            }
        }

        var caps = new WorkerCaps(wantShell, mcp);
        return caps.Any ? (caps, null) : (null, null);
    }
}
