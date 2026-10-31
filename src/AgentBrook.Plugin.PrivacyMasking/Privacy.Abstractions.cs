using System.Text.Json;

namespace AgentBrook.Privacy;

/// <summary>占位符标签类别全集（15 号设计附录 A）。词典侧与正则侧共用此清单做合法性校验。</summary>
public static class PrivacyCategories
{
    public const string Person = "PERSON";
    public const string Org = "ORG";
    public const string Product = "PRODUCT";
    public const string Project = "PROJECT";
    public const string Address = "ADDRESS";
    public const string Custom = "CUSTOM";
    public const string Phone = "PHONE";
    public const string Email = "EMAIL";
    public const string IdCard = "IDCARD";
    public const string BankCard = "BANKCARD";
    public const string Uscc = "USCC";
    public const string Plate = "PLATE";
    public const string Ip = "IP";
    public const string Url = "URL";
    public const string Passport = "PASSPORT";

    public static readonly IReadOnlyList<string> All =
    [
        Person, Org, Product, Project, Address, Custom,
        Phone, Email, IdCard, BankCard, Uscc, Plate, Ip, Url, Passport,
    ];

    public static bool IsKnown(string category) =>
        All.Contains(category, StringComparer.Ordinal);

    /// <summary>
    /// 归一化开放类别（词典不限于内置类，用户/模型可加 KEY、SECRET 等）：
    /// 大写、非 ASCII 字母数字折为 _、折叠重复下划线、去首尾 _、数字开头补 K_、≤32 字符。
    /// 结果为空 → null（调用方拒绝）。产物须匹配占位符文法 [类别_编号]。
    /// </summary>
    public static string? Normalize(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }
        var sb = new System.Text.StringBuilder(category.Trim().Length);
        foreach (var ch in category.Trim().ToUpperInvariant())
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        }
        var norm = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_{2,}", "_").Trim('_');
        if (norm.Length == 0)
        {
            return null;
        }
        if (!char.IsAsciiLetter(norm[0]) && norm[0] != '_')
        {
            norm = "K_" + norm;
        }
        return norm.Length <= 32 ? norm : norm[..32].TrimEnd('_');
    }
}

/// <summary>词典实体（entity 行）。Placeholder = [{category}_{no}]，编号分类内持久自增、删除不复用。</summary>
public sealed record EntityInfo(
    long Id,
    string Category,
    string Canonical,
    int PlaceholderNo,
    string Source,
    bool Enabled,
    string? Note,
    long CreatedAt,
    long UpdatedAt,
    long? LastUsedAt,
    long UseCount)
{
    public string Placeholder => $"[{Category}_{PlaceholderNo}]";
}

/// <summary>实体别名（entity_alias 行）。normalized = 匹配形（ASCII 转小写），全局唯一。</summary>
public sealed record AliasInfo(
    long Id,
    long EntityId,
    string Alias,
    string Normalized,
    string MatchMode,
    bool CaseSensitive,
    bool Enabled);

/// <summary>白名单条目（永不脱敏区段）。literal 存规范化形（ASCII 小写、空白折叠；CJK 原样）。</summary>
public sealed record WhitelistEntry(long Id, string Pattern, string Kind, string? Note);

/// <summary>overlay 批量落库的待写实体（尚未持久化，无 Id；Flush 时按 canonical 归并）。</summary>
public sealed record PendingEntity(string Category, string Canonical, int PlaceholderNo, string Source);

/// <summary>用量增量（按 canonical 键；对刚落库与已存在实体统一生效）。</summary>
public sealed record UsageDelta(string Category, string Canonical, int UseDelta, long LastUsedAtUnix);

/// <summary>CSV 批量导入结果（冲突跳过计数；非法类别直接抛错并带行号）。</summary>
public sealed record CsvImportResult(int Imported, int Skipped);

