using System.Text.Json;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Core.Sessions;

namespace AgentBrook.A2A.Transport.Protocol;

/// <summary>
/// 群组元数据自愈（设计 §12）：按收到的群组事件维护本地群组记录——
/// GroupCreated 重建会话（成员端会话中心可见的前提）、GroupRenamed/GroupClosed/MemberLeft/MemberJoined/MemberRejected
/// 跟随变更；本机被移出（MemberLeft.nodeId == self）时本地群组置 Closed。
/// HTTP 推送方（AgentBrookExtensionHandler）与主动拉取方（Helper 轮询补拉）共用，保证两条同步路径语义一致。
/// </summary>
public static class GroupSessionSync
{
    public static async Task ApplyAsync(A2AStore store, NodeIdentityService identity, IEnumerable<SessionEvent> events, CancellationToken cancellationToken)
    {
        foreach (var e in events)
        {
            try
            {
                switch (e.Type)
                {
                    case SessionEventType.GroupCreated:
                        await ApplyCreatedAsync(store, e, cancellationToken);
                        break;
                    case SessionEventType.GroupRenamed:
                        await ApplyRenamedAsync(store, e, cancellationToken);
                        break;
                    case SessionEventType.GroupClosed:
                        await ApplyClosedAsync(store, e, cancellationToken);
                        break;
                    case SessionEventType.MemberJoined or SessionEventType.MemberRejected or SessionEventType.MemberLeft:
                        await ApplyMemberStateAsync(store, identity, e, cancellationToken);
                        break;
                }
            }
            catch
            {
                // 元数据自愈失败不影响事件落库：事件已存储，后续事件流仍可重建
            }
        }
    }

    private static async Task ApplyCreatedAsync(A2AStore store, SessionEvent e, CancellationToken cancellationToken)
    {
        if (await store.GetSessionAsync(e.SessionId, cancellationToken) is not null)
        {
            return;
        }
        var restored = RestoreGroupFromCreatedEvent(e);
        if (restored is not null)
        {
            await store.SaveSessionAsync(restored, cancellationToken);
        }
    }

    private static async Task ApplyRenamedAsync(A2AStore store, SessionEvent e, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(e.SessionId, cancellationToken);
        var name = ReadStringProperty(e.PayloadJson, "name");
        if (session is not null && !string.IsNullOrWhiteSpace(name) && session.Name != name)
        {
            session.Name = name;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveSessionAsync(session, cancellationToken);
        }
    }

    private static async Task ApplyClosedAsync(A2AStore store, SessionEvent e, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(e.SessionId, cancellationToken);
        if (session is not null && session.Status != SessionStatus.Closed)
        {
            session.Status = SessionStatus.Closed;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveSessionAsync(session, cancellationToken);
        }
    }

    private static async Task ApplyMemberStateAsync(A2AStore store, NodeIdentityService identity, SessionEvent e, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(e.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }
        var nodeId = ReadStringProperty(e.PayloadJson, "nodeId");
        if (string.IsNullOrEmpty(nodeId))
        {
            return;
        }

        if (e.Type == SessionEventType.MemberLeft && nodeId == identity.Identity.NodeId)
        {
            // 本机被移出群：本地群组置 Closed，会话列表保留（状态即"已被移出"）
            if (session.Status != SessionStatus.Closed)
            {
                session.Status = SessionStatus.Closed;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await store.SaveSessionAsync(session, cancellationToken);
            }
            return;
        }

        var member = session.Members.FirstOrDefault(x => x.NodeId == nodeId);
        switch (e.Type)
        {
            case SessionEventType.MemberJoined or SessionEventType.MemberRejected:
            {
                var target = e.Type == SessionEventType.MemberJoined ? MemberJoinState.Joined : MemberJoinState.Rejected;
                if (member is not null)
                {
                    if (member.JoinState != target)
                    {
                        member.JoinState = target;
                        session.UpdatedAt = DateTimeOffset.UtcNow;
                        await store.SaveSessionAsync(session, cancellationToken);
                    }
                }
                else if (nodeId != identity.Identity.NodeId)
                {
                    // 成员表缺行（罕见）：按参与者补入，保证成员列表完整
                    session.Members.Add(new SessionMember { NodeId = nodeId, Role = "participant", JoinState = target });
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    await store.SaveSessionAsync(session, cancellationToken);
                }
                break;
            }
            case SessionEventType.MemberLeft:
            {
                if (member is not null)
                {
                    session.Members.Remove(member);
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    await store.SaveSessionAsync(session, cancellationToken);
                }
                break;
            }
        }
    }

    /// <summary>从 GroupCreated 事件载荷重建群组会话。载荷缺 members（如周报旧格式）时返回 null，交由各业务自身的成员侧流程建群。</summary>
    private static A2ASession? RestoreGroupFromCreatedEvent(SessionEvent e)
    {
        using var doc = ParsePayload(e.PayloadJson);
        if (doc?.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var root = doc.RootElement;
        if (!root.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var session = new A2ASession
        {
            SessionId = e.SessionId,
            Kind = SessionKind.Group,
            CreatedBy = e.SenderNodeId,
            CoordinatorNodeId = root.TryGetProperty("coordinator", out var coord) && coord.ValueKind == JsonValueKind.String ? coord.GetString() : e.SenderNodeId,
            Name = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            Status = SessionStatus.Active,
            CreatedAt = e.Timestamp,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        foreach (var m in members.EnumerateArray())
        {
            // 成员形态兼容两种载荷：纯 nodeId 字符串数组，或 { nodeId, role, joinState } 对象数组
            if (m.ValueKind == JsonValueKind.String)
            {
                var id = m.GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    session.Members.Add(new SessionMember { NodeId = id, Role = id == e.SenderNodeId ? "coordinator" : "participant", JoinState = MemberJoinState.Joined });
                }
            }
            else if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("nodeId", out var nid) && nid.ValueKind == JsonValueKind.String)
            {
                var join = m.TryGetProperty("joinState", out var js) && Enum.TryParse<MemberJoinState>(js.GetString(), out var parsed) ? parsed : MemberJoinState.Invited;
                session.Members.Add(new SessionMember
                {
                    NodeId = nid.GetString()!,
                    Role = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "participant",
                    JoinState = join
                });
            }
        }
        return session.Members.Count == 0 ? null : session;
    }

    private static string? ReadStringProperty(string? payloadJson, string propertyName)
    {
        try
        {
            using var doc = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonDocument.Parse(payloadJson);
            if (doc?.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // 载荷非法：按缺失处理
        }
        return null;
    }

    private static JsonDocument? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
