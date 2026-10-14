using AgentBrook.A2A.Core.Artifacts;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Reputation;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Core.Tasks;

namespace AgentBrook.A2A.Transport.Protocol;

public sealed class ProfileDto
{
    public string Schema { get; set; } = PeerProfile.Schema;
    public string? NodeId { get; set; }
    public string? DisplayName { get; set; }
    public string? Icon { get; set; }
    public string? AgentVersion { get; set; }
    public string? Platform { get; set; }
    public List<ModelCapability> Models { get; set; } = new();
    public DeviceCapability? Device { get; set; }
    public List<SkillDescriptor> Skills { get; set; } = new();
    public PeerLimits Limits { get; set; } = new();
    public ReputationSummary? ReputationSummary { get; set; }
    public long ProfileVersion { get; set; }

    public PeerProfile ToCore() => new()
    {
        NodeId = NodeId,
        DisplayName = DisplayName,
        Icon = Icon,
        AgentVersion = AgentVersion,
        Platform = Platform,
        Models = Models,
        Device = Device,
        Skills = Skills,
        Limits = Limits,
        ReputationSummary = ReputationSummary,
        ProfileVersion = ProfileVersion
    };

    public static ProfileDto FromCore(PeerProfile profile) => new()
    {
        NodeId = profile.NodeId,
        DisplayName = profile.DisplayName,
        Icon = profile.Icon,
        AgentVersion = profile.AgentVersion,
        Platform = profile.Platform,
        Models = profile.Models,
        Device = profile.Device,
        Skills = profile.Skills,
        Limits = profile.Limits,
        ReputationSummary = profile.ReputationSummary,
        ProfileVersion = profile.ProfileVersion
    };
}

public sealed class PresenceDto
{
    public string Schema { get; set; } = PresenceSnapshot.Schema;
    public string? NodeId { get; set; }
    public string? InstanceId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public PresenceAvailability Availability { get; set; } = PresenceAvailability.Online;
    public WorkState WorkState { get; set; } = WorkState.Idle;
    public AgentReadiness AgentState { get; set; } = AgentReadiness.Starting;
    public MoodState? Mood { get; set; }
    public PresenceLoad Load { get; set; } = new();
    public int ActiveGroupCount { get; set; }
    public long ProfileVersion { get; set; }

    public PresenceSnapshot ToCore() => new()
    {
        NodeId = NodeId,
        InstanceId = InstanceId,
        Sequence = Sequence,
        ObservedAt = ObservedAt,
        Availability = Availability,
        WorkState = WorkState,
        AgentState = AgentState,
        Mood = Mood,
        Load = Load,
        ActiveGroupCount = ActiveGroupCount,
        ProfileVersion = ProfileVersion
    };

    public static PresenceDto FromCore(PresenceSnapshot snapshot) => new()
    {
        NodeId = snapshot.NodeId,
        InstanceId = snapshot.InstanceId,
        Sequence = snapshot.Sequence,
        ObservedAt = snapshot.ObservedAt,
        Availability = snapshot.Availability,
        WorkState = snapshot.WorkState,
        AgentState = snapshot.AgentState,
        Mood = snapshot.Mood,
        Load = snapshot.Load,
        ActiveGroupCount = snapshot.ActiveGroupCount,
        ProfileVersion = snapshot.ProfileVersion
    };
}

/// <summary>群组元数据快照：成员端补拉用（成员表/群名/状态不随事件走，需要独立同步端点）。</summary>
public sealed class SessionDto
{
    public string? SessionId { get; set; }
    public SessionKind Kind { get; set; }
    public string? Name { get; set; }
    public string? CreatedBy { get; set; }
    public string? CoordinatorNodeId { get; set; }
    public List<SessionMemberDto> Members { get; set; } = new();
    public SessionStatus Status { get; set; }
    public long LastSequence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static SessionDto FromCore(A2ASession session) => new()
    {
        SessionId = session.SessionId,
        Kind = session.Kind,
        Name = session.Name,
        CreatedBy = session.CreatedBy,
        CoordinatorNodeId = session.CoordinatorNodeId,
        Members = session.Members.Select(m => new SessionMemberDto
        {
            NodeId = m.NodeId,
            Role = m.Role,
            JoinState = m.JoinState.ToString()
        }).ToList(),
        Status = session.Status,
        LastSequence = session.LastSequence,
        UpdatedAt = session.UpdatedAt
    };
}

public sealed class SessionMemberDto
{
    public string? NodeId { get; set; }
    public string? Role { get; set; }
    public string? JoinState { get; set; }
}

