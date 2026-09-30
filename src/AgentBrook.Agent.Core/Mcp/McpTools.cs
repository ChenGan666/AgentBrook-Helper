using System.ComponentModel;
using AgentBrook.Agent.Configuration;

namespace AgentBrook.Agent.Mcp;

/// <summary>
/// MCP 热插拔工具：把新的 MCP 服务器写入用户级配置（workspace/mcp-user.json），
/// 重启应用后自动连接生效。
/// </summary>
public sealed class McpTools(string workspaceRoot, IReadOnlyList<string> configuredServers)
{
    private readonly string _workspaceRoot = workspaceRoot;
    private readonly IReadOnlyList<string> _configuredServers = configuredServers;

    [Description("添加一个 MCP 服务器到用户级配置（重启应用后自动连接生效）。添加属于敏感操作，通常需要用户批准。命令须为本机可直接执行的程序。")]
    public string mcp_add_server(
        [Description("服务器名称（唯一，英文/数字/连字符）")] string name,
        [Description("启动命令，如 npx、uvx、node")] string command,
        [Description("命令参数，用 | 分隔，如：-y|@modelcontextprotocol/server-filesystem|/path")] string args,
        [Description("可选环境变量，格式 K=V，多个用 | 分隔")] string? env = null)
    {
        var cleanName = name.Trim();
        if (cleanName.Length < 2 || command.Trim().Length == 0)
        {
            return "添加失败：name 与 command 不能为空";
        }
        if (_configuredServers.Any(s => string.Equals(s, cleanName, StringComparison.OrdinalIgnoreCase)))
        {
            return $"MCP 服务器 {cleanName} 已在配置中";
        }

        var cfg = new McpServerConfig
        {
            Name = cleanName,
            Enabled = true,
            Command = command.Trim(),
            Args = args.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
        };
        if (env is not null)
        {
            foreach (var kv in env.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = kv.IndexOf('=');
                if (eq > 0)
                {
                    cfg.Env[kv[..eq].Trim()] = kv[(eq + 1)..].Trim();
                }
            }
        }

        var error = UserMcpConfigStore.AddServer(_workspaceRoot, cfg);
        if (error is not null)
        {
            return error;
        }
        return $"✔ MCP 服务器「{cleanName}」已写入用户级配置（workspace/mcp-user.json）。重启应用后将自动连接并加载其工具。";
    }

    [Description("列出已配置的 MCP 服务器（含用户级配置）。")]
    public string mcp_list_servers()
    {
        if (_configuredServers.Count == 0)
        {
            return "（未配置任何 MCP 服务器）";
        }
        return "已配置 MCP 服务器：\n" + string.Join("\n", _configuredServers.Select(s => $"- {s}"));
    }
}
