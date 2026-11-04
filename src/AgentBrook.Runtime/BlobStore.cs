using System.Security.Cryptography;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Runtime;

/// <summary>
/// 宿主 Blob 存储（设计 §8.2）：物理路径完全使用本机生成 ID；
/// 路径规范化 + 根前缀校验（防目录逃逸/软链接越界）；流式写入限额 + hash/长度校验；
/// namespace+purpose 授权（拥有引用不等于可读取，H06）；引用计数与清理。
/// 与提交日志共享同一 ImDatabase（§3.3：不制造第二个数据库）。
/// </summary>
public sealed class BlobStore(ImDatabase db, string rootPath)
{
    private readonly string _root = Path.Combine(rootPath, "blobs");

    /// <summary>允许的 (namespace, purpose) 授权集——由宿主按实例注入（S3 起按命名空间隔离）。</summary>
    public HashSet<(string Namespace, string Purpose)> Authorized { get; init; } = new();

    public const long DefaultQuotaBytes = 512L * 1024 * 1024;

    /// <summary>流式写入：分块拷贝+限额+流式 hash；超限/超配额即拒绝（已写部分清理）。</summary>
    public async Task<ContentRef> PutAsync(
        string namespaceId, string purpose, Stream content, long maxBytes,
        string fileName, string? mediaType, CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(namespaceId, purpose, write: true);
        if (!ContentRef.IsValidFileName(fileName))
            throw new RuntimePipelineException(ImErrorCodes.BadPayload, $"非法文件名：{fileName}");

        var blobId = "blb-" + Guid.NewGuid().ToString("N");
        var blobPath = PathFor(blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);

        long total = 0;
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var fs = new FileStream(blobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new RuntimePipelineException(ImErrorCodes.QuotaExceeded, $"超过单文件上限 {maxBytes}B。");
                incremental.AppendData(buffer, 0, read);
                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            EnsureQuota(namespaceId, total);
            var hash = Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
            db.Execute("""
                INSERT INTO blobs(blob_id, namespace, purpose, sha256, length, file_name, media_type, ref_count, created_at)
                VALUES($id, $ns, $purpose, $sha, $len, $name, $media, 0, $at)
                """,
                ("$id", blobId), ("$ns", namespaceId), ("$purpose", purpose), ("$sha", hash), ("$len", total),
                ("$name", fileName), ("$media", mediaType), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            return new ContentRef(blobId, hash, total, fileName, mediaType);
        }
        catch
        {
            try { File.Delete(blobPath); } catch (IOException) { /* 已无该文件 */ }
            throw;
        }
    }

    /// <summary>按授权读取：引用必须属于本 namespace；存储内容 hash/长度复验（防篡改/替换）。</summary>
    public async Task<Stream> OpenReadAsync(string namespaceId, string purpose, ContentRef reference,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(namespaceId, purpose, write: false);
        var row = await GetAsync(reference.BlobId).ConfigureAwait(false)
            ?? throw new RuntimePipelineException(ImErrorCodes.ServiceNotGranted, $"Blob 不存在：{reference.BlobId}");
        if (row.Namespace != namespaceId)
            throw new RuntimePipelineException(ImErrorCodes.ServiceNotGranted,
                "跨命名空间读取拒绝（拥有引用不等于可读取）。");

        var path = PathFor(reference.BlobId);
        if (!File.Exists(path))
            throw new RuntimePipelineException(ImErrorCodes.StorageUnavailable, $"Blob 字节缺失：{reference.BlobId}");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != row.Length || stream.Length != reference.Length)
            {
                stream.Dispose();
                throw new RuntimePipelineException(ImErrorCodes.StorageUnavailable, "Blob 长度校验失败。");
            }
            var hash = await ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            if (hash != row.Sha256 || hash != reference.Sha256)
            {
                stream.Dispose();
                throw new RuntimePipelineException(ImErrorCodes.StorageUnavailable, "Blob hash 校验失败（内容被替换？）。");
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>引用计数（发送中的附件/待恢复任务持有引用；GC 不删被引用版本内容）。</summary>
    public void AddReference(string blobId)
    {
        db.Execute("UPDATE blobs SET ref_count = ref_count + 1 WHERE blob_id = $id", ("$id", blobId));
    }

    public void ReleaseReference(string blobId)
    {
        db.Execute("UPDATE blobs SET ref_count = MAX(ref_count - 1, 0) WHERE blob_id = $id", ("$id", blobId));
    }

    /// <summary>清理无引用且超过保留期的 Blob（GC；被引用的不删）。</summary>
    public IReadOnlyList<string> CleanupUnreferenced(TimeSpan retention)
    {
        var removed = new List<string>();
        var cutoff = (DateTimeOffset.UtcNow - retention).ToString("O");
        using (var cmd = db.CreateCommand(
                   "SELECT blob_id FROM blobs WHERE ref_count <= 0 AND created_at < $cut", ("$cut", cutoff)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) removed.Add(reader.GetString(0));
        }
        foreach (var id in removed)
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
            db.Execute("DELETE FROM blobs WHERE blob_id = $id", ("$id", id));
        }
        return removed;
    }

    private string PathFor(string blobId)
    {
        if (!ContentRef.IsValidBlobId(blobId))
            throw new RuntimePipelineException(ImErrorCodes.BadPayload, $"非法 BlobId：{blobId}");
        // 生成 ID 两级分片；Path.GetFullPath + 根前缀校验（软链接/逃逸防御）
        var path = Path.GetFullPath(Path.Combine(_root, blobId[..3], blobId));
        var rootFull = Path.GetFullPath(_root);
        if (!path.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new RuntimePipelineException(ImErrorCodes.BadPayload, "路径逃逸拒绝。");
        return path;
    }

    private void EnsureAuthorized(string namespaceId, string purpose, bool write)
    {
        if (string.IsNullOrWhiteSpace(namespaceId) || string.IsNullOrWhiteSpace(purpose))
            throw new RuntimePipelineException(ImErrorCodes.ServiceNotGranted, "namespace/purpose 为空。");
        if (Authorized.Count > 0 && !Authorized.Contains((namespaceId, purpose)))
            throw new RuntimePipelineException(ImErrorCodes.ServiceNotGranted,
                $"未授权的 Blob 用途：{namespaceId}/{purpose}（{(write ? "写" : "读")}）");
    }

    private void EnsureQuota(string namespaceId, long incomingBytes)
    {
        using var cmd = db.CreateCommand(
            "SELECT COALESCE(SUM(length), 0) FROM blobs WHERE namespace = $ns", ("$ns", namespaceId));
        var used = Convert.ToInt64(cmd.ExecuteScalar());
        if (used + incomingBytes > DefaultQuotaBytes)
            throw new RuntimePipelineException(ImErrorCodes.QuotaExceeded,
                $"命名空间 {namespaceId} 磁盘配额超限（{used}+{incomingBytes} > {DefaultQuotaBytes}）。");
    }

    private async Task<BlobRow?> GetAsync(string blobId)
    {
        using var cmd = db.CreateCommand("SELECT * FROM blobs WHERE blob_id = $id", ("$id", blobId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BlobRow(
            reader.GetString(reader.GetOrdinal("blob_id")),
            reader.GetString(reader.GetOrdinal("namespace")),
            reader.GetString(reader.GetOrdinal("purpose")),
            reader.GetString(reader.GetOrdinal("sha256")),
            reader.GetInt64(reader.GetOrdinal("length")));
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            incremental.AppendData(buffer, 0, read);
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record BlobRow(string BlobId, string Namespace, string Purpose, string Sha256, long Length);
}
