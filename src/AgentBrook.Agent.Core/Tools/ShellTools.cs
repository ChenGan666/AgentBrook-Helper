using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Agent.Tools;

/// <summary>
/// 命令行执行工具：在工作区目录内运行 shell 命令，带超时与输出截断。
/// 注意：该工具具备真实系统操作能力，指令中已要求模型对破坏性操作保持谨慎。
/// </summary>
public sealed class ShellTools(Workspace workspace, int defaultTimeoutSeconds, int maxOutputChars)
{
    // 极高危模式直接拒绝执行（无法穷举，仅作最后防线；主要安全边界仍是运行环境本身）。
    private static readonly string[] DenyPatterns =
    [
        "rm -rf /", "rm -rf ~", "rm -rf /*",
        "mkfs", "diskutil eraseDisk", "> /dev/sda",
        "shutdown", "reboot",
    ];

    [Description("在工作区目录内执行一条 shell 命令，返回退出码与标准输出/错误。适合 git、构建、查询系统信息等操作。破坏性操作请先向用户确认。")]
    public async Task<string> run_command(
        [Description("要执行的命令，如 git status 或 dotnet build")] string command,
        [Description("超时秒数，0 表示使用默认超时")] int timeout_seconds = 0)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return "（空命令）";
        }
        if (DenyPatterns.Any(p => trimmed.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return $"已拒绝执行高危命令：{trimmed}";
        }

        var timeout = timeout_seconds > 0 ? TimeSpan.FromSeconds(timeout_seconds) : TimeSpan.FromSeconds(defaultTimeoutSeconds);
        using var cts = new CancellationTokenSource(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/zsh",
            WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // 用 ArgumentList 把整条命令作为单个参数交给 zsh -c，避免 .NET 参数解析拆坏引号。
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(trimmed);

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            return $"命令超时（>{timeout.TotalSeconds:N0}s），已终止：{trimmed}";
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var sb = new StringBuilder();
        sb.AppendLine($"exit_code: {process.ExitCode}");
        sb.AppendLine("--- stdout ---");
        sb.AppendLine(Truncate(stdout));
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(Truncate(stderr));
        }
        return sb.ToString().TrimEnd();
    }

    private string Truncate(string output)
    {
        if (output.Length <= maxOutputChars)
        {
            return output.TrimEnd();
        }
        return output[..maxOutputChars] + $"\n…（输出过长已截断：全文 {output.Length:N0} 字符）";
    }
}
