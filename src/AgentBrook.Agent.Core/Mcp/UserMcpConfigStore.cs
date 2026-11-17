using System.Text.Json;
using AgentBrook.Agent.Configuration;

namespace AgentBrook.Agent.Mcp;

/// <summary>
/// 用户级 MCP 配置（workspace/mcp-user.json）：
/// 由模型通过 mcp_add_server 工具写入、设置页「MCP 管理」启停/卸载写入，
/// 应用启动时与 appsettings 的 MCP 配置合并（同名用户级覆盖应用级；Removed=true 墓碑删除应用级条目）。
/// </summary>
public static class UserMcpConfigStore
{
    private static JsonSerializerOptions ReadOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    // 不用全局 WhenWritingDefault：会把 Enabled=false 一并省略，而 Enabled 默认 true，
    // 重载后停用状态被静默翻转（单测 Merge_RoundTrip 实锤）。Removed=false 的省略走属性上的 JsonIgnore。
    private static JsonSerializerOptions WriteOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

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
            return JsonSerializer.Deserialize<List<McpServerConfig>>(json, ReadOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>添加一个 MCP 服务器配置；重名返回错误（同名卸载墓碑不算占用，直接顶掉）。返回错误信息或 null。</summary>
    public static string? AddServer(string workspaceRoot, McpServerConfig config)
    {
        var list = Load(workspaceRoot);
        var existing = Find(list, config.Name);
        if (existing is not null && !existing.Removed)
        {
            return $"MCP 服务器 {config.Name} 已存在";
        }
        if (existing is not null)
        {
            list.Remove(existing);   // 墓碑让位于真实条目
        }
        list.Add(config);
        return Save(workspaceRoot, list);
    }

    /// <summary>按名替换或追加（设置页启停/卸载写用户级覆盖与墓碑用）。返回错误信息或 null。</summary>
    public static string? UpsertServer(string workspaceRoot, McpServerConfig config)
    {
        var list = Load(workspaceRoot);
        var existing = Find(list, config.Name);
        if (existing is not null)
        {
            list[list.IndexOf(existing)] = config;
        }
        else
        {
            list.Add(config);
        }
        return Save(workspaceRoot, list);
    }

    /// <summary>按名物理删除用户级条目（应用级条目的卸载走墓碑 Upsert，不经此方法）。找到并删除返回 true。</summary>
    public static bool RemoveServer(string workspaceRoot, string name)
    {
        var list = Load(workspaceRoot);
        var existing = Find(list, name);
        if (existing is null)
        {
            return false;
        }
        list.Remove(existing);
        Save(workspaceRoot, list);
        return true;
    }

    private static McpServerConfig? Find(List<McpServerConfig> list, string name) =>
        list.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 启动合并（就地修改 appServers）：墓碑（Removed=true）删除应用级同名条目、自身不入列表；
    /// 其余用户条目同名覆盖应用级（设置页启停写用户级生效）、新名追加（mcp_add_server）。
    /// 用户级获胜——此前同名被静默忽略，启停/卸载写不进去；同名用户条目重复（手改文件）取第一个。
    /// </summary>
    public static void MergeInto(List<McpServerConfig> appServers, List<McpServerConfig> userServers)
    {
        foreach (var tombstone in userServers.Where(u => u.Removed))
        {
            appServers.RemoveAll(c =>
                string.Equals(c.Name, tombstone.Name, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var server in userServers.Where(u => !u.Removed).DistinctBy(u => u.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            var idx = appServers.FindIndex(c =>
                string.Equals(c.Name, server.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                appServers[idx] = server;
            }
            else
            {
                appServers.Add(server);
            }
        }
    }

    private static string? Save(string workspaceRoot, List<McpServerConfig> list)
    {
        try
        {
            File.WriteAllText(DefaultPath(workspaceRoot), JsonSerializer.Serialize(list, WriteOptions));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
