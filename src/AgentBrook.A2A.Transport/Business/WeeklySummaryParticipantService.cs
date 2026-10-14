using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBrook.A2A.Core.Business;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Core.Tasks;
using AgentBrook.A2A.Transport.Hosting;
using AgentBrook.A2A.Transport.Protocol;

namespace AgentBrook.A2A.Transport.Business;

public sealed record WeeklyDeliveryReceipt(bool CoordinatorAccepted, string FeedbackText, List<string> MissingSections, string ArtifactId, string ArtifactName);

/// <summary>
/// §26.2 场景二 · 业务员 Helper 侧（Participant）：收到主管 TaskOffer 后展示审批信息，
/// 经本地用户同意后接受任务，按 Manifest 允许范围生成周报 Markdown，
/// 以 Artifact + 群组事件交付给主管，并处理其结构化修订请求后重新提交。
/// </summary>
public sealed class WeeklySummaryParticipantService
{
    private readonly A2AHost _host;
    private readonly Dictionary<string, int> _deliveryAttempts = new();

    public WeeklySummaryParticipantService(A2AHost host)
    {
        _host = host;
    }

    /// <summary>本地用户同意后的接受动作：任务置为 Ready，并向主管 Group 推送带名称/图标的签名接受事件。</summary>
    public async Task AcceptAsync(string taskId, string coordinatorEndpoint, CancellationToken cancellationToken = default)
    {
        var task = await _host.Store.GetTaskAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} not found on participant.");
        if (task.State == DistributedTaskState.AwaitingAcceptances)
        {
            await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.Ready, cancellationToken);
        }
        var groupId = task.GroupId ?? throw new InvalidOperationException("Task has no group id.");
        await AppendAndPushSignedEventAsync(groupId, coordinatorEndpoint, SessionEventType.TaskAccepted,
            new { taskId, acceptedManifestVersion = task.Manifest.ManifestVersion }, cancellationToken);
    }

    /// <summary>本地用户拒绝：任务置为 Rejected，并向主管推送带理由的签名事件。</summary>
    public async Task RejectAsync(string taskId, string coordinatorEndpoint, string reason, CancellationToken cancellationToken = default)
    {
        var task = await _host.Store.GetTaskAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} not found on participant.");
        if (task.State is DistributedTaskState.AwaitingAcceptances or DistributedTaskState.Ready)
        {
            await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.Rejected, cancellationToken);
        }
        var groupId = task.GroupId ?? throw new InvalidOperationException("Task has no group id.");
        await AppendAndPushSignedEventAsync(groupId, coordinatorEndpoint, SessionEventType.MemberRejected,
            new { taskId, reason }, cancellationToken);
    }

    /// <summary>
    /// 生成并交付周报：contentProvider 模拟"读取本周工作记录并生成结构化总结"的本地技能；
    /// 主管要求修订时 missingSections 非空，应据此补齐后再次调用。
    /// </summary>
    public async Task<WeeklyDeliveryReceipt> DeliverAsync(
        string taskId,
        string coordinatorEndpoint,
        Func<WeeklySummaryTaskSpec, string> contentProvider,
        CancellationToken cancellationToken = default)
    {
        var task = await _host.Store.GetTaskAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} not found on participant.");
        var spec = WeeklySummaryTaskSpec.TryReadFrom(task.Manifest)
            ?? throw new InvalidOperationException("Task manifest has no weekly-summary extension.");
        var groupId = task.GroupId ?? throw new InvalidOperationException("Task has no group id.");
        var identity = _host.Identity.Identity;

        _deliveryAttempts.TryGetValue(taskId, out var attempt);
        var revision = attempt + 1;
        _deliveryAttempts[taskId] = revision;
        var content = contentProvider(spec);
        var artifactName = revision == 1
            ? spec.ExpectedOutputName
            : Path.GetFileNameWithoutExtension(spec.ExpectedOutputName) + $"-rev{revision}" + Path.GetExtension(spec.ExpectedOutputName);

        var bytes = Encoding.UTF8.GetBytes(content);
        var offer = await _host.Client.CreateArtifactOfferAsync(new Uri(coordinatorEndpoint), new ArtifactOfferDto
        {
            Name = artifactName,
            MediaType = "text/markdown",
            Size = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            CreatedBy = identity.NodeId,
            TaskId = taskId,
            SessionId = groupId
        }, cancellationToken);
        var stored = await _host.Client.UploadArtifactAsync(new Uri(coordinatorEndpoint), offer.ArtifactId, new MemoryStream(bytes), "text/markdown", cancellationToken);

        var published = await AppendAndPushSignedEventAsync(groupId, coordinatorEndpoint, SessionEventType.ArtifactPublished,
            new { taskId, artifactId = stored.ArtifactId, name = stored.Name, sha256 = stored.Sha256, revision }, cancellationToken);

        // 本地记录交付事件，业务员 Helper 的会话流可见自己的提交。
        await _host.Store.AppendEventAsync(SessionEvent.Create(groupId, identity.NodeId, SessionEventType.TaskStatusChanged,
            new { taskId, status = "Delivered", artifactId = stored.ArtifactId, revision },
            senderName: identity.DisplayName, senderIcon: identity.Icon), cancellationToken: cancellationToken);

        return new WeeklyDeliveryReceipt(true, "已提交，等待主管校验。", new List<string>(), stored.ArtifactId, stored.Name);
    }

    /// <summary>解析主管 RevisionRequested 事件中要求的缺失章节（供用户/Agent 补齐内容）。</summary>
    public static List<string> ParseMissingSections(string revisionEventPayloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(revisionEventPayloadJson);
            if (document.RootElement.TryGetProperty("missingSections", out var sections))
            {
                return sections.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            }
        }
        catch (JsonException)
        {
        }
        return new List<string>();
    }

    private async Task<SessionEvent> AppendAndPushSignedEventAsync(
        string groupId, string coordinatorEndpoint, SessionEventType type, object payload, CancellationToken cancellationToken)
    {
        var identity = _host.Identity.Identity;
        var entry = SessionEvent.Create(groupId, identity.NodeId, type, payload,
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(entry, cancellationToken: cancellationToken);

        // 远端 sequence 由主管统计得出；事件带参与者签名，主管可验证发送主体。
        var remoteCount = (await _host.Client.ReadGroupEventsAsync(new Uri(coordinatorEndpoint), groupId, cancellationToken: cancellationToken)).Count;
        var copy = new SessionEvent
        {
            EventId = entry.EventId,
            SessionId = entry.SessionId,
            SenderNodeId = entry.SenderNodeId,
            SenderName = entry.SenderName,
            SenderIcon = entry.SenderIcon,
            Timestamp = entry.Timestamp,
            Type = entry.Type,
            PayloadJson = entry.PayloadJson,
            PrevHash = entry.PrevHash
        };
        copy.Sequence = remoteCount + 1;
        copy.Signature = Convert.ToBase64String(_host.Identity.Sign(copy.SigningPayload()));
        await _host.Client.AppendGroupEventsAsync(new Uri(coordinatorEndpoint), groupId, [SessionEventDto.FromCore(copy)], cancellationToken);
        return entry;
    }
}
