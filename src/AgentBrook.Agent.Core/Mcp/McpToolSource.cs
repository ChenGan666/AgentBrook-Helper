using System.Text.Json;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AgentBrook.Agent.Mcp;

/// <summary>一个已连接的 MCP 服务器（stdio）及其工具面。</summary>
public sealed class McpConnection : IAsyncDisposable
{
    public required string ServerName { get; init; }
    public required McpClient Client { get; init; }
    public required IReadOnlyList<AIFunction> Tools { get; init; }

    public IEnumerable<string> ToolNames => Tools.Select(t => t.Name);

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();
}

/// <summary>按配置连接多个 MCP 服务器；单个服务器失败不影响其余启动。</summary>
public static class McpToolSource
{
    public static async Task<List<McpConnection>> ConnectAllAsync(
        McpOptions options, Workspace workspace, Action<string> log)
    {
        var connections = new List<McpConnection>();

        foreach (var server in options.Servers.Where(s => s.Enabled))
        {
            try
            {
                log($"⟳ 连接 MCP 服务器 {server.Name}（{server.Command}）…");

                // 注意：transport 由 McpClient 管理生命周期，这里不能提前释放。
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = [.. server.Args.Select(workspace.Expand)],
                    WorkingDirectory = workspace.Expand(server.WorkingDirectory ?? workspace.Root),
                    EnvironmentVariables = server.Env.Count > 0
                        ? server.Env.ToDictionary(kv => kv.Key, kv => (string?)workspace.Expand(kv.Value))
                        : null,
                });

                var client = await McpClient.CreateAsync(transport);
                var tools = (await client.ListToolsAsync()).ToList();

                connections.Add(new McpConnection
                {
                    ServerName = server.Name,
                    Client = client,
                    Tools = tools.Select(t => (AIFunction)new PrefixedAIFunction(t, Prefix(server.Name))).ToList(),
                });

                log($"✔ MCP {server.Name} 已连接，{tools.Count} 个工具：{string.Join("、", tools.Select(t => t.Name))}");
            }
            catch (Exception ex)
            {
                log($"✖ MCP 服务器 {server.Name} 连接失败（已跳过）：{ex.Message}");
            }
        }

        return connections;
    }

    /// <summary>汇总所有 MCP 工具（已加服务器名前缀，可直接并入智能体工具面）。</summary>
    public static IEnumerable<AIFunction> SelectTools(IEnumerable<McpConnection> connections) =>
        connections.SelectMany(c => c.Tools);

    private static string Prefix(string serverName)
    {
        var safe = new string(serverName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return $"mcp_{safe}_";
    }

    /// <summary>给 MCP 工具名加前缀的包装器，避免与内置工具或其他服务器重名。</summary>
    private sealed class PrefixedAIFunction(AIFunction inner, string prefix) : AIFunction
    {
        public override string Name => prefix + inner.Name;
        public override string Description => inner.Description;
        public override JsonElement JsonSchema => inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            inner.InvokeAsync(arguments, cancellationToken);
    }
}
