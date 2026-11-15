using System.Security.Cryptography;
using System.Text.Json;
using AgentBrook.Plugin.Abstractions;

namespace AgentBrook.Plugin.Tts;

/// <summary>单个模型文件描述：名称 + 期望 SHA-256 + 大小（服务端清单/本地校验共用）。</summary>
public sealed class TtsModelFileSpec
{
    public required string Name { get; init; }

    /// <summary>相对模型组目录的子路径（如 dict/jieba.zhz...）；服务端按 {group}/{subpath} 发布。</summary>
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
}

/// <summary>模型组（kokoro-zh-v1_1：model/voices/tokens/lexicon×2/fst×3/dict/*；不含 espeak-ng-data——R2 定案）。</summary>
public sealed class TtsModelGroupSpec
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required TtsModelFileSpec[] Files { get; init; }
}

/// <summary>
/// 模型存管（照抄 Asr/ModelStore 范式，doc/Voice/07 §5.5）：服务端统一发布 + 恒定 SHA-256
/// + host.Download 逐文件下载（.part 原子入位），落 workspace/models/tts/ 共享区
/// （插件升级/重装/卸载不删模型——需求 §3.2），models.json 记录已装组。
/// SHA-256 为 S0 下载件实测钉死（构建脚本含漂移检查——services 与插件内置 spec 必须一致）。
/// </summary>
public sealed class TtsModelStore
{
    /// <summary>Kokoro v1.1 中英多语 int8（S0-1 实测钉 SHA；dict/* 明细见 InitDictFiles）。</summary>
    public static readonly TtsModelGroupSpec KokoroZhV11Int8 = new()
    {
        Id = "kokoro-int8-multi-lang-v1_1",
        DisplayName = "Kokoro v1.1 中英 (int8, ~170MB, 中/英)",
        Files = TtsModelStoreSpecs.AllFiles,
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>服务端基址（默认生产；本地测试指向 AgentBrook.Service /updated/plugin/tts）。</summary>
    public string ServerBase { get; set; } = "https://helper.agentbrook.com/updated/plugin/tts";

    private readonly string _dir;
    private readonly string _manifestPath;
    private readonly IPluginDownloadService _download;
    private readonly Action<string> _log;

    public TtsModelStore(string workspaceRoot, IPluginDownloadService download, Action<string> log)
    {
        _dir = Path.Combine(workspaceRoot, "models", "tts");
        _manifestPath = Path.Combine(_dir, "models.json");
        _download = download;
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    public string Dir => _dir;

    /// <summary>模型组目录（{dir}/{groupId}/；模型按组分目录，支持未来多组并存）。</summary>
    public string GroupDir(TtsModelGroupSpec? group = null) =>
        Path.Combine(_dir, (group ?? KokoroZhV11Int8).Id);

    /// <summary>是否已安装且校验通过（轻量：只查存在 + 大小；完整校验走 VerifyAsync）。</summary>
    public bool IsInstalledQuick(TtsModelGroupSpec? group = null)
    {
        group ??= KokoroZhV11Int8;
        foreach (var f in group.Files)
        {
            var p = Path.Combine(GroupDir(group), f.Name);
            if (!File.Exists(p)) return false;
            try
            {
                if (new FileInfo(p).Length != f.Size) return false;
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>完整校验：逐文件 SHA-256。慢（~170MB），仅自检动作调用。</summary>
    public async Task<(bool Ok, string Message)> VerifyAsync(TtsModelGroupSpec? group = null, CancellationToken ct = default)
    {
        group ??= KokoroZhV11Int8;
        foreach (var f in group.Files)
        {
            var p = Path.Combine(GroupDir(group), f.Name);
            if (!File.Exists(p)) return (false, $"missing:{f.Name}");
            var sha = await Task.Run(() => ComputeSha256(p), ct);
            if (!string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return (false, $"sha-mismatch:{f.Name}");
        }
        return (true, "ok");
    }

    /// <summary>
    /// 从服务端安装：{ServerBase}/models/{groupId}/{subpath} 逐文件下载（.part 原子入位），
    /// SHA-256 与插件内置 spec 一致才落位并写 models.json。失败即报、已落位文件保留（续装只补缺）。
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallAsync(TtsModelGroupSpec group, IProgress<string>? progress, CancellationToken ct = default)
    {
        var root = GroupDir(group);
        Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i < group.Files.Length; i++)
            {
                var f = group.Files[i];
                if (ct.IsCancellationRequested) return (false, "cancelled");
                var dest = Path.Combine(root, f.Name);
                if (File.Exists(dest) && new FileInfo(dest).Length == f.Size)
                {
                    var quickSha = await Task.Run(() => ComputeSha256(dest), ct);
                    if (string.Equals(quickSha, f.Sha256, StringComparison.OrdinalIgnoreCase))
                        continue;   // 断点续装：已就位文件跳过
                }
                var part = dest + ".part";
                var url = $"{ServerBase}/models/{group.Id}/{f.Name}";
                try
                {
                    _log($"[tts] 下载 {f.Name} ← {ServerBase}");
                    progress?.Report($"{f.Name} ← 服务端");
                    // 先建子目录（dict/、dict/pos_dict/）再动 .part——父目录不存在时 File.Delete 会抛 DirectoryNotFoundException
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
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
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log($"[tts] 下载失败（{f.Name}）：{ex.Message}");
                    try { File.Delete(part); } catch { }
                    // 状态码/简因带进设置页消息（IIS 未映射扩展名→404 排查直通车）
                    var brief = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                    return (false, $"download-failed:{f.Name}（{brief}）");
                }
            }

            WriteManifest(group);
            return (true, "installed");
        }
        finally
        {
            foreach (var p in Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories))
                try { File.Delete(p); } catch { }
        }
    }

    /// <summary>删除已装模型（共享区组目录 + models.json；重装需重新下载——需求 §3.2 显式动作）。</summary>
    public (bool Ok, string Message) Remove(TtsModelGroupSpec? group = null)
    {
        group ??= KokoroZhV11Int8;
        var dir = GroupDir(group);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
            return (true, "removed");
        }
        catch (Exception ex)
        {
            _log($"[tts] 删除模型失败：{ex.Message}");
            return (false, $"remove-failed:{ex.Message}");
        }
    }

    private void WriteManifest(TtsModelGroupSpec group)
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

/// <summary>S0-1 钉死的文件清单（SHA-256 来自实际下载件 2026-11-15；build 脚本会做漂移检查）。
/// 不含 espeak-ng-data——sherpa-onnx v2.0 已移除该依赖（R2 定案，运行时实测输出 deprecation 警告）。</summary>
internal static class TtsModelStoreSpecs
{
    public static readonly TtsModelFileSpec[] AllFiles =
    [
        new() { Name = "model.int8.onnx", Sha256 = "05d793700b0b507f760c183c0e39bdd6a97b43d32fdc7bf7b54a5179f1b11222", Size = 114203914 },
        new() { Name = "voices.bin", Sha256 = "e64a5a581d8c2a350d848f51c3121657cd83aa07ed6109172177345874a7244c", Size = 53790720 },
        new() { Name = "tokens.txt", Sha256 = "931ab2df2400cd65d580a22402024c2347ced8ae9ea300e545144b1aacc48e14", Size = 1111 },
        new() { Name = "lexicon-zh.txt", Sha256 = "fea060803db3e59193987f1a3436f56917bda09f3b3595b6588e46ae60cebf93", Size = 2120131 },
        new() { Name = "lexicon-us-en.txt", Sha256 = "7daaab53a181be9885b853a8582bf1838186317e5dadacbcef9c426d6fa0da14", Size = 5956885 },
        new() { Name = "lexicon-gb-en.txt", Sha256 = "c4cbb37316f62210dff52718a7afcaae24f50c032cc75ab47ae67b831d1049e7", Size = 6366635 },
        new() { Name = "phone-zh.fst", Sha256 = "1ac2b6fa56b1442320c4de7db08353bab8963a2b57f365eebcdd3a2d3562f8d7", Size = 88630 },
        new() { Name = "date-zh.fst", Sha256 = "eb8aa079ae3cb81d8f4404992f39d61a0cb990947512b5b8d1e54d1f6980e718", Size = 59154 },
        new() { Name = "number-zh.fst", Sha256 = "743f402181fcfebf76cc2f0546b71fa26476e626fbe4e460fb7b4c3a7a8bd5bd", Size = 64482 },
        new() { Name = "dict/hmm_model.utf8", Sha256 = "f17790586ac86dd048c8adffed052c4bd2b28ed0682972c1275e59040c0589a7", Size = 519739 },
        new() { Name = "dict/idf.utf8", Sha256 = "dbd1e03d72b2263cc8d84a4304ed77677eed9e7deaf43a1a5133bbba9733b535", Size = 5998717 },
        new() { Name = "dict/jieba.dict.utf8", Sha256 = "3043b77068e09c9904f27cad82f12b6ebe9dbdb5aeff3b25e45ab7f9c1122b55", Size = 5071204 },
        new() { Name = "dict/stop_words.utf8", Sha256 = "b788b8a939d2e2fe079abd579ea98f12f9fb84370bfd0dddd81bb9381f7ab42c", Size = 8974 },
        new() { Name = "dict/user.dict.utf8", Sha256 = "5faabcd0c52adf512de77fd7eee3d556da8c98b50f493b47c66a981b3a24959c", Size = 829194 },
        new() { Name = "dict/pos_dict/char_state_tab.utf8", Sha256 = "28b7be1dd7369766a51445af4d42e9a2ba4bf374c13be5bc1ca7721e27271dbb", Size = 327139 },
        new() { Name = "dict/pos_dict/prob_emit.utf8", Sha256 = "c33c4cb7edf3b3a5947df7209b6e9f267eae1f21335d9e2bd2521ea07105457a", Size = 1687686 },
        new() { Name = "dict/pos_dict/prob_start.utf8", Sha256 = "13623ea0e9300bdb597cb2da28770b7b385d6c0098d66e516083fb01b6bd5d96", Size = 4347 },
        new() { Name = "dict/pos_dict/prob_trans.utf8", Sha256 = "f22363e2307408293d180c6f9f6b5cb75879d52f722f7764fa2d3d0ae2400236", Size = 124159 },
    ];
}
