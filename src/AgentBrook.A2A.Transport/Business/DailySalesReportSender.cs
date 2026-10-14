using System.Text.Json;
using AgentBrook.A2A.Core.Business;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Transport.Hosting;

namespace AgentBrook.A2A.Transport.Business;

public sealed record DailySalesSendResult(
    DailySalesReport Report,
    string PeerSessionId,
    string ManagerReceiptJson,
    string ManagerReceiptText);

/// <summary>
/// §26.1 场景一 · 业务员 Helper 侧：识别"向部门主管汇报当日销售数据"意图后，
/// 组装带签名的个人日报，经既有 Peer Session 通过标准 A2A 消息发送给主管 Helper，
/// 并在本地会话与主管会话中各自留下带 Helper 名称与图标的事件。
/// </summary>
public sealed class DailySalesReportSender
{
    private readonly A2AHost _host;
    private readonly DepartmentDirectory _directory;

    public DailySalesReportSender(A2AHost host, DepartmentDirectory directory)
    {
        _host = host;
        _directory = directory;
    }

    public async Task<DailySalesSendResult> SendDailyReportAsync(
        DateOnly reportDate,
        DailySalesSummary summary,
        CancellationToken cancellationToken = default)
    {
        var manager = _directory.FindManager()
            ?? throw new InvalidOperationException("部门目录中没有 department-manager 成员。");
        var peer = await _host.Store.GetPeerAsync(manager.NodeId, cancellationToken)
            ?? throw new InvalidOperationException($"主管 {manager.NodeId} 尚未发现或配对。");
        if (peer.TrustState != PeerTrustState.Trusted)
        {
            throw new InvalidOperationException("主管 Helper 未处于 Trusted 状态，不能上报数据。");
        }
        var endpoint = peer.Endpoints.FirstOrDefault()
            ?? throw new InvalidOperationException("主管 Helper 没有可用端点。");

        var identity = _host.Identity.Identity;
        var timeZone = SafeTimeZone(_directory.TimeZoneId);
        var dayStart = new DateTimeOffset(reportDate, TimeOnly.MinValue, timeZone.GetUtcOffset(new DateTime(reportDate, TimeOnly.MinValue)));
        var report = new DailySalesReport
        {
            ReportDate = reportDate.ToString("yyyy-MM-dd"),
            DepartmentId = _directory.DepartmentId,
            ReporterNodeId = identity.NodeId,
            ReporterDisplayName = identity.DisplayName,
            ReporterIcon = identity.Icon,
            Summary = summary,
            SourcePeriod = new ReportSourcePeriod { Start = dayStart, End = dayStart.AddDays(1).AddTicks(-1) }
        };
        report.Sign(payload => _host.Identity.SignPayload(payload));

        // 复用与主管的既有 Peer Session；没有则创建，sessionId 通过 contextId 传给主管。
        var sessionId = await GetOrCreatePeerSessionIdAsync(identity.NodeId, manager.NodeId, cancellationToken);

        // 本地会话事件：显示发送主体（本 Helper 名称 + 图标）。
        var localEvent = SessionEvent.Create(sessionId, identity.NodeId, SessionEventType.MessageSent,
            new { kind = DailySalesReport.Schema, report = report.ToJson(), text = "向部门主管汇报当日销售数据" },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(localEvent, cancellationToken: cancellationToken);

        // 标准 A2A 消息把日报发到主管 Helper；元数据携带名称与图标供对端显示。
        var response = await _host.Client.SendMessageAsync(new Uri(endpoint), report.ToJson(), sessionId, cancellationToken);
        var receiptText = response.Message?.Parts?.Select(p => p.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";

        // 主管回执写入本地会话，便于业务员查看"已接收/需修订"。
        var replyEvent = SessionEvent.Create(sessionId, identity.NodeId, SessionEventType.MessageReceived,
            new { text = receiptText, kind = "daily-sales-report-receipt" },
            senderName: peer.DisplayName ?? manager.DisplayName, senderIcon: peer.Icon ?? manager.Icon);
        await _host.Store.AppendEventAsync(replyEvent, cancellationToken: cancellationToken);

        return new DailySalesSendResult(report, sessionId, receiptText, receiptText);
    }

    private async Task<string> GetOrCreatePeerSessionIdAsync(string selfNodeId, string managerNodeId, CancellationToken cancellationToken)
    {
        var sessions = await _host.Store.GetSessionsAsync(cancellationToken);
        var existing = sessions.FirstOrDefault(s => s.Kind == SessionKind.Peer &&
            s.Members.Any(m => m.NodeId == managerNodeId));
        if (existing is not null)
        {
            return existing.SessionId;
        }
        var session = A2ASession.CreatePeer(Guid.CreateVersion7().ToString("N"), selfNodeId, managerNodeId);
        await _host.Store.SaveSessionAsync(session, cancellationToken);
        return session.SessionId;
    }

    private static TimeZoneInfo SafeTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
