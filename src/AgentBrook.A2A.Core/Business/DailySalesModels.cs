using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBrook.A2A.Core.Business;

/// <summary>业务员当日销售汇总（文档 §26.1 的统一上报数据结构摘要部分）。</summary>
public sealed class DailySalesSummary
{
    public int OrderCount { get; set; }
    public decimal SalesAmount { get; set; }
    public decimal RefundAmount { get; set; }
    public int NewCustomerCount { get; set; }
}

public sealed class ReportSourcePeriod
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
}

/// <summary>
/// 业务员个人销售日报，schema io.agentbrook.business.daily-sales-report/v1。
/// 同一业务员同一日期重复上报时按 Revision 递增，主管 Helper 幂等去重。
/// </summary>
public sealed class DailySalesReport
{
    public const string Schema = "io.agentbrook.business.daily-sales-report/v1";

    public string SchemaField { get; set; } = Schema;
    public string ReportId { get; set; } = Guid.CreateVersion7().ToString("N");
    public string ReportDate { get; set; } = "";
    public string DepartmentId { get; set; } = "";
    public string ReporterNodeId { get; set; } = "";
    public string ReporterDisplayName { get; set; } = "";
    public string ReporterIcon { get; set; } = "🧑‍💻";
    public string Currency { get; set; } = "CNY";
    public DailySalesSummary Summary { get; set; } = new();
    public string? DetailsArtifactId { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public ReportSourcePeriod SourcePeriod { get; set; } = new();
    public string? Signature { get; set; }

    /// <summary>参与签名的规范化载荷（不含签名字段本身）。</summary>
    public string CanonicalPayload() =>
        string.Join("|", SchemaField, ReportId, ReportDate, DepartmentId, ReporterNodeId,
            Revision, Summary.OrderCount, Summary.SalesAmount, Summary.RefundAmount, Summary.NewCustomerCount,
            DetailsArtifactId ?? "", GeneratedAt.ToUnixTimeMilliseconds());

    public void Sign(Func<string, byte[]> sign) =>
        Signature = Convert.ToBase64String(sign(CanonicalPayload()));

    public bool Verify(byte[] subjectPublicKeyInfo) =>
        !string.IsNullOrWhiteSpace(Signature) &&
        NodeIdentitySigning.VerifyWithPublicKey(subjectPublicKeyInfo, CanonicalPayload(), Signature!);

    public string ToJson(JsonSerializerOptions? options = null) => JsonSerializer.Serialize(this, options);
}

public static class NodeIdentitySigning
{
    public static bool VerifyWithPublicKey(byte[] subjectPublicKeyInfo, string payload, string signatureBase64)
    {
        try
        {
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public enum DailyReportReceiptStatus
{
    Received,
    Rejected,
    RevisionRequested
}

/// <summary>主管 Helper 校验日报后返回的接收回执。</summary>
public sealed class DailySalesReportReceipt
{
    public string Schema { get; set; } = "io.agentbrook.business.daily-sales-report-receipt/v1";
    public string ReportId { get; set; } = "";
    public DailyReportReceiptStatus Status { get; set; } = DailyReportReceiptStatus.Received;
    public string ReceiverNodeId { get; set; } = "";
    public string ReceiverDisplayName { get; set; } = "";
    public List<string> Errors { get; set; } = new();
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public string ToJson(JsonSerializerOptions? options = null) => JsonSerializer.Serialize(this, options);
}

public enum DepartmentReportState
{
    Completed,
    PartialCompleted
}

public sealed class DepartmentDailyReportEntry
{
    public string ReporterNodeId { get; set; } = "";
    public string ReporterDisplayName { get; set; } = "";
    public string ReporterIcon { get; set; } = "🧑‍💻";
    public string ReportId { get; set; } = "";
    public int Revision { get; set; }
    public DailySalesSummary Summary { get; set; } = new();
    public string Status { get; set; } = "submitted";
    public DateTimeOffset? SubmittedAt { get; set; }
    public bool SignatureValid { get; set; }
}

/// <summary>
/// 部门销售日报汇总结果（文档 §26.1 用户 B Helper 的最终成果）：
/// 含全员指标、成员明细、已交/缺交名单、覆盖率与 PartialCompleted 状态。
/// </summary>
public sealed class DepartmentDailyReport
{
    public const string Schema = "io.agentbrook.business.department-daily-report/v1";

    public string DepartmentId { get; set; } = "";
    public string ReportDate { get; set; } = "";
    public string Currency { get; set; } = "CNY";
    public int ExpectedCount { get; set; }
    public int TotalOrderCount { get; set; }
    public decimal TotalSalesAmount { get; set; }
    public decimal TotalRefundAmount { get; set; }
    public int TotalNewCustomerCount { get; set; }
    public List<DepartmentDailyReportEntry> Entries { get; set; } = new();
    public List<string> MissingNodeIds { get; set; } = new();
    public List<string> InvalidReportIds { get; set; } = new();
    public DepartmentReportState State { get; set; } = DepartmentReportState.Completed;
    public double Coverage => ExpectedCount == 0 ? 0 : Math.Round(Entries.Count(x => x.Status == "submitted") * 100.0 / ExpectedCount, 1);
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    public string ReportId { get; set; } = Guid.CreateVersion7().ToString("N");

    /// <summary>汇总报告 Markdown 落盘后的 ArtifactId（由主管 Helper 写入）。</summary>
    public string? ReportArtifactId { get; set; }

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# 部门销售日报（{ReportDate}）");
        builder.AppendLine();
        builder.AppendLine($"- 部门：{DepartmentId}");
        builder.AppendLine($"- 状态：{(State == DepartmentReportState.Completed ? "全员完整数据" : "部分完成（存在缺交成员）")}");
        builder.AppendLine($"- 覆盖率：应交 {ExpectedCount} 人，已交 {Entries.Count(x => x.Status == "submitted")} 人，覆盖率 {Coverage}%");
        builder.AppendLine();
        builder.AppendLine("## 部门汇总");
        builder.AppendLine();
        builder.AppendLine($"- 总销售额：{TotalSalesAmount} {Currency}");
        builder.AppendLine($"- 总订单量：{TotalOrderCount}");
        builder.AppendLine($"- 总退款额：{TotalRefundAmount} {Currency}");
        builder.AppendLine($"- 新增客户数：{TotalNewCustomerCount}");
        builder.AppendLine();
        builder.AppendLine("## 成员明细");
        builder.AppendLine();
        builder.AppendLine("| 业务员 | 报告ID | 订单数 | 销售额 | 退款额 | 新客户 | 状态 | 签名校验 |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---|---|");
        foreach (var entry in Entries)
        {
            builder.AppendLine($"| {entry.ReporterIcon} {entry.ReporterDisplayName} | {entry.ReportId[..8]} | {entry.Summary.OrderCount} | {entry.Summary.SalesAmount} | {entry.Summary.RefundAmount} | {entry.Summary.NewCustomerCount} | {entry.Status} | {(entry.SignatureValid ? "通过" : "失败")} |");
        }
        if (MissingNodeIds.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"## 缺交名单（{MissingNodeIds.Count} 人）");
            foreach (var nodeId in MissingNodeIds)
            {
                builder.AppendLine($"- {nodeId}");
            }
        }
        if (InvalidReportIds.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"## 校验失败报告（{InvalidReportIds.Count} 份）");
            foreach (var reportId in InvalidReportIds)
            {
                builder.AppendLine($"- {reportId}");
            }
        }
        builder.AppendLine();
        builder.AppendLine($"生成时间：{GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        return builder.ToString();
    }
}
