using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBrook.Agent.Plugins;

/// <summary>服务端插件索引（index.json）。</summary>
public sealed class PluginIndex
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("plugins")] public List<PluginIndexEntry> Plugins { get; set; } = [];
}

/// <summary>服务端插件索引条目（index.json plugins[]）。</summary>
public sealed class PluginIndexEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("latest")] public string Latest { get; set; } = "";

    /// <summary>最低宿主版本（对齐 Helper version.txt；低于则不显示安装按钮）。</summary>
    [JsonPropertyName("minHost")] public string MinHost { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];

    /// <summary>模型清单地址（相对 index.json；doc/17 §5.1 下载+自检用）。</summary>
    [JsonPropertyName("modelsUrl")] public string? ModelsUrl { get; set; }

    /// <summary>按 RID 分包：win-x64 / win-arm64 / osx-x64 / osx-arm64。</summary>
    [JsonPropertyName("packages")] public Dictionary<string, PluginPackageInfo> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>按 RID 的插件包信息。</summary>
public sealed class PluginPackageInfo
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>插件版本清单（manifest.json，随包分发；逐文件 SHA-256）。</summary>
public sealed class PluginManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>入口程序集文件名（含 [PluginEntry] 类型）。</summary>
    [JsonPropertyName("entry")] public string Entry { get; set; } = "";

    [JsonPropertyName("files")] public List<PluginFileHash> Files { get; set; } = [];
}

public sealed class PluginFileHash
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>本地已装插件状态（state.json plugins[id]）。</summary>
public sealed class PluginStateEntry
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>插件声明式设置的当前值（宿主持有；插件未加载时设置页仍可显示）。</summary>
    [JsonPropertyName("settings")] public JsonElement? Settings { get; set; }

    /// <summary>
    /// 实际安装目录名（plugins/{id}/ 下的叶子目录）。缺省 = {version}。
    /// 热插拔下旧版本目录可能仍被 ALC 占用（GC 回收完成前删不掉），
    /// 重装写入 {version}-r{n} 新目录，旧目录由清扫回收。
    /// </summary>
    [JsonPropertyName("dir")] public string? Dir { get; set; }
}

/// <summary>插件安装进度（下载/校验/解压各阶段；Total 为索引声明的包体积，未知为 null）。</summary>
public sealed record PluginInstallProgress(string Stage, long Bytes, long? Total)
{
    public const string Download = "download";
    public const string Verify = "verify";
    public const string Extract = "extract";
    public const string Done = "done";
}
