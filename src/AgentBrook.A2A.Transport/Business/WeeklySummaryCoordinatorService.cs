using System.Text;
using System.Text.Json;
using AgentBrook.A2A.Core.Artifacts;
using AgentBrook.A2A.Core.Business;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Reputation;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Core.Tasks;
using AgentBrook.A2A.Transport.Hosting;
using AgentBrook.A2A.Transport.Protocol;

namespace AgentBrook.A2A.Transport.Business;

public sealed record WeeklyDistributionResult(string TaskId, string GroupId, List<string> OfferedNodeIds);

public sealed record WeeklyDeliveryDecision(bool Accepted, List<string> MissingSections, string FeedbackText);

public sealed record WeeklyAggregationResult(DepartmentWeeklyReport Report, string? ArtifactId);

/// <summary>
/// §26.2 场景二 · 主管 Helper 侧（Coordinator）：从部门目录解析全部业务员，
/// 创建签名 Task Manifest 与 Group Session，逐一下发 TaskOffer，
/// 跟踪成员状态，校验交付章节、发起结构化修订，截止后生成部门周报并对成功交付者签发三维评价。
/// </summary>
public sealed class WeeklySummaryCoordinatorService
{
    private readonly A2AHost _host;
    private readonly DepartmentDirectory _directory;

    // 成员跟踪状态（Offered/Accepted/Delivered/...）。演示期内与 Group 事件同库同源：
    // 事件本身已持久化，可随时据此重建。
    private sealed class MemberTrack
    {
        public string NodeId = "";
        public string DisplayName = "";
        public string Icon = "🧑‍💻";
        public string Endpoint = "";
        public WeeklyMemberStatus Status = WeeklyMemberStatus.Pending;
        public int Revisions;
        public string? ArtifactId;
        public string? SummaryContent;
        public string? RejectionReason;
    }

    private readonly Dictionary<string, MemberTrack> _members = new();
    private readonly Dictionary<string, (string GroupId, WeeklySummaryTaskSpec Spec, string TaskId)> _tasks = new();

    public WeeklySummaryCoordinatorService(A2AHost host, DepartmentDirectory directory)
    {
        _host = host;
        _directory = directory;
    }

    public IReadOnlyCollection<string> TrackedTaskIds => _tasks.Keys.ToList().AsReadOnly();

