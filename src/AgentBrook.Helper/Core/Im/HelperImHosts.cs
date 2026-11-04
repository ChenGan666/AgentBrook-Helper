using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.IM;
using AgentBrook.Messaging;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;
using Microsoft.Agents.AI;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper.Core.Im;

/// <summary>Helper 宿主的 Blob 服务（workspace/im/attachments；生成 ID 落盘）。</summary>
public sealed class HelperBlobService : IBlobService
{
    private readonly string _root;

    public HelperBlobService(string workspaceRoot) => _root = Path.Combine(workspaceRoot, "im", "attachments");

    /// <summary>Blob 磁盘路径（宿主约定 root/&lt;前缀&gt;/&lt;id&gt;；会话界面渲染附件用）。</summary>
    public string GetLocalPath(ContentRef content) => Path.Combine(_root, content.BlobId[..3], content.BlobId);

    public async Task<ContentRef> WriteAsync(string purpose, Stream content, long maxBytes, string fileName, string? mediaType, CancellationToken cancellationToken)
    {
        if (!ContentRef.IsValidFileName(fileName))
            throw new InvalidOperationException("[BadPayload] 非法文件名。");
        var blobId = "blb-" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(_root, blobId[..3]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, blobId);
        var total = 0L;
        var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException("[QuotaExceeded] 超过附件上限。");
                sha.AppendData(buffer, 0, read);
                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        return new ContentRef(blobId, hash, total, fileName, mediaType);
    }

    public Task<Stream> OpenReadAsync(ContentRef reference, string purpose, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, reference.BlobId[..3], reference.BlobId);
        if (!File.Exists(path))
            throw new InvalidOperationException("[StorageUnavailable] Blob 缺失。");
        Stream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(fs);
    }
}



/// <summary>IM 回合的审批端口（S8 最小形态：拒绝——等待 P1 "等待本机确认"消息模型；§13 矩阵）。</summary>
public sealed class HelperApprovalPort : IApprovalPort
{
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Denied);
}

/// <summary>最小上下文提供者（AgentFactory 形参占位）。</summary>
public sealed class NoopImContextProvider : AIContextProvider
{
}
