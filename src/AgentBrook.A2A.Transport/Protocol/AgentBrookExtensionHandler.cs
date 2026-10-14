using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBrook.A2A.Core.Artifacts;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Core.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace AgentBrook.A2A.Transport.Protocol;

public sealed class AgentBrookExtensionHandler
{
    private readonly NodeIdentityService _identity;
    private readonly A2AStore _store;
    private readonly ArtifactStore _artifactStore;
    private readonly Pairing.PairingService _pairing;
    private readonly Hosting.A2AHost _host;
    private readonly ConcurrentDictionary<string, ArtifactOfferDto> _artifactOffers = new();

    public AgentBrookExtensionHandler(NodeIdentityService identity, A2AStore store, ArtifactStore artifactStore,
        Pairing.PairingService pairing, Hosting.A2AHost host)
    {
        _identity = identity;
        _store = store;
        _artifactStore = artifactStore;
        _pairing = pairing;
        _host = host;
    }

    public async Task<List<SessionEventDto>> ReadEventsAsync(string sessionId, long afterSequence, CancellationToken cancellationToken)
    {
        var events = await _store.ReadEventsAsync(sessionId, afterSequence, cancellationToken);
        return events.Select(SessionEventDto.FromCore).ToList();
    }

    public async Task<List<SessionEventDto>> AppendRemoteEventsAsync(string sessionId, List<SessionEventDto> events, CancellationToken cancellationToken)
    {
        var result = new List<SessionEventDto>();
        foreach (var dto in events)
        {
            var entry = dto.ToCore();
            var appended = await _store.AppendEventAsync(entry, assignLocalSequence: false, cancellationToken);
            result.Add(SessionEventDto.FromCore(appended));
        }
        return result;
    }

    public async Task<List<SessionEventDto>> ReadGroupEventsAsync(string groupId, long afterSequence, CancellationToken cancellationToken)
    {
        return await ReadEventsAsync(groupId, afterSequence, cancellationToken);
    }

    public async Task<List<SessionEventDto>> AppendRemoteGroupEventsAsync(string groupId, List<SessionEventDto> events, CancellationToken cancellationToken)
    {
        var result = new List<SessionEventDto>();
        var coreEvents = new List<SessionEvent>();
        var session = await _store.GetSessionAsync(groupId, cancellationToken);
        // 星型拓扑（§12.3）：成员发言以 Sequence=0 提交，只有协调者有权分配全局 sequence；带序号的事件按原样落库
        var isCoordinator = session is not null && session.CoordinatorNodeId == _identity.Identity.NodeId;
        foreach (var dto in events)
        {
            var entry = dto.ToCore();
            if (entry.Sequence <= 0)
            {
                if (!isCoordinator)
                {
                    continue;
                }
                var assigned = await _store.AppendEventAsync(entry, assignLocalSequence: true, cancellationToken);
                result.Add(SessionEventDto.FromCore(assigned));
                coreEvents.Add(assigned);
            }
            else
            {
                var appended = await _store.AppendEventAsync(entry, assignLocalSequence: false, cancellationToken);
                result.Add(SessionEventDto.FromCore(appended));
                coreEvents.Add(appended);
            }
        }
        // 群组元数据自愈：本端缺群记录时按 GroupCreated 重建（否则成员端会话列表永远看不到该群），
        // 并跟随 GroupRenamed/GroupClosed/MemberLeft/MemberJoined/MemberRejected
        await GroupSessionSync.ApplyAsync(_store, _identity, coreEvents, cancellationToken);
        // 协调者联动（群自动交流等）：fire-and-forget，不阻塞对端响应
        if (coreEvents.Count > 0)
        {
            var ingested = coreEvents.ToList();
            _ = Task.Run(() => _host.RaiseGroupEventsIngested(groupId, ingested), cancellationToken);
        }
        return result;
    }

    /// <summary>群组元数据读取：成员端补拉用（成员表/群名/状态不随事件走，需要独立同步）。</summary>
    public async Task<IResult> GetGroupSessionAsync(string groupId, CancellationToken cancellationToken)
    {
        var session = await _store.GetSessionAsync(groupId, cancellationToken);
        return session is null ? Results.NotFound() : Results.Json(SessionDto.FromCore(session));
    }

