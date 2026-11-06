using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using AgentBrook.Agent;
using AgentBrook.Helper.Support;
using AgentBrook.Messaging;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Runtime.Abstractions;
using ApprovalDecision = AgentBrook.Agent.ApprovalDecision;

namespace AgentBrook.Helper.Core.Im;

/// <summary>IM 授权回复的解析结果。</summary>
internal enum ImApprovalReply
{
    /// <summary>不是授权回复（用户改发了新消息）。</summary>
    Unmatched = 0,
    Approve = 1,
    Reject = 2,
}

/// <summary>IM 对话确认的文本协议：授权选项/数字回答/选项编号映射。</summary>
internal static class ImInteractionReply
{
    /// <summary>解析授权回复：1/允许/yes… = 允许；2/拒绝/no… = 拒绝；其余 = 新消息。</summary>
    internal static ImApprovalReply ParseApproval(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return ImApprovalReply.Unmatched;
        switch (t.ToLowerInvariant())
        {
            case "1": case "y": case "yes": case "ok": case "allow": case "允许": case "同意": case "批准":
                return ImApprovalReply.Approve;
            case "2": case "n": case "no": case "deny": case "reject": case "拒绝": case "不允许": case "不同意":
                return ImApprovalReply.Reject;
            default:
                return ImApprovalReply.Unmatched;
        }
    }

    /// <summary>把回复映射为答案：选项编号（1..N）→ 对应选项文本；其他 → 原文（自由回答）。</summary>
    internal static string MapAnswer(string? reply, string? choices)
    {
        var t = (reply ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(choices) && int.TryParse(t, out var n))
        {
            var options = choices.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (n >= 1 && n <= options.Length) return options[n - 1];
        }
        return t;
    }

