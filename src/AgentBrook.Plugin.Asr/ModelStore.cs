using System.Security.Cryptography;
using System.Text.Json;
using AgentBrook.Plugin.Abstractions;

namespace AgentBrook.Plugin.Asr;

/// <summary>单个模型文件描述：名称 + 期望 SHA-256 + 大小（服务端清单/本地校验共用）。</summary>
public sealed class ModelFileSpec
{
    public required string Name { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
}

/// <summary>模型组（model + tokens 必须配对同组校验）。</summary>
public sealed class ModelGroupSpec
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ModelFileSpec[] Files { get; init; }
}

/// <summary>
/// 模型存管（doc/Voice/02 §4.3，服务端统一发布模式——对齐 privacy-masking）：
/// 模型发布在服务端插件目录 {ServerBase}/models/{groupId}/，下载一律走宿主统一下载服务
/// （host.Download，禁自建 HttpClient），逐文件 SHA-256 校验（.part 原子入位），
/// 落 workspace/models/asr/ 共享区（D7——插件升级/重装不重下），models.json 记录已装组。
/// </summary>
public sealed class ModelStore
{
    /// <summary>SenseVoice-Small int8（G-A0 spike 实测 2026-11-06：RTF≈0.02）。</summary>
    public static readonly ModelGroupSpec SenseVoiceSmallInt8 = new()
    {
        Id = "sensevoice-small-int8",
        DisplayName = "SenseVoice-Small (int8, ~228MB, 中/英/日/韩/粤)",
        Files =
        [
            new ModelFileSpec
            {
                Name = "model.int8.onnx",
                Sha256 = "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51",
                Size = 239233841,
            },
            new ModelFileSpec
            {
                Name = "tokens.txt",
                Sha256 = "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc",
                Size = 315894,
            },
        ],
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>服务端基址（默认生产；本地测试指向 AgentBrook.Service /updated/plugin/asr——同 privacy 约定）。</summary>
    public string ServerBase { get; set; } = "https://helper.agentbrook.com/updated/plugin/asr";

    private readonly string _dir;
    private readonly string _manifestPath;
    private readonly IPluginDownloadService _download;
    private readonly Action<string> _log;

    public ModelStore(string workspaceRoot, IPluginDownloadService download, Action<string> log)
    {
        _dir = Path.Combine(workspaceRoot, "models", "asr");
        _manifestPath = Path.Combine(_dir, "models.json");
        _download = download;
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    public string Dir => _dir;

    /// <summary>当前激活模型组的文件路径（未安装返回 null）。默认 = SenseVoice 组。</summary>
    public (string ModelPath, string TokensPath)? GetInstalledPaths(ModelGroupSpec? group = null)
    {
        group ??= SenseVoiceSmallInt8;
        var modelPath = Path.Combine(_dir, group.Files[0].Name);
        var tokensPath = Path.Combine(_dir, group.Files[1].Name);
        return File.Exists(modelPath) && File.Exists(tokensPath) ? (modelPath, tokensPath) : null;
    }

    /// <summary>是否已安装且校验通过（轻量：只查存在 + 大小；完整校验走 VerifyAsync）。</summary>
    public bool IsInstalledQuick(ModelGroupSpec? group = null)
    {
        group ??= SenseVoiceSmallInt8;
        foreach (var f in group.Files)
        {
            var p = Path.Combine(_dir, f.Name);
            if (!File.Exists(p)) return false;
            try
            {
                var len = new FileInfo(p).Length;
                if (len != f.Size && f.Name != "tokens.txt") return false;   // tokens 行数差异允许（版本微调）
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>完整校验：逐文件 SHA-256。慢（228MB），仅自检动作调用。</summary>
    public async Task<(bool Ok, string Message)> VerifyAsync(ModelGroupSpec? group = null, CancellationToken ct = default)
    {
        group ??= SenseVoiceSmallInt8;
        foreach (var f in group.Files)
        {
            var p = Path.Combine(_dir, f.Name);
            if (!File.Exists(p)) return (false, $"missing:{f.Name}");
            var sha = await Task.Run(() => ComputeSha256(p), ct);
            if (!string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return (false, $"sha-mismatch:{f.Name}");
        }
        return (true, "ok");
    }

    /// <summary>
    /// 从服务端安装：{ServerBase}/models/{groupId}/{file} 逐文件下载（.part 原子入位），
    /// SHA-256 与插件内置 spec 校验一致才落位并写 models.json。单源（服务端统一发布），失败即报。
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallAsync(ModelGroupSpec group, IProgress<string>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var installed = new List<string>();
        try
        {
            foreach (var f in group.Files)
            {
                if (ct.IsCancellationRequested) return (false, "cancelled");
                var dest = Path.Combine(_dir, f.Name);
                var part = dest + ".part";
                var url = $"{ServerBase}/models/{group.Id}/{f.Name}";
                try
                {
                    _log($"[asr] 下载 {f.Name} ← {ServerBase}");
                    progress?.Report($"{f.Name} ← 服务端");
                    File.Delete(part);
                    var fileProgress = progress is null ? null : new Progress<PluginDownloadProgress>(p =>
                    {
                        var pct = p.Total is > 0 ? (int)(p.Bytes * 100 / p.Total.Value) : 0;
                        progress.Report($"{f.Name} {pct}% ({p.Bytes / 1024 / 1024}MB)");
                    });
                    await _download.DownloadFileAsync(url, part, fileProgress, ct);
                    var sha = await Task.Run(() => ComputeSha256(part), ct);
                    if (!string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(part);
                        return (false, $"sha-mismatch:{f.Name}（服务端文件与插件内置校验值不符——版本不匹配？）");
                    }
                    File.Move(part, dest, overwrite: true);
                    installed.Add(dest);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log($"[asr] 下载失败（{f.Name}）：{ex.Message}");
                    try { File.Delete(part); } catch { }
                    return (false, $"download-failed:{f.Name}");
                }
            }

            WriteManifest(group);
            return (true, "installed");
        }
        finally
        {
            foreach (var p in installed) try { File.Delete(p + ".part"); } catch { }
        }
    }

    /// <summary>删除已装模型（共享区文件 + models.json；重装需重新下载）。</summary>
    public (bool Ok, string Message) Remove(ModelGroupSpec? group = null)
    {
        group ??= SenseVoiceSmallInt8;
        var removed = 0;
        foreach (var f in group.Files)
        {
            var p = Path.Combine(_dir, f.Name);
            if (File.Exists(p)) { File.Delete(p); removed++; }
        }
        if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
        return (true, $"removed:{removed}");
    }

    private void WriteManifest(ModelGroupSpec group)
    {
        var manifest = new
        {
            version = 1,
            activeGroup = group.Id,
            serverBase = ServerBase,
            downloadedAt = DateTimeOffset.UtcNow.ToString("O"),
            files = group.Files.Select(f => new { name = f.Name, sha256 = f.Sha256, size = f.Size }).ToArray(),
        };
        File.WriteAllText(_manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
