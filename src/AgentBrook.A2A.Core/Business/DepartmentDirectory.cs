using System.Text.Json;

namespace AgentBrook.A2A.Core.Business;

/// <summary>部门成员角色（文档 §26）：主管负责收集与汇总，业务员负责上报与提交总结。</summary>
public static class DepartmentRoles
{
    public const string Manager = "department-manager";
    public const string SalesRepresentative = "sales-representative";
}

public sealed class DepartmentMember
{
    public required string NodeId { get; init; }
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "🧑‍💼";
    public string Role { get; set; } = DepartmentRoles.SalesRepresentative;
    public bool Active { get; set; } = true;
}

/// <summary>
/// 可信部门成员目录（文档 §26 前提）：由用户或管理员预先建立，
/// 以稳定 nodeId 标识成员并以角色区分职责，不得仅凭显示名称判断角色。
/// </summary>
public sealed class DepartmentDirectory
{
    public const string FileName = "department-directory.json";

    public string DepartmentId { get; set; } = "sales-east";
    public string Name { get; set; } = "";
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public List<DepartmentMember> Members { get; set; } = new();

    public DepartmentMember? Find(string nodeId) =>
        Members.FirstOrDefault(x => x.NodeId == nodeId && x.Active);

    public DepartmentMember? FindManager() =>
        Members.FirstOrDefault(x => x.Active && x.Role == DepartmentRoles.Manager);

    public List<DepartmentMember> SalesRepresentatives() =>
        Members.Where(x => x.Active && x.Role == DepartmentRoles.SalesRepresentative).ToList();

    public string? RoleOf(string nodeId) => Find(nodeId)?.Role;

    public static string PathFor(string a2aDataDirectory) => Path.Combine(a2aDataDirectory, FileName);

    public async Task SaveAsync(string a2aDataDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(a2aDataDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(PathFor(a2aDataDirectory), json, cancellationToken);
    }

    public static async Task<DepartmentDirectory> LoadAsync(string a2aDataDirectory, CancellationToken cancellationToken = default)
    {
        var path = PathFor(a2aDataDirectory);
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<DepartmentDirectory>(json)
            ?? throw new InvalidDataException($"Invalid department directory: {path}");
    }
}
