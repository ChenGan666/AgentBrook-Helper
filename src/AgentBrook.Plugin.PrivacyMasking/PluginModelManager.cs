using System.Security.Cryptography;
using System.Text.Json;
using AgentBrook.Plugin.Abstractions;
using AgentBrook.Privacy.Ner;

namespace AgentBrook.Plugin.PrivacyMasking;

/// <summary>
/// 识别模型按需下载管理（doc/17 §5.1 + doc/18 §3）：从服务端插件目录
/// 拉取 models.json → 下载三件套（tmp → SHA-256 → 原子入位 workspace/privacy/models/{name}/）
/// → 自动四步自检（完整性 → 会话加载 → 预热推理 → 指标）。
/// </summary>
public sealed class PluginModelManager(string modelsDir, IPluginDownloadService download, Action<string>? log = null)
{
    public const string ServerModelsPath = "models/models.json";

    /// <summary>服务端基址（默认生产；本地测试指向 AgentBrook.Service /updated/plugin/privacy-masking）。</summary>
    public string ServerBase { get; set; } = "https://helper.agentbrook.com/updated/plugin/privacy-masking";

    private sealed record ModelManifest(string Name, string Size, string Sha256, List<ModelFile> Files);

    private sealed record ModelFile(string Path, string Sha256);

    public sealed record InstallResult(bool Success, string Message);

    /// <summary>模型是否已在本地就绪（三件套齐备）。</summary>
    public bool IsModelReady(string name)
    {
        var dir = Path.Combine(modelsDir, name);
        return File.Exists(Path.Combine(dir, "model.onnx"))
            && File.Exists(Path.Combine(dir, "vocab.txt"))
            && File.Exists(Path.Combine(dir, "labels.txt"));
    }

    /// <summary>安装模型：下载三件套（模型目录旁暂存 → SHA-256 → 原子入位）；progress 回报逐文件百分比。</summary>
    public async Task<InstallResult> InstallAsync(string name, CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        var modelsJsonUrl = $"{ServerBase}/models/models.json";
        try
        {
            var json = await download.DownloadStringAsync(modelsJsonUrl, ct);
            var models = JsonSerializer.Deserialize<Dictionary<string, ModelManifest>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (models is null || !models.TryGetValue(name, out var manifest))
            {
                return new InstallResult(false, $"服务端无模型 {name}");
            }
            if (manifest.Files is not { Count: > 0 })
            {
                return new InstallResult(false, "模型清单为空");
            }

            var dir = Path.Combine(modelsDir, name);
            Directory.CreateDirectory(dir);
            foreach (var file in manifest.Files)
            {
                var target = Path.Combine(dir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var tmp = target + ".tmp";
                // 小文本文件（vocab/labels）清单中可无 sha256：只打印有哈希的
                log?.Invoke(file.Sha256 is { Length: > 0 } s
                    ? $"下载 {file.Path}（{s[..Math.Min(12, s.Length)]}…）…"
                    : $"下载 {file.Path}…");
                // 统一下载服务（宿主注入）：分块进度 → 百分比文案（≥512KB 一条防刷屏）
                var label = $"{name}/{file.Path}";
                var last = new long[] { -1 };
                var fileProgress = progress is null ? null : new Progress<PluginDownloadProgress>(p =>
                {
                    var prev = last[0];
                    if (prev >= 0 && p.Bytes - prev < 512 * 1024)
                    {
                        return;
                    }
                    last[0] = p.Bytes;
                    progress.Report(p.Total is { } total && total > 0
                        ? $"{label} {p.Bytes * 100 / total}%（{p.Bytes / 1048576.0:0.0}/{total / 1048576.0:0.0} MB）"
                        : $"{label} {p.Bytes / 1048576.0:0.0} MB");
                });
                await download.DownloadFileAsync($"{ServerBase}/models/{name}/{file.Path}", tmp,
                    fileProgress, ct);
                if (!string.IsNullOrEmpty(file.Sha256))
                {
                    var actual = Sha256Of(tmp);
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmp);
                        return new InstallResult(false, $"SHA-256 校验失败：{file.Path}");
                    }
                }
                File.Move(tmp, target, overwrite: true);
            }
            if (!IsModelReady(name))
            {
                return new InstallResult(false, "安装后三件套不齐备");
            }
            log?.Invoke($"模型 {name} 安装完成");
            return new InstallResult(true, "模型安装完成");
        }
        catch (Exception ex)
        {
            // 取消类异常 Message 可能为空——兜底异常类型名，避免只显示"下载失败："
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return new InstallResult(false, $"下载失败：{msg}");
        }
    }

    /// <summary>四步自检（doc/17 §5.1）：三件套完整性 → 会话加载 → 预热推理 → 指标。</summary>
    public SelfCheckResult RunSelfCheck(string name)
    {
        var dir = Path.Combine(modelsDir, name);
        if (!IsModelReady(name))
        {
            return new SelfCheckResult(false, "三件套不齐备（model.onnx / vocab.txt / labels.txt）", 0, 0);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        NerRecognizer? recognizer;
        try
        {
            var tokenizer = new WordPieceTokenizer(Path.Combine(dir, "vocab.txt"));
            var model = new OnnxNerModel(Path.Combine(dir, "model.onnx"), Path.Combine(dir, "labels.txt"));
            recognizer = new NerRecognizer(model, tokenizer,
                new Dictionary<string, bool> { ["PERSON"] = true, ["ORG"] = true, ["ADDRESS"] = true }, 0.60);
        }
        catch (Exception ex)
        {
            return new SelfCheckResult(false, $"会话加载失败：{ex.Message}", sw.ElapsedMilliseconds, 0);
        }
        var coldLoad = sw.ElapsedMilliseconds;

        sw.Restart();
        try
        {
            recognizer.Detect("张伟和李四在杭州市西湖区开会");   // 预热 + 全链（分词→推理→BIOES）
        }
        catch (Exception ex)
        {
            return new SelfCheckResult(false, $"预热推理失败：{ex.Message}", coldLoad, sw.ElapsedMilliseconds);
        }
        var warm = sw.ElapsedMilliseconds;
        return new SelfCheckResult(true, "自检通过", coldLoad, warm);
    }

    public sealed record SelfCheckResult(bool Success, string Message, long ColdLoadMs, long WarmDetectMs);

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
