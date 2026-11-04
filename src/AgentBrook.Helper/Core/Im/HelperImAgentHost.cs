using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.IM;
using AgentBrook.Messaging;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper.Core.Im;

/// <summary>
/// Helper 宿主的 MAF Agent 宿主（S8）：惰性构建 AIAgent（同供应商/模型配置，真 IChatClient），
/// 供 IM 回合执行器与会话工厂共用。workspaceDir 用于读取 models.json 的活动供应商。
/// </summary>
public sealed class HelperImAgentHost
{
    private readonly string _workspaceDir;
    private readonly Lock _gate = new();
    private AIAgent? _agent;
    private Microsoft.Extensions.AI.IChatClient? _chatClient;
    private readonly AIAgent? _agentOverride;   // 测试注入口（假 IChatClient 装配的真实 AgentFactory 产物）

    public HelperImAgentHost(string workspaceDir, AIAgent? agentOverride = null)
    {
        _workspaceDir = workspaceDir;
        _agentOverride = agentOverride;
    }

    public AIAgent GetOrCreateAgent()
    {
        lock (_gate)
        {
            if (_agentOverride is not null) return _agentOverride;
            if (_agent is not null) return _agent;
            var config = LoadConfig();
            var provider = ResolveProvider(config);
            var key = provider.ApiKey ?? "";
            var keyDesc = string.IsNullOrWhiteSpace(key)
                ? "(空)"
                : ProviderConfig.IsPlaceholderApiKey(key)
                    ? "(占位符！模型未配置)"
                    : $"{key[..Math.Min(4, key.Length)]}…（{key.Length} 字符）";
            ImDiag.Log($"[im] IM 模型：{provider.BaseUrl} / {provider.ActiveModel ?? config.LLM.DefaultModel} / key={keyDesc}");
            _chatClient = AgentFactory.CreateChatClient(provider.BaseUrl, provider.ApiKey,
                provider.ActiveModel ?? config.LLM.DefaultModel);
            _agent = AgentFactory.CreateAgent(config, _chatClient, [], "", new NoopImContextProvider());
            return _agent;
        }
    }

    /// <summary>
    /// 指定模型的多模态能力（权威来源：models.json 的 ModelInputTypes+ModelTypes——服务端 /v1/models
    /// input_types+model_type 属性，模型列表刷新时落盘）。图片/视频理解 = input_types 含对应类型
    /// **且 model_type=1（对话模型）**——图像生成模型（type=4）不承担输入解析，发图反馈"不支持"；
    /// 无记录保守按纯文本。
    /// </summary>
    public (string Model, bool ImageInput, bool VideoInput) GetModelCapabilities(string modelName)
    {
        try
        {
            var settings = ProviderSettings.Load(Path.Combine(_workspaceDir, "models.json"));
            var inputTypes = settings.Active?.ModelInputTypes;
            var types = settings.Active?.ModelTypes;
            if (inputTypes is not null && inputTypes.TryGetValue(modelName, out var it))
            {
                var parts = it.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hasImage = parts.Contains("image", StringComparer.OrdinalIgnoreCase);
                var hasVideo = parts.Contains("video", StringComparer.OrdinalIgnoreCase);
                var modelType = types is not null && types.TryGetValue(modelName, out var t) ? t : 1;
                var isChat = modelType == 1;
                return (modelName, ImageInput: hasImage && isChat, VideoInput: hasVideo && isChat);
            }
            return (modelName, ImageInput: false, VideoInput: false);
        }
        catch
        {
            return (modelName, ImageInput: false, VideoInput: false);
        }
    }

