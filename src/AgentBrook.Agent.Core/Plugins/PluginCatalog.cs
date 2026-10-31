using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Plugin.Abstractions;

namespace AgentBrook.Agent.Plugins;

/// <summary>服务端插件索引拉取 + 插件包下载安装（doc/18 §3/§4）。</summary>
public sealed class PluginCatalog(Action<string>? log = null)
{
    /// <summary>索引基址（相对 URL 解析）；由 Manager 注入。</summary>
    public string? IndexBase { get; init; }

    /// <summary>插件安装根目录（workspace/plugins）；由 Manager 注入。</summary>
    public string? PluginsRoot { get; init; }

    /// <summary>拉取服务端插件索引（统一下载服务）。</summary>
    public async Task<PluginIndex> FetchAsync(Uri indexUrl, CancellationToken ct = default)
    {
        var json = await ResourceDownloader.Instance.DownloadStringAsync(indexUrl.ToString(), ct);
        return JsonSerializer.Deserialize<PluginIndex>(json) ?? new PluginIndex();
    }

    /// <summary>按当前 RID 下载并安装插件包，返回插件安装目录。</summary>
    public async Task<string> InstallAsync(PluginIndexEntry entry, CancellationToken ct = default,
        IProgress<PluginInstallProgress>? progress = null)
    {
        var rid = RidOf();
        if (!entry.Packages.TryGetValue(rid, out var pkg))
        {
            throw new InvalidOperationException($"插件 {entry.Id} 不提供 {rid} 包");
        }
        var baseUrl = IndexBase ?? throw new InvalidOperationException("插件索引基址未配置");
        var packageUrl = new Uri(new Uri(baseUrl), pkg.Url);
        var pluginsRoot = PluginsRoot ?? throw new InvalidOperationException("插件根目录未配置");

        // 暂存/解压都在插件根目录内（与最终目标同卷 → Directory.Move 原子入位；
        // 用系统临时目录会跨卷（Windows 上 %TEMP% 与工作区常不同盘，Move 直接失败）
        var stageRoot = Path.Combine(pluginsRoot, ".stage");
        Directory.CreateDirectory(stageRoot);
        // 清理历史暂存残留（进程中断会跳过 finally；>24h 的残留按垃圾处理，失败不阻断安装）
        try
        {
            foreach (var stale in Directory.EnumerateFileSystemEntries(stageRoot))
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-24))
                {
                    TryDelete(stale);
                }
            }
        }
        catch
        {
            // 残留清理失败不影响本次安装
        }
        var token = Guid.NewGuid().ToString("N");
        var tmpZip = Path.Combine(stageRoot, $"abk-plugin-{entry.Id}-{token}.zip");
        var stageDir = Path.Combine(stageRoot, $"{entry.Id}-{entry.Latest}-{token}.stage");
        try
        {
            log?.Invoke($"[plugins] 下载 {entry.Id} {entry.Latest}（{rid}）…");
            long? totalBytes = pkg.Size > 0 ? pkg.Size : null;
            var downloadProgress = progress is null ? null
                : new Progress<PluginDownloadProgress>(p => progress.Report(
                    new PluginInstallProgress(PluginInstallProgress.Download, p.Bytes, p.Total)));
            // 统一下载服务（升级功能同源）：分块落盘 + 百分比进度 + .part 原子改名
            await ResourceDownloader.Instance.DownloadFileAsync(
                packageUrl.ToString(), tmpZip, downloadProgress, ct);
            {
                var size = new FileInfo(tmpZip).Length;
                totalBytes = totalBytes ?? (size > 0 ? size : null);
                progress?.Report(new PluginInstallProgress(PluginInstallProgress.Download, size, totalBytes));
            }
            progress?.Report(new PluginInstallProgress(PluginInstallProgress.Verify, 0, totalBytes));
            VerifySha256(tmpZip, pkg.Sha256, $"插件包 {entry.Id}");

            progress?.Report(new PluginInstallProgress(PluginInstallProgress.Extract, 0, totalBytes));
            ExtractWithGuard(tmpZip, stageDir);
            VerifyManifest(stageDir, Path.Combine(stageDir, "manifest.json"));

            var idRoot = Path.Combine(pluginsRoot, entry.Id);
            var dirName = entry.Latest;
            var targetDir = Path.Combine(idRoot, dirName);
            if (Directory.Exists(targetDir))
            {
                try
                {
                    Directory.Delete(targetDir, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 热插拔下旧目录可能仍被 ALC 占用（卸载后装饰器引用未及 GC）→ 换新目录安装，
                    // 旧目录由下方清扫在锁释放后回收；极少走到 32 层仍全占用
                    var ok = false;
                    for (var n = 1; n < 32 && !ok; n++)
                    {
                        dirName = $"{entry.Latest}-r{n}";
                        targetDir = Path.Combine(idRoot, dirName);
                        ok = !Directory.Exists(targetDir);
                    }
                    if (!ok)
                    {
                        throw new InvalidOperationException(CoreStrings.L(
                            $"插件 {entry.Id} 安装目录无法分配（旧版本文件长期占用）。请重启 Helper 后重试。",
                            $"Could not allocate an install directory for plugin {entry.Id} (old files stuck). Restart Helper and retry.",
                            $"Tidak bisa menyiapkan direktori instalasi plugin {entry.Id} (file lama terkunci). Mulai ulang Helper lalu coba lagi."),
                            ex);
                    }
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);

            // 清扫同插件下的旧目录（含 .stage 残留）：已被 ALC 释放的可删，仍被占用的下次再清
            try
            {
                foreach (var old in Directory.EnumerateDirectories(idRoot))
                {
                    if (!string.Equals(Path.GetFullPath(old), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(old);
                    }
                }
            }
            catch
            {
                // 清扫失败不影响安装
            }
            Directory.Move(stageDir, targetDir);
            log?.Invoke($"[plugins] {entry.Id} {entry.Latest} 安装完成");
            return targetDir;
        }
        finally
        {
            TryDelete(tmpZip);
            TryDelete(stageDir);
        }
    }

    /// <summary>按 RID 解析（win-x64 / win-arm64 / osx-x64 / osx-arm64 / linux-x64 / linux-arm64）。</summary>
    public static string RidOf()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"{os}-{arch}";
    }

    internal static void ExtractWithGuard(string zipPath, string stageDir)
    {
        if (Directory.Exists(stageDir))
        {
            Directory.Delete(stageDir, true);
        }
        Directory.CreateDirectory(stageDir);
        using var zip = ZipFile.OpenRead(zipPath);
        var rootFull = Path.GetFullPath(stageDir) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(stageDir, entry.FullName));
            if (!target.StartsWith(rootFull, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"zip 条目路径穿越：{entry.FullName}");
            }
            if (entry.FullName.EndsWith('/') || entry.Length == 0)
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    internal static void VerifyManifest(string stageDir, string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return;   // 无 manifest 时仅包级校验（双层哈希的第一层）
        }
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath));
        if (manifest is null)
        {
            return;
        }
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(stageDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || Sha256Of(path) != file.Sha256)
            {
                throw new InvalidOperationException($"manifest 校验失败：{file.Path}");
            }
        }
    }

    internal static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static void VerifySha256(string path, string expected, string label)
    {
        var actual = Sha256Of(path);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label} SHA-256 校验失败");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时产物清理失败可忽略
        }
    }
}