/// <summary>出站脱敏结果：ΔEmitted（tag→canonical，宿主累计）与 ΔEscape（全角转义形→原字面）。</summary>
public sealed record MaskOutcome(
    string Text,
    int MaskedCount,
    IReadOnlyDictionary<string, string> Emitted,
    IReadOnlyDictionary<string, string> Escapes)
{
    public static readonly MaskOutcome None = new("", 0,
        new Dictionary<string, string>(), new Dictionary<string, string>());
}

/// <summary>工具实参脱敏结果（与 MaskOutcome 同构——实参独现的占位符必须同样进 emittedMap）。</summary>
public sealed record ArgsMaskOutcome(
    object? Args,
    IReadOnlyDictionary<string, string> Emitted,
    IReadOnlyDictionary<string, string> Escapes);

/// <summary>
/// 引擎快照（不可变；写操作 bump Version 后惰性重建）。含设置视图 + NER 配置（17 号方案）；
/// 词典 / AC 自动机视图随识别器任务扩展。热路径（中间件）只读快照，不走 SQL。
/// </summary>
public sealed record PrivacySnapshot(
    long Version,
    bool MasterEnabled,
    string Engine,
    bool FailOpen,
    bool MaskNote,
    int HoldbackMax,
    int AutoRegisterLimit,
    IReadOnlyDictionary<string, bool> Categories,
    IReadOnlyDictionary<string, string> EgressPolicy,
    bool NerEnabled,
    string NerModel,
    double NerThreshold,
    IReadOnlyDictionary<string, bool> NerCategories,
    IReadOnlyDictionary<string, string> RawSettings);

/// <summary>
/// 脱敏引擎契约（15 号设计 §4.3，v4/v6）。引擎无会话集合状态：
/// emittedMap / escapeMap 由宿主中间件壳持有，既作出站判定的入参、又作入站还原的唯一依据；
/// 还原不查 Snapshot（回退即全表反查 = 重开枚举通道）。
/// </summary>
public interface IPrivacyEngine
{
    /// <summary>词典 + 设置 + 双向映射快照；MCP 形态下为同步副本。</summary>
    PrivacySnapshot Snapshot { get; }

    /// <summary>出站脱敏（文本）。emitted 半角占位符保护；形似占位符转义全角；增量随结果返回。</summary>
    MaskOutcome MaskText(string text,
        IReadOnlyDictionary<string, string> emittedMap,
        IReadOnlyDictionary<string, string> escapeMap);

    /// <summary>出站脱敏（工具实参；JSON 递归含属性名 key）。与 MaskOutcome 同构返回增量。</summary>
    ArgsMaskOutcome MaskToolArgs(object? args,
        IReadOnlyDictionary<string, string> emittedMap,
        IReadOnlyDictionary<string, string> escapeMap);

    /// <summary>入站还原（文本）：emittedMap 命中的半角标签还原 canonical；escapeMap 命中还原原字面。</summary>
    string UnmaskText(string text,
        IReadOnlyDictionary<string, string> emittedMap,
        IReadOnlyDictionary<string, string> escapeMap);

    /// <summary>入站还原（工具实参）：按 EgressPolicy（两级匹配）决定 restore / keep。</summary>
    object? RestoreToolArgs(object? args, string toolName,
        IReadOnlyDictionary<string, string> emittedMap,
        IReadOnlyDictionary<string, string> escapeMap);

    /// <summary>overlay 批量落库 + 统计写回（响应完成时由壳触发；幂等）。</summary>
    Task FlushAsync();

    /// <summary>回合边界（每次响应开始时由宿主调用）：auto 登记计数清零；临时映射不受影响（§8.1 / D16）。</summary>
    void BeginTurn();

    /// <summary>快照版本变化（词典 / 设置写操作后触发，宿主可热更新）。</summary>
    event Action? Changed;
}

/// <summary>词典写冲突：normalized / canonical 唯一约束命中（UI 单条添加需明确报错）。</summary>
public sealed class PrivacyConflictException(string message) : Exception(message);

/// <summary>CSV 格式 / 类别非法（带行号，批量导入 fail-fast）。</summary>
public sealed class PrivacyFormatException(string message) : Exception(message);
