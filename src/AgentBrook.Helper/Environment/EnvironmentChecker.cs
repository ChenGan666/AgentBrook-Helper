using System.Diagnostics;
using AgentBrook.Agent;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.Mcp;

namespace AgentBrook.Helper.EnvironmentCheck;

/// <summary>
/// 系统环境检测：文件读写、命令执行、工作空间授权等。
/// 检测项可修复时，调用 RepairAsync 尝试一键修复。
/// </summary>
public static class EnvironmentChecker
{
    public static async Task<List<EnvironmentCheckResult>> CheckAllAsync(BrookAgent? agent = null)
    {
        var results = new List<EnvironmentCheckResult>
        {
            await CheckAppDataWriteAsync(),
            await CheckWorkspaceWriteAsync(),
            await CheckWorkspaceAuthorizationAsync(),
            await CheckCommandExecutionAsync(),
            await CheckNetworkAsync(),
        };
        if (agent is not null)
        {
            results.AddRange(await CheckMcpAsync(agent));
        }
        return results;
    }

    /// <summary>修复指定检测项，返回修复后的结果。</summary>
    public static async Task<EnvironmentCheckResult> RepairAsync(EnvironmentCheckResult item)
    {
        return item.Id switch
        {
            "AppDataWrite" => await RepairAppDataWriteAsync(item),
            "WorkspaceWrite" => await RepairWorkspaceAsync(item),
            "WorkspaceAuthorization" => await RepairWorkspaceAsync(item),
            _ => item.Clone(),
        };
    }

    // ─────────────── 检测实现 ───────────────

    private static async Task<EnvironmentCheckResult> CheckAppDataWriteAsync()
    {
        var result = Base("AppDataWrite", I18n.T("ui.app.data.directory.writable"));
        try
        {
            var dir = Workspace.LocateAppRoot();
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            await File.WriteAllTextAsync(probe, "ok");
            var read = await File.ReadAllTextAsync(probe);
            File.Delete(probe);
            if (read == "ok")
            {
                result.Status = CheckStatus.Ok;
                result.Message = I18n.T("ui.directory.readable.and.writable.value", dir);
            }
            else
            {
                result.Status = CheckStatus.Error;
                result.Message = I18n.T("ui.probe.read.does.not.match.write");
                result.Solution = I18n.T("ui.try.creating.fixing.app.data.directory.permissions");
                result.CanRepair = true;
            }
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("ui.cannot.read.or.write.value", ex.Message);
            result.Solution = I18n.T("ui.try.creating.fixing.app.data.directory.permissions");
            result.CanRepair = true;
        }
        return result;
    }

    private static async Task<EnvironmentCheckResult> CheckWorkspaceWriteAsync()
    {
        var result = Base("WorkspaceWrite", I18n.T("ui.workspace.directory.writable"));
        try
        {
            var dir = UiPrefs.WorkspaceDir();
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok");
            var read = await File.ReadAllTextAsync(probe);
            File.Delete(probe);
            if (read == "ok")
            {
                result.Status = CheckStatus.Ok;
                result.Message = I18n.T("ui.value.3", dir);
            }
            else
            {
                result.Status = CheckStatus.Error;
                result.Message = I18n.T("ui.workspace.probe.read.does.not.match.write");
                result.Solution = I18n.T("ui.choose.a.writable.workspace.directory.in.settings");
                result.CanRepair = true;
            }
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("ui.workspace.cannot.read.or.write.value", ex.Message);
            result.Solution = I18n.T("ui.choose.a.writable.workspace.directory.in.settings");
            result.CanRepair = true;
        }
        return result;
    }

