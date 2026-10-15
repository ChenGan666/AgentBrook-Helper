using AgentBrook.Agent.Infrastructure;
using Avalonia.Controls;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper;

/// <summary>
/// AgentBrook Helper 窗口基类：收敛跨窗口共用的本地化赋值辅助（SetT/SetB/SetC）
/// 与语言切换刷新联动。派生窗口在构造后调用 <see cref="WatchLanguageChanges"/>，
/// 并 override <see cref="OnLanguageChanged"/> 刷新各自文案。
/// </summary>
public abstract class WindowBase : Window
{
    /// <summary>AgentBrook.API 中转地址（生产部署改为线上域名，如 https://api.example.com）</summary>
    protected const string RelayApiBase = "https://api.agentbrook.com";
    /// <summary>AgentBrook.Service Token卡购买页</summary>
    protected const string TokenPurchaseUrl = "https://helper.agentbrook.com/Token";

    /// <summary>按控件名把 TextBlock 文本设为翻译值（键为中文原文或语义化 Key）。</summary>
    protected void SetT(string name, string key)
    {
        if (this.FindControl<TextBlock>(name) is { } c)
        {
            c.Text = I18n.T(key);
        }
    }

    /// <summary>按控件名把 Button 文本设为翻译值。</summary>
    protected void SetB(string name, string key)
    {
        if (this.FindControl<Button>(name) is { } c)
        {
            c.Content = I18n.T(key);
        }
    }

    /// <summary>按控件名把 ComboBoxItem 文本设为翻译值。</summary>
    protected void SetC(string name, string key)
    {
        if (this.FindControl<ComboBoxItem>(name) is { } c)
        {
            c.Content = I18n.T(key);
        }
    }

    /// <summary>在 UI 线程安全更新提示文本。</summary>
    protected static void PostHint(TextBlock? hint, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (hint is not null)
            {
                hint.Text = message;
            }
        });
    }

    /// <summary>订阅语言切换事件；切换时回调 <see cref="OnLanguageChanged"/>（派生窗口刷新各自文案）。</summary>
    protected void WatchLanguageChanges()
    {
        I18n.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>语言切换回调（派生窗口刷新静态文案）。</summary>
    protected virtual void OnLanguageChanged()
    {
    }

    /// <summary>模型供应商预设：名称 →（默认端点，默认模型 CSV）。与设置窗口保持一致。</summary>
    protected static readonly (string Name, string BaseUrl, string Models)[] Presets =
    [
        ("AgentBrook", "https://api.agentbrook.com", ""),
        ("DeepSeek", "https://api.deepseek.com", "deepseek-flash, deepseek-v4-pro"),
        ("OpenAI", "https://api.openai.com/v1", "gpt-4o, gpt-4o-mini"),
        ("Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k, moonshot-v1-32k"),
        ("GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.5-flash"),
        ("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini"),
    ];

    /// <summary>
    /// 大模型连通性测试（测试按钮/取名共用）：快速失败客户端（禁用 SDK 重试）直连一次
    /// 对话补全，服务端 503/过载/Key 错误即时暴露。返回模型回复文本。
    /// </summary>
    protected static async Task<string> PingModelAsync(string baseUrl, string apiKey, string model)
    {
        var client = AgentFactory.CreateChatClient(baseUrl, apiKey, model, maxRetries: 0);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = "model-connectivity-test",
            Name = "model-connectivity-test",
            ChatOptions = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
                Instructions = "Connectivity test. Reply with exactly: pong",
            },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var reply = "";
        await foreach (var update in agent.RunStreamingAsync("ping", await agent.CreateSessionAsync(cts.Token),
            new ChatClientAgentRunOptions(new ChatOptions { ModelId = model }), cts.Token))
        {
            reply += update.Text ?? "";
        }
        return reply.Trim();
    }
}
