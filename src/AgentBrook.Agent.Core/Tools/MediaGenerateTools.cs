using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Agent.Tools;

/// <summary>
/// 多模态生成工具：用户已选择的多模态模型（ActiveMediaModel，model_type≥4）作为子 Agent 能力。
/// 主 Agent 决定调用并构造目标模型所需参数（parameters_json 透传进提交体）。
/// 统一走异步任务：POST /tasks/submit → GET /tasks/status?task_id= 轮询（3s/5min 超时）。
/// </summary>
public sealed class MediaGenerateTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(130) };
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly string[] MediaExts =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v", ".mp3", ".wav", ".m4a", ".flac", ".ogg"];

    private readonly Func<ProviderConfig?> _activeProvider;
    private readonly Func<IReadOnlyList<ProviderConfig>>? _allProviders;
    private readonly Action<string> _log;

    public MediaGenerateTools(
        Func<ProviderConfig?> activeProvider,
        Action<string>? log = null,
        Func<IReadOnlyList<ProviderConfig>>? allProviders = null)
    {
        _activeProvider = activeProvider;
        _allProviders = allProviders;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// 用已选择的多模态模型生成图片/视频/音频等媒体内容（异步任务，可能耗时几分钟）。
    /// 成功返回 JSON（含 url）；失败返回 JSON（含 error 供转述给用户）。
    /// </summary>
    [Description("生成图片/视频/音频等媒体内容：当用户要求生成媒体且已选择多模态模型时调用。" +
                 "prompt 写完整详细的生成描述；parameters_json 写目标模型需要的附加参数 JSON（如 {\"size\":\"1080x1080\",\"duration\":6,\"ratio\":\"16:9\"}），" +
                 "参数名必须匹配目标模型的接口要求。异步任务超时返回 task_id 时，再次调用本工具并把该值传入 task_id 参数即可查询状态" +
                 "（对应服务端 GET /v1/tasks/status?task_id=xxx），不要自行拼接其他 /tasks/ 路径或用 run_command 探测。" +
                 "成功后请在回复中用 markdown 媒体链接呈现 url（图片用 ![描述](url)，视频/音频用 [下载/查看](url)）。")]
    public async Task<string> media_generate(
        [Description("生成内容的完整详细描述（画质/风格/镜头/时长等），由主 Agent 组装")] string prompt,
        [Description("目标模型附加参数的 JSON 字符串；无附加参数传 \"\"")] string parameters_json = "",
        [Description("指定使用的多模态模型名（须已获用户授权）；留空使用用户当前已选的多模态模型")] string model = "",
        [Description("查询已提交任务的状态：传此前返回的 task_id（此时 prompt/parameters_json 可传 \"\"），不会重复提交新任务")] string task_id = "",
        CancellationToken cancellationToken = default)
    {
        // 模型解析：显式指定 → 按名跨供应商查找；缺省 → 用户当前已选的 ActiveMediaModel
        ProviderConfig? p;
        if (string.IsNullOrWhiteSpace(model))
        {
            p = _activeProvider();
            model = p?.ActiveMediaModel ?? "";
            if (string.IsNullOrWhiteSpace(model) || p is null)
            {
                // 仅查询任务状态时，模型可为空（凭据仍按供应商解析）
                if (string.IsNullOrWhiteSpace(task_id) || p is null)
                    return Fail("当前未选择多模态模型，且未通过 model 参数指定。可用多模态模型：" + CandidateList());
            }
        }
        else
        {
            var providers = _allProviders?.Invoke() ?? [];
            p = providers.FirstOrDefault(x =>
                x.Models.Contains(model, StringComparer.Ordinal) && x.TypeOf(model) >= 4);
            if (p is null)
            {
                return Fail($"模型 {model} 不在可用多模态清单内。候选：" + CandidateList());
            }
        }
        if (string.IsNullOrWhiteSpace(p.BaseUrl) || string.IsNullOrWhiteSpace(p.ApiKey))
        {
            return Fail("供应商 BaseUrl/ApiKey 未配置，无法提交媒体任务。");
        }

        // 组装提交体：model 固定为所选多模态模型，prompt + parameters_json 合并（model 不可被覆盖）
        JsonObject body;
        try
        {
            body = string.IsNullOrWhiteSpace(parameters_json)
                ? new JsonObject()
                : JsonNode.Parse(parameters_json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return Fail($"parameters_json 不是合法 JSON：{parameters_json}");
        }
        body["model"] = model;
        body.TryAdd("prompt", prompt);

        var apiBase = p.BaseUrl.TrimEnd('/');
        if (!apiBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            apiBase += "/v1";   // API 路由均在 /v1 前缀下（/v1/tasks/submit、/v1/tasks/status）
        }
        var submitUrl = apiBase + "/tasks/submit";
        var statusUrl = apiBase + "/tasks/status?task_id=";
        var guide = "任务状态查询唯一正确方式：再次调用 media_generate 并把 task_id 传入 task_id 参数" +
                    $"（服务端对应 GET {statusUrl}<task_id>）。不要自行拼接 /tasks/... 其他路径，也不要用 run_command 探测接口。";

        // ---- 仅查询既有任务：传了 task_id 时跳过提交，单次查询返回当前状态 ----
        if (!string.IsNullOrWhiteSpace(task_id))
        {
            try
            {
                using var qReq = new HttpRequestMessage(HttpMethod.Get, statusUrl + Uri.EscapeDataString(task_id));
                qReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
                using var qResp = await Http.SendAsync(qReq, cancellationToken);
                var qBody = await qResp.Content.ReadAsStringAsync(cancellationToken);
                if (!qResp.IsSuccessStatusCode)
                {
                    return Fail($"任务状态查询失败 HTTP {(int)qResp.StatusCode}：{Truncate(qBody, 300)}。" + guide);
                }
                var qStatus = ExtractStatus(qBody);
                var qUrl = ExtractMediaUrl(qBody);
                return JsonSerializer.Serialize(new
                {
                    ok = true, task_id, model, status = qStatus,
                    url = qUrl ?? "",
                    note = qUrl is not null ? "任务已完成，请在回复中用 markdown 媒体链接呈现该 url"
                        : (qStatus.Equals("Failure", StringComparison.OrdinalIgnoreCase) ||
                           qStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                          ? "任务已失败，详情见 raw" : "任务仍在进行中，稍后可用同一 task_id 再查",
                    raw = qUrl is null ? Truncate(qBody, 800) : null,
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Fail($"任务状态查询请求失败：{ex.Message}。" + guide);
            }
        }

        // ---- 图像分流：endpoints 声明含 images（或 type 4/5）走同步 /images/generations，一次返回 url 无需轮询 ----
        var eps = p.ModelEndpoints.TryGetValue(model, out var ep) ? ep : "";
        var isImageModel = p.TypeOf(model) is 4 or 5;
        if (isImageModel && (eps.Length == 0 || eps.Split(',').Any(e => e.Trim().Equals("images", StringComparison.OrdinalIgnoreCase))))
        {
            var genUrl = apiBase + "/images/generations";
            string genBody;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, genUrl)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
                using var resp = await Http.SendAsync(req, cancellationToken);
                genBody = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    return Fail($"图像生成失败 HTTP {(int)resp.StatusCode}：{Truncate(genBody, 400)}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Fail($"图像生成请求失败：{ex.Message}");
            }
            var genMediaUrl = ExtractMediaUrl(genBody);
            if (genMediaUrl is null)
            {
                return Fail($"图像生成响应中未找到媒体 URL：{Truncate(genBody, 400)}");
            }
            _log($"✅ 图像生成完成 model={model}");
            return JsonSerializer.Serialize(new
            {
                ok = true, model,
                url = genMediaUrl,
                note = "请在回复中用 markdown 媒体链接呈现该 url",
            });
        }

        // ---- 提交任务 ----
        string submitBody;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, submitUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var resp = await Http.SendAsync(req, cancellationToken);
            submitBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return Fail($"任务提交失败 HTTP {(int)resp.StatusCode}：{Truncate(submitBody, 400)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail($"任务提交请求失败：{ex.Message}");
        }

        var taskId = ExtractTaskId(submitBody);
        if (taskId is null)
        {
            // 上游可能已直接返回结果或异构结构：给出查询指引便于主 Agent 判断
            return Fail($"提交响应中未找到 task_id：{Truncate(submitBody, 400)}。" + guide);
        }
        _log($"🎬 媒体任务已提交 task={taskId} model={model}");

        // ---- 轮询状态（3s / 5min）----
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        string lastBody = "";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, cancellationToken);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, statusUrl + Uri.EscapeDataString(taskId));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
                using var resp = await Http.SendAsync(req, cancellationToken);
                lastBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                continue;   // 网络抖动继续轮询
            }

            var status = ExtractStatus(lastBody);
            if (status.Equals("Success", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Finished", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Done", StringComparison.OrdinalIgnoreCase))
            {
                var url = ExtractMediaUrl(lastBody);
                _log($"✅ 媒体任务完成 task={taskId}");
                return JsonSerializer.Serialize(new
                {
                    ok = true, task_id = taskId, model,
                    url = url ?? "",
                    note = url is null ? "任务成功但响应中未解析到媒体 URL，原始响应见 raw" : "请在回复中用 markdown 媒体链接呈现该 url",
                    raw = url is null ? Truncate(lastBody, 800) : null,
                });
            }
            if (status.Equals("Failure", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"任务终态 {status}：{Truncate(lastBody, 400)}。如需再查可用 task_id={taskId}。" + guide);
            }
        }
        return JsonSerializer.Serialize(new
        {
            ok = false,
            error = $"任务提交成功但 5 分钟内未达终态（task_id={taskId}），任务可能仍在后台执行。",
            task_id = taskId,
            guide,
            last = Truncate(lastBody, 300),
        });
    }

    // ---------- JSON 解析（容忍不同上游响应结构） ----------

    private static string? ExtractTaskId(string body)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            var id = FindString(root, "task_id") ?? FindString(root, "id");
            return id is { Length: < 64 } ? id : FindString(root, "task_id");
        }
        catch { return null; }
    }

    private static string ExtractStatus(string body)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            return FindString(root, "task_status") ?? FindString(root, "status") ?? "";
        }
        catch { return ""; }
    }

    private static string? ExtractMediaUrl(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            // 1) 常见字段名
            foreach (var key in new[] { "video_url", "image_url", "file_url", "url", "result_url" })
            {
                var v = FindString(root as JsonObject, key);
                if (v is { Length: > 0 } && v.StartsWith("http")) return v;
            }
            // 2) 深度扫描：找第一个带媒体扩展名的 http(s) URL
            string? found = null;
            ScanUrls(root, s =>
            {
                if (found is null && MediaExts.Any(e => s.Split('?')[0].EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    found = s;
                }
            });
            if (found != null) return found;
            // 3) 兜底：任意 http(s) 字符串
            ScanUrls(root, s => { if (found is null) found = s; });
            return found;
        }
        catch { return null; }
    }

    private static string? FindString(JsonObject? obj, string name)
    {
        if (obj is null) return null;
        if (obj.TryGetPropertyValue(name, out var v))
        {
            if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)) return s;
            if (v is JsonObject jo) { var inner = FindString(jo, name); if (inner != null) return inner; }
        }
        foreach (var kv in obj)
        {
            if (kv.Value is JsonObject child)
            {
                var r = FindString(child, name);
                if (r != null) return r;
            }
        }
        return null;
    }

    private static void ScanUrls(JsonNode? node, Action<string> onHttp)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o) ScanUrls(kv.Value, onHttp);
                break;
            case JsonArray a:
                foreach (var item in a) ScanUrls(item, onHttp);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) &&
                (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)):
                onHttp(s);
                break;
        }
    }

    /// <summary>全部供应商的多模态候选模型（type≥4），格式：model@供应商(类型)。</summary>
    private string CandidateList()
    {
        var providers = _allProviders?.Invoke() ?? [];
        var items = providers.SelectMany(x => x.Models
            .Where(m => x.TypeOf(m) >= 4)
            .Select(m => $"{m}@{x.Name}({x.TypeOf(m) switch { 4 or 5 => "图", 6 or 7 => "视", _ => "媒体" }})"));
        var joined = string.Join("、", items);
        return joined.Length > 0 ? joined : "无（请先在模型设置中拉取多模态模型）";
    }

    private static string Fail(string error) =>
        JsonSerializer.Serialize(new { ok = false, error });

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
