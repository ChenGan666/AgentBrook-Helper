using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentBrook.Helper;

/// <summary>
/// 任务完成提示音（跨平台，零依赖）：
/// Windows 走 winmm PlaySound 系统音别名（跟随用户声音方案）；macOS 用 afplay 播系统音。
/// </summary>
internal static class NotifySound
{
    /// <summary>预置音色（Id 与平台音映射）。</summary>
    public static readonly (string Id, string NameZh)[] Presets =
    [
        ("ding", "叮"),
        ("glass", "玻璃"),
        ("hero", "和弦"),
    ];

    public static bool IsValidId(string id) => Presets.Any(p => p.Id == id);

    public static void Play(string id)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // SND_ASYNC 立即返回；别名跟随系统声音方案设置
                var alias = id switch
                {
                    "glass" => "SystemExclamation",
                    "hero" => "SystemDefault",
                    _ => "SystemAsterisk",
                };
                PlaySound(alias, IntPtr.Zero, SndAlias | SndAsync);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var file = id switch
                {
                    "glass" => "Glass",
                    "hero" => "Hero",
                    _ => "Ping",
                };
                var psi = new ProcessStartInfo("/usr/bin/afplay", $"\"/System/Library/Sounds/{file}.aiff\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                Process.Start(psi);   // 不等待播完
            }
        }
        catch
        {
            // 提示音失败不影响主流程
        }
    }

    private const int SndAsync = 0x0001;
    private const int SndAlias = 0x10000;

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(string name, IntPtr hmod, int flags);
}
