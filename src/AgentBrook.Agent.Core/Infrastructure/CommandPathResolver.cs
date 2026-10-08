using System.Runtime.InteropServices;
using Microsoft.Win32;

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

    /// <summary>完整搜索目录：当前 PATH → 注册表登录 PATH（HKLM+HKCU，修复 GUI 启动丢 PATH）→ 常见补齐目录（去重）。</summary>
    public static IReadOnlyList<string> SearchDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var dirs = path.Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        foreach (var d in RegistryPaths()
                     .Where(d => !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)))
        {
            dirs.Add(d);
        }
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
    /// 从注册表恢复登录用户的完整 PATH：HKLM 系统环境 + HKCU 用户环境。
    /// GUI/"应用程序" 启动的进程只会继承系统默认 PATH（丢失安装器写入的用户 PATH），
    /// 从注册表读回是唯一可靠的恢复方式。
    /// </summary>
    private static IEnumerable<string> RegistryPaths()
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }
        foreach (var keyPath in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                     @"HKEY_CURRENT_USER\Environment",
                 })
        {
            try
            {
                if (Registry.GetValue(keyPath, "Path", null) is not string p)
                {
                    continue;
                }
                foreach (var d in p.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!result.Contains(d, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(d);
                    }
                }
            }
            catch
            {
                // 注册表不可读时跳过该来源
            }
        }
        return result;
    }

    /// <summary>
    /// 取 Windows 8.3 短路径（去除空格）：含空格的路径（如 C:\Program Files\…）在
    /// 子进程命令行拼接中可能被断开，短路径彻底规避。8.3 不可用或非 Windows 时原样返回。
    /// </summary>
    public static string GetShortPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains(' '))
        {
            return path;
        }
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var len = GetShortPathName(path, sb, (uint)sb.Capacity);
            return len > 0 && len <= sb.Capacity ? sb.ToString(0, (int)len) : path;
        }
        catch
        {
            return path;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

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
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        // Windows：npm 的可执行垫片是 npx.cmd/npm.cmd（并非 .exe），
        // 按 exe → cmd → bat → 无扩展名 的优先级解析。
        var candidates = OperatingSystem.IsWindows()
            ? new[] { command + ".exe", command + ".cmd", command + ".bat", command }
            : new[] { command };
        foreach (var dir in SearchDirectories())
        {
            foreach (var cand in candidates)
            {
                try
                {
                    var full = Path.Combine(dir, cand);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // 非法路径字符等，跳过该目录
                }
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
