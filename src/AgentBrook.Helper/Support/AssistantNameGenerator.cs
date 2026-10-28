using AgentBrook.Helper.Windows;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using AgentBrook.Agent.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper.Support;

/// <summary>
/// 助手取名：用当前激活供应商直连一次对话补全（快速失败——禁用 SDK 重试，503/错误即时暴露）。
/// 初始化向导与设置页共用。
/// </summary>
internal static class AssistantNameGenerator
{
    public static async Task<string> GenerateAsync(string replyLang)
    {
        var controller = App.Controller;
        var providers = controller.Providers;
        var active = providers.FirstOrDefault(p => p.Name == controller.CurrentProvider)
            ?? providers.FirstOrDefault(p => p.IsConfigured)
            ?? throw new InvalidOperationException(I18n.T("ui.models.test.need.config"));
        var (model, client) = (active.ActiveModel,
            AgentFactory.CreateChatClient(active.BaseUrl, active.ApiKey, active.ActiveModel, maxRetries: 0));
        var style = new[] { "中式古典", "欧美", "卡通", "二次元" }[Random.Shared.Next(4)];
        var langRule = replyLang switch
        {
            "en" => "The name should be a single English word.",
            "id" => "The name should be in Bahasa Indonesia.",
            _ => "名字用中文，1-4 个字。",
        };
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
            Instructions = $"用「{style}」风格为一位桌面 AI 助手起一个名字。{langRule} 只输出名字本身。",
        };
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = "assistant-name-gen",
            Name = "assistant-name-gen",
            Description = "初始化向导：为助手生成名字",
            ChatOptions = options,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));   // 推理型模型首响应可能较慢
        var result = "";
        await foreach (var update in agent.RunStreamingAsync(
            I18n.T("onboarding.s1.naming.prompt"),
            await agent.CreateSessionAsync(cts.Token),
            new ChatClientAgentRunOptions(new ChatOptions { ModelId = model }), cts.Token))
        {
            result += update.Text ?? "";
        }
        result = result.Trim().Trim('"', '\u201c', '\u201d', '\u300c', '\u300d', '\'', ' ');
        if (string.IsNullOrWhiteSpace(result) || result.Length > 24)
        {
            throw new InvalidOperationException(I18n.T("onboarding.s1.naming.failed"));
        }
        return result;
    }
}
