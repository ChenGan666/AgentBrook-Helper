using System.Security.Cryptography;
using System.Text;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Messaging.Abstractions;

/// <summary>
/// 出站幂等指纹（桥契约 v1.4 精确定义）：同 DeliveryId 判重依据 =
/// <b>目标账号 + chatId + 文本 + 有序附件元组（name, mediaType, sha256）</b>。
/// 顺序、重复文件、文件名都参与比较；重签的下载 URL/临时凭证不属于 payload
/// （本指纹输入根本不含 URL/凭证——结构上保证"重签 URL 不冲突"，C05）。
/// 桥端实现必须与该算法语义一致（契约测试 B02 验证）。
/// </summary>
public static class DeliveryFingerprint
{
    public static string Compute(DeliveryRequest request) =>
        Compute(request.AccountId, request.ChatId, request.Text, request.Files);

    public static string Compute(string accountId, string chatId, string? text, IReadOnlyList<ContentRef>? files)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            WriteField(w, accountId ?? "");
            WriteField(w, chatId ?? "");
            WriteField(w, text ?? "");
            var count = files?.Count ?? 0;
            w.Write(count);
            if (files is not null)
            {
                foreach (var f in files)   // 有序：顺序变化 → 指纹变化
                {
                    WriteField(w, f.FileName);
                    WriteField(w, f.MediaType ?? "");
                    WriteField(w, f.Sha256);
                }
            }
        }
        var hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>长度前缀写入，避免字段拼接歧义（"ab"+"c" vs "a"+"bc"）。</summary>
    private static void WriteField(BinaryWriter w, string value)
    {
        w.Write7BitEncodedInt(value.Length);
        w.Write(Encoding.UTF8.GetBytes(value));
    }
}
