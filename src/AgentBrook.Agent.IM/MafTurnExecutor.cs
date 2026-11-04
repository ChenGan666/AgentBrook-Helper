using System.Text.Json;
using System.Text;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.IM;

/// <summary>
/// 真实 MAF 快照编解码（I01）：AIAgent.Serialize/DeserializeSessionAsync 的 IM 侧封装。
/// </summary>
public sealed class MafSnapshotCodec
{
    /// <summary>序列化会话状态为 JSON 字节（提交单元 payload）。</summary>
    public static byte[] Encode(AIAgent agent, AgentSession session)
    {
        var json = agent.SerializeSessionAsync(session).GetAwaiter().GetResult();
        return Encoding.UTF8.GetBytes(json.GetRawText());
    }

    /// <summary>从提交单元 payload 恢复会话状态。</summary>
    public static AgentSession Decode(AIAgent agent, byte[] payload)
    {
        var json = JsonDocument.Parse(payload).RootElement;
        return agent.DeserializeSessionAsync(json).GetAwaiter().GetResult();
    }
}

/// <summary>
/// IM 侧真实 MAF 回合执行器（§5-S7 任务1/4）：每回合从 CURRENT 载入最新会话状态
/// （陈旧快照被 CAS/CURRENT 语义排除——租约交付版本与本执行器无关，载入即最新），
/// 经 RunStreamingAsync 执行（真实 MAF 管线，IChatClient 可为测试替身），
/// 序列化回快照随 TurnOutcome.SnapshotPayload 提交。IChatClient 未就绪/脚本耗尽明确失败，不静默。
/// </summary>
public sealed class MafTurnExecutor : ITurnExecutor
{
    private readonly Func<AIAgent> _agentFactory;   // 每回合可重建（配置切换安全）；通常同一实例
    private readonly VersionedSessionStore _store;
    private readonly string _userText;

    /// <summary>模型调用计数（I01 断言用）。</summary>
    public int ModelCalls { get; private set; }

    public MafTurnExecutor(Func<AIAgent> agentFactory, VersionedSessionStore store, string userText)
    {
        _agentFactory = agentFactory;
        _store = store;
        _userText = userText;
    }

    public async Task<TurnOutcome> ExecuteAsync(TurnExecutionContext context, CancellationToken cancellationToken)
    {
        var agent = _agentFactory();
        var payload = _store.ReadCurrentPayload(context.SessionId);
        var session = MafSnapshotCodec.Decode(agent, payload);
        _ = session;   // 载入即续接（历史连续性由 I01 断言）

        var events = new List<TurnEvent>();
        var answer = new StringBuilder();
        var eventIndex = 0;

        await foreach (var update in agent.RunStreamingAsync(_userText, session, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var delta = update.Text ?? "";
            if (delta.Length == 0) continue;
            answer.Append(delta);
            events.Add(new TurnEvent(context.TurnId, eventIndex++, "assistant", delta, null));
        }

        ModelCalls++;
        var snapshot = MafSnapshotCodec.Encode(agent, session);
        return new TurnOutcome(
            Completed: true,
            AnswerText: answer.ToString(),
            Events: events,
            UnsafeAttempts: [],
            Error: null,
            SnapshotPayload: snapshot);
    }
}

/// <summary>
/// 脚本化 IChatClient 测试替身（I01/I02）：记录每次调用收到的完整消息历史（上下文连续性断言），
/// 按脚本回放响应（流式按字符分片）；脚本耗尽抛错（测试必须显式排队）。
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Lock _gate = new();
    private readonly Queue<string> _responses = [];
    private readonly List<(int CallIndex, string AllText, string LastUserText)> _invocations = [];

    public int CallCount { get; private set; }

    /// <summary>第 n 次调用收到的全部文本（含系统/历史/用户——上下文连续性断言用）。</summary>
    public IReadOnlyList<string> Invocations
    {
        get { lock (_gate) return [.. _invocations.Select(i => $"{i.CallIndex}:{i.AllText}")]; }
    }

    public ScriptedChatClient(params string[] responses)
    {
        foreach (var r in responses) _responses.Enqueue(r);
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(messages);
        var text = Take();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Record(messages);
        var text = Take();
        foreach (var ch in text)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, ch.ToString());
            await Task.Yield();
        }
    }

    private void Record(IEnumerable<ChatMessage> messages)
    {
        lock (_gate)
        {
            CallCount++;
            var all = string.Join("\n", messages.Select(m => m.Text ?? ""));
            var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
            _invocations.Add((CallCount, all, lastUser));
        }
    }

    private string Take()
    {
        lock (_gate)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("ScriptedChatClient 脚本耗尽——测试必须显式排队每次响应。");
            return _responses.Dequeue();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
