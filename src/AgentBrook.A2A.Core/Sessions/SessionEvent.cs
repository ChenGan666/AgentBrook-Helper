using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBrook.A2A.Core.Sessions;

public enum SessionEventType
{
    MessageSent,
    MessageReceived,
    ArtifactOffered,
    ArtifactAccepted,
    ArtifactReceived,
    TaskOffered,
    TaskAccepted,
    TaskStatusChanged,
    TaskCompleted,
    RatingIssued,
    MemberPresenceChanged,
    GroupCreated,
    MemberInvited,
    MemberJoined,
    MemberRejected,
    MemberLeft,
    MessagePosted,
    ProgressReported,
    ArtifactPublished,
    AssignmentChanged,
    RevisionRequested,
    TaskStateChanged,
    RatingPublished,
    GroupClosed,
    GroupRenamed,
    ContextCompressed
}

public sealed class SessionEvent
{
    public required string EventId { get; init; }
    public required string SessionId { get; init; }
    public required string SenderNodeId { get; init; }

    /// <summary>发送方 Helper 显示名称，供对端会话流直接渲染，不依赖 Profile 在线拉取。</summary>
    public string? SenderName { get; set; }

    /// <summary>发送方 Helper 图标（emoji 或图标键），与 SenderName 一同在对话中展示发送主体。</summary>
    public string? SenderIcon { get; set; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public long Sequence { get; set; }
    public SessionEventType Type { get; init; }
    public string? PayloadJson { get; set; }
    public string? PrevHash { get; set; }
    public string? Signature { get; set; }

    public string ComputeHash()
    {
        var canonical = CanonicalString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public byte[] SigningPayload() => Encoding.UTF8.GetBytes(CanonicalString());

    private string CanonicalString() =>
        string.Join("|", EventId, SessionId, SenderNodeId, SenderName ?? "", SenderIcon ?? "",
            Timestamp.ToUnixTimeMilliseconds(), Sequence, (int)Type, PayloadJson ?? "", PrevHash ?? "");

    public static SessionEvent Create(string sessionId, string senderNodeId, SessionEventType type, object? payload = null,
        string? senderName = null, string? senderIcon = null) =>
        new()
        {
            EventId = NewEventId(),
            SessionId = sessionId,
            SenderNodeId = senderNodeId,
            SenderName = senderName,
            SenderIcon = senderIcon,
            Type = type,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload)
        };

    public static string NewEventId() => Guid.CreateVersion7().ToString("N");
}
