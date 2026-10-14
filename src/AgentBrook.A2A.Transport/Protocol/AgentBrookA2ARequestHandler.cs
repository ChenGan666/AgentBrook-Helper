using System.Text.Json;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Core.Tasks;
using A2A;

namespace AgentBrook.A2A.Transport.Protocol;

public sealed class AgentBrookA2ARequestHandler : IA2ARequestHandler
{
    private readonly A2AStore _store;
    private readonly NodeIdentityService _identity;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AgentBrookA2ARequestHandler(A2AStore store, NodeIdentityService identity, IHttpContextAccessor httpContextAccessor)
    {
        _store = store;
        _identity = identity;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>宿主组合根注入的业务消息钩子（如 §26 主管日报汇总）。返回值作为给对端的业务回执文本。</summary>
    public Func<string, string, Task<string?>>? IncomingMessageHook { get; set; }

    internal Hosting.A2AHost? Host { get; set; }

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken)
    {
        var senderNodeId = GetAuthenticatedNodeId();
        if (string.IsNullOrEmpty(senderNodeId))
        {
            throw new A2AException("Authentication required.", A2AErrorCode.InvalidRequest);
        }

        var message = request.Message;
        var sessionId = message.ContextId ?? $"peer-{_identity.Identity.NodeId}-{senderNodeId}";
        var session = await _store.GetSessionAsync(sessionId) ?? A2ASession.CreatePeer(sessionId, senderNodeId, _identity.Identity.NodeId);
        await _store.SaveSessionAsync(session);

        var text = message.Parts.Select(p => p.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
        // 会话流必须呈现真实发送主体：优先取消息元数据中的名称/图标，其次取已配对 Peer 资料。
        var sender = await _store.GetPeerAsync(senderNodeId);
        var senderName = message.Metadata.TryGetValue("abk.senderName", out var nameElement) ? nameElement.GetString() : null;
        var senderIcon = message.Metadata.TryGetValue("abk.senderIcon", out var iconElement) ? iconElement.GetString() : null;
        var entry = SessionEvent.Create(sessionId, senderNodeId, SessionEventType.MessageReceived, new { text, messageId = message.MessageId },
            senderName: senderName ?? sender?.DisplayName,
            senderIcon: senderIcon ?? sender?.Icon);
        await _store.AppendEventAsync(entry);

        // 业务回执文本只随 HTTP 响应返回给发送方；不再向会话写"Ack"自回复事件，
        // 避免把系统回执渲染成发送者本人的消息（文档 §17.2.4 真实发送主体要求）。
        bool businessHandled;
        string replyText;
        var hook = IncomingMessageHook ?? Host?.IncomingMessageHook;
        if (hook is not null)
        {
            try
            {
                var hookResult = await hook(senderNodeId, text);
                businessHandled = hookResult is not null;
                replyText = hookResult ?? $"Ack: {text}";
            }
            catch (Exception ex)
            {
                businessHandled = true;   // 业务处理已接管，失败由回执文本表达
                replyText = $"Error: {ex.Message}";
            }
        }
        else
        {
            businessHandled = false;
            replyText = $"Ack: {text}";
        }

        // 对端自动回复（abk.autoReply）不再触发本端自动处理，防止两个自动处理 Helper 互相回声循环。
        var isAutoReply = message.Metadata.TryGetValue("abk.autoReply", out var autoEl) &&
            autoEl.ValueKind == JsonValueKind.String && autoEl.GetString() == "true";
        if (!businessHandled && !isAutoReply)
        {
            var peerArgs = new Hosting.PeerMessageReceivedArgs(
                senderNodeId, senderName ?? sender?.DisplayName, senderIcon ?? sender?.Icon, sessionId, text);
            Console.Error.WriteLine($"[a2a-remote] 消息到达：sender={senderNodeId[..Math.Min(12, senderNodeId.Length)]} session={sessionId[..Math.Min(12, sessionId.Length)]} handled={businessHandled} auto={isAutoReply} host={(Host is null ? "null" : "ok")}");
            // 注意：不能绑定请求的 cancellationToken——HTTP 响应返回后请求即结束，
            // fire-and-forget 的自动处理必须独立于请求生命周期。
            _ = Task.Run(() =>
            {
                try
                {
                    Console.Error.WriteLine("[a2a-remote] 触发 PeerMessageReceived");
                    Host?.RaisePeerMessageReceived(peerArgs);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[a2a-remote] 触发失败：{ex}");
                }
            });
        }

        return new SendMessageResponse
        {
            Message = new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Role = Role.Agent,
                ContextId = sessionId,
                Parts = new List<Part> { Part.FromText(replyText) }
            }
        };
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken)
    {
        var task = await _store.GetTaskAsync(request.Id);
        if (task is null)
        {
            throw new A2AException("Task not found.", A2AErrorCode.TaskNotFound);
        }
        return MapAgentTask(task);
    }

    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken)
    {
        var tasks = await _store.GetPeersAsync();
        return new ListTasksResponse();
    }

    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken)
    {
        var task = await _store.GetTaskAsync(request.Id);
        if (task is null)
        {
            throw new A2AException("Task not found.", A2AErrorCode.TaskNotFound);
        }
        task.TransitionTo(DistributedTaskState.Cancelled);
        await _store.SaveTaskAsync(task);
        return MapAgentTask(task);
    }

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken)
    {
        throw new A2AException("Extended agent card not configured.", A2AErrorCode.ExtendedAgentCardNotConfigured);
    }

    private string? GetAuthenticatedNodeId()
    {
        return _httpContextAccessor.HttpContext?.Items["A2AAuthenticatedNodeId"] as string;
    }

    private static AgentTask MapAgentTask(DistributedTask task)
    {
        return new AgentTask
        {
            Id = task.TaskId,
            Status = new global::A2A.TaskStatus
            {
                State = MapTaskState(task.State),
                Message = new Message { Role = Role.Agent, Parts = new List<Part> { Part.FromText(task.State.ToString()) } }
            },
            Metadata = new Dictionary<string, JsonElement> { ["schema"] = JsonSerializer.SerializeToElement(TaskManifest.Schema) }
        };
    }

    private static TaskState MapTaskState(DistributedTaskState state)
    {
        return state switch
        {
            DistributedTaskState.Completed => TaskState.Completed,
            DistributedTaskState.Failed => TaskState.Failed,
            DistributedTaskState.Cancelled => TaskState.Canceled,
            DistributedTaskState.Rejected => TaskState.Rejected,
            _ => TaskState.Working
        };
    }
}
