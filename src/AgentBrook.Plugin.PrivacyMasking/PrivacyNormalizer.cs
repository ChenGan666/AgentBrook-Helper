namespace AgentBrook.Privacy;

/// <summary>命中文本 → 规范形（canonical，§5.4 规范化规则）。同一实体多种写法归一到同一键。</summary>
public static class PrivacyNormalizer
{
    public static string Canonicalize(string category, string text) => category switch
    {
        PrivacyCategories.Phone => DigitsOnly(text),
        PrivacyCategories.BankCard => DigitsOnly(text),
        PrivacyCategories.IdCard => text.Trim().ToUpperInvariant(),   // 尾字母 X 统一大写
        PrivacyCategories.Uscc => text.Trim().ToUpperInvariant(),
        PrivacyCategories.Email => text.Trim().ToLowerInvariant(),
        _ => text.Trim(),
    };

    private static string DigitsOnly(string text)
    {
        var sb = new string(text.Where(char.IsAsciiDigit).ToArray());
        return sb;
    }
}
