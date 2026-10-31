namespace AgentBrook.Privacy.Detectors;

/// <summary>
/// 识别命中（区段 + 标签类别）。Canonical 由词典命中直接给出（实体规范形）；
/// 正则命中为 null，由 Masker 按类别归一（§5.4 规范化规则）。
/// Priority 用于贪心排序键（17 号 §3.3：Priority 降序 → 起点升序 → 长度降序；词典 95 / 规则 50–90 / NER 30）。
/// AutoFill=false（P2 NER）：命中走高位临时段占位——不进 overlay、不耗号段、不进 DrainPending（防误报固化）。
/// </summary>
public sealed record PrivacyMatch(int Start, int Length, string Category, string? Canonical = null, int Priority = 50, bool AutoFill = true);

/// <summary>识别器：返回文本内全部命中（与保护段的重叠过滤由 Masker 统一处理）。</summary>
public interface IRecognizer
{
    IReadOnlyList<PrivacyMatch> Detect(string text);
}
