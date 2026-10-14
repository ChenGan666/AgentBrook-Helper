namespace AgentBrook.A2A.Core.Sessions;

public enum SessionKind
{
    Peer,
    Group
}

public enum SessionStatus
{
    Active,
    Forming,
    Paused,
    Closing,
    Archived,
    Closed
}

public enum MemberJoinState
{
    Invited,
    Joined,
    Rejected,
    Left,
    Removed
}

public sealed class SessionMember
{
    public required string NodeId { get; init; }
    public string Role { get; set; } = "participant";
    public MemberJoinState JoinState { get; set; } = MemberJoinState.Invited;
    public List<string> Permissions { get; set; } = new();
}

public sealed class A2ASession
{
    public required string SessionId { get; init; }
    public required SessionKind Kind { get; init; }
    public string? ContextId { get; set; }
    public string? LinkedTaskId { get; set; }
    public required string CreatedBy { get; init; }
    public string? CoordinatorNodeId { get; set; }
    public List<SessionMember> Members { get; set; } = new();
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public long LastSequence { get; set; }
    public long GroupKeyEpoch { get; set; } = 1;

    /// <summary>群组显示名：创建时按成员名自动生成，用户可手动修改（Peer 会话不使用）。</summary>
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static A2ASession CreatePeer(string sessionId, string createdBy, string peerNodeId) =>
        new()
        {
            SessionId = sessionId,
            Kind = SessionKind.Peer,
            CreatedBy = createdBy,
            Members =
            {
                new SessionMember { NodeId = createdBy, Role = "owner", JoinState = MemberJoinState.Joined },
                new SessionMember { NodeId = peerNodeId, Role = "peer", JoinState = MemberJoinState.Joined }
            }
        };

    public static A2ASession CreateGroup(string groupId, string coordinatorNodeId, string? linkedTaskId = null) =>
        new()
        {
            SessionId = groupId,
            Kind = SessionKind.Group,
            CreatedBy = coordinatorNodeId,
            CoordinatorNodeId = coordinatorNodeId,
            LinkedTaskId = linkedTaskId,
            Status = SessionStatus.Forming,
            Members = { new SessionMember { NodeId = coordinatorNodeId, Role = "coordinator", JoinState = MemberJoinState.Joined } }
        };
}