    private static Task<EnvironmentCheckResult> CheckWorkspaceAuthorizationAsync()
    {
        var result = Base("WorkspaceAuthorization", I18n.T("ui.workspace.authorized"));
        try
        {
            var dir = UiPrefs.WorkspaceDir();
            if (!Directory.Exists(dir))
            {
                result.Status = CheckStatus.Warning;
                result.Message = I18n.T("ui.workspace.directory.does.not.exist.it.will.be.created.automatically");
                result.Solution = I18n.T("ui.message.3");
                result.CanRepair = true;
                return Task.FromResult(result);
            }

            // 尝试列出目录内容：macOS/Windows 沙盒化应用需要显式授权
            var _ = Directory.GetFiles(dir).Length;
            result.Status = CheckStatus.Ok;
            result.Message = I18n.T("ui.workspace.accessible.value", dir);
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("ui.no.permission.to.access.workspace.value", ex.Message);
            result.Solution = I18n.T("ui.choose.a.workspace.directory.with.access.permission.in.settings");
            result.CanRepair = true;
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Warning;
            result.Message = I18n.T("ui.workspace.access.restricted.value", ex.Message);
            result.Solution = I18n.T("ui.choose.another.workspace.directory.in.settings");
            result.CanRepair = true;
        }
        return Task.FromResult(result);
    }

    private static async Task<EnvironmentCheckResult> CheckCommandExecutionAsync()
    {
        var result = Base("CommandExecution", I18n.T("ui.external.commands.executable"));
        string program = "";
        var args = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                program = "cmd.exe";
                args.AddRange(new[] { "/c", "echo AgentBrook-Probe" });
            }
            else
            {
                program = "/bin/sh";
                args.AddRange(new[] { "-c", "echo AgentBrook-Probe" });
            }