public sealed class SessionEventDto
{
    public string? EventId { get; set; }
    public string? SessionId { get; set; }
    public string? SenderNodeId { get; set; }
    public string? SenderName { get; set; }
    public string? SenderIcon { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public long Sequence { get; set; }
    public SessionEventType Type { get; set; }
    public string? PayloadJson { get; set; }
    public string? PrevHash { get; set; }
    public string? Signature { get; set; }

    public SessionEvent ToCore() => new()
    {
        EventId = EventId ?? SessionEvent.NewEventId(),
        SessionId = SessionId ?? "",
        SenderNodeId = SenderNodeId ?? "",
        SenderName = SenderName,
        SenderIcon = SenderIcon,
        Timestamp = Timestamp,
        Sequence = Sequence,
        Type = Type,
        PayloadJson = PayloadJson,
        PrevHash = PrevHash,
        Signature = Signature
    };

    public static SessionEventDto FromCore(SessionEvent entry) => new()
    {
        EventId = entry.EventId,
        SessionId = entry.SessionId,
        SenderNodeId = entry.SenderNodeId,
        SenderName = entry.SenderName,
        SenderIcon = entry.SenderIcon,
        Timestamp = entry.Timestamp,
        Sequence = entry.Sequence,
        Type = entry.Type,
        PayloadJson = entry.PayloadJson,
        PrevHash = entry.PrevHash,
        Signature = entry.Signature
    };
}

public sealed class TaskManifestDto
{
    public string? TaskId { get; set; }
    public string? GroupId { get; set; }
    public string? Coordinator { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<ExpectedOutput> ExpectedOutputs { get; set; } = new();
    public List<string> RequiredCapabilities { get; set; } = new();
    public TaskConstraints Constraints { get; set; } = new();
    public Dictionary<string, string> EvaluationRubric { get; set; } = new();
    public Dictionary<string, System.Text.Json.JsonElement>? Extensions { get; set; }
    public int ManifestVersion { get; set; } = 1;

    public TaskManifest ToCore() => new()
    {
        TaskId = TaskId,
        GroupId = GroupId,
        Coordinator = Coordinator,
        Title = Title,
        Description = Description,
        ExpectedOutputs = ExpectedOutputs,
        RequiredCapabilities = RequiredCapabilities,
        Constraints = Constraints,
        EvaluationRubric = EvaluationRubric,
        Extensions = Extensions,
        ManifestVersion = ManifestVersion
    };
}

public sealed class RatingDto
{
    public string? RatingId { get; set; }
    public string? TaskId { get; set; }
    public string? FromNodeId { get; set; }
    public string? ToNodeId { get; set; }
    public string? DeliveryHash { get; set; }
    public RatingScores Scores { get; set; } = new();
    public string Outcome { get; set; } = "accepted";
    public string? Comment { get; set; }
    public int Revision { get; set; } = 1;
    public string? Signature { get; set; }

    public RatingCredential ToCore() => new()
    {
        RatingId = RatingId ?? Guid.CreateVersion7().ToString("N"),
        TaskId = TaskId ?? "",
        FromNodeId = FromNodeId ?? "",
        ToNodeId = ToNodeId ?? "",
        DeliveryHash = DeliveryHash,
        Scores = Scores,
        Outcome = Outcome,
        Comment = Comment,
        Revision = Revision,
        Signature = Signature
    };

    public static RatingDto FromCore(RatingCredential rating) => new()
    {
        RatingId = rating.RatingId,
        TaskId = rating.TaskId,
        FromNodeId = rating.FromNodeId,
        ToNodeId = rating.ToNodeId,
        DeliveryHash = rating.DeliveryHash,
        Scores = rating.Scores,
        Outcome = rating.Outcome,
        Comment = rating.Comment,
        Revision = rating.Revision,
        Signature = rating.Signature
    };
}

public sealed class ArtifactOfferDto
{
    public string? ArtifactId { get; set; }
    public string? Name { get; set; }
    public string? MediaType { get; set; }
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public string? CreatedBy { get; set; }
    public string? TaskId { get; set; }
    public string? SessionId { get; set; }
    public bool RangeSupported { get; set; } = true;

    public ArtifactDescriptor ToDescriptor() => new()
    {
        ArtifactId = ArtifactId ?? Guid.CreateVersion7().ToString("N"),
        Name = ArtifactStore.SanitizeFileName(Name ?? "artifact.bin"),
        MediaType = MediaType,
        Size = Size,
        Sha256 = Sha256 ?? "",
        CreatedBy = CreatedBy,
        TaskId = TaskId,
        SessionId = SessionId,
        Transfer = new ArtifactTransfer { Mode = "pull", RangeSupported = RangeSupported }
    };
}

public sealed class PairingRequestDto
{
    public string? InitiatorNodeId { get; set; }
    public string? InitiatorDisplayName { get; set; }
    public string? InitiatorIcon { get; set; }
    public string? InitiatorPublicKeyBase64 { get; set; }
    public string? InitiatorEndpoint { get; set; }
    public string? Challenge { get; set; }
    public string? ChallengeSignature { get; set; }
}

public sealed class PairingResponseDto
{
    public string? ResponderNodeId { get; set; }
    public string? ResponderDisplayName { get; set; }
    public string? ResponderIcon { get; set; }
    public string? ResponderPublicKeyBase64 { get; set; }
    public string? Challenge { get; set; }
    public string? ChallengeSignature { get; set; }
    public string? ShortCode { get; set; }
}

public sealed class PairingConfirmDto
{
    public string? InitiatorNodeId { get; set; }
    public string? ResponderNodeId { get; set; }
    public string? Challenge { get; set; }
    public string? ConfirmationSignature { get; set; }
    public string? ShortCode { get; set; }
}
