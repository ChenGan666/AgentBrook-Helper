using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgentBrook.Agent.Infrastructure;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Mcp;

/// <summary>
/// MCP 工具按需调度（渐进披露，参考 Anthropic「Code execution with MCP」）：
/// MCP 工具的 JSON Schema 不再整体常驻系统提示词，改为在主索引中只列「工具名 + 一句话用途」，
/// 由模型按需 mcp_tool_help 查看参数定义、mcp_call 实际调用。
/// 固定 token 成本从「全部 schema（数千 token）」降为「一行一个工具名」。
/// </summary>
public sealed class McpGateTools
{
    private readonly List<McpConnection> _connections;
    private readonly int _maxOutputChars;

    /// <summary>拒绝调用的工具（读取媒体为 base64 会把上下文撑爆，当前模型无视觉能力）。</summary>
    private static readonly HashSet<string> DenyList = new(StringComparer.OrdinalIgnoreCase) { "read_media_file" };

    /// <summary>
    /// allowedServers 非空时为 worker 作用域门面：共享主连接，但只放行白名单服务器的工具
    /// （连接由主 Agent 启动时建立并复用，不重复拉起服务器进程）。
    /// </summary>
    public McpGateTools(List<McpConnection> connections, int maxOutputChars, IReadOnlyList<string>? allowedServers = null)
    {
        _connections = allowedServers is null
            ? connections
            : connections.Where(c => allowedServers.Any(a =>
                    string.Equals(a, c.ServerName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        _maxOutputChars = maxOutputChars;
    }

    /// <summary>已连接的 MCP 工具总数（供状态展示）。</summary>
    public int ToolCount => _connections.Sum(c => c.Tools.Count);

    [Description("调用一个 MCP 工具（工具名用主索引中的完整前缀名，如 mcp_filesystem_read_file）。" +
        "arguments_json 是参数对象 JSON；不确定参数结构时先调 mcp_tool_help 查看该工具的参数定义。")]
    public async Task<string> mcp_call(
        [Description("MCP 工具完整名（带 mcp_服务器_ 前缀）")] string tool,
        [Description("参数对象 JSON，如 {\"path\":\"notes/a.md\"}")] string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var f = FindTool(tool);
        if (f is null)
        {
            return $"未找到 MCP 工具「{tool}」。可用工具见主索引（mcp_服务器_工具名），或用 mcp_tool_help 探查。";
        }
        try
        {
            var args = new AIFunctionArguments();
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    args[p.Name] = p.Value.Clone();
                }
            }
            var result = await f.InvokeAsync(args, cancellationToken);
            var text = result switch
            {
                string s => s,
                null => "（工具执行完成，无返回内容）",
                _ => JsonSerializer.Serialize(result),
            };
            if (text.Length > _maxOutputChars)
            {
                text = text[.._maxOutputChars] + "\n…（输出过长已截断）";
            }
            return text;
        }
        catch (JsonException ex)
        {
            return $"arguments_json 不是合法 JSON：{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"MCP 工具 {tool} 调用失败：{ex.Message}";
        }
    }

    [Description("查看某个 MCP 工具的参数定义（JSON Schema）。调用 mcp_call 前不确定参数结构时使用。")]
    public string mcp_tool_help(
        [Description("MCP 工具完整名（带 mcp_服务器_ 前缀）")] string tool)
    {
        var f = FindTool(tool);
        if (f is null)
        {
            return $"未找到 MCP 工具「{tool}」。";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"工具：{f.Name}");
        sb.AppendLine($"说明：{f.Description}");
        sb.AppendLine("参数定义：");
        var schema = f.JsonSchema.ValueKind is JsonValueKind.Object
            ? f.JsonSchema.GetRawText()
            : "{}";
        if (schema.Length > 3000)
        {
            schema = schema[..3000] + "…（已截断）";
        }
        sb.AppendLine(schema);
        return sb.ToString();
    }

    private AIFunction? FindTool(string tool) =>
        _connections.SelectMany(c => c.Tools)
            .Where(t => !DenyList.Contains(t.Name))
            .FirstOrDefault(t => string.Equals(t.Name, tool.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// 生成注入系统提示词的 MCP 索引块：一个工具一行（完整名 + 一句话用途）。
    /// </summary>
    public string BuildCatalogBlock()
    {
        if (_connections.Count == 0)
        {
            return "## MCP 外部工具\n（当前未接入 MCP 服务器。可用 mcp_add_server 接入。）";
        }
        var sb = new StringBuilder();
        sb.AppendLine("## MCP 外部工具（按需调用）");
        sb.AppendLine("调用方式：mcp_call(工具完整名, arguments_json)；参数结构不确定时先用 mcp_tool_help(工具完整名) 查看。可用工具：");
        foreach (var conn in _connections)
        {
            foreach (var t in conn.Tools)
            {
                sb.Append($"- {t.Name}：{OneLine(t.Description)}");
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>取描述第一句（按 。/. /换行切分），超长截到 60 字符。</summary>
    private static string OneLine(string? description)
    {
        var s = (description ?? "")
            .Split(new[] { '。', '.', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Length > 0) ?? "";
        return s.Length > 60 ? s[..60] + "…" : s;
    }
}
