using System.Security.Cryptography;
using System.Text.Json;
using AgentBrook.A2A.Core.Artifacts;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Transport.Hosting;

namespace AgentBrook.A2A.Transport.Business;

/// <summary>对端发来的文件公告（以普通 A2A 消息文本承载，kind 标识类型）。</summary>
public sealed record FileAnnouncement(string ArtifactId, string Name, long Size, string Sha256, string? MediaType)
{
    public const string Kind = "io.agentbrook.file/v1";

    public string ToJson() => JsonSerializer.Serialize(new
    {
        kind = Kind,
        artifactId = ArtifactId,
        name = Name,
        size = Size,
        sha256 = Sha256,
        mediaType = MediaType
    });

    public static FileAnnouncement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != Kind) return null;
            if (!root.TryGetProperty("artifactId", out var artifactId)) return null;
            return new FileAnnouncement(
                artifactId.GetString() ?? "",
                root.TryGetProperty("name", out var n) ? n.GetString() ?? "file" : "file",
                root.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                root.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "",
                root.TryGetProperty("mediaType", out var m) ? m.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Helper 间文件传递（用户需求：Agent2Agent 之间通讯增加文件的传递）。
/// 复用既有 Artifact 推送端点（offer → multipart 上传 → SHA256 校验）：
/// 发送方把文件推入对端隔离区后，以标准 A2A 消息发送文件公告（携带 artifactId/name/sha256）；
/// 接收方校验完整性并在会话流登记 ArtifactReceived 事件。
/// 文件始终落在接收方 ArtifactStore 隔离区，后续使用仍遵循本机审批。
/// </summary>
public static class A2AFileTransferService
{
    public static bool IsLoopbackEndpoint(string endpoint)
    {
        return !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
               uri.Host is "127.0.0.1" or "localhost" or "::1" or "";
    }

    /// <summary>向对端推送本机文件并发送文件公告。返回对端登记的 Artifact 描述。</summary>
    public static async Task<ArtifactDescriptor> SendFileAsync(
        A2AHost host, string peerNodeId, string localPath, string sessionId, string? note = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("本地文件不存在。", localPath);
        }
        var peer = await host.Store.GetPeerAsync(peerNodeId, cancellationToken)
            ?? throw new InvalidOperationException("对端记录不存在。");

        var fileInfo = new FileInfo(localPath);
        await using var stream = fileInfo.OpenRead();
        var sha256Hex = Convert.ToHexString(SHA256.HashData(await ReadAllAsync(stream, cancellationToken))).ToLowerInvariant();
        stream.Position = 0;
        var fileName = Path.GetFileName(localPath);
        var mediaType = GuessMediaType(localPath);

        // ① 对端会话（无则按调用方给定的会话 id 创建，文件公告与事件都归属该会话）
        var sessions = await host.Store.GetSessionsAsync(cancellationToken);
        var session = sessions.FirstOrDefault(s => s.Kind == SessionKind.Peer && s.Members.Any(m => m.NodeId == peerNodeId));
        if (session is null)
        {
            session = A2ASession.CreatePeer(sessionId, host.Identity.Identity.NodeId, peerNodeId);
            await host.Store.SaveSessionAsync(session, cancellationToken);
        }
        else
        {
            sessionId = session.SessionId;   // 复用既有会话的真实 id
        }

        // ② 推送文件到对端隔离区：offer（登记）→ multipart 上传（SHA256 校验）
        var offer = new Transport.Protocol.ArtifactOfferDto
        {
            Name = fileName,
            Size = fileInfo.Length,
            Sha256 = sha256Hex,
            CreatedBy = host.Identity.Identity.NodeId,
            SessionId = sessionId,
        };
        Exception? lastError = null;
        Core.Artifacts.ArtifactDescriptor? uploaded = null;
        string? usedEndpoint = null;
        foreach (var endpoint in peer.Endpoints.ToList())
        {
            try
            {
                var offered = await host.Client.CreateArtifactOfferAsync(new Uri(endpoint), offer, cancellationToken);
                uploaded = await host.Client.UploadArtifactAsync(new Uri(endpoint), offered.ArtifactId, stream, mediaType, cancellationToken);
                if (!string.Equals(peer.Endpoints.FirstOrDefault(), endpoint, StringComparison.OrdinalIgnoreCase))
                {
                    peer.Endpoints.Remove(endpoint);
                    peer.Endpoints.Insert(0, endpoint);
                    await host.Store.UpsertPeerAsync(peer, cancellationToken);
                }
                usedEndpoint = endpoint;
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or InvalidOperationException)
            {
                lastError = ex;
                stream.Position = 0;
            }
        }
        if (uploaded is null)
        {
            throw new InvalidOperationException(
                $"所有已知端点均无法接收文件（对端可能已下线）。{(lastError is null ? "" : "最后错误：" + lastError.Message)}", lastError);
        }
        uploaded.MediaType = mediaType;

        var identity = host.Identity.Identity;

        // ③ 本地留痕：发送事件（会话流显示；path 仅供本机事件流点击打开，事件不跨机传输）
        await host.Store.AppendEventAsync(Core.Sessions.SessionEvent.Create(sessionId, identity.NodeId, SessionEventType.ArtifactOffered,
            new { file = fileName, size = fileInfo.Length, sha256 = sha256Hex, note, path = localPath },
            senderName: identity.DisplayName, senderIcon: identity.Icon), cancellationToken: cancellationToken);

        // ④ 文件公告：标准 A2A 消息（对端据此登记收到的文件）
        var announcement = new FileAnnouncement(uploaded.ArtifactId, fileName, fileInfo.Length, sha256Hex, mediaType);
        await host.Client.SendMessageAsync(new Uri(usedEndpoint!), announcement.ToJson(), sessionId, cancellationToken);

        // ⑤ 本地留痕：公告消息
        await host.Store.AppendEventAsync(Core.Sessions.SessionEvent.Create(sessionId, identity.NodeId, SessionEventType.MessageSent,
            new { text = $"📄 已发送文件「{fileName}」（{fileInfo.Length} 字节）给对端", announcement = announcement.ToJson() },
            senderName: identity.DisplayName, senderIcon: identity.Icon), cancellationToken: cancellationToken);

        return uploaded;
    }

    /// <summary>
    /// 接收方登记：校验文件已在隔离区（推送已发生）、SHA256 与公告一致、来源与登记人一致，
    /// 然后追加 ArtifactReceived 事件。返回描述符供回执使用。
    /// </summary>
    public static async Task<(ArtifactDescriptor Descriptor, bool ShaValid)> RegisterReceivedAsync(
        A2AHost host, string senderNodeId, string sessionId, FileAnnouncement announcement, CancellationToken cancellationToken = default)
    {
        if (!host.ArtifactStore.TryGet(announcement.ArtifactId, out var descriptor) || descriptor is null)
        {
            throw new InvalidOperationException($"文件 {announcement.Name}（{announcement.ArtifactId}）尚未到达本机。");
        }
        if (!string.Equals(descriptor.CreatedBy, senderNodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("文件来源与公告方不一致，已拒绝登记。");
        }
        // 完整性复核：重新计算落盘文件的 SHA256。不符 → 记录失败事件并拒绝登记（文件保留在隔离区，
        // 不因伪造公告销毁已收到的内容）。
        await using var stream = await host.ArtifactStore.OpenReadAsync(announcement.ArtifactId, null, cancellationToken);
        var actual = Convert.ToHexString(SHA256.HashData(await ReadAllAsync(stream, cancellationToken))).ToLowerInvariant();
        var shaValid = string.Equals(actual, announcement.Sha256, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(actual, descriptor.Sha256, StringComparison.OrdinalIgnoreCase);

        var peer = await host.Store.GetPeerAsync(senderNodeId, cancellationToken);
        var identity = host.Identity.Identity;
        await host.Store.AppendEventAsync(SessionEvent.Create(sessionId, identity.NodeId, SessionEventType.ArtifactReceived,
            new
            {
                file = descriptor.Name,
                artifactId = descriptor.ArtifactId,
                size = descriptor.Size,
                sha256 = descriptor.Sha256,
                shaValid,
                from = senderNodeId
            },
            senderName: peer?.DisplayName ?? senderNodeId[..Math.Min(10, senderNodeId.Length)],
            senderIcon: peer?.Icon ?? "🤖"), cancellationToken: cancellationToken);

        if (!shaValid)
        {
            throw new InvalidOperationException("文件 SHA256 校验失败，已拒绝登记（文件保留在隔离区）。");
        }
        return (descriptor, true);
    }

    /// <summary>对端会话的查找/创建（发送路径使用）。</summary>
    public static async Task<string> GetOrCreatePeerSessionIdAsync(A2AHost host, string selfNodeId, string peerNodeId, CancellationToken cancellationToken = default)
    {
        var sessions = await host.Store.GetSessionsAsync(cancellationToken);
        var existing = sessions.FirstOrDefault(s => s.Kind == SessionKind.Peer && s.Members.Any(m => m.NodeId == peerNodeId));
        if (existing is not null) return existing.SessionId;
        var session = A2ASession.CreatePeer(Guid.CreateVersion7().ToString("N"), selfNodeId, peerNodeId);
        await host.Store.SaveSessionAsync(session, cancellationToken);
        return session.SessionId;
    }

    public static string GuessMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".html" => "text/html",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".zip" => "application/zip",
        ".gz" => "application/gzip",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream"
    };

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

}
