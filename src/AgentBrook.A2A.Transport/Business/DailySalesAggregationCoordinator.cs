using System.Text.Json;
using AgentBrook.A2A.Core.Artifacts;
using AgentBrook.A2A.Core.Business;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Transport.Hosting;

namespace AgentBrook.A2A.Transport.Business;

public sealed record DailyReportIngestResult(DailySalesReportReceipt Receipt, bool Accepted);

/// <summary>
/// §26.1 场景一 · 主管 Helper 侧：接收业务员日报，校验身份/角色/签名/日期/Schema 并幂等去重，
/// 按 departmentId + reportDate 维护"每日销售汇总"Group Session（仅主管可见，满足最小披露），
/// 截止后聚合生成部门日报（含覆盖率、缺交名单与 PartialCompleted 状态）。
/// </summary>
public sealed class DailySalesAggregationCoordinator
{
    private readonly A2AHost _host;
    private readonly DepartmentDirectory _directory;

    public DailySalesAggregationCoordinator(A2AHost host, DepartmentDirectory directory)
    {
        _host = host;
        _directory = directory;
    }

    public string GroupIdFor(DateOnly reportDate) =>
        $"dsr-{_directory.DepartmentId}-{reportDate:yyyy-MM-dd}";

    /// <summary>业务消息钩子入口：文本为日报 JSON 时 ingest 并返回回执文本，否则返回 null 交回默认 Ack。</summary>
    public async Task<string?> HandleIncomingMessageAsync(string senderNodeId, string text, CancellationToken cancellationToken = default)
    {
        var report = TryParseReport(text);
        if (report is null)
        {
            return null;
        }
        var result = await IngestAsync(senderNodeId, report, cancellationToken);
        return result.Receipt.ToJson();
    }

    public static DailySalesReport? TryParseReport(string text)
    {
        try
        {
            var report = JsonSerializer.Deserialize<DailySalesReport>(text);
            return report is not null && report.SchemaField == DailySalesReport.Schema ? report : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<DailyReportIngestResult> IngestAsync(string senderNodeId, DailySalesReport report, CancellationToken cancellationToken = default)
    {
        var receipt = new DailySalesReportReceipt
        {
            ReportId = report.ReportId,
            ReceiverNodeId = _host.Identity.Identity.NodeId,
            ReceiverDisplayName = _host.Identity.Identity.DisplayName
        };
        var errors = Validate(senderNodeId, report);
        if (errors.Count == 0 && !await VerifyReportSignatureAsync(report, cancellationToken))
        {
            errors.Add("report signature does not verify against the paired peer public key.");
        }
        if (errors.Count > 0)
        {
            receipt.Status = DailyReportReceiptStatus.Rejected;
            receipt.Errors = errors;
            return new DailyReportIngestResult(receipt, Accepted: false);
        }

        var reportDate = DateOnly.Parse(report.ReportDate);
        var groupId = GroupIdFor(reportDate);
        await EnsureAggregationSessionAsync(reportDate, groupId, cancellationToken);

        var existing = await FindExistingReportEventAsync(groupId, report.ReportId, cancellationToken);
        if (existing is not null && ParseReportPayload(existing) is { } previous && previous.Revision >= report.Revision)
        {
            receipt.Status = DailyReportReceiptStatus.Received;
            receipt.Errors.Add($"duplicate report {report.ReportId} revision {report.Revision} ignored (have {previous.Revision})");
            return new DailyReportIngestResult(receipt, Accepted: false);
        }

        var identity = _host.Identity.Identity;
        var entry = SessionEvent.Create(groupId, senderNodeId, SessionEventType.MessageReceived,
            new { kind = DailySalesReport.Schema, report = report.ToJson() },
            senderName: report.ReporterDisplayName, senderIcon: report.ReporterIcon);
        await _host.Store.AppendEventAsync(entry, cancellationToken: cancellationToken);

        var receiptEvent = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.MessagePosted,
            new { kind = receipt.Schema, receipt = receipt.ToJson(), summary = $"已接收 {report.ReporterDisplayName} 的日报（revision {report.Revision}）" },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(receiptEvent, cancellationToken: cancellationToken);

        return new DailyReportIngestResult(receipt, Accepted: true);
    }

    /// <summary>聚合当日全部有效日报，生成部门日报 Markdown Artifact 与结构化结果。</summary>
    public async Task<DepartmentDailyReport> AggregateAsync(DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        var groupId = GroupIdFor(reportDate);
        var events = await _host.Store.ReadEventsAsync(groupId, cancellationToken: cancellationToken);
        var latest = new Dictionary<string, DailySalesReport>();
        foreach (var entry in events)
        {
            if (entry.Type != SessionEventType.MessageReceived) continue;
            var report = ParseReportPayload(entry);
            if (report is null) continue;
            if (!latest.TryGetValue(report.ReporterNodeId, out var current) || report.Revision > current.Revision)
            {
                latest[report.ReporterNodeId] = report;
            }
        }

        var reps = _directory.SalesRepresentatives();
        var aggregate = new DepartmentDailyReport
        {
            DepartmentId = _directory.DepartmentId,
            ReportDate = reportDate.ToString("yyyy-MM-dd"),
            Currency = "CNY",
            ExpectedCount = reps.Count
        };
        foreach (var report in latest.Values.OrderBy(x => x.ReporterNodeId))
        {
            var signatureValid = await VerifyReportSignatureAsync(report, cancellationToken);
            var member = _directory.Find(report.ReporterNodeId);
            aggregate.Entries.Add(new DepartmentDailyReportEntry
            {
                ReporterNodeId = report.ReporterNodeId,
                ReporterDisplayName = report.ReporterDisplayName,
                ReporterIcon = report.ReporterIcon,
                ReportId = report.ReportId,
                Revision = report.Revision,
                Summary = report.Summary,
                Status = "submitted",
                SubmittedAt = entrySubmittedAt(events, report),
                SignatureValid = signatureValid
            });
            aggregate.TotalOrderCount += report.Summary.OrderCount;
            aggregate.TotalSalesAmount += report.Summary.SalesAmount;
            aggregate.TotalRefundAmount += report.Summary.RefundAmount;
            aggregate.TotalNewCustomerCount += report.Summary.NewCustomerCount;
            if (!signatureValid) aggregate.InvalidReportIds.Add(report.ReportId);
        }
        foreach (var rep in reps.Where(x => !latest.ContainsKey(x.NodeId)))
        {
            aggregate.MissingNodeIds.Add($"{rep.Icon} {rep.DisplayName} ({rep.NodeId[..Math.Min(14, rep.NodeId.Length)]}…)");
        }
        aggregate.State = aggregate.MissingNodeIds.Count == 0 && aggregate.InvalidReportIds.Count == 0
            ? DepartmentReportState.Completed
            : DepartmentReportState.PartialCompleted;

        // 部门日报同时作为 Artifact 落盘，供后续系统消费与追溯。
        var markdown = aggregate.ToMarkdown();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown));
        var stored = await _host.ArtifactStore.PutAsync(stream, new ArtifactMetadata
        {
            Name = $"department-daily-{aggregate.ReportDate}.md",
            MediaType = "text/markdown",
            CreatedBy = _host.Identity.Identity.NodeId,
            SessionId = groupId
        }, cancellationToken);
        aggregate.ReportArtifactId = stored.ArtifactId;

        var identity = _host.Identity.Identity;
        var summaryEvent = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.ProgressReported,
            new
            {
                kind = DepartmentDailyReport.Schema,
                reportId = aggregate.ReportId,
                state = aggregate.State.ToString(),
                coverage = aggregate.Coverage,
                totalSales = aggregate.TotalSalesAmount,
                missing = aggregate.MissingNodeIds.Count
            },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(summaryEvent, cancellationToken: cancellationToken);
        return aggregate;
    }

