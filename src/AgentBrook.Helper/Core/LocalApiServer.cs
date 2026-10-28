using AgentBrook.Helper.Support;
using AgentBrook.Helper.Windows;
using AgentBrook.Helper.Views;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentBrook.Agent.Infrastructure;
using Avalonia.Threading;

namespace AgentBrook.Helper.Core;

/// <summary>
/// 本机回环 HTTP 服务（仅 127.0.0.1，不监听外部网卡）：
/// 收银台「自动回填」按钮把新购 Token卡 Key POST 过来，写入模型设置的 AgentBrook 供应商并打开设置窗。
/// 端点：GET /ping 探活；POST /setup/provider {"name","baseUrl","apiKey","models"}。
/// </summary>
internal static class LocalApiServer
{
    private const int Port = 47852;
    private static readonly HttpListener _listener = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };


    public static void Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(AcceptLoop);
            Console.Error.WriteLine($"[local-api] listening on 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            // 端口被占用等：仅禁用自动回填，不影响主功能
            Console.Error.WriteLine($"[local-api] 启动失败（自动回填不可用）：{ex.Message}");
        }
    }

    private static async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private static async Task HandleAsync(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        // 浏览器跨源/PNA 预检放行（公网页面 → 回环）
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        resp.Headers["Access-Control-Allow-Private-Network"] = "true";
        try
        {
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 204;
                return;
            }
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (ctx.Request.HttpMethod == "GET" && path == "/ping")
            {
                await WriteJsonAsync(resp, 200, new { code = 0, app = "AgentBrook.Helper" });
                return;
            }
            if (path == "/focus" && ctx.Request.HttpMethod is "GET" or "POST")
            {
                // 第二个实例启动时被 Mutex 拦截后调用：把已运行实例的对话窗前置
                await Dispatcher.UIThread.InvokeAsync(() =>
                    (App.Controller.ConversationWindow as MainWindow)?.ShowFromBubble());
                await WriteJsonAsync(resp, 200, new { code = 0 });
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/setup/provider")
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = await reader.ReadToEndAsync();
                var req = JsonSerializer.Deserialize<SetupProviderRequest>(body, JsonOpts);
                var result = await Dispatcher.UIThread.InvokeAsync(
                    () => ApplyProviderAsync(req?.Name ?? "AgentBrook", req?.BaseUrl ?? "", req?.ApiKey ?? "", req?.Models));
                await WriteJsonAsync(resp, 200, new { code = result.Code, message = result.Message });
                return;
            }
            resp.StatusCode = 404;
        }
        catch (Exception ex)
        {
            try { await WriteJsonAsync(resp, 500, new { code = 1, message = ex.Message }); }
            catch { }
        }
        finally
        {
            resp.OutputStream.Close();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class SetupProviderRequest
    {
        public string? Name { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public List<string>? Models { get; set; }
    }

    /// <summary>幂等写入 AgentBrook 供应商：同名则更新参数（不重复添加），完全一致则仅打开窗口。</summary>
    private static async Task<(int Code, string Message)> ApplyProviderAsync(
        string name, string baseUrl, string apiKey, List<string>? models)
    {
        name = string.IsNullOrWhiteSpace(name) ? "AgentBrook" : name.Trim();
        baseUrl = baseUrl.Trim();
        apiKey = apiKey.Trim();
        if (apiKey.Length == 0) return (1, "缺少 API Key");

        // 供应商集：Agent 就绪取内存态（随后走热应用），否则读 models.json 兜底
        List<ProviderConfig> providers;
        string active;
        if (App.Controller.Ready && App.Controller.Providers.Count > 0)
        {
            providers = App.Controller.Providers.Select(p => new ProviderConfig
            {
                Name = p.Name, Protocol = p.Protocol, BaseUrl = p.BaseUrl, ApiKey = p.ApiKey,
                Models = [.. p.Models], ActiveModel = p.ActiveModel,
                ActiveMediaModel = p.ActiveMediaModel,
                ModelTypes = new Dictionary<string, int>(p.ModelTypes),
                ModelNames = new Dictionary<string, string>(p.ModelNames),
                ModelEndpoints = new Dictionary<string, string>(p.ModelEndpoints),
                ModelParams = new Dictionary<string, string>(p.ModelParams),
            }).ToList();
            active = App.Controller.CurrentProvider;
        }
        else
        {
            var s = ProviderSettings.Load(ModelsJsonPath());
            providers = s.Providers;
            active = s.ActiveProvider;
        }

        var p = providers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var changed = false;
        if (p is null)
        {
            p = new ProviderConfig { Name = name };
            providers.Add(p);
            changed = true;
        }
        if (p.BaseUrl != baseUrl) { p.BaseUrl = baseUrl; changed = true; }
        if (p.ApiKey != apiKey) { p.ApiKey = apiKey; changed = true; }
        if (models is { Count: > 0 } && !models.SequenceEqual(p.Models)) { p.Models = models; changed = true; }

        // 模型清单缺失时向中转拉一次（Key 刚发卡，/v1/models 即可用）；全量模型入列，非对话模型发送时按类型拦截
        if (p.Models.Count == 0)
        {
            var fetched = await FetchModelsAsync(p);
            if (fetched.Count > 0)
            {
                changed |= ModelCatalog.Apply(p, fetched);
            }
        }
        if (!p.Models.Contains(p.ActiveModel) || !p.IsChatModel(p.ActiveModel))
            // 默认激活优先文本对话模型，避免默认落到图像/视频模型
            p.ActiveModel = p.Models.FirstOrDefault(m => p.IsChatModel(m)) ?? "";

        // Key 可用则直接设为当前供应商；不可用保持原激活项
        var newActive = p.IsConfigured ? p.Name : active;
        if (App.Controller.Ready)
            await App.Controller.SaveProvidersAsync(providers, newActive);
        else
            new ProviderSettings { Providers = providers, ActiveProvider = newActive }.Save(ModelsJsonPath());

        ShowSettings(name);
        return (0, changed ? "已写入 AgentBrook 配置" : "配置已是最新，无需更改");
    }

    /// <summary>调中转 OpenAI 兼容 /v1/models 拉取可用模型（Bearer Key），保留类型与显示名供接口路由。</summary>
    private static async Task<List<ModelCatalog.Entry>> FetchModelsAsync(ProviderConfig provider)
    {
        try
        {
            // 全量返回（含多模态）；model_type 缺失按 1=文本兼容旧服务端
            return await ModelCatalog.FetchAsync(provider);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>打开（或聚焦）设置窗口并定位到模型页的指定供应商。</summary>
    private static void ShowSettings(string providerName)
    {
        var settings = ModelSettingsWindow.ShowSingle();
        settings.ShowModelsPage(providerName);
    }

    private static string ModelsJsonPath()
    {
        var dir = UiPrefs.WorkspaceDir();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "models.json");
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
    }
}
