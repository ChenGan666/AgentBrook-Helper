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
        var result = Base("AppDataWrite", I18n.T("应用数据目录可写"));
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
                result.Message = I18n.T("目录可读写：{0}", dir);
            }
            else
            {
                result.Status = CheckStatus.Error;
                result.Message = I18n.T("读取探针内容与写入不一致");
                result.Solution = I18n.T("尝试创建/修复应用数据目录权限");
                result.CanRepair = true;
            }
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("无法读写：{0}", ex.Message);
            result.Solution = I18n.T("尝试创建/修复应用数据目录权限");
            result.CanRepair = true;
        }
        return result;
    }

    private static async Task<EnvironmentCheckResult> CheckWorkspaceWriteAsync()
    {
        var result = Base("WorkspaceWrite", I18n.T("工作空间目录可写"));
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
                result.Message = I18n.T("工作空间可读写：{0}", dir);
            }
            else
            {
                result.Status = CheckStatus.Error;
                result.Message = I18n.T("工作空间读取探针内容与写入不一致");
                result.Solution = I18n.T("在设置中更换可写的工作空间目录");
                result.CanRepair = true;
            }
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("工作空间无法读写：{0}", ex.Message);
            result.Solution = I18n.T("在设置中更换可写的工作空间目录");
            result.CanRepair = true;
        }
        return result;
    }

    private static Task<EnvironmentCheckResult> CheckWorkspaceAuthorizationAsync()
    {
        var result = Base("WorkspaceAuthorization", I18n.T("工作空间已授权"));
        try
        {
            var dir = UiPrefs.WorkspaceDir();
            if (!Directory.Exists(dir))
            {
                result.Status = CheckStatus.Warning;
                result.Message = I18n.T("工作空间目录不存在，将自动创建");
                result.Solution = I18n.T("尝试创建工作空间目录");
                result.CanRepair = true;
                return Task.FromResult(result);
            }

            // 尝试列出目录内容：macOS/Windows 沙盒化应用需要显式授权
            var _ = Directory.GetFiles(dir).Length;
            result.Status = CheckStatus.Ok;
            result.Message = I18n.T("工作空间已可访问：{0}", dir);
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("没有访问工作空间的权限：{0}", ex.Message);
            result.Solution = I18n.T("在设置中更换有访问权限的工作空间目录");
            result.CanRepair = true;
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Warning;
            result.Message = I18n.T("访问工作空间受限：{0}", ex.Message);
            result.Solution = I18n.T("在设置中更换工作空间目录");
            result.CanRepair = true;
        }
        return Task.FromResult(result);
    }

    private static async Task<EnvironmentCheckResult> CheckCommandExecutionAsync()
    {
        var result = Base("CommandExecution", I18n.T("外部命令可执行"));
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
                result.Message = I18n.T("无法启动子进程");
                result.Solution = I18n.T("检查系统 PATH 与进程权限");
                result.Extra = $"{program} {string.Join(' ', args)}";
                return result;
            }
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0 && stdout.Trim() == "AgentBrook-Probe")
            {
                result.Status = CheckStatus.Ok;
                result.Message = I18n.T("可以执行外部命令");
            }
            else
            {
                result.Status = CheckStatus.Error;
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                result.Message = I18n.T("命令返回异常：{0}", string.IsNullOrWhiteSpace(detail) ? I18n.T("无输出") : detail);
                result.Solution = I18n.T("检查系统 PATH 与进程权限");
                result.Extra = $"{program} {string.Join(' ', args)}";
            }
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Error;
            result.Message = I18n.T("命令执行失败：{0}", ex.Message);
            result.Solution = I18n.T("检查系统 PATH 与进程权限");
            result.Extra = $"{program} {string.Join(' ', args)}";
        }
        return result;
    }

    private static async Task<EnvironmentCheckResult> CheckNetworkAsync()
    {
        var result = Base("Network", I18n.T("网络连接"));
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
            result.Message = I18n.T("网络可连接（HTTP {0}）", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Warning;
            result.Message = I18n.T("网络连接受限：{0}", ex.Message);
            result.Solution = I18n.T("检查网络或代理设置");
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
                Title = I18n.T("MCP 服务器"),
                Status = CheckStatus.Ok,
                Message = I18n.T("未配置 MCP 服务器"),
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
                    Message = I18n.T("已连接，{0} 个工具", conn.Tools.Count),
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
                    Message = I18n.T("未找到启动命令：{0}", server.Command),
                    Solution = I18n.T("安装对应运行时（如 Node.js）并加入 PATH，然后重启应用"),
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
                    Message = I18n.T("命令存在但连接失败：{0}", resolved),
                    Solution = I18n.T("检查 MCP 配置、依赖包及工作目录，然后重启应用"),
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
                Message = read == "ok" ? I18n.T("已修复，目录可读写：{0}", dir) : I18n.T("修复后仍无法读写"),
                Solution = read == "ok" ? "" : I18n.T("请手动检查目录权限或更换安装位置"),
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
                Message = I18n.T("修复失败：{0}", ex.Message),
                Solution = I18n.T("请手动检查目录权限或更换安装位置"),
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
                Message = read == "ok" ? I18n.T("已修复，工作空间可读写：{0}", dir) : I18n.T("修复后仍无法读写"),
                Solution = read == "ok" ? "" : I18n.T("请手动选择可写目录"),
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
                Message = I18n.T("修复失败：{0}", ex.Message),
                Solution = I18n.T("请手动选择可写目录"),
                CanRepair = false,
            };
        }
    }

    private static EnvironmentCheckResult Base(string id, string title)
        => new() { Id = id, Title = title, Status = CheckStatus.Unknown };
}