    public async Task<WeeklyDistributionResult> DistributeAsync(
        WeeklySummaryTaskSpec spec,
        IReadOnlyList<string>? excludeNodeIds = null,
        CancellationToken cancellationToken = default)
    {
        var identity = _host.Identity.Identity;
        var reps = _directory.SalesRepresentatives()
            .Where(x => excludeNodeIds is null || !excludeNodeIds.Contains(x.NodeId))
            .ToList();
        if (reps.Count == 0)
        {
            throw new InvalidOperationException("部门目录中没有可邀请的 sales-representative 成员。");
        }

        var taskId = Guid.CreateVersion7().ToString("N");
        var groupId = Guid.CreateVersion7().ToString("N");
        var manifest = new TaskManifest
        {
            TaskId = taskId,
            GroupId = groupId,
            Coordinator = identity.NodeId,
            Title = spec.Title,
            Description = $"部门 {_directory.DepartmentId} 周总结任务（{spec.StartDate} ~ {spec.EndDate}），提交对象：{identity.DisplayName}",
            ExpectedOutputs = [new ExpectedOutput { Name = spec.ExpectedOutputName, MediaType = "text/markdown", Required = true }],
            EvaluationRubric = new Dictionary<string, string>
            {
                ["quality"] = "章节完整、数据准确、符合模板",
                ["efficiency"] = "在截止时间内交付",
                ["cooperation"] = "响应提醒并配合修订"
            },
            Constraints = new TaskConstraints { Deadline = spec.Deadline }
        };
        WeeklySummaryTaskSpec.WriteTo(manifest, spec);

        var group = A2ASession.CreateGroup(groupId, identity.NodeId, taskId);
        group.Status = SessionStatus.Forming;
        foreach (var rep in reps)
        {
            group.Members.Add(new SessionMember { NodeId = rep.NodeId, Role = "participant", JoinState = MemberJoinState.Invited });
        }
        await _host.Store.SaveSessionAsync(group, cancellationToken);

        var task = new DistributedTask
        {
            TaskId = taskId,
            GroupId = groupId,
            CoordinatorNodeId = identity.NodeId,
            Manifest = manifest
        };
        await _host.Store.SaveTaskAsync(task, cancellationToken);
        await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.Offering, cancellationToken);
        await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.AwaitingAcceptances, cancellationToken);

        var created = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.GroupCreated,
            new { taskId, title = spec.Title, period = $"{spec.StartDate}~{spec.EndDate}", deadline = spec.Deadline },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(created, cancellationToken: cancellationToken);

        foreach (var rep in reps)
        {
            var peer = await _host.Store.GetPeerAsync(rep.NodeId, cancellationToken);
            var endpoint = peer?.Endpoints.FirstOrDefault()
                ?? throw new InvalidOperationException($"业务员 {rep.DisplayName} 没有可用端点，无法下发任务。");
            _members[rep.NodeId] = new MemberTrack
            {
                NodeId = rep.NodeId,
                DisplayName = rep.DisplayName,
                Icon = rep.Icon,
                Endpoint = endpoint,
                Status = WeeklyMemberStatus.Offered
            };

            // 在远端 Helper 建立任务（进入 AwaitingAcceptances，等待其用户授权后接受）。
            await _host.Client.CreateTaskAsync(new Uri(endpoint), new TaskManifestDto
            {
                TaskId = taskId,
                GroupId = groupId,
                Coordinator = identity.NodeId,
                Title = manifest.Title,
                Description = manifest.Description,
                ExpectedOutputs = manifest.ExpectedOutputs,
                EvaluationRubric = manifest.EvaluationRubric,
                Constraints = manifest.Constraints,
                Extensions = manifest.Extensions,
                ManifestVersion = manifest.ManifestVersion
            }, cancellationToken);

            var offerEvent = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.TaskOffered,
                new { taskId, to = rep.NodeId, title = spec.Title, deadline = spec.Deadline, requiredSections = spec.RequiredSections },
                senderName: identity.DisplayName, senderIcon: identity.Icon);
            await _host.Store.AppendEventAsync(offerEvent, cancellationToken: cancellationToken);

            // 把任务邀请同步给参与者，其会话流同样显示发起 Helper 的名称与图标。
            await PushGroupEventsAsync(endpoint, groupId, new[] { offerEvent }, cancellationToken);
        }

        _tasks[taskId] = (groupId, spec, taskId);
        return new WeeklyDistributionResult(taskId, groupId, reps.Select(x => x.NodeId).ToList());
    }

    /// <summary>参与者已接受（参与者侧已把本地任务置为 Ready，并推送了签名 TaskAccepted 事件）。</summary>
    public async Task RegisterAcceptedAsync(string taskId, string participantNodeId, CancellationToken cancellationToken = default)
    {
        var (groupId, spec, _) = await ResolveAsync(taskId, cancellationToken);
        if (!_members.TryGetValue(participantNodeId, out var member)) return;
        member.Status = WeeklyMemberStatus.Accepted;

        var identity = _host.Identity.Identity;
        var joined = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.MemberJoined,
            new { taskId, member = participantNodeId, displayName = member.DisplayName, icon = member.Icon },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(joined, cancellationToken: cancellationToken);

        var group = await _host.Store.GetSessionAsync(groupId, cancellationToken);
        if (group is not null)
        {
            var sessionMember = group.Members.FirstOrDefault(x => x.NodeId == participantNodeId);
            if (sessionMember is not null) sessionMember.JoinState = MemberJoinState.Joined;
            group.Status = SessionStatus.Active;
            await _host.Store.SaveSessionAsync(group, cancellationToken);
        }

        await AdvanceToRunningOnceAsync(taskId, groupId, spec, cancellationToken);
    }

    /// <summary>参与者拒绝（参与者侧已推送带理由的签名 MemberRejected 事件）。</summary>
    public async Task RegisterRejectedAsync(string taskId, string participantNodeId, string reason, CancellationToken cancellationToken = default)
    {
        var (groupId, _, _) = await ResolveAsync(taskId, cancellationToken);
        if (!_members.TryGetValue(participantNodeId, out var member)) return;
        member.Status = WeeklyMemberStatus.Rejected;
        member.RejectionReason = reason;
    }

    /// <summary>
    /// 校验参与者交付：缺章节 → RevisionRequested（结构化修订请求）；
    /// 合格 → 记录 Delivery 并广播 ArtifactPublished / TaskStatusChanged。
    /// </summary>
    public async Task<WeeklyDeliveryDecision> RegisterDeliveryAsync(
        string taskId, string participantNodeId, string artifactId, CancellationToken cancellationToken = default)
    {
        var (groupId, spec, _) = await ResolveAsync(taskId, cancellationToken);
        if (!_members.TryGetValue(participantNodeId, out var member))
        {
            throw new InvalidOperationException($"Unknown participant {participantNodeId}.");
        }
        var identity = _host.Identity.Identity;

        if (!_host.ArtifactStore.TryGet(artifactId, out var descriptor) || descriptor is null)
        {
            throw new InvalidOperationException($"Artifact {artifactId} not found on coordinator.");
        }
        var markdown = await ReadArtifactMarkdownAsync(_host.ArtifactStore, artifactId, cancellationToken);
        var missing = WeeklySummaryValidator.MissingSections(markdown, spec.RequiredSections);

        if (missing.Count > 0)
        {
            member.Status = WeeklyMemberStatus.RevisionRequested;
            member.Revisions++;
            var revisionEvent = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.RevisionRequested,
                new { taskId, from = participantNodeId, artifactId, missingSections = missing, deadline = spec.Deadline },
                senderName: identity.DisplayName, senderIcon: identity.Icon);
            await _host.Store.AppendEventAsync(revisionEvent, cancellationToken: cancellationToken);
            await PushGroupEventsAsync(member.Endpoint, groupId, new[] { revisionEvent }, cancellationToken);
            return new WeeklyDeliveryDecision(false, missing,
                $"缺少必填章节：{string.Join("、", missing)}，请在 {spec.Deadline:yyyy-MM-dd HH:mm} 前补齐并重新提交。");
        }

        member.Status = WeeklyMemberStatus.Delivered;
        member.ArtifactId = artifactId;
        member.SummaryContent = markdown;

        var task = await _host.Store.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Task {taskId} not found.");
        task.Deliveries.Add(new TaskDelivery
        {
            DeliveryId = Guid.CreateVersion7().ToString("N"),
            TaskId = taskId,
            FromNodeId = participantNodeId,
            Artifacts = [descriptor],
            Summary = markdown,
            ContentHash = descriptor.Sha256,
            Status = TaskDeliveryStatus.Accepted
        });
        await _host.Store.SaveTaskAsync(task, cancellationToken);

        var published = SessionEvent.Create(groupId, participantNodeId, SessionEventType.ArtifactPublished,
            new { taskId, artifactId, name = descriptor.Name, sha256 = descriptor.Sha256 },
            senderName: member.DisplayName, senderIcon: member.Icon);
        await _host.Store.AppendEventAsync(published, cancellationToken: cancellationToken);

        var accepted = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.TaskStatusChanged,
            new { taskId, member = participantNodeId, status = "DeliveryAccepted", revisions = member.Revisions },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(accepted, cancellationToken: cancellationToken);
        await PushGroupEventsAsync(member.Endpoint, groupId, new[] { published, accepted }, cancellationToken);
        return new WeeklyDeliveryDecision(true, new List<string>(), "交付已接收。");
    }

    /// <summary>截止或全员交付后汇总部门周报，写入 Artifact，并对成功交付成员签发三维评价。</summary>
    public async Task<WeeklyAggregationResult> AggregateAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var (groupId, spec, _) = await ResolveAsync(taskId, cancellationToken);
        var identity = _host.Identity.Identity;

        if ((await _host.Store.GetTaskAsync(taskId, cancellationToken))!.State == DistributedTaskState.Running)
        {
            await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.AwaitingDeliveries, cancellationToken);
        }
        await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.Aggregating, cancellationToken);

        var report = new DepartmentWeeklyReport
        {
            DepartmentId = _directory.DepartmentId,
            StartDate = spec.StartDate,
            EndDate = spec.EndDate,
            ExpectedCount = _members.Count
        };
        foreach (var member in _members.Values.OrderBy(x => x.NodeId))
        {
            report.Entries.Add(new DepartmentWeeklyReportEntry
            {
                MemberNodeId = member.NodeId,
                MemberDisplayName = member.DisplayName,
                MemberIcon = member.Icon,
                Status = member.Status,
                ArtifactId = member.ArtifactId,
                Revisions = member.Revisions,
                SummaryContent = member.Status == WeeklyMemberStatus.Delivered ? member.SummaryContent : null,
                RejectionReason = member.RejectionReason
            });
            if (member.Status == WeeklyMemberStatus.Delivered) report.AcceptedCount++;
            if (member.Status == WeeklyMemberStatus.Rejected) report.RejectedCount++;
            if (member.Status is WeeklyMemberStatus.Offered or WeeklyMemberStatus.Accepted or WeeklyMemberStatus.Overdue) report.MissingCount++;
        }
        report.State = report.AcceptedCount == report.ExpectedCount
            ? DepartmentReportState.Completed
            : DepartmentReportState.PartialCompleted;

        var markdown = report.ToMarkdown();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        var artifact = await _host.ArtifactStore.PutAsync(stream, new ArtifactMetadata
        {
            Name = $"department-weekly-{spec.StartDate}_{spec.EndDate}.md",
            MediaType = "text/markdown",
            CreatedBy = identity.NodeId,
            TaskId = taskId,
            SessionId = groupId
        }, cancellationToken);

        // 对成功交付成员签发配合度/效率/质量评价：远端保存可验签凭证 + 群内评价事件。
        foreach (var member in _members.Values.Where(x => x.Status == WeeklyMemberStatus.Delivered))
        {
            var scores = new RatingScores
            {
                Cooperation = member.Revisions == 0 ? 5 : 4,
                Efficiency = 5,
                Quality = member.Revisions == 0 ? 5 : 4
            };
            var rating = new RatingCredential
            {
                RatingId = Guid.CreateVersion7().ToString("N"),
                TaskId = taskId,
                FromNodeId = identity.NodeId,
                ToNodeId = member.NodeId,
                DeliveryHash = member.ArtifactId,
                Scores = scores,
                Outcome = "accepted",
                Comment = member.Revisions == 0 ? "一次交付合格，配合顺畅" : "经一次修订后合格交付"
            };
            rating.Signature = Convert.ToBase64String(_host.Identity.Sign(rating.SigningPayload()));
            await _host.Client.SaveRatingAsync(new Uri(member.Endpoint), RatingDto.FromCore(rating), cancellationToken);
            member.Status = WeeklyMemberStatus.Delivered;

            var ratingEvent = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.RatingPublished,
                new { taskId, to = member.NodeId, scores = new { cooperation = scores.Cooperation, efficiency = scores.Efficiency, quality = scores.Quality }, comment = rating.Comment },
                senderName: identity.DisplayName, senderIcon: identity.Icon);
            await _host.Store.AppendEventAsync(ratingEvent, cancellationToken: cancellationToken);
            await PushGroupEventsAsync(member.Endpoint, groupId, new[] { ratingEvent }, cancellationToken);
        }

        var finalState = report.State == DepartmentReportState.Completed
            ? DistributedTaskState.Completed
            : DistributedTaskState.PartialCompleted;
        await _host.Store.TransitionTaskStateAsync(taskId, finalState, cancellationToken);

        var completed = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.TaskStateChanged,
            new { taskId, state = finalState.ToString(), coverage = report.Coverage, artifactId = artifact.ArtifactId },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(completed, cancellationToken: cancellationToken);

        var group = await _host.Store.GetSessionAsync(groupId, cancellationToken);
        if (group is not null)
        {
            group.Status = SessionStatus.Archived;
            await _host.Store.SaveSessionAsync(group, cancellationToken);
        }

        return new WeeklyAggregationResult(report, artifact.ArtifactId);
    }

    private async Task AdvanceToRunningOnceAsync(string taskId, string groupId, WeeklySummaryTaskSpec spec, CancellationToken cancellationToken)
    {
        var task = await _host.Store.GetTaskAsync(taskId, cancellationToken);
        if (task is null) return;
        if (task.State == DistributedTaskState.AwaitingAcceptances)
        {
            await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.Ready, cancellationToken);
            await _host.Store.TransitionTaskStateAsync(taskId, DistributedTaskState.Running, cancellationToken);
        }
    }

    private async Task<(string GroupId, WeeklySummaryTaskSpec Spec, string TaskId)> ResolveAsync(string taskId, CancellationToken cancellationToken)
    {
        if (_tasks.TryGetValue(taskId, out var known))
        {
            return (known.GroupId, known.Spec, known.TaskId);
        }
        var task = await _host.Store.GetTaskAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} not found.");
        var spec = WeeklySummaryTaskSpec.TryReadFrom(task.Manifest)
            ?? throw new InvalidOperationException($"Task {taskId} has no weekly-summary extension.");
        _tasks[taskId] = (task.GroupId ?? "", spec, taskId);
        return (task.GroupId ?? "", spec, taskId);
    }

    private async Task PushGroupEventsAsync(string endpoint, string groupId, IEnumerable<SessionEvent> events, CancellationToken cancellationToken)
    {
        var dtos = new List<SessionEventDto>();
        foreach (var entry in events)
        {
            var remote = (await _host.Store.ReadEventsAsync(groupId, cancellationToken: cancellationToken)).Count + dtos.Count + 1;
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
                PrevHash = entry.PrevHash,
                Signature = entry.Signature
            };
            copy.Sequence = remote;
            copy.Signature = Convert.ToBase64String(_host.Identity.Sign(copy.SigningPayload()));
            dtos.Add(SessionEventDto.FromCore(copy));
        }
        await _host.Client.AppendGroupEventsAsync(new Uri(endpoint), groupId, dtos, cancellationToken);
    }

    public static async Task<string> ReadArtifactMarkdownAsync(ArtifactStore store, string artifactId, CancellationToken cancellationToken)
    {
        await using var stream = await store.OpenReadAsync(artifactId, null, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
