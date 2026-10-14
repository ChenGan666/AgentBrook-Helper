using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBrook.A2A.Core.Identity;

public sealed class NodeIdentityService
{
    private const string PrivateKeyFileName = "node.key";
    private const string IdentityFileName = "node.json";

    private readonly string _identityDirectory;
    private readonly ECDsa _key;
    private readonly NodeIdentity _identity;

    private NodeIdentityService(string identityDirectory, ECDsa key, NodeIdentity identity)
    {
        _identityDirectory = identityDirectory;
        _key = key;
        _identity = identity;
    }

    public NodeIdentity Identity => _identity;

    public static async Task<NodeIdentityService> LoadOrCreateAsync(
        string identityDirectory,
        string displayName,
        CancellationToken cancellationToken = default,
        string icon = "🤖")
    {
        Directory.CreateDirectory(identityDirectory);
        var keyPath = Path.Combine(identityDirectory, PrivateKeyFileName);
        var identityPath = Path.Combine(identityDirectory, IdentityFileName);

        ECDsa key;
        NodeIdentity identity;

        if (File.Exists(keyPath) && File.Exists(identityPath))
        {
            var keyBytes = Convert.FromBase64String(await File.ReadAllTextAsync(keyPath, cancellationToken));
            key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out _);
            var stored = JsonSerializer.Deserialize<StoredIdentity>(await File.ReadAllTextAsync(identityPath, cancellationToken))
                ?? throw new InvalidDataException("Invalid identity file.");
            var expectedNodeId = DeriveNodeId(key.ExportSubjectPublicKeyInfo());
            if (!string.Equals(expectedNodeId, stored.NodeId, StringComparison.Ordinal))
            {
                key.Dispose();
                throw new InvalidDataException("Identity file does not match private key.");
            }
            identity = new NodeIdentity
            {
                NodeId = stored.NodeId!,
                DisplayName = stored.DisplayName ?? displayName,
                Icon = stored.Icon ?? icon,
                PublicKeyBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                InstanceId = Guid.NewGuid().ToString("N"),
                CreatedAt = stored.CreatedAt
            };
        }
        else
        {
            key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = key.ExportSubjectPublicKeyInfo();
            var nodeId = DeriveNodeId(publicKey);
            identity = new NodeIdentity
            {
                NodeId = nodeId,
                DisplayName = displayName,
                Icon = icon,
                PublicKeyBase64 = Convert.ToBase64String(publicKey),
                InstanceId = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow
            };
            var privateKey = key.ExportPkcs8PrivateKey();
            await File.WriteAllTextAsync(keyPath, Convert.ToBase64String(privateKey), cancellationToken);
            TryRestrictPrivateKeyPermissions(keyPath);
            var stored = new StoredIdentity
            {
                NodeId = nodeId,
                DisplayName = displayName,
                Icon = icon,
                CreatedAt = identity.CreatedAt
            };
            await File.WriteAllTextAsync(identityPath, JsonSerializer.Serialize(stored), cancellationToken);
        }

        return new NodeIdentityService(identityDirectory, key, identity);
    }

    public static string DeriveNodeId(byte[] subjectPublicKeyInfo)
    {
        var hash = SHA256.HashData(subjectPublicKeyInfo);
        return "abk:" + Base64UrlEncode(hash);
    }

    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public byte[] SignPayload(string payload) => Sign(System.Text.Encoding.UTF8.GetBytes(payload));

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        _key.VerifyData(data, signature, HashAlgorithmName.SHA256);

    public static bool VerifyWithPublicKey(byte[] subjectPublicKeyInfo, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public bool VerifyNodeId(string nodeId) =>
        string.Equals(nodeId, DeriveNodeId(_key.ExportSubjectPublicKeyInfo()), StringComparison.Ordinal);

    public async Task UpdateDisplayNameAsync(string displayName, CancellationToken cancellationToken = default) =>
        await UpdateProfileAsync(displayName, _identity.Icon, cancellationToken);

    public async Task UpdateIconAsync(string icon, CancellationToken cancellationToken = default) =>
        await UpdateProfileAsync(_identity.DisplayName, icon, cancellationToken);

    public async Task UpdateProfileAsync(string displayName, string icon, CancellationToken cancellationToken = default)
    {
        _identity.DisplayName = displayName;
        _identity.Icon = icon;
        var stored = new StoredIdentity
        {
            NodeId = _identity.NodeId,
            DisplayName = displayName,
            Icon = icon,
            CreatedAt = _identity.CreatedAt
        };
        await File.WriteAllTextAsync(
            Path.Combine(_identityDirectory, IdentityFileName),
            JsonSerializer.Serialize(stored),
            cancellationToken);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void TryRestrictPrivateKeyPermissions(string keyPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else
            {
                var info = new FileInfo(keyPath);
                var acl = info.GetAccessControl();
                acl.SetAccessRuleProtection(true, false);
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (sid is not null)
                {
                    acl.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        sid,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                    info.SetAccessControl(acl);
                }
            }
        }
        catch
        {
        }
    }

    private sealed class StoredIdentity
    {
        public string? NodeId { get; set; }
        public string? DisplayName { get; set; }
        public string? Icon { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
