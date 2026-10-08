using System.Runtime.InteropServices;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 命令路径解析与 PATH 增强。
/// 从 Finder/"应用程序" 启动的 GUI 进程 PATH 只有系统默认目录（/usr/bin:/bin:/usr/sbin:/sbin），
/// 找不到 Homebrew/nvm/官方安装器安装的 node、npx、git 等工具。
/// 这里提供：常见目录补齐搜索 + 子进程 PATH 增强，保证 MCP 服务器与 run_command 在任何启动方式下可用。
/// </summary>
public static class CommandPathResolver
{
    /// <summary>GUI 进程容易缺失的常见可执行目录（按平台）。</summary>
    public static IReadOnlyList<string> CommonDirectories()
    {
        var dirs = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/opt/homebrew/bin");           // Homebrew（Apple Silicon）
            dirs.Add("/usr/local/bin");              // Homebrew（Intel）/ 官方 Node 安装器
            dirs.Add("/opt/homebrew/sbin");
            dirs.Add("/usr/local/sbin");
            var nvm = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvm))
            {
                var latest = Directory.GetDirectories(nvm)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).LastOrDefault();
                if (latest is not null)
                {
                    dirs.Add(Path.Combine(latest, "bin"));   // nvm 最新版本
                }
            }
            dirs.Add(Path.Combine(home, ".local", "bin"));
        }
        else if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            dirs.Add(Path.Combine(appData, "npm"));          // npm 全局
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            dirs.Add(Path.Combine(pf, "nodejs"));            // Node.js 官方安装器
        }
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(home, "bin"));
        return dirs;
    }

    /// <summary>完整搜索目录：当前 PATH 在前，常见补齐目录在后（去重）。</summary>
    public static IReadOnlyList<string> SearchDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var dirs = path.Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        foreach (var d in CommonDirectories())
        {
            if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(d);
            }
        }
        return dirs;
    }

    /// <summary>
    /// 解析命令的可执行路径。绝对路径原样返回（含存在性校验）；
    /// 相对命令名在 PATH 与常见补齐目录中查找；找不到返回 null（调用方给出安装指引）。
    /// </summary>
    public static string? Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        var name = OperatingSystem.IsWindows() && !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? command + ".exe"
            : command;
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }
        foreach (var dir in SearchDirectories())
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法路径字符等，跳过该目录
            }
        }
        return null;
    }

    /// <summary>增强后的 PATH（继承 PATH + 常见补齐目录），写入子进程环境变量，保证 npx 能找到 node 等依赖。</summary>
    public static string AugmentedPath()
    {
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        return string.Join(sep, SearchDirectories());
    }
}
