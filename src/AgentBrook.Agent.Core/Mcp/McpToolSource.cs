using System.Text.Json;
using System.Text.RegularExpressions;
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

        // 声明在 foreach 作用域：catch 的失败日志需要引用（try 内声明的变量 catch 不可见）
        foreach (var server in options.Servers.Where(s => s.Enabled))
        {
            // 命令解析与传输参数声明在 try 之外：catch 的失败日志需要引用（含空格路径会转短路径）
            var resolvedCommand = Infrastructure.CommandPathResolver.Resolve(server.Command);
            var transportCommand = resolvedCommand ?? server.Command;
            var transportArgs = (IReadOnlyList<string>) [.. server.Args.Select(workspace.Expand)];

            try
            {
                if (resolvedCommand is null)
                {
                    log(Infrastructure.CoreStrings.L(
                        $"✖ MCP 服务器 {server.Name} 连接失败（已跳过）：未找到命令「{server.Command}」。请安装对应运行时（如 Node.js：https://nodejs.org）或将安装目录加入 PATH，然后重启应用。",
                        $"✖ MCP server {server.Name} connection failed (skipped): command \"{server.Command}\" not found. Install the runtime (e.g. Node.js from https://nodejs.org) or add its dir to PATH, then restart."));
                    continue;
                }

                // Windows：命令与参数里的路径含空格时（C:\Program Files\…），
                // SDK 拼接命令行可能不加引号而在空格处断开 —— 统一转 8.3 短路径规避
                if (OperatingSystem.IsWindows())
                {
                    transportCommand = Infrastructure.CommandPathResolver.GetShortPath(transportCommand);
                    transportArgs = [.. transportArgs.Select(Infrastructure.CommandPathResolver.GetShortPath)];
                }

                // Windows：npx/npm 只有 .cmd 垫片（Process 无法直接启动）。
                // 首选：解析 npx.cmd 垫片文本拿到真实 node.exe 与 npx-cli.js（node 直接运行，无 cmd 引号问题）；
                // 次选同目录布局猜测；末选 cmd.exe /s /c 包装兜底。
                if (OperatingSystem.IsWindows())
                {
                    var fileName = Path.GetFileName(transportCommand);
                    var isNpxShim = fileName.Equals("npx.cmd", StringComparison.OrdinalIgnoreCase) ||
                                    fileName.Equals("npx.exe", StringComparison.OrdinalIgnoreCase);
                    if (isNpxShim)
                    {
                        var (shimNode, shimJs) = ParseNpxCmdShim(resolvedCommand);
                        if (shimNode is not null && shimJs is not null)
                        {
                            shimNode = Infrastructure.CommandPathResolver.GetShortPath(shimNode);
                            shimJs = Infrastructure.CommandPathResolver.GetShortPath(shimJs);
                            transportCommand = shimNode;
                            var withJs = new List<string> { shimJs };
                            withJs.AddRange(transportArgs);
                            transportArgs = withJs;
                        }
                        else
                        {
                            var cmdDir = Path.GetDirectoryName(resolvedCommand)!;
                            var nodeExe = Path.Combine(cmdDir, "node.exe");
                            var npxJs = Path.Combine(cmdDir, "node_modules", "npm", "bin", "npx-cli.js");
                            if (!File.Exists(nodeExe))
                            {
                                var viaPath = Infrastructure.CommandPathResolver.Resolve("node");
                                if (viaPath is not null)
                                {
                                    nodeExe = viaPath;
                                }
                            }
                            if (File.Exists(nodeExe) && File.Exists(npxJs))
                            {
                                resolvedCommand = nodeExe;
                                var withJs = new List<string> { npxJs };
                                withJs.AddRange(transportArgs);
                                transportArgs = withJs;
                            }
                            else if (fileName.Equals("npx.cmd", StringComparison.OrdinalIgnoreCase))
                            {
                                var comspec = Environment.ExpandEnvironmentVariables("%ComSpec%");
                                var inner = string.Join(" ", transportArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                                transportCommand = comspec;
                                transportArgs = ["/d", "/s", "/c", $"\"{resolvedCommand}\" {inner}"];
                            }
                        }
                        // npx.exe：直接可执行，无需处理
                    }
                }

                log(Infrastructure.CoreStrings.L(
                    $"⟳ 连接 MCP 服务器 {server.Name}（{transportCommand}）…",
                    $"⟳ Connecting MCP server {server.Name} ({transportCommand})…"));

                // 注意：transport 由 McpClient 管理生命周期，这里不能提前释放。
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = transportCommand,
                    Arguments = [.. transportArgs],
                    WorkingDirectory = workspace.Expand(server.WorkingDirectory ?? workspace.Root),
                    EnvironmentVariables = BuildChildEnvironment(server, workspace),
                });

                var client = await McpClient.CreateAsync(transport);
                var tools = (await client.ListToolsAsync()).ToList();

                connections.Add(new McpConnection
                {
                    ServerName = server.Name,
                    Client = client,
                    Tools = tools.Select(t => (AIFunction)new PrefixedAIFunction(t, Prefix(server.Name))).ToList(),
                });

                log(Infrastructure.CoreStrings.L(
                    $"✔ MCP {server.Name} 已连接，{tools.Count} 个工具：{string.Join("、", tools.Select(t => t.Name))}",
                    $"✔ MCP {server.Name} connected, {tools.Count} tools: {string.Join(", ", tools.Select(t => t.Name))}"));
            }
            catch (Exception ex)
            {
                // 失败必须带上解析后的完整启动命令：用户反馈的截图直接包含定位信息
                var cmdLine = transportCommand + " " + string.Join(" ", transportArgs);
                log(Infrastructure.CoreStrings.L(
                    $"✖ MCP 服务器 {server.Name} 连接失败（已跳过）：{ex.Message}\n启动命令：{cmdLine}\n工作目录：{workspace.Root}",
                    $"✖ MCP server {server.Name} connection failed (skipped): {ex.Message}\nCommand: {cmdLine}\nWorking dir: {workspace.Root}"));
            }
        }

        return connections;
    }

    /// <summary>汇总所有 MCP 工具（已加服务器名前缀，可直接并入智能体工具面）。</summary>
    public static IEnumerable<AIFunction> SelectTools(IEnumerable<McpConnection> connections) =>
        connections.SelectMany(c => c.Tools);

    /// <summary>
    /// 构建子进程环境：服务器配置的环境变量（{WORKSPACE} 展开）+ PATH 增强。
    /// PATH 增强必须项：npx 是 "env node" 脚本，子进程 PATH 里没有 node 目录就无法运行。
    /// </summary>
    private static Dictionary<string, string?> BuildChildEnvironment(McpServerConfig server, Workspace workspace)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in server.Env)
        {
            env[kv.Key] = workspace.Expand(kv.Value);
        }
        if (!env.ContainsKey("PATH"))
        {
            env["PATH"] = Infrastructure.CommandPathResolver.AugmentedPath();
        }
        return env;
    }

    private static string Prefix(string serverName)
    {
        var safe = new string(serverName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return $"mcp_{safe}_";
    }

    /// <summary>
    /// 解析 npx.cmd 垫片文本，提取真实的 node.exe 与 npx-cli.js 路径。
    /// 兼容两种形态：老式 SET 行（NODE_EXE / NPX_CLI_JS），
    /// 以及现代版（JS 路径内联在调用行、node 用 IF EXIST 探测）。%~dp0 展开为垫片所在目录。
    /// 找不到时返回 (null, null)，调用方走同目录猜测 / cmd 包装兜底。
    /// </summary>
    private static (string? NodeExe, string? NpxJs) ParseNpxCmdShim(string cmdFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(cmdFile)!;
            var text = File.ReadAllText(cmdFile);
            string Expand(string v) => Environment.ExpandEnvironmentVariables(
                v.Replace("%~dp0", dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var hasFile = (string? p) => p is not null && File.Exists(p);

            // node.exe：现代垫片的 IF EXIST 行 → 老式 SET 行 → 同目录猜测 → PATH/常见目录
            string? nodeExe = null;
            var nodeIf = Regex.Match(text, "IF EXIST \"([^\"]*node\\.exe)\"", RegexOptions.IgnoreCase);
            if (nodeIf.Success)
            {
                var cand = Expand(nodeIf.Groups[1].Value);
                if (File.Exists(cand))
                {
                    nodeExe = cand;
                }
            }
            if (nodeExe is null)
            {
                var setNode = Regex.Match(text, "SET \"NODE_EXE=([^\"]+)\"", RegexOptions.IgnoreCase);
                if (setNode.Success)
                {
                    var cand = Expand(setNode.Groups[1].Value);
                    if (File.Exists(cand))
                    {
                        nodeExe = cand;
                    }
                }
            }
            if (nodeExe is null)
            {
                var beside = Path.Combine(dir, "node.exe");
                if (File.Exists(beside))
                {
                    nodeExe = beside;
                }
            }
            if (nodeExe is null)
            {
                nodeExe = CommandPathResolver.Resolve("node");
            }
            if (nodeExe is null || !File.Exists(nodeExe))
            {
                return (null, null);
            }

            // npx-cli.js：优先 SET 行，其次调用行里的内联引用，最后同目录猜测
            string? npxJs = null;
            var setJs = Regex.Match(text, "SET \"NPX_CLI_JS=([^\"]+)\"", RegexOptions.IgnoreCase);
            if (setJs.Success)
            {
                var cand = Expand(setJs.Groups[1].Value);
                if (File.Exists(cand))
                {
                    npxJs = cand;
                }
            }
            if (npxJs is null)
            {
                var inline = Regex.Match(text, "\"([^\"]*npx-cli\\.js)\"", RegexOptions.IgnoreCase);
                if (inline.Success)
                {
                    var cand = Expand(inline.Groups[1].Value);
                    if (File.Exists(cand))
                    {
                        npxJs = cand;
                    }
                }
            }
            if (npxJs is null)
            {
                var beside = Path.Combine(dir, "node_modules", "npm", "bin", "npx-cli.js");
                if (File.Exists(beside))
                {
                    npxJs = beside;
                }
            }
            if (npxJs is null || !File.Exists(npxJs))
            {
                return (null, null);
            }
            return (nodeExe, npxJs);
        }
        catch
        {
            // 解析失败交给上层回退
        }
        return (null, null);
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
