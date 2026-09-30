using System.Text.Json;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>一个模型供应商：OpenAI 兼容端点 + 模型清单。</summary>
public sealed class ProviderConfig
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public List<string> Models { get; set; } = new();
    public string ActiveModel { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) && Models.Count > 0;
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
