using System.Text;
using System.Text.Json;

namespace AgentBrook.A2A.Core.Business;

/// <summary>
/// 主管下达的本周工作总结任务规格（文档 §26.2 的业务 Manifest）。
/// 通过 TaskManifest.Extensions 携带，签名覆盖整个 Manifest，因此业务字段同样不可篡改。
/// </summary>
public sealed class WeeklySummaryTaskSpec
{
    public const string Schema = "io.agentbrook.business.weekly-summary-task/v1";
    public const string ManifestExtensionKey = "io.agentbrook.business.weekly-summary-task";

    public string DepartmentId { get; set; } = "";
    public string Title { get; set; } = "本周工作总结";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string TimeZone { get; set; } = "Asia/Shanghai";
    public DateTimeOffset? Deadline { get; set; }
    public List<string> RequiredSections { get; set; } = new();
    public string ExpectedOutputName { get; set; } = "weekly-summary.md";
    public bool AllowAttachments { get; set; } = true;

    public static void WriteTo(Tasks.TaskManifest manifest, WeeklySummaryTaskSpec spec)
    {
        manifest.Extensions ??= new Dictionary<string, JsonElement>();
        manifest.Extensions[ManifestExtensionKey] = JsonSerializer.SerializeToElement(spec);
    }

    public static WeeklySummaryTaskSpec? TryReadFrom(Tasks.TaskManifest manifest)
    {
        if (manifest.Extensions is null ||
            !manifest.Extensions.TryGetValue(ManifestExtensionKey, out var element))
        {
            return null;
        }
        try
        {
            return element.Deserialize<WeeklySummaryTaskSpec>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public enum WeeklyMemberStatus
{
    Pending,
    Offered,
    Accepted,
    Rejected,
    Running,
    Delivered,
    RevisionRequested,
    Overdue,
    Offline
}

/// <summary>校验成员周报 Markdown 是否包含全部必填章节（文档 §26.2 的结构化修订依据）。</summary>
public static class WeeklySummaryValidator
{
    public static List<string> MissingSections(string markdown, IReadOnlyList<string> requiredSections)
    {
        var missing = new List<string>();
        foreach (var section in requiredSections)
        {
            if (!markdown.Contains(section, StringComparison.Ordinal))
            {
                missing.Add(section);
            }
        }
        return missing;
    }
}

public sealed class DepartmentWeeklyReportEntry
{
    public string MemberNodeId { get; set; } = "";
    public string MemberDisplayName { get; set; } = "";
    public string MemberIcon { get; set; } = "🧑‍💻";
    public WeeklyMemberStatus Status { get; set; } = WeeklyMemberStatus.Pending;
    public string? ArtifactId { get; set; }
    public int Revisions { get; set; }
    public string? SummaryContent { get; set; }
    public string? RejectionReason { get; set; }
    public List<int> Ratings { get; set; } = new();
}

/// <summary>部门工作周报（文档 §26.2 用户 B Helper 的最终成果）。</summary>
public sealed class DepartmentWeeklyReport
{
    public const string Schema = "io.agentbrook.business.department-weekly-report/v1";

    public string DepartmentId { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public int ExpectedCount { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public int MissingCount { get; set; }
    public double Coverage => ExpectedCount == 0 ? 0 : Math.Round(AcceptedCount * 100.0 / ExpectedCount, 1);
    public List<DepartmentWeeklyReportEntry> Entries { get; set; } = new();
    public DepartmentReportState State { get; set; } = DepartmentReportState.Completed;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ReportId { get; set; } = Guid.CreateVersion7().ToString("N");

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# 部门工作周报（{StartDate} 至 {EndDate}）");
        builder.AppendLine();
        builder.AppendLine($"- 部门：{DepartmentId}");
        builder.AppendLine($"- 状态：{(State == DepartmentReportState.Completed ? "全员完整交付" : "部分完成（存在拒绝/缺交成员）")}");
        builder.AppendLine($"- 覆盖率：应交 {ExpectedCount} 人，已交 {AcceptedCount} 人，覆盖率 {Coverage}%");
        builder.AppendLine();
        builder.AppendLine("## 成员提交状态");
        builder.AppendLine();
        builder.AppendLine("| 成员 | 状态 | 修订次数 | 成果 |");
        builder.AppendLine("|---|---|---:|---|");
        foreach (var entry in Entries)
        {
            var artifact = entry.ArtifactId is null ? "-" : entry.ArtifactId[..Math.Min(12, entry.ArtifactId.Length)];
            builder.AppendLine($"| {entry.MemberIcon} {entry.MemberDisplayName} | {entry.Status} | {entry.Revisions} | {artifact} |");
        }
        builder.AppendLine();
        builder.AppendLine("## 成员总结汇编");
        foreach (var entry in Entries.Where(x => !string.IsNullOrWhiteSpace(x.SummaryContent)))
        {
            builder.AppendLine();
            builder.AppendLine($"### {entry.MemberIcon} {entry.MemberDisplayName}");
            builder.AppendLine();
            builder.AppendLine(entry.SummaryContent!.TrimEnd());
        }
        builder.AppendLine();
        builder.AppendLine($"生成时间：{GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"ReportId：{ReportId}");
        return builder.ToString();
    }
}