    /// <summary>活动模型（models.json ActiveModel）的能力——无主界面运行时模型时用。</summary>
    public (string Model, bool ImageInput, bool VideoInput) GetModelCapabilities()
    {
        var config = LoadConfig();
        var provider = ResolveProvider(config);
        return GetModelCapabilities(provider.ActiveModel ?? config.LLM.DefaultModel);
    }
    /// <summary>活动供应商：workspace models.json 的 Active 优先，回退 appsettings LLM。
    /// 读取带重试——工作区可能在网络共享上（VM 场景），瞬时 IO 失败静默回退 appsettings 占位符
    /// 会被永久缓存成坏客户端（每回合秒失败、无回复）。</summary>
    private (string BaseUrl, string ApiKey, string? ActiveModel) ResolveProvider(AppConfig config)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var settings = ProviderSettings.Load(Path.Combine(_workspaceDir, "models.json"));
                var active = settings.Active;
                if (active is not null && !string.IsNullOrWhiteSpace(active.BaseUrl) && !string.IsNullOrWhiteSpace(active.ApiKey))
                    return (active.BaseUrl, active.ApiKey, active.ActiveModel);
                break;   // 读取成功但无有效 Active：不必重试，走回退
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[im] models.json 读取失败（第 {attempt + 1} 次）：{ex.Message}");
                if (attempt < 2) Thread.Sleep(300);
            }
        }
        Console.Error.WriteLine("[im] models.json 读取最终失败：回退 appsettings LLM（如主界面模型可用而 IM 不回复，多半是这里）");
        return (config.LLM.BaseUrl, config.LLM.ApiKey, config.LLM.DefaultModel);
    }

    private static AppConfig LoadConfig()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables("AGENTBROOK_")
            .Build();
        return configuration.Get<AppConfig>() ?? new AppConfig();
    }
}

/// <summary>IM 会话工厂：peerKey → 独立会话 id；新会话用真实 AIAgent 种子化首版快照。
/// legacyRegister/legacyMirrorDir：把会话注册进 legacy index + 镜像 session.json——会话界面可见、点开可读。</summary>
public sealed class HelperImSessionFactory : ISessionFactory
{
    private readonly VersionedSessionStore _store;
    private readonly HelperImAgentHost _agentHost;
    private readonly string _workspaceRoot;
    private readonly object _gate = new();
    private readonly HashSet<string> _created = [];
    private readonly Action<string, string>? _legacyRegister;   // (id, title)：id 先落库（§11.2 创建意图）
    private readonly string? _legacyMirrorDir;

    public HelperImSessionFactory(string workspaceRoot, VersionedSessionStore store, HelperImAgentHost agentHost,
        Action<string, string>? legacyRegister = null, string? legacyMirrorDir = null)
    {
        _workspaceRoot = workspaceRoot;
        _store = store;
        _agentHost = agentHost;
        _legacyRegister = legacyRegister;
        _legacyMirrorDir = legacyMirrorDir;
    }

    public async Task CreateSessionAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_created.Add(sessionId))
            {
                // 幂等（M04 不重复建）——但删除后重建（同进程内）须重新种子化：版本已不在即视为新会话
                if (_store.ReadVersion(sessionId) != default) return;
            }
        }
        // 新会话首版快照 = 真实 AIAgent.CreateSessionAsync() 序列化（避免 "{}" 无法反序列化）
        var agent = _agentHost.GetOrCreateAgent();
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var payload = MafSnapshotCodec.Encode(agent, session);
        var legacyFile = Path.Combine(_workspaceRoot, "sessions", sessionId, "session.json");
        if (File.Exists(legacyFile))
        {
            payload = await File.ReadAllBytesAsync(legacyFile, cancellationToken).ConfigureAwait(false);   // legacy 原样迁移优先
        }
        _store.SeedSession(sessionId, payload);

        // 会话界面可见：注册 legacy index（幂等）+ 镜像首版 session.json（点开可读历史）
        try { _legacyRegister?.Invoke(sessionId, title); } catch { /* 注册失败不拖垮回合管道 */ }
        if (_legacyMirrorDir is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(_legacyMirrorDir, sessionId));
                await File.WriteAllBytesAsync(Path.Combine(_legacyMirrorDir, sessionId, "session.json"),
                    payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[im] legacy 种子镜像写失败 {sessionId}：{ex.Message}");
            }
        }
    }

    public bool Exists(string sessionId) => _store.ReadVersion(sessionId) != default;

    public void DeleteSession(string sessionId) { /* 删除协议走 DeleteProtocol */ }
}