    /// <summary>选项渲染（1. 选项 / 2. 选项…；空则返回空串）。</summary>
    internal static string FormatChoices(string? choices)
    {
        if (string.IsNullOrWhiteSpace(choices)) return "";
        var options = choices.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (options.Length == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < options.Length; i++) sb.Append(i + 1).Append(". ").Append(options[i]).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// IM 交互等待注册表（进程级）：回合内发起授权/提问 → 登记；通道入站线程在<b>准入层</b>截获回复
/// （身份已校验、入 inbox 之前）直接完成等待——调度器单循环，走 inbox 会死锁。
/// 键 = 通道+实例|账号|聊天；只接受同一配对发送者的回复。
/// </summary>
internal static class ImInteractionRegistry
{
    internal enum Kind
    {
        Approval,
        Ask,
    }

    internal sealed class Entry
    {
        public required string Key { get; init; }
        public required string ChannelId { get; init; }
        public required string InstanceId { get; init; }
        public required string AccountId { get; init; }
        public required string ChatId { get; init; }
        public required string SenderId { get; init; }
        public required string SessionId { get; init; }
        public required Kind Kind { get; init; }

        /// <summary>回复通道（RunContinuationsAsynchronously：绝不在准入持 DB 门时内联续体）。</summary>
        public TaskCompletionSource<string> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly ConcurrentDictionary<string, Entry> Pending = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Consumed = new();
    private static readonly TimeSpan ConsumedTtl = TimeSpan.FromMinutes(15);

    private static string KeyOf(string channel, string instance, string account, string chat) =>
        $"{channel}+{instance}|{account}|{chat}";

    internal static Entry Begin(string channel, string instance, string account, string chat,
        string sender, Kind kind, string sessionId)
    {
        var entry = new Entry
        {
            Key = KeyOf(channel, instance, account, chat),
            ChannelId = channel, InstanceId = instance, AccountId = account, ChatId = chat,
            SenderId = sender, Kind = kind, SessionId = sessionId,
        };
        Pending[entry.Key] = entry;   // 同一聊天串行交互：后发覆盖前发（前一个会超时/取消收尾）
        return entry;
    }

    internal static void End(Entry entry) =>
        Pending.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry));   // 只删自己

    /// <summary>
    /// 准入层拦截（同步、非阻塞、不碰 DB）：命中等待中的交互 → 完成 TCS 并消费该消息。
    /// 授权提示期间收到不匹配文本：当前审批按拒绝结束，消息继续作为新任务（返回 false）。
    /// </summary>
    internal static bool TryConsume(PairedIdentity identity, ImInboundMessage message)
    {
        var consumedKey = $"{identity.ChannelId}+{identity.InstanceId}|{identity.AccountId}|" +
                          $"{identity.ChatId}|{message.MessageId}";
        if (Consumed.TryGetValue(consumedKey, out var at) && DateTimeOffset.UtcNow - at < ConsumedTtl)
            return true;   // 通道重投：已消费过，按已处理确认（不再起回合）

        if (!Pending.TryGetValue(KeyOf(identity.ChannelId, identity.InstanceId, identity.AccountId,
                identity.ChatId), out var entry))
            return false;
        if (!string.Equals(entry.SenderId, identity.SenderId, StringComparison.Ordinal))
            return false;   // 只认配对发送者本人

        var text = (message.Text ?? "").Trim();
        if (text.Length == 0) return false;   // 纯附件/空消息：不是交互回复，走正常回合

        if (entry.Kind == Kind.Approval && ImInteractionReply.ParseApproval(text) == ImApprovalReply.Unmatched)
        {
            // 不匹配 → 当前审批按拒绝结束；该消息继续作为新任务（返回 false，不消费）
            entry.Reply.TrySetResult(text);
            End(entry);
            return false;
        }

        Consumed[consumedKey] = DateTimeOffset.UtcNow;
        PruneConsumed();
        entry.Reply.TrySetResult(text);
        End(entry);
        return true;
    }

    private static void PruneConsumed()
    {
        if (Consumed.Count < 256) return;
        var cutoff = DateTimeOffset.UtcNow - ConsumedTtl;
        foreach (var (key, at) in Consumed)
        {
            if (at < cutoff) Consumed.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// 一个 IM 回合的对话确认实现（<see cref="IBrookInteraction"/>）：
/// 授权/提问 → IM 消息提示 → 等待手机回复（注册表在准入层完成）→ 返回决定/答案。
/// 仅在该回合的异步流内可见（BrookAgent.BeginExternalInteraction/AsyncLocal），不影响本地回合。
/// 超时：授权默认拒绝、提问返回空；通道发不出提示：授权拒绝、提问返回空（安全侧）。
/// </summary>
internal sealed class ImTurnInteractionRouter : IBrookInteraction
{
    private readonly string _sessionId;
    private readonly ReplyTarget _target;
    private readonly string _senderId;
    private readonly Func<string, bool> _send;
    private readonly Action<string> _log;
    private readonly TimeSpan _timeout;
    private readonly Func<bool>? _fullAccess;
    private readonly Func<string, string>? _restoreDisplay;

    internal ImTurnInteractionRouter(string sessionId, ReplyTarget target, string senderId,
        Func<string, bool> send, Action<string>? log = null, TimeSpan? timeout = null,
        Func<bool>? fullAccess = null, Func<string, string>? restoreDisplay = null)
    {
        _sessionId = sessionId;
        _target = target;
        _senderId = senderId;
        _send = send;
        _log = log ?? ImDiag.Log;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _fullAccess = fullAccess;
        _restoreDisplay = restoreDisplay;
    }

    public async Task<ApprovalDecision> GetApprovalAsync(string toolName, string? argumentsJson,
        CancellationToken cancellationToken = default)
    {
        // 完全访问模式（与本地一致）：自动批准，不再打扰 IM 用户
        if (_fullAccess?.Invoke() == true)
        {
            _log($"[im] 完全访问：自动批准 {toolName}（IM 同权）");
            return ApprovalDecision.Approve();
        }
        // 隐私插件的占位符先还原：提示会发到聊天平台，用户看到的应是真实内容
        var args = Restore(SummarizeArgs(argumentsJson));
        var prompt = I18n.T("im.interaction.approval", toolName, args.Length == 0 ? "" : args + "\n");
        var entry = ImInteractionRegistry.Begin(_target.ChannelId, _target.InstanceId, _target.AccountId,
            _target.ChatId, _senderId, ImInteractionRegistry.Kind.Approval, _sessionId);
        try
        {
            _log($"[im] 授权请求（IM 对话确认）：{toolName}（session={_sessionId}）");
            if (!_send(prompt))
            {
                _log("[im] 授权提示发送失败（通道/路由不可用）→ 按拒绝处理");
                return ApprovalDecision.Reject(I18n.T("im.interaction.reject.unavailable"));
            }

            var reply = await WaitAsync(entry, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                _log($"[im] 授权等待超时：{toolName} → 拒绝");
                _send(I18n.T("im.interaction.timeout.approval"));
                return ApprovalDecision.Reject(I18n.T("im.interaction.reject.timeout"));
            }

            var parsed = ImInteractionReply.ParseApproval(reply);
            _log($"[im] 授权回复：{toolName} ← {parsed}");
            return parsed switch
            {
                ImApprovalReply.Approve => ApprovalDecision.Approve(),
                ImApprovalReply.Reject => ApprovalDecision.Reject(I18n.T("im.interaction.reject.user")),
                // 不匹配文本已在准入层按拒绝处理，同时该消息作为新任务继续入队
                _ => ApprovalDecision.Reject(I18n.T("im.interaction.reject.newmessage")),
            };
        }
        finally
        {
            ImInteractionRegistry.End(entry);
        }
    }

    public async Task<string> AskUserAsync(string question, string? choices,
        CancellationToken cancellationToken = default)
    {
        var prompt = I18n.T("im.interaction.ask", Restore(question),
            ImInteractionReply.FormatChoices(Restore(choices ?? "")));
        var entry = ImInteractionRegistry.Begin(_target.ChannelId, _target.InstanceId, _target.AccountId,
            _target.ChatId, _senderId, ImInteractionRegistry.Kind.Ask, _sessionId);
        try
        {
            _log($"[im] 提问（IM 对话确认）：session={_sessionId}");
            if (!_send(prompt))
            {
                _log("[im] 提问发送失败（通道/路由不可用）→ 按未回答处理");
                return "";
            }

            var reply = await WaitAsync(entry, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                _log("[im] 提问等待超时 → 按未回答处理");
                _send(I18n.T("im.interaction.timeout.ask"));
                return "";
            }
            return ImInteractionReply.MapAnswer(reply, choices);
        }
        finally
        {
            ImInteractionRegistry.End(entry);
        }
    }

    /// <summary>等待回复：超时返回 null；用户停止/取消则抛出取消（回合按取消收尾）。</summary>
    private async Task<string?> WaitAsync(ImInteractionRegistry.Entry entry, CancellationToken ct)
    {
        var timeout = Task.Delay(_timeout, CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancel = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        var finished = await Task.WhenAny(entry.Reply.Task, timeout, cancel).ConfigureAwait(false);
        if (finished == entry.Reply.Task)
        {
            linked.Cancel();
            return await entry.Reply.Task.ConfigureAwait(false);
        }
        linked.Cancel();
        if (finished == cancel) ct.ThrowIfCancellationRequested();
        return null;   // 超时
    }

    private string Restore(string text) => _restoreDisplay?.Invoke(text) ?? text;

    /// <summary>参数摘要：压成单行、截断 220 字；常见敏感字段打码（提示会发到聊天平台）。</summary>
    internal static string SummarizeArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        var t = Regex.Replace(json, @"\s+", " ").Trim();
        t = Regex.Replace(t,
            @"(""(?:token|api_?key|password|passwd|secret|authorization|bot_?token)""\s*:\s*"")[^""]*("")",
            "$1***$2", RegexOptions.IgnoreCase);
        return t.Length > 220 ? t[..220] + "…" : t;
    }
}
