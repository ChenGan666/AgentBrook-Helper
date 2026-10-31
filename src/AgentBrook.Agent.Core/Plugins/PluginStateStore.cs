using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBrook.Agent.Plugins;

/// <summary>
/// 插件状态持久化（workspace/plugins/state.json）：
/// 每插件 { version, enabled, settings }——settings 值由宿主持有（声明式设置的存储层，
/// 插件未加载时设置页仍可显示只读值）。
/// </summary>
public sealed class PluginStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, PluginStateEntry> _plugins = new(StringComparer.Ordinal);

    public PluginStateStore(string path)
    {
        _path = path;
        Load();
    }

    public bool IsEnabled(string id)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(id, out var e) && e.Enabled;
        }
    }

    public string? GetVersion(string id)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(id, out var e) ? e.Version : null;
        }
    }

    /// <summary>实际安装目录名（plugins/{id}/ 下叶子）；null = 默认 {version} 目录。</summary>
    public string? GetDirName(string id)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(id, out var e) ? e.Dir : null;
        }
    }

    public string? GetSettingsJson(string id)
    {
        lock (_gate)
        {
            if (_plugins.TryGetValue(id, out var e) && e.Settings is { } s)
            {
                return s.GetRawText();
            }
            return null;
        }
    }

    public void SetSettingsJson(string id, string json)
    {
        lock (_gate)
        {
            var e = Ensure(id);
            e.Settings = JsonDocument.Parse(json).RootElement.Clone();
            Save();
        }
    }

    public void SetInstalled(string id, string version, bool enabled, string? dirName = null)
    {
        lock (_gate)
        {
            var e = Ensure(id);
            e.Version = version;
            e.Enabled = enabled;
            e.Dir = dirName;
            Save();
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            Ensure(id).Enabled = enabled;
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_plugins.Remove(id))
            {
                Save();
            }
        }
    }

    /// <summary>全部已装条目（快照）。</summary>
    public IReadOnlyDictionary<string, PluginStateEntry> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, PluginStateEntry>(_plugins, StringComparer.Ordinal);
        }
    }

    private PluginStateEntry Ensure(string id) =>
        _plugins.TryGetValue(id, out var e) ? e : _plugins[id] = new PluginStateEntry();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.TryGetProperty("plugins", out var plugins))
            {
                _plugins = plugins.Deserialize<Dictionary<string, PluginStateEntry>>(JsonOpts)
                    ?? new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugins] state.json 读取失败（按空状态继续）：{ex.Message}");
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(_path, JsonSerializer.Serialize(new { plugins = _plugins }, JsonOpts));
    }
}
