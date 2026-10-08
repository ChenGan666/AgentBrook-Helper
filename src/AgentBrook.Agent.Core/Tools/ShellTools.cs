using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Agent.Tools;

/// <summary>
/// 命令行执行工具：在工作区目录内运行 shell 命令，带超时与输出截断。
/// 跨平台：Windows 使用 cmd.exe，macOS/Linux 依次回退 zsh → bash → sh。
/// 可选 audit 回调：每条命令执行后回传摘要（团队 worker 场景写入项目审计日志）。
/// 注意：该工具具备真实系统操作能力，指令中已要求模型对破坏性操作保持谨慎。
/// </summary>
public sealed class ShellTools(Workspace workspace, int defaultTimeoutSeconds, int maxOutputChars, Action<string>? audit = null)
{
    // 极高危模式直接拒绝执行（无法穷举，仅作最后防线；主要安全边界仍是运行环境本身）。
    private static readonly string[] DenyPatterns =
    [
        "rm -rf /", "rm -rf ~", "rm -rf /*",
        "mkfs", "diskutil eraseDisk", "> /dev/sda",
        "shutdown", "reboot",
    ];

    // 中文/非西文 Windows 的控制台程序默认输出 OEM 代码页（如简中 936/GBK），需按其解码避免乱码。
    private static readonly Encoding? WindowsOemEncoding = TryGetOemEncoding();

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

        var psi = CreateStartInfo(trimmed);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            // shell 不存在、工作区目录失效等启动期失败，作为工具结果返回而非抛异常，避免模型误判工具不可用。
            return $"命令启动失败（shell: {psi.FileName}）：{ex.Message}";
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            audit?.Invoke($"超时终止（>{timeout.TotalSeconds:N0}s）:: {trimmed}");
            return $"命令超时（>{timeout.TotalSeconds:N0}s），已终止：{trimmed}";
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        audit?.Invoke($"exit={process.ExitCode} :: {trimmed}");
        var sb = new StringBuilder();
        sb.AppendLine($"exit_code: {process.ExitCode}");
        sb.AppendLine("--- stdout ---");
        sb.AppendLine(Truncate(stdout));
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(Truncate(stderr));
        }
        if (process.ExitCode != 0)
        {
            // 失败时的行为引导：小参数模型容易连续失败两次就「宣布换方案」收尾，把这句随结果注入对抗该倾向
            sb.AppendLine("（命令执行失败。请根据上方输出定位原因并修正重试；该路径确实不可行时换其他方法继续完成任务，不要只宣布计划就结束。）");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>按平台选择 shell 并组装启动参数：Windows 用 ComSpec（cmd.exe），类 Unix 依次回退 zsh → bash → sh。</summary>
    private ProcessStartInfo CreateStartInfo(string command)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Windows 下 GUI 宿主启动控制台程序默认会弹出新的控制台窗口，须显式抑制（Unix 上此属性无效）
            CreateNoWindow = true,
        };
        // PATH 增强：GUI/"应用程序"启动的进程只有系统 PATH，补齐 Homebrew/nvm 等目录，
        // 否则模型执行的 git/node 等命令会"找不到命令"
        psi.EnvironmentVariables["PATH"] = Infrastructure.CommandPathResolver.AugmentedPath();

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            // cmd 不遵循 MSVCRT 引号转义规则，ArgumentList 会把命令内的引号写成 \" 而破坏命令；
            // 因此直接拼 Arguments，由 /s 让 cmd 把首尾引号视为包裹、内部引号原样保留。
            psi.Arguments = $"/d /s /c \"{command}\"";
            if (WindowsOemEncoding is { } oem)
            {
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
            }
        }
        else
        {
            psi.FileName = ResolveUnixShell();
            // 用 ArgumentList 把整条命令作为单个参数交给 shell -c，避免 .NET 参数解析拆坏引号。
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        return psi;
    }

    private static string ResolveUnixShell()
    {
        foreach (var shell in (string[])["/bin/zsh", "/bin/bash", "/bin/sh"])
        {
            if (File.Exists(shell))
            {
                return shell;
            }
        }
        return "/bin/sh";
    }

    private static Encoding? TryGetOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return null;   // 拿不到 OEM 代码页时退回默认解码
        }
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