    private static DateTimeOffset? entrySubmittedAt(List<SessionEvent> events, DailySalesReport report) =>
        events.FirstOrDefault(e => e.Type == SessionEventType.MessageReceived &&
            ParseReportPayload(e)?.ReportId == report.ReportId)?.Timestamp;

    private List<string> Validate(string senderNodeId, DailySalesReport report)
    {
        var errors = new List<string>();
        var member = _directory.Find(senderNodeId);
        if (member is null)
        {
            errors.Add($"sender {senderNodeId} is not an active member of department {_directory.DepartmentId}.");
        }
        else if (member.Role != DepartmentRoles.SalesRepresentative)
        {
            errors.Add($"sender role '{member.Role}' is not allowed to submit daily sales reports.");
        }
        if (!string.Equals(report.ReporterNodeId, senderNodeId, StringComparison.Ordinal))
        {
            errors.Add("reporterNodeId does not match the authenticated sender.");
        }
        if (!DateOnly.TryParse(report.ReportDate, out _))
        {
            errors.Add($"invalid reportDate '{report.ReportDate}'.");
        }
        if (report.Summary.SalesAmount < 0 || report.Summary.RefundAmount < 0)
        {
            errors.Add("sales/refund amounts must be non-negative.");
        }
        return errors;
    }

    private async Task<bool> VerifyReportSignatureAsync(DailySalesReport report, CancellationToken cancellationToken)
    {
        var peer = await _host.Store.GetPeerAsync(report.ReporterNodeId, cancellationToken);
        if (peer?.PublicKeyBase64 is null)
        {
            return false;
        }
        return report.Verify(Convert.FromBase64String(peer.PublicKeyBase64));
    }

    private async Task EnsureAggregationSessionAsync(DateOnly reportDate, string groupId, CancellationToken cancellationToken)
    {
        var existing = await _host.Store.GetSessionAsync(groupId, cancellationToken);
        if (existing is not null)
        {
            return;
        }
        var identity = _host.Identity.Identity;
        // 最小披露：聚合 Group Session 只含主管自己的执行上下文，业务员互不可见个人数据。
        var session = A2ASession.CreateGroup(groupId, identity.NodeId);
        session.Status = SessionStatus.Active;
        await _host.Store.SaveSessionAsync(session, cancellationToken);
        var created = SessionEvent.Create(groupId, identity.NodeId, SessionEventType.GroupCreated,
            new { departmentId = _directory.DepartmentId, reportDate = reportDate.ToString("yyyy-MM-dd"), purpose = "daily-sales-aggregation" },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await _host.Store.AppendEventAsync(created, cancellationToken: cancellationToken);
    }

    private async Task<SessionEvent?> FindExistingReportEventAsync(string groupId, string reportId, CancellationToken cancellationToken)
    {
        var events = await _host.Store.ReadEventsAsync(groupId, cancellationToken: cancellationToken);
        return events.FirstOrDefault(e => e.Type == SessionEventType.MessageReceived &&
            ParseReportPayload(e)?.ReportId == reportId);
    }

    private static DailySalesReport? ParseReportPayload(SessionEvent entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PayloadJson))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            if (!document.RootElement.TryGetProperty("kind", out var kind) ||
                kind.GetString() != DailySalesReport.Schema ||
                !document.RootElement.TryGetProperty("report", out var payload))
            {
                return null;
            }
            return payload.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<DailySalesReport>(payload.GetString()!)
                : payload.Deserialize<DailySalesReport>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
