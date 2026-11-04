using System.Globalization;

namespace AgentBrook.Runtime.Abstractions;

/// <summary>
/// 宿主 Blob 服务签发的内容引用（设计 §4.2）：出站附件与物化结果统一形态。
/// 持久化 DTO 规则：不含流/委托/宿主绝对路径；审批与幂等比较都绑定内容 hash。
/// </summary>
public sealed record ContentRef(string BlobId, string Sha256, long Length, string FileName, string? MediaType)
{
    public bool IsValid =>
        IsValidBlobId(BlobId) &&
        IsValidSha256(Sha256) &&
        Length >= 0 &&
        IsValidFileName(FileName);

    /// <summary>BlobId 由宿主生成：非空、无路径分隔符、非相对导航片段（防目录逃逸）。</summary>
    public static bool IsValidBlobId(string? blobId) =>
        !string.IsNullOrWhiteSpace(blobId) &&
        blobId.IndexOfAny(['/', '\\']) < 0 &&
        blobId != "." && blobId != "..";

    /// <summary>SHA256 十六进制小写 64 位。</summary>
    public static bool IsValidSha256(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64) return false;
        foreach (var c in sha256)
        {
            var isHexLower = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHexLower) return false;
        }
        return true;
    }

    /// <summary>FileName 仅作显示元数据：禁止路径分隔符与导航片段（物理路径一律用生成 BlobId）。</summary>
    public static bool IsValidFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName.IndexOfAny(['/', '\\']) < 0 &&
        fileName != "." && fileName != "..";

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{FileName}#{Sha256[..12]}({Length}B)");
}
