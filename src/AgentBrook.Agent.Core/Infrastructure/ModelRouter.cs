using System.Collections.Concurrent;
using AgentBrook.Agent.Configuration;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 按任务角色把工作路由到不同能力的模型（分级用模）：
/// 主对话用用户当前选中的模型；子 Agent / 摘要压缩等辅助工作默认用清单中的轻量模型，
/// 可在 appsettings.json 的 Agent:SubAgentModel / Agent:SummarizeModel 显式指定。
/// 辅助客户端按「baseUrl#model」缓存，供应商切换后调用 Reset 失效重建。
/// </summary>
public sealed class ModelRouter
{
    /// <summary>角色：子 Agent（委派任务）。</summary>
    public const string RoleSubAgent = "subagent";

    /// <summary>角色：会话摘要压缩。</summary>
    public const string RoleSummarize = "summarize";

    private readonly AppConfig _config;
    private readonly Func<ProviderConfig> _activeProvider;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);

    public ModelRouter(AppConfig config, Func<ProviderConfig> activeProvider)
    {
        _config = config;
        _activeProvider = activeProvider;
    }

    /// <summary>解析角色应使用的模型名。角色配置为空时取供应商清单中的首个模型（约定为轻量模型）。</summary>
    public string ResolveModel(string role) => role switch
    {
        RoleSubAgent => Pick(_config.Agent.SubAgentModel),
        RoleSummarize => Pick(_config.Agent.SummarizeModel),
        _ => MainModel(),
    };

    /// <summary>
    /// 获取执行角色任务所需的 (模型, 客户端)。模型与主模型相同时返回主客户端本身（调用方传入），
    /// 避免为同一模型重复建客户端。
    /// </summary>
    public (string Model, IChatClient Client) Resolve(string role, IChatClient mainClient)
    {
        var provider = _activeProvider();
        var model = ResolveModel(role);
        if (string.Equals(model, MainModel(), StringComparison.Ordinal))
        {
            return (model, mainClient);
        }
        var key = $"{provider.BaseUrl}#{model}";
        return (model, _clients.GetOrAdd(key, _ => AgentFactory.CreateChatClient(provider.BaseUrl, provider.ApiKey, model)));
    }

    /// <summary>供应商或其连接参数变化后清空辅助客户端缓存。</summary>
    public void Reset()
    {
        foreach (var kv in _clients)
        {
            try { (kv.Value as IDisposable)?.Dispose(); } catch { }
        }
        _clients.Clear();
    }

    private string MainModel()
    {
        var p = _activeProvider();
        return string.IsNullOrEmpty(p.ActiveModel) ? p.Models.FirstOrDefault() ?? "" : p.ActiveModel;
    }

    private string Pick(string? configured)
    {
        var p = _activeProvider();
        if (!string.IsNullOrWhiteSpace(configured) && p.Models.Contains(configured, StringComparer.Ordinal))
        {
            return configured;
        }
        // 未配置或不在清单：回退为清单首个模型（约定清单按能力降序，首个为轻量模型）
        return p.Models.FirstOrDefault() ?? MainModel();
    }
}
