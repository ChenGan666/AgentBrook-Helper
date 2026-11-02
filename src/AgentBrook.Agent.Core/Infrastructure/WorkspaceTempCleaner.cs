using System.Text.RegularExpressions;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 工作区临时目录卫生清理：外部浏览器自动化工具（probe/截图/多实例 profile 等）
/// 常把工作区根当 user-data-dir 父目录，遗留成堆的 .chrome-* / .playwright-* 隐藏目录，
/// 长期累积占空间。本清扫器只在两个时机触发（Agent 构造强扫 + 回合入口节流复查），
/// 白名单匹配 + 活动性闸门，绝不动未匹配目录。
/// </summary>
public static class WorkspaceTempCleaner
{
    /// <summary>明确的浏览器自动化临时 profile 命名前缀（白名单，不匹配的隐藏目录一律不碰）。</summary>
    private static readonly Regex TempDirRx =
        new(@"^\.(chrome|msedge|chromium|playwright|chromedriver|firefox)-",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /// <summary>活动性闸门：目录及其顶层文件 2 小时无写入才判定为可回收，避免误删运行中的浏览器 profile。</summary>
    private static readonly TimeSpan MinIdleAge = TimeSpan.FromHours(2);

    /// <summary>回合入口节流：同一进程内最多 15 分钟扫一次（构造强扫不受限）。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    private static long _lastSweepMs = -1;

    /// <summary>
    /// 非阻塞清扫入口。force=true 时跳过节流（用于启动时首扫）。
    /// 后台执行，任何删除失败（被占用/权限）静默跳过，下轮再试。
    /// </summary>
    public static void SweepAsync(string? workspaceRoot, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return;
        }
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastSweepMs);
        if (!force && last >= 0 && now - last < (long)SweepInterval.TotalMilliseconds)
        {
            return;   // 节流期内
        }
        if (Interlocked.CompareExchange(ref _lastSweepMs, now, last) != last)
        {
            return;   // 并发放行失败
        }
        _ = Task.Run(() => SweepCore(workspaceRoot));
    }

    private static void SweepCore(string workspaceRoot)
    {
        var removed = 0;
        var failed = 0;
        var cutoff = DateTime.UtcNow - MinIdleAge;
        DirectoryInfo[] dirs;
        try
        {
            dirs = new DirectoryInfo(workspaceRoot).EnumerateDirectories().ToArray();
        }
        catch
        {
            return;
        }
        foreach (var dir in dirs)
        {
            if (!TempDirRx.IsMatch(dir.Name))
            {
                continue;
            }
            try
            {
                // 目录自身 mtime 不可靠（浏览器运行中可能不写根目录）——
                // 顺带扫顶层文件（SingletonLock/DevToolsActivePort/Crashpad 等）取最新写入时刻
                var lastWrite = dir.LastWriteTimeUtc;
                foreach (var f in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if (f.LastWriteTimeUtc > lastWrite)
                    {
                        lastWrite = f.LastWriteTimeUtc;
                    }
                }
                if (lastWrite > cutoff)
                {
                    continue;   // 仍活跃：跳过
                }
                dir.Delete(recursive: true);
                removed++;
            }
            catch
            {
                failed++;   // 被占用或权限不足：跳过，下轮重试
            }
        }
        if (removed + failed > 0)
        {
            Console.Error.WriteLine(
                $"[workspace-cleaner] 清理临时浏览器目录：删除 {removed} 个，跳过 {failed} 个（占用/权限）");
        }
    }
}
