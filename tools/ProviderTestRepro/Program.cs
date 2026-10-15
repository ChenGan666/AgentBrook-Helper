using System.Text.Json;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// ─────────────────────────────────────────────────────────────────────────────
// 模型连通性无头复现：读取本机 workspace/models.json 的真实配置，
// 按「测试」按钮的完全相同路径（AgentFactory.CreateChatClient → 流式对话补全）
// 依次测试：带 /v1 的地址、不带 /v1 的地址、onboarding 表单解析出的参数。
// ─────────────────────────────────────────────────────────────────────────────

var ws = args.Length > 0 ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentBrook", "workspace");
var modelsPath = Path.Combine(ws, "models.json");
Log($"models.json = {modelsPath}（存在：{File.Exists(modelsPath)}）");
if (!File.Exists(modelsPath))
{
    Log("✖ 找不到 models.json，退出");
    return;
}

var settings = ProviderSettings.Load(modelsPath);
var active = settings.Providers.FirstOrDefault(p => p.Name == settings.ActiveProvider)
    ?? settings.Providers.FirstOrDefault(p => p.IsConfigured);
if (active is null)
{
    Log("✖ models.json 中没有已配置的供应商");
    return;
}
Log($"激活供应商：{active.Name} | ActiveModel={active.ActiveModel} | 模型=[{string.Join(", ", active.Models)}]");
Log($"BaseUrl={active.BaseUrl}");

// 变体 1：配置原样
await TestAsync("原样（models.json 的 BaseUrl）", active.BaseUrl, active.ApiKey, active.ActiveModel);
// 变体 2：去掉 /v1（onboarding 预设的新默认）
await TestAsync("去 /v1", active.BaseUrl.Replace("/v1", ""), active.ApiKey, active.ActiveModel);
// 变体 3：onboarding 表单的参数解析路径（逗号分隔第一个）
var first = active.Models.FirstOrDefault() ?? "";
await TestAsync("onboarding 解析（首模型）", active.BaseUrl, active.ApiKey, first);

Log("复现结束");
return;

static async Task TestAsync(string label, string baseUrl, string apiKey, string model)
{
    Log($"── 测试 [{label}] baseUrl={baseUrl} model={model} ──");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var client = AgentFactory.CreateChatClient(baseUrl, apiKey, model);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = "repro",
            Name = "repro",
            ChatOptions = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
                Instructions = "Connectivity test. Reply with exactly: pong",
            },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var reply = "";
        await foreach (var update in agent.RunStreamingAsync("ping", await agent.CreateSessionAsync(cts.Token),
            new ChatClientAgentRunOptions(new ChatOptions { ModelId = model }), cts.Token))
        {
            reply += update.Text ?? "";
        }
        sw.Stop();
        Log($"✅ [{label}] {sw.ElapsedMilliseconds}ms 回复：{(reply.Trim().Length == 0 ? "(空)" : reply.Trim())}");
    }
    catch (Exception ex)
    {
        sw.Stop();
        Log($"✖ [{label}] {sw.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null)
        {
            Log($"   └ 内层：{ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