            var psi = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                result.Status = CheckStatus.Error;
                result.Message = I18n.T("ui.message.6");
                result.Solution = I18n.T("ui.check.system.path.and.process.permissions");
                result.Extra = $"{program} {string.Join(' ', args)}";
                return result;
            }
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0 && stdout.Trim() == "AgentBrook-Probe")
            {
                result.Status = CheckStatus.Ok;
                result.Message = I18n.T("ui.external.commands.can.be.executed");
            }
            else
            {
                result.Status = CheckStatus.Error;
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                result.Message = I18n.T("ui.command.returned.an.exception.value", string.IsNullOrWhiteSpace(detail) ? I18n.T("ui.no.output") : detail);
                result.Solution = I18n.T("ui.check.system.path.and.process.permissions");
                result.Extra = $"{program} {string.Join(' ', args)}";
            }
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("ui.command.execution.failed.value", ex.Message);
            result.Solution = I18n.T("ui.check.system.path.and.process.permissions");
            result.Extra = $"{program} {string.Join(' ', args)}";
        }
        return result;
    }

    private static async Task<EnvironmentCheckResult> CheckNetworkAsync()
    {
        var result = Base("Network", I18n.T("ui.network.connection"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new HttpClient();
            // 使用 HEAD 请求，减少流量
            var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, "https://www.baidu.com"),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            result.Status = response.IsSuccessStatusCode || (int)response.StatusCode < 500
                ? CheckStatus.Ok
                : CheckStatus.Warning;
            result.Message = I18n.T("ui.network.reachable.http.value", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Warning;
            result.Message = I18n.T("ui.network.restricted.value", ex.Message);
            result.Solution = I18n.T("ui.check.network.or.proxy.settings");
        }
        return result;
    }

    private static Task<List<EnvironmentCheckResult>> CheckMcpAsync(BrookAgent agent)
    {
        var results = new List<EnvironmentCheckResult>();
        var config = agent.McpOptions;
        var connected = agent.McpConnections;

        if (config.Servers.Count == 0)
        {
            results.Add(new()
            {
                Id = "Mcp-None",
                Title = I18n.T("ui.mcp.servers"),
                Status = CheckStatus.Ok,
                Message = I18n.T("ui.no.mcp.servers.configured"),
                CanRepair = false,
            });
            return Task.FromResult(results);
        }

        foreach (var server in config.Servers.Where(s => s.Enabled))
        {
            var id = $"Mcp-{server.Name}";
            var conn = connected.FirstOrDefault(c => c.ServerName == server.Name);
            if (conn is not null)
            {
                results.Add(new()
                {
                    Id = id,
                    Title = $"MCP: {server.Name}",
                    Status = CheckStatus.Ok,
                    Message = I18n.T("ui.connected.value.tool.s", conn.Tools.Count),
                    CanRepair = false,
                });
                continue;
            }

            // 未连接：检查命令是否可解析
            var resolved = CommandPathResolver.Resolve(server.Command);
            if (resolved is null)
            {
                results.Add(new()
                {
                    Id = id,
                    Title = $"MCP: {server.Name}",
                    Status = CheckStatus.Error,
                    Message = I18n.T("ui.start.command.not.found.value", server.Command),
                    Solution = I18n.T("ui.install.the.required.runtime.e.g.node.js.add.it.to.path.then.restart.the.app"),
                    Extra = server.Command,
                    CanRepair = false,
                });
            }
            else
            {
                results.Add(new()
                {
                    Id = id,
                    Title = $"MCP: {server.Name}",
                    Status = CheckStatus.Error,
                    Message = I18n.T("ui.command.exists.but.connection.failed.value", resolved),
                    Solution = I18n.T("ui.check.mcp.config.dependencies.working.directory.then.restart.the.app"),
                    Extra = server.Command,
                    CanRepair = false,
                });
            }
        }

        return Task.FromResult(results);
    }

    // ─────────────── 修复实现 ───────────────

    private static async Task<EnvironmentCheckResult> RepairAppDataWriteAsync(EnvironmentCheckResult original)
    {
        try
        {
            var dir = Workspace.LocateAppRoot();
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            await File.WriteAllTextAsync(probe, "ok");
            var read = await File.ReadAllTextAsync(probe);
            File.Delete(probe);
            return new()
            {
                Id = original.Id,
                Title = original.Title,
                Status = read == "ok" ? CheckStatus.Ok : CheckStatus.Error,
                Message = read == "ok" ? I18n.T("ui.repaired.directory.readable.and.writable.value", dir) : I18n.T("ui.still.cannot.read.or.write.after.repair"),
                Solution = read == "ok" ? "" : I18n.T("ui.please.check.directory.permissions.or.change.install.location"),
                CanRepair = read != "ok",
            };
        }
        catch (Exception ex)
        {
            return new()
            {
                Id = original.Id,
                Title = original.Title,
                Status = CheckStatus.Error,
                Message = I18n.T("ui.value.5", ex.Message),
                Solution = I18n.T("ui.please.check.directory.permissions.or.change.install.location"),
                CanRepair = false,
            };
        }
    }

    private static async Task<EnvironmentCheckResult> RepairWorkspaceAsync(EnvironmentCheckResult original)
    {
        try
        {
            var dir = UiPrefs.WorkspaceDir();
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok");
            var read = await File.ReadAllTextAsync(probe);
            File.Delete(probe);
            return new()
            {
                Id = original.Id,
                Title = original.Title,
                Status = read == "ok" ? CheckStatus.Ok : CheckStatus.Error,
                Message = read == "ok" ? I18n.T("ui.repaired.workspace.readable.and.writable.value", dir) : I18n.T("ui.still.cannot.read.or.write.after.repair"),
                Solution = read == "ok" ? "" : I18n.T("ui.please.choose.a.writable.directory"),
                CanRepair = read != "ok",
            };
        }
        catch (Exception ex)
        {
            return new()
            {
                Id = original.Id,
                Title = original.Title,
                Status = CheckStatus.Error,
                Message = I18n.T("ui.value.5", ex.Message),
                Solution = I18n.T("ui.please.choose.a.writable.directory"),
                CanRepair = false,
            };
        }
    }

    private static EnvironmentCheckResult Base(string id, string title)
        => new() { Id = id, Title = title, Status = CheckStatus.Unknown };
}

