using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBrook.Plugin.Im.Weixin;

/// <summary>
/// 微信媒体（iLink CDN）引用与加解密（协议 §2.9）：
/// 下载优先 <c>media.full_url</c>；否则 `{cdnBase}/download?encrypted_query_param=…`。
/// AES-128-ECB + PKCS#7；aes_key 两种实际编码都要兼容（base64(16 字节原文) 或 base64(32 字符 hex)）。
/// </summary>
internal static class WeixinMedia
{
    /// <summary>官方默认 CDN 基础地址（仅在无 full_url 时用于拼接）。</summary>
    internal const string CdnBase = "https://novac2c.cdn.weixin.qq.com/c2c";

    internal const string Provider = "weixin-cdn";

    /// <summary>附件描述符里 RemoteId 承载的引用（JSON → base64，避免丢失字段）。</summary>
    internal sealed record MediaRef(
        string FullUrl,
        string EncryptQueryParam,
        string AesKeyRaw,       // 原始字符串（base64 或 hex），解析在 Decrypt 时做
        string? FileName,
        long? Size);

    internal static string Encode(MediaRef reference) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reference)));

    internal static MediaRef? Decode(string remoteId)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(remoteId));
            return JsonSerializer.Deserialize<MediaRef>(json);
        }
        catch { return null; }
    }

    /// <summary>下载 URL：full_url 优先；否则按 CDN 基础地址拼接。</summary>
    internal static string ResolveDownloadUrl(MediaRef reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.FullUrl)) return reference.FullUrl!;
        if (string.IsNullOrWhiteSpace(reference.EncryptQueryParam))
            throw new InvalidOperationException("[BadPayload] 媒体引用缺少可下载参数（full_url/encrypt_query_param 均为空）。");
        return $"{CdnBase}/download?encrypted_query_param={Uri.EscapeDataString(reference.EncryptQueryParam)}";
    }

    /// <summary>AES key 解析：支持 base64(16 字节) / base64(32 字符 hex) / 直接 32 字符 hex。</summary>
    internal static byte[] ResolveKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("[BadPayload] 媒体引用缺少 aes_key。");
        var s = raw.Trim();
        // 直接 hex
        if (s.Length == 32 && IsHex(s)) return Convert.FromHexString(s);
        // base64 包装
        byte[] decoded;
        try { decoded = Convert.FromBase64String(s); }
        catch (FormatException) { throw new InvalidOperationException("[BadPayload] aes_key 不是合法的 base64/hex。"); }
        if (decoded.Length == 16) return decoded;                       // base64(16 字节原文)
        var ascii = Encoding.UTF8.GetString(decoded);
        if (ascii.Length == 32 && IsHex(ascii)) return Convert.FromHexString(ascii);   // base64(32 字符 hex)
        throw new InvalidOperationException("[BadPayload] aes_key 长度不符合（需 16 字节或 32 字符 hex）。");
    }

    /// <summary>AES-128-ECB + PKCS#7 解密。</summary>
    internal static byte[] DecryptAesEcb(byte[] cipher, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    /// <summary>AES-128-ECB + PKCS#7 加密（出站媒体上传用）。</summary>
    internal static byte[] EncryptAesEcb(byte[] plain, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    /// <summary>messsage item 的 aes_key 形态：base64(32 字符 hex)。</summary>
    internal static string ToItemAesKey(byte[] key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexString(key).ToLowerInvariant()));

    private static bool IsHex(string s) => s.All(c => Uri.IsHexDigit(c));
}
