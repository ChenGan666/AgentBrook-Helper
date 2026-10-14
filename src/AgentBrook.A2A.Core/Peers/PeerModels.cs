namespace AgentBrook.A2A.Core.Peers;

public enum PeerTrustState
{
    Discovered,
    PairingPending,
    Trusted,
    Blocked,
    Revoked
}

public enum PresenceAvailability
{
    Online,
    Away,
    Draining,
    Offline
}

public enum WorkState
{
    Idle,
    Busy,
    AwaitingUser,
    Unavailable
}

public enum AgentReadiness
{
    Starting,
    Ready,
    Degraded,
    Error
}

public sealed class PeerNode
{
    public required string NodeId { get; init; }
    public string? DisplayName { get; set; }
    public string? Icon { get; set; }
    public PeerTrustState TrustState { get; set; } = PeerTrustState.Discovered;
    public string? PublicKeyBase64 { get; set; }
    public string? ProfileJson { get; set; }
    public long ProfileVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public List<string> Endpoints { get; set; } = new();
}

public sealed class PeerProfile
{
    public const string Schema = "io.agentbrook.profile/v1";

    public string? NodeId { get; set; }
    public string? DisplayName { get; set; }
    public string? Icon { get; set; }
    public string? AgentVersion { get; set; }
    public string? Platform { get; set; }
    public List<ModelCapability> Models { get; set; } = new();
    public DeviceCapability? Device { get; set; }
    public List<SkillDescriptor> Skills { get; set; } = new();
    public PeerLimits Limits { get; set; } = new();
    public Reputation.ReputationSummary? ReputationSummary { get; set; }
    public long ProfileVersion { get; set; }
}

public sealed class ModelCapability
{
    public string? Id { get; set; }
    public List<string> Modalities { get; set; } = new();
    public List<string> Roles { get; set; } = new();
}

public sealed class DeviceCapability
{
    public string? CpuClass { get; set; }
    public int? LogicalCores { get; set; }
    public int? MemoryGiB { get; set; }
    public string? GpuClass { get; set; }
    public int? FreeDiskGiB { get; set; }
}

public sealed class SkillDescriptor
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed class PeerLimits
{
    public int MaxConcurrentTasks { get; set; } = 1;
    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;
}

public sealed class PresenceSnapshot
{
    public const string Schema = "io.agentbrook.presence/v1";

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
}

public sealed class MoodState
{
    public string Code { get; set; } = "neutral";
    public string? Label { get; set; }
    public string Source { get; set; } = "user";
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class PresenceLoad
{
    public int RunningTasks { get; set; }
    public int QueuedTasks { get; set; }
    public int Capacity { get; set; } = 1;
}

public sealed class PeerSnapshot
{
    public required PeerNode Node { get; init; }
    public PresenceSnapshot? Presence { get; set; }
    public bool IsStale { get; set; }
    public bool IsOnline => Presence?.Availability is PresenceAvailability.Online or PresenceAvailability.Away;
}
