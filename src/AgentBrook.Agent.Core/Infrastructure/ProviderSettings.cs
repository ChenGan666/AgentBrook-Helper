using System.Text.Json;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>一个模型供应商：OpenAI 兼容端点 + 模型清单。</summary>
public sealed class ProviderConfig
{
    public string Name { get; set; } = "";
    /// <summary>协议族（openai/anthropic/zhipu/minimax/compshare/custom）；空=openai 兼容</summary>
    public string Protocol { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public List<string> Models { get; set; } = new();
    /// <summary>模型名 → model_type（API /v1/models 返回；1-3 文本，4-5 图像，6-7 视频，≥8 其他媒体）。未知不记录按文本对待。</summary>
    public Dictionary<string, int> ModelTypes { get; set; } = new();
    public string ActiveModel { get; set; } = "";
    /// <summary>已选多模态模型（type≥4，供 media_generate 工具/子 Agent 调用）；空=无</summary>
    public string ActiveMediaModel { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) && Models.Count > 0;

    /// <summary>模型名 → 显示名（API /v1/models 的 name 字段）；UI 展示用，接口调用仍用 Models 中的真实模型名。</summary>
    public Dictionary<string, string> ModelNames { get; set; } = new();
    /// <summary>模型名 → 支持的端点（API /v1/models 的 endpoints 字段，逗号分隔，如 "tasks" / "images,tasks"）；媒体工具选接口用。</summary>
    public Dictionary<string, string> ModelEndpoints { get; set; } = new();
    /// <summary>模型名 → 调用参数说明（API /v1/models 的 parameters 字段 JSON，含 endpoint/required/optional）；注入上下文供 Agent 组装请求。</summary>
    public Dictionary<string, string> ModelParams { get; set; } = new();

    /// <summary>模型显示名：无映射时回退真实模型名</summary>
    public string DisplayNameOf(string model) =>
        ModelNames.TryGetValue(model, out var n) && !string.IsNullOrWhiteSpace(n) ? n : model;

    /// <summary>模型类型：未知（无记录）按 1=文本处理，保证旧配置兼容</summary>
    public int TypeOf(string model) =>
        ModelTypes.TryGetValue(model, out var t) ? t : 1;

    /// <summary>是否为对话模型（chat/completions 可用）</summary>
    public bool IsChatModel(string model) => TypeOf(model) < 4;
}

/// <summary>模型供应商集合的持久化（workspace/models.json）。</summary>
public sealed class ProviderSettings
{
    public List<ProviderConfig> Providers { get; set; } = new();
    public string ActiveProvider { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ProviderSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<ProviderSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到空设置（调用方会用 appsettings 默认供应商兜底）
        }
        return new ProviderSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public ProviderConfig? Active => Providers.FirstOrDefault(p => p.Name == ActiveProvider) ?? Providers.FirstOrDefault();
}
