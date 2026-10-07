using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace AgentBrook.Helper;

/// <summary>
/// 开机自启动（登录后自动运行，气泡常驻）：
/// Windows 写注册表 HKCU\...\Run 键；macOS 写 ~/Library/LaunchAgents 的 plist（RunAtLoad）。
/// 状态以系统侧（注册表值 / plist 文件）是否存在为准，设置界面据此回显。
/// </summary>
internal static class AutoStart
{
    private const string RunValueName = "AgentBrook.Helper";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string MacPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.agentbrook.helper.plist");

    /// <summary>当前系统是否支持开机自启动。</summary>
    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>读取系统侧实际状态。</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is not null;
            }
            if (OperatingSystem.IsMacOS())
            {
                return File.Exists(MacPlistPath);
            }
        }
        catch
        {
            // 读取失败按未开启处理，避免设置窗口打不开
        }
        return false;
    }

    /// <summary>开启/关闭开机自启动。返回 null 表示成功，否则为失败原因（供界面提示）。</summary>
    public static string? SetEnabled(bool enable)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null)
                {
                    return "无法打开注册表 Run 键";
                }
                if (enable)
                {
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe))
                    {
                        return "无法定位可执行文件";
                    }
                    key.SetValue(RunValueName, $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
                return null;
            }

            if (OperatingSystem.IsMacOS())
            {
                if (enable)
                {
                    var plist = BuildMacPlist();
                    if (plist is null)
                    {
                        return "无法定位应用本体";
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
                    File.WriteAllText(MacPlistPath, plist);
                    RunLaunchctl("unload");   // 先卸旧再载新，重复开启幂等
                    RunLaunchctl("load");
                }
                else
                {
                    RunLaunchctl("unload");
                    if (File.Exists(MacPlistPath))
                    {
                        File.Delete(MacPlistPath);
                    }
                }
                return null;
            }

            return "当前系统不支持开机自启动";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>launchctl load/unload 让改动立即生效（失败只影响本次，重启登录后仍按 plist 生效）。</summary>
    private static void RunLaunchctl(string action)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/launchctl",
                Arguments = $"{action} \"{MacPlistPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch
        {
            // 见方法注释
        }
    }

    /// <summary>
    /// 生成 LaunchAgent plist（RunAtLoad = 登录即拉起）。
    /// 在 .app 包内运行时用 open 拉起（走 LaunchServices，Dock/身份正确）；dotnet 裸跑用自身路径。
    /// </summary>
    private static string? BuildMacPlist()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }
        // exe 形如 <X.app>/Contents/MacOS/AgentBrook.Helper 时取 <X.app>
        var macosDir = Path.GetDirectoryName(exe);
        var bundle = macosDir is not null && Path.GetFileName(macosDir) == "MacOS"
            ? Directory.GetParent(Directory.GetParent(macosDir)!.FullName)
            : null;
        string[] programArgs = bundle is not null && bundle.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? ["/usr/bin/open", bundle.FullName]
            : [exe];

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\">");
        sb.AppendLine("<dict>");
        sb.AppendLine("  <key>Label</key><string>com.agentbrook.helper</string>");
        sb.AppendLine("  <key>ProgramArguments</key>");
        sb.AppendLine("  <array>");
        foreach (var a in programArgs)
        {
            sb.AppendLine($"    <string>{Esc(a)}</string>");
        }
        sb.AppendLine("  </array>");
        sb.AppendLine("  <key>RunAtLoad</key><true/>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