    /// <summary>群组发现（离线补齐，§12.4）：返回指定节点作为成员的所有活跃群组。
    /// 建群时离线而错过 GroupCreated 推送的成员，上线后据此发现群组并落地本地会话。</summary>
    public async Task<List<SessionDto>> GetMemberGroupSessionsAsync(string nodeId, CancellationToken cancellationToken)
    {
        var sessions = await _store.GetSessionsAsync(cancellationToken);
        return sessions
            .Where(x => x.Kind == SessionKind.Group
                && x.Status is SessionStatus.Active or SessionStatus.Forming
                && x.Members.Any(m => m.NodeId == nodeId
                    && m.JoinState is MemberJoinState.Invited or MemberJoinState.Joined))
            .Select(SessionDto.FromCore)
            .ToList();
    }

    public async Task<string> CreateLocalTaskAsync(TaskManifestDto dto, CancellationToken cancellationToken)
    {
        var taskId = dto.TaskId ?? Guid.CreateVersion7().ToString("N");
        var task = new DistributedTask
        {
            TaskId = taskId,
            GroupId = dto.GroupId,
            CoordinatorNodeId = string.IsNullOrWhiteSpace(dto.Coordinator) ? _identity.Identity.NodeId : dto.Coordinator,
            Manifest = dto.ToCore()
        };
        task.Manifest.TaskId = taskId;
        await _store.SaveTaskAsync(task, cancellationToken);
        task.TransitionTo(DistributedTaskState.Offering);
        task.TransitionTo(DistributedTaskState.AwaitingAcceptances);
        await _store.SaveTaskAsync(task, cancellationToken);
        return taskId;
    }

    public async Task<DistributedTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        return await _store.GetTaskAsync(taskId, cancellationToken);
    }

    public async Task AcceptTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await _store.GetTaskAsync(taskId, cancellationToken);
        if (task is null)
        {
            throw new KeyNotFoundException($"Task {taskId} not found.");
        }
        task.TransitionTo(DistributedTaskState.Ready);
        await _store.SaveTaskAsync(task, cancellationToken);
    }

    /// <summary>对端通知解除配对：发起通知的节点（已验签）在本端置为 Revoked。</summary>
    public async Task RevokePairingByPeerAsync(string revokerNodeId, CancellationToken cancellationToken)
    {
        await _pairing.RevokeByPeerAsync(revokerNodeId, cancellationToken);
    }

    public async Task SaveRatingAsync(RatingDto dto, CancellationToken cancellationToken)
    {
        var rating = dto.ToCore();
        await _store.SaveRatingAsync(rating, cancellationToken);
    }

    public Task<ArtifactDescriptor> CreateArtifactOfferAsync(ArtifactOfferDto dto, CancellationToken cancellationToken)
    {
        var descriptor = dto.ToDescriptor();
        dto.ArtifactId = descriptor.ArtifactId;
        dto.Name = descriptor.Name;
        _artifactOffers[descriptor.ArtifactId] = dto;
        return Task.FromResult(descriptor);
    }

    public async Task<ArtifactDescriptor> UploadArtifactAsync(string artifactId, IFormFile file, CancellationToken cancellationToken)
    {
        if (!_artifactOffers.TryRemove(artifactId, out var offer))
        {
            throw new KeyNotFoundException($"Artifact offer {artifactId} not found.");
        }
        if (offer.Size > 0 && file.Length != offer.Size)
        {
            throw new InvalidOperationException("Artifact size does not match the accepted offer.");
        }
        var metadata = new ArtifactMetadata
        {
            Name = offer.Name ?? file.FileName,
            MediaType = offer.MediaType ?? file.ContentType,
            CreatedBy = offer.CreatedBy,
            TaskId = offer.TaskId,
            SessionId = offer.SessionId
        };
        await using var stream = file.OpenReadStream();
        var stored = await _artifactStore.PutAsync(stream, metadata, cancellationToken);
        if (!string.IsNullOrWhiteSpace(offer.Sha256) &&
            !string.Equals(offer.Sha256, stored.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await _artifactStore.DeleteAsync(stored.ArtifactId, cancellationToken);
            throw new InvalidOperationException("Artifact hash does not match the accepted offer.");
        }
        return stored;
    }

    public async Task<IResult> DownloadArtifactAsync(string artifactId, long? offset, long? length, CancellationToken cancellationToken)
    {
        if (!_artifactStore.TryGet(artifactId, out var descriptor))
        {
            return Results.NotFound();
        }
        ByteRange? range = offset.HasValue ? new ByteRange(offset.Value, length) : null;
        var stream = await _artifactStore.OpenReadAsync(artifactId, range, cancellationToken);
        var result = Results.Stream(stream, descriptor!.MediaType ?? "application/octet-stream", descriptor.Name, enableRangeProcessing: true);
        return result;
    }
}
