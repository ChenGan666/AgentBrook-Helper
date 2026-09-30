using System.Text.Json;
using AgentBrook.Agent.Configuration;

namespace AgentBrook.Agent.Mcp;

/// <summary>
/// 用户级 MCP 配置（workspace/mcp-user.json）：
/// 由模型通过 mcp_add_server 工具写入，应用启动时与 appsettings 的 MCP 配置合并。
/// </summary>
public static class UserMcpConfigStore
{
    public static string DefaultPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "mcp-user.json");

    public static List<McpServerConfig> Load(string workspaceRoot)
    {
        var path = DefaultPath(workspaceRoot);
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<McpServerConfig>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>添加一个 MCP 服务器配置；重名返回错误。返回错误信息或 null。</summary>
    public static string? AddServer(string workspaceRoot, McpServerConfig config)
    {
        var path = DefaultPath(workspaceRoot);
        var list = Load(workspaceRoot);
        if (list.Any(s => string.Equals(s.Name, config.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"MCP 服务器 {config.Name} 已存在";
        }
        list.Add(config);
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        });
        File.WriteAllText(path, json);
        return null;
    }
}
