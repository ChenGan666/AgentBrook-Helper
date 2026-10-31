namespace AgentBrook.Privacy;

public enum PolicyAction
{
    /// <summary>实参还原为真实值（本地工具需要真实值才能执行）。</summary>
    Restore,

    /// <summary>实参保留占位符（出网工具：还原即旁路泄漏）。</summary>
    Keep,
}

/// <summary>
/// 工具实参出站策略（15 号设计 §7.4，v6 两级匹配）：
/// ① 精确名（用户覆写最优先 → 内置精确表）→ ② `mcp_*` 前缀按所属 server 的 transport
/// （stdio → restore / 远程 → keep；`mcp_call` 门现网全 stdio → restore，出现远程 transport 后自动转 keep）
/// → ③ 未知工具（含已卸载 server 遗留名）keep（默认从紧）。
/// transport 前缀与 `McpToolSource.Prefix()` 同规则：`mcp_{非字母数字→_}_`。
/// </summary>
public sealed class EgressPolicy
{
    /// <summary>内置精确还原表：本地文件 / Shell / 记忆 / Todo / 用户交互 / 子智能体 / 本地 MCP 门工具。</summary>
    private static readonly HashSet<string> ExactRestore = new(StringComparer.Ordinal)
    {
        "list_dir", "read_file", "write_file", "append_file",
        "run_command",
        "memory_read", "memory_write", "memory_append",
        "todo_write", "todo_list",
        "ask_user",
        "delegate_task",
        "mcp_add_server", "mcp_list_servers", "mcp_tool_help",
    };

    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly IReadOnlyDictionary<string, string> _mcpTransports;   // serverName → stdio|remote
    private readonly List<(string Prefix, string Transport)> _mcpPrefixes; // 最长前缀优先

    public EgressPolicy(IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyDictionary<string, string>? mcpTransports = null)
    {
        _overrides = overrides ?? new Dictionary<string, string>();
        _mcpTransports = mcpTransports ?? new Dictionary<string, string>();
        _mcpPrefixes = [.. _mcpTransports
            .Select(kv => (Prefix: $"mcp_{Sanitize(kv.Key)}_", Transport: kv.Value))
            .OrderByDescending(p => p.Prefix.Length)];
    }

    public PolicyAction Decide(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return PolicyAction.Keep;
        }
        // ① 精确名：用户覆写最优先（可覆写任何内置策略），再查内置精确表
        if (_overrides.TryGetValue(toolName, out var overrideAction) && TryParse(overrideAction, out var overridden))
        {
            return overridden;
        }
        if (ExactRestore.Contains(toolName))
        {
            return PolicyAction.Restore;
        }
        if (toolName == "media_generate")
        {
            return PolicyAction.Keep;   // 提示词发送至云端
        }
        if (toolName.StartsWith("a2a_", StringComparison.Ordinal))
        {
            return PolicyAction.Keep;   // 对端无 Vault 无法还原
        }
        // ② mcp 两层
        if (toolName == "mcp_call")
        {
            // 门工具：全部 server 均为 stdio（或未配置）→ restore；任一远程 → keep
            return _mcpTransports.Count == 0 || _mcpTransports.Values.All(t => !IsRemote(t))
                ? PolicyAction.Restore
                : PolicyAction.Keep;
        }
        if (toolName.StartsWith("mcp_", StringComparison.Ordinal))
        {
            foreach (var (prefix, transport) in _mcpPrefixes)
            {
                if (toolName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return IsRemote(transport) ? PolicyAction.Keep : PolicyAction.Restore;
                }
            }
            return PolicyAction.Keep;   // 已卸载 server 遗留名：transport 未知 → 从紧
        }
        // ③ 未知工具
        return PolicyAction.Keep;
    }

    private static string Sanitize(string serverName) =>
        new(serverName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    private static bool IsRemote(string transport) => !transport.Equals("stdio", StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string value, out PolicyAction action)
    {
        if (value.Equals("restore", StringComparison.OrdinalIgnoreCase))
        {
            action = PolicyAction.Restore;
            return true;
        }
        if (value.Equals("keep", StringComparison.OrdinalIgnoreCase))
        {
            action = PolicyAction.Keep;
            return true;
        }
        action = PolicyAction.Keep;   // 非法覆写值从紧
        return false;
    }
}
