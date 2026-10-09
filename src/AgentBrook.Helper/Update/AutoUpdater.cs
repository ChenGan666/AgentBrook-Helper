using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AgentBrook.Helper.Update;

internal static class AutoUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static (string Os, string Arch) CurrentPlatform
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "osx"
                : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                _ => "arm64"
            };
            return (os, arch);
        }
    }

    private static string DefaultVersionUrl =>
        $"http://helper.agentbrook.com/api/update/version?os={CurrentPlatform.Os}&arch={CurrentPlatform.Arch}";

    private static string DefaultDownloadUrl =>
        $"http://helper.agentbrook.com/api/update/download?os={CurrentPlatform.Os}&arch={CurrentPlatform.Arch}";

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    public static async Task<UpdateInfo?> CheckAsync(string? versionUrl = null)
    {
        var requestUrl = string.IsNullOrWhiteSpace(versionUrl) ? DefaultVersionUrl : versionUrl;
        try
        {
            var dto = await Http.GetFromJsonAsync<VersionDto>(requestUrl).ConfigureAwait(false);
            if (dto?.Version is null || !Version.TryParse(dto.Version, out var latest))
            {
                return null;
            }

            var downloadUrl = ResolveDownloadUrl(requestUrl, dto.DownloadUrl);

            return new UpdateInfo(
                latest,
                downloadUrl,
                dto.FileName ?? "update.zip",
                dto.ReleaseNotes ?? "",
                dto.Hash ?? "",
                dto.HashAlgorithm ?? "SHA256",
                dto.Size ?? 0,
                dto.Platform ?? $"{CurrentPlatform.Os}-{CurrentPlatform.Arch}");
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveDownloadUrl(string versionUrl, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return DefaultDownloadUrl;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out _))
        {
            return candidate;
        }

        if (Uri.TryCreate(new Uri(versionUrl), candidate, out var combined))
        {
            return combined.AbsoluteUri;
        }

        return DefaultDownloadUrl;
    }

    public static bool IsNewer(Version latest) => latest > CurrentVersion;

    public static async Task<string?> UpgradeAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AgentBrookUpdate", info.Version.ToString());
            Directory.CreateDirectory(tempRoot);

            var zipPath = Path.Combine(tempRoot, info.FileName);

            // 多线程分块下载
            string? downloadError;
            if (info.Size > 0)
            {
                downloadError = await DownloadWithProgressAsync(info.DownloadUrl, info.Size, zipPath, progress, ct).ConfigureAwait(false);
            }
            else
            {
                var zipBytes = await Http.GetByteArrayAsync(info.DownloadUrl, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(zipPath, zipBytes, ct).ConfigureAwait(false);
                downloadError = null;
            }

            if (!string.IsNullOrEmpty(downloadError))
            {
                return downloadError;
            }

            // 完整性校验
            if (!string.IsNullOrWhiteSpace(info.Hash))
            {
                var zipBytes = await File.ReadAllBytesAsync(zipPath, ct).ConfigureAwait(false);
                var actualHash = ComputeHash(zipBytes);
                if (!string.Equals(actualHash, info.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    return $"文件校验失败（SHA256 不匹配）。预期：{info.Hash}，实际：{actualHash}";
                }
            }

            var extractDir = Path.Combine(tempRoot, "extracted");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                currentExe = OperatingSystem.IsWindows()
                    ? Path.Combine(AppContext.BaseDirectory, "AgentBrook.Helper.exe")
                    : Path.Combine(AppContext.BaseDirectory, "AgentBrook.Helper");
            }

            var appDir = Path.GetDirectoryName(currentExe)!;
            var appName = Path.GetFileName(currentExe);
            var isWindows = OperatingSystem.IsWindows();

            var scriptName = isWindows ? "update.bat" : "update.sh";
            var scriptPath = Path.Combine(tempRoot, scriptName);
            string scriptContent;

            if (isWindows)
            {
                scriptContent = $$"""
                    @echo off
                    chcp 65001 > nul
                    echo Waiting for AgentBrook to exit...
                    timeout /t 2 /nobreak > nul
                    echo Updating files...
                    xcopy /s /y /i "{{extractDir}}\*" "{{appDir}}\"
                    if errorlevel 1 (
                        echo Update failed, please update manually.
                        pause
                        exit /b 1
                    )
                    echo Starting {{appName}}...
                    start "" "{{currentExe}}"
                    exit
                    """;
            }
            else
            {
                scriptContent = $$"""
                    #!/bin/bash
                    sleep 2
                    echo "Updating files..."
                    rm -rf "{{appDir}}"/*
                    cp -R "{{extractDir}}"/* "{{appDir}}"/
                    chmod -R +x "{{appDir}}"
                    echo "Starting {{appName}}..."
                    nohup "{{currentExe}}" >/dev/null 2>&1 &
                    exit
                    """;
            }

            await File.WriteAllTextAsync(scriptPath, scriptContent).ConfigureAwait(false);

            var psi = isWindows
                ? new ProcessStartInfo(scriptPath)
                {
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = tempRoot,
                }
                : new ProcessStartInfo("bash", $"\"{scriptPath}\"")
                {
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = tempRoot,
                };

            Process.Start(psi);

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> DownloadWithProgressAsync(
        string url,
        long totalSize,
        string destination,
        IProgress<double>? progress,
        CancellationToken ct,
        int chunkCount = 4)
    {
        if (totalSize <= 0)
        {
            return "无效的文件大小，无法分块下载。";
        }

        try
        {
            var supportsRange = await SupportsRangeAsync(url, ct).ConfigureAwait(false);
            if (!supportsRange)
            {
                await SingleDownloadWithProgressAsync(url, destination, totalSize, progress, ct).ConfigureAwait(false);
                return null;
            }

            const long minChunkSize = 256 * 1024; // 256 KB
            long chunkSize = totalSize / chunkCount;
            if (chunkSize < minChunkSize)
            {
                chunkCount = Math.Max(1, (int)(totalSize / minChunkSize));
                chunkSize = totalSize / chunkCount;
            }

            // 预分配目标文件，避免临时分块文件和合并步骤
            using (var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 81920, true))
            {
                fs.SetLength(totalSize);
            }

            var totalDownloaded = 0L;
            var tasks = new Task[chunkCount];

            for (int i = 0; i < chunkCount; i++)
            {
                long from = i * chunkSize;
                long to = (i == chunkCount - 1) ? totalSize - 1 : ((i + 1) * chunkSize) - 1;
                var chunkFrom = from;
                var chunkTo = to;

                tasks[i] = Task.Run(async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(chunkFrom, chunkTo);
                    using var response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fs = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 81920, true);
                    fs.Seek(chunkFrom, SeekOrigin.Begin);

                    var buffer = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        Interlocked.Add(ref totalDownloaded, read);
                        progress?.Report(totalDownloaded / (double)totalSize);
                    }
                }, ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            progress?.Report(1.0);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"下载失败：{ex.Message}";
        }
    }

    private static async Task<bool> SupportsRangeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)response.StatusCode == 206;
        }
        catch
        {
            return false;
        }
    }

    private static async Task SingleDownloadWithProgressAsync(
        string url,
        string destination,
        long totalSize,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var response = await DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[8192];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            progress?.Report(downloaded / (double)totalSize);
        }

        progress?.Report(1.0);
    }

    private static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record VersionDto(
        string? Version,
        string? DownloadUrl,
        string? FileName,
        string? ReleaseNotes,
        string? Hash,
        string? HashAlgorithm,
        long? Size,
        string? Platform);
}

internal sealed record UpdateInfo(
    Version Version,
    string DownloadUrl,
    string FileName,
    string ReleaseNotes,
    string Hash,
    string HashAlgorithm,
    long Size,
    string Platform);
