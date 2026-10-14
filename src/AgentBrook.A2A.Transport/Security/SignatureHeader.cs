using System.Security.Cryptography;
using System.Text;

namespace AgentBrook.A2A.Transport.Security;

public sealed record SignatureHeader(string NodeId, long Timestamp, string Nonce, string SignatureBase64);

public static class SignatureHeaderParser
{
    public const string HeaderName = "X-AgentBrook-Signature";

    public static bool TryParse(string? headerValue, out SignatureHeader? header)
    {
        header = null;
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }
        var parts = headerValue.Split(';', 4);
        if (parts.Length != 4)
        {
            return false;
        }
        if (!long.TryParse(parts[1], out var timestamp))
        {
            return false;
        }
        header = new SignatureHeader(parts[0], timestamp, parts[2], parts[3]);
        return true;
    }

    public static string Build(string nodeId, long timestamp, string nonce, byte[] signature)
    {
        return $"{nodeId};{timestamp};{nonce};{Convert.ToBase64String(signature)}";
    }

    public static string BuildSigningPayload(string method, string path, long timestamp, string nonce)
    {
        return $"{method}|{path}|{timestamp}|{nonce}";
    }
}
