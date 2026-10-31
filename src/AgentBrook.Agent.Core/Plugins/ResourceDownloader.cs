using AgentBrook.Plugin.Abstractions;

namespace AgentBrook.Agent.Plugins;

/// <summary>
/// 宿主统一资源下载器（doc/18 下载服务标准；升级功能同源策略）：
/// 全进程一个 HttpClient（30 分钟超时——大模型/插件包不被默认 100s 掐断）、
/// 分块落盘 + Content-Length 百分比进度、同目录 .part 暂存原子改名（失败自清）。
/// 插件经 <see cref="IPluginHostContext.Download"/> 使用；宿主自更新共用同一客户端。
/// </summary>
public sealed class ResourceDownloader : IPluginDownloadService
{
    /// <summary>共享客户端：插件包/模型/升级包统一走它（超时/代理/连接池集中维护）。</summary>
    public static HttpClient SharedClient { get; } = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static ResourceDownloader Instance { get; } = new();

    public async Task DownloadFileAsync(string url, string destPath,
        IProgress<PluginDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var partPath = destPath + ".part";
        try
        {
            using (var resp = await SharedClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var dst = File.Create(partPath);
                var buffer = new byte[81920];
                long written = 0, reported = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    if (progress is not null && written - reported >= 256 * 1024)
                    {
                        reported = written;
                        progress.Report(new PluginDownloadProgress(written, total));
                    }
                }
                progress?.Report(new PluginDownloadProgress(written, total));
            }
            File.Move(partPath, destPath, overwrite: true);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    public async Task<string> DownloadStringAsync(string url, CancellationToken cancellationToken = default) =>
        await SharedClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 残留 .part 由下次下载覆盖或用户清理
        }
    }
}
