using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBrook.Helper.Core.Im;

/// <summary>IM 通道声明（config.json channels[]；token 只存宿主持有的 secretRef，不入明文）。</summary>
public sealed class ImChannelConfig
{
    public string Type { get; set; } = "";          // "telegram" | "bridge"
    public string ChannelId { get; set; } = "";     // telegram | bridge-wx …
    public string InstanceId { get; set; } = "inst-1";
    public bool Enabled { get; set; } = true;
    public string DisplayName { get; set; } = "";
    public string SecretRef { get; set; } = "";     // 宿主凭据引用（不落明文）
    public string? BridgeUrl { get; set; }          // 桥专用：http://127.0.0.1:<port>
    public string? BaseUrl { get; set; }            // 微信专用：iLink 业务节点（登录响应 baseurl）
    public string? AccountId { get; set; }          // 微信专用：ilink_bot_id（入站 AccountId）
    public string? BotUsername { get; set; }         // telegram 展示用
}

/// <summary>IM 配置（workspace/im/config.json）。默认 ImEnabled=false——未启用零副作用（U01）。</summary>
public sealed class ImSettings
{
    [JsonPropertyName("ImEnabled")]
    public bool ImEnabled { get; set; } = false;

    [JsonPropertyName("StorageMode")]
    public string StorageMode { get; set; } = "versioned";   // versioned | legacy

    [JsonPropertyName("channels")]
    public List<ImChannelConfig> Channels { get; set; } = [];

    /// <summary>调试专用：启动时种子化进宿主凭据库（正式部署走设置 UI 输入，不落明文）。</summary>
    [JsonPropertyName("DebugSecrets")]
    public Dictionary<string, string> DebugSecrets { get; set; } = [];

    private static string PathFor(string workspaceRoot) => Path.Combine(workspaceRoot, "im", "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>加载；文件缺失/损坏 → 默认（关闭）。</summary>
    public static ImSettings Load(string workspaceRoot)
    {
        try
        {
            var path = PathFor(workspaceRoot);
            if (!File.Exists(path)) return new ImSettings();
            return JsonSerializer.Deserialize<ImSettings>(File.ReadAllText(path)) ?? new ImSettings();
        }
        catch (JsonException)
        {
            return new ImSettings();
        }
    }

    /// <summary>保存：tmp+rename 原子（目录随写创建；未启用前不创建 im/ 目录——U01 零副作用）。</summary>
    public void Save(string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, "im");
        Directory.CreateDirectory(dir);
        var path = PathFor(workspaceRoot);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
