using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBrook.A2A.Core.Tasks;

public enum DistributedTaskState
{
    Draft,
    Offering,
    AwaitingAcceptances,
    Ready,
    Running,
    Paused,
    AwaitingDeliveries,
    Aggregating,
    Completed,
    PartialCompleted,
    Rejected,
    Expired,
    Withdrawn,
    Failed,
    CancelRequested,
    Cancelled,
    Orphaned
}

public sealed class TaskManifest
{
    public const string Schema = "io.agentbrook.distributed-task/v1";

    public string? TaskId { get; set; }
    public string? GroupId { get; set; }
    public string? Coordinator { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<ExpectedOutput> ExpectedOutputs { get; set; } = new();
    public List<string> RequiredCapabilities { get; set; } = new();
    public TaskConstraints Constraints { get; set; } = new();
    public Dictionary<string, string> EvaluationRubric { get; set; } = new();

    /// <summary>
    /// 版本化业务扩展（例如 io.agentbrook.business.weekly-summary-task）。
    /// CanonicalJson 覆盖该字段，因此 Manifest 签名同样保护扩展内容。
    /// </summary>
    public Dictionary<string, System.Text.Json.JsonElement>? Extensions { get; set; }

    public int ManifestVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Signature { get; set; }

    public string CanonicalJson() => JsonSerializer.Serialize(this);
}

public sealed class ExpectedOutput
{
    public string? Name { get; set; }
    public string? MediaType { get; set; }
    public bool Required { get; set; } = true;
}

public sealed class TaskConstraints
{
    public DateTimeOffset? Deadline { get; set; }
    public long MaxArtifactBytes { get; set; } = 100L * 1024 * 1024;
    public string DataClassification { get; set; } = "internal";
    public bool AllowSubdelegation { get; set; }
}

public sealed class TaskOffer
{
    public required string OfferId { get; init; }
    public required string TaskId { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public required TaskManifest Manifest { get; init; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Signature { get; set; }
}

public sealed class TaskAcceptance
{
    public required string TaskId { get; init; }
    public required string ParticipantNodeId { get; init; }
    public int AcceptedManifestVersion { get; init; }
    public DateTimeOffset AcceptedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Signature { get; set; }
}

public sealed class TaskDelivery
{
    public required string DeliveryId { get; init; }
    public required string TaskId { get; init; }
    public required string FromNodeId { get; init; }
    public List<Artifacts.ArtifactDescriptor> Artifacts { get; set; } = new();
    public string? Summary { get; set; }
    public List<string> KnownLimitations { get; set; } = new();
    public string? ContentHash { get; set; }
    public string? Signature { get; set; }
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
    public TaskDeliveryStatus Status { get; set; } = TaskDeliveryStatus.Submitted;
}

public enum TaskDeliveryStatus
{
    Submitted,
    Accepted,
    RevisionRequested,
    Rejected
}

public sealed class DistributedTask
{
    private static readonly Dictionary<DistributedTaskState, HashSet<DistributedTaskState>> Allowed = new()
    {
        [DistributedTaskState.Draft] = new() { DistributedTaskState.Offering, DistributedTaskState.Cancelled },
        [DistributedTaskState.Offering] = new() { DistributedTaskState.AwaitingAcceptances, DistributedTaskState.Cancelled, DistributedTaskState.Failed, DistributedTaskState.Expired },
        [DistributedTaskState.AwaitingAcceptances] = new() { DistributedTaskState.Ready, DistributedTaskState.Rejected, DistributedTaskState.Cancelled, DistributedTaskState.Expired },
        [DistributedTaskState.Ready] = new() { DistributedTaskState.Running, DistributedTaskState.Cancelled },
        [DistributedTaskState.Running] = new() { DistributedTaskState.Paused, DistributedTaskState.AwaitingDeliveries, DistributedTaskState.Failed, DistributedTaskState.CancelRequested, DistributedTaskState.Orphaned },
        [DistributedTaskState.Paused] = new() { DistributedTaskState.Running, DistributedTaskState.CancelRequested, DistributedTaskState.Failed },
        [DistributedTaskState.AwaitingDeliveries] = new() { DistributedTaskState.Aggregating, DistributedTaskState.PartialCompleted, DistributedTaskState.Failed, DistributedTaskState.CancelRequested },
        [DistributedTaskState.Aggregating] = new() { DistributedTaskState.Completed, DistributedTaskState.PartialCompleted, DistributedTaskState.Failed },
        [DistributedTaskState.CancelRequested] = new() { DistributedTaskState.Cancelled, DistributedTaskState.Failed },
        [DistributedTaskState.PartialCompleted] = new() { DistributedTaskState.Aggregating, DistributedTaskState.Failed },
        [DistributedTaskState.Completed] = new(),
        [DistributedTaskState.Rejected] = new(),
        [DistributedTaskState.Expired] = new(),
        [DistributedTaskState.Withdrawn] = new(),
        [DistributedTaskState.Failed] = new(),
        [DistributedTaskState.Cancelled] = new(),
        [DistributedTaskState.Orphaned] = new()
    };

    public required string TaskId { get; init; }
    public string? GroupId { get; set; }
    public required string CoordinatorNodeId { get; init; }
    public required TaskManifest Manifest { get; set; }
    [JsonInclude]
    public DistributedTaskState State { get; private set; } = DistributedTaskState.Draft;
    public List<TaskAcceptance> Acceptances { get; set; } = new();
    public List<TaskDelivery> Deliveries { get; set; } = new();
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static bool IsTerminal(DistributedTaskState state) => state is
        DistributedTaskState.Completed or
        DistributedTaskState.Rejected or
        DistributedTaskState.Expired or
        DistributedTaskState.Withdrawn or
        DistributedTaskState.Failed or
        DistributedTaskState.Cancelled or
        DistributedTaskState.Orphaned;

    public static bool CanTransition(DistributedTaskState from, DistributedTaskState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public void TransitionTo(DistributedTaskState next)
    {
        if (!CanTransition(State, next))
        {
            throw new InvalidOperationException($"Illegal task transition {State} -> {next} for task {TaskId}.");
        }
        State = next;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ForceState(DistributedTaskState state) => State = state;
}
