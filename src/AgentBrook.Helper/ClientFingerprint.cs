using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AgentBrook.Helper;

/// <summary>
/// 客户端硬件指纹：跨平台取系统唯一标识（macOS IOPlatformUUID / Linux machine-id /
/// Windows MachineGuid），回退为首次生成并持久化的随机 GUID。SHA-256 后作为试用卡防重复依据。
/// </summary>
public static class ClientFingerprint
{
    private static string? _cached;

    public static (string Fingerprint, string Hostname, string Os) Get()
    {
        if (_cached != null) return (_cached, Environment.MachineName, RuntimeInformation.OSDescription);

        var hw = HardwareId();
        if (string.IsNullOrWhiteSpace(hw))
        {
            // 回退：首次生成随机标识并持久化到工作区
            try
            {
                var dir = UiPrefs.WorkspaceDir();
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, ".device-id");
                if (File.Exists(file)) hw = File.ReadAllText(file).Trim();
                if (string.IsNullOrWhiteSpace(hw))
                {
                    hw = Guid.NewGuid().ToString("N");
                    File.WriteAllText(file, hw);
                }
            }
            catch { hw = Guid.NewGuid().ToString("N"); }
        }

        _cached = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hw))).ToLowerInvariant();
        return (_cached, Environment.MachineName, RuntimeInformation.OSDescription);
    }

    private static string HardwareId()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new ProcessStartInfo("ioreg", "-rd1 -c IOPlatformExpertDevice")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(3000);
                var m = System.Text.RegularExpressions.Regex.Match(output, "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : "";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id").Trim() : "";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "")?.ToString() ?? "";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fingerprint] {ex.Message}");
        }
        return "";
    }
}
