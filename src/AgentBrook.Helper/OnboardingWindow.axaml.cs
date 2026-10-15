using AgentBrook.Agent.Infrastructure;
using AgentBrook.Helper.EnvironmentCheck;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using System.Diagnostics;

namespace AgentBrook.Helper;

/// <summary>
/// 首次启动初始化向导（5 步）：Helper 身份 → 大模型 → 语言与提示词 → 工作空间 → 环境检测。
/// 仅新安装弹出（无 onboarding 标记且未配置 models.json）；跳过/完成后写入标记不再出现。
/// 全部文案走 I18n（默认跟随系统语言），语言切换即时刷新。
/// </summary>
public partial class OnboardingWindow : WindowBase
{
    /// <summary>预设模板：名称 →（默认端点，默认模型 CSV）。与设置窗口保持一致。</summary>
    /// <summary>向导用 Helper 图标（emoji 子集，完整 84 个在设置页可选）。</summary>
    private static readonly string[] IconChoices =
    [
        "🤖", "🧑‍🚀", "🦊", "🐱", "🐼", "🦉", "🦄", "🚀", "⭐", "💡", "🎯", "⚡",
        "🧠", "🔮", "🔥", "🛸", "👑", "🌈", "🍀", "💎", "🎧", "🧩", "✨", "🌏",
    ];

    private int _step;
    private string _icon = "🦊";
    private bool _providerSaved;   // 步骤 1 的大模型是否已落盘（取名按钮依赖它激活模型）

    public OnboardingWindow()
    {
        InitializeComponent();
        Title = I18n.T("onboarding.title");

        BuildIconGrid();

        var presets = this.FindControl<ComboBox>("PresetBox")!;
        foreach (var (name, _, _) in Presets)
        {
            presets.Items.Add(new ComboBoxItem { Content = name });
        }
        presets.SelectedIndex = 0;
        presets.SelectionChanged += (_, _) => ApplyPreset();

        this.FindControl<Button>("SkipBtn")!.Click += (_, _) => CloseOnboarding("skipped");

        this.FindControl<Button>("WorkspacePickBtn")!.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = I18n.T("onboarding.s4.choose"),
                AllowMultiple = false,
            });
            if (folders.Count > 0)
            {
                this.FindControl<TextBox>("WorkspaceBox")!.Text = folders[0].Path.LocalPath;
            }
        };

        this.FindControl<Button>("RunCheckBtn")!.Click += async (_, _) => await RunEnvironmentCheckAsync();

        this.FindControl<Button>("ModelTestBtn")!.Click += async (_, _) => await TestProviderInputsAsync();

        var back = this.FindControl<Button>("BackBtn")!;
        var next = this.FindControl<Button>("NextBtn")!;
        back.Click += (_, _) => { if (_step > 0) { _step--; ShowStep(_step); } };
        next.Click += async (_, _) =>
        {
            if (_step == 0)
            {
                _step = 1;
                SaveProviderFromInputs();   // 大模型先行落盘：第 2 步的「取名」才能调用模型
                ShowStep(_step);
                return;
            }
            if (_step < 4)
            {
                _step++;
                ShowStep(_step);
            }
            else
            {
                next.IsEnabled = false;
                await FinishAsync();
            }
        };
        var nameGen = this.FindControl<Button>("NameGenBtn")!;
        nameGen.Click += async (_, _) => await GenerateNameAsync(nameGen);

        this.FindControl<Button>("RestartBtn")!.Click += (_, _) => RestartApp();

        WatchLanguageChanges();
        OnLanguageChanged();
        ShowStep(0);
        LoadExistingIntoForms();
    }

    /// <summary>已有配置回填表单（设置页可重复打开本向导）：模型/身份/语言/提示词/工作空间。
    /// 模型已配置时标记"已落盘"，取名与完成流程可直接使用。</summary>
    private void LoadExistingIntoForms()
    {
        // 身份
        this.FindControl<TextBox>("NameBox")!.Text =
            string.IsNullOrWhiteSpace(AssistantIdentity.Current) ? "Brook" : AssistantIdentity.Current;
        var a2aIcon = App.A2A?.Controller?.Config?.Icon;
        if (!string.IsNullOrWhiteSpace(a2aIcon))
        {
            _icon = a2aIcon;
            foreach (ToggleButton b in this.FindControl<WrapPanel>("IconGrid")!.Children.OfType<ToggleButton>())
            {
                b.IsChecked = (b.Content as string) == _icon;
            }
        }
        // 大模型：workspace/models.json 的激活供应商
        try
        {
            var modelsPath = Path.Combine(UiPrefs.WorkspaceDir(), "models.json");
            if (File.Exists(modelsPath))
            {
                var settings = ProviderSettings.Load(modelsPath);
                var active = settings.Providers.FirstOrDefault(p => p.Name == settings.ActiveProvider)
                    ?? settings.Providers.FirstOrDefault(p => p.IsConfigured);
                if (active is not null)
                {
                    this.FindControl<TextBox>("BaseUrlBox")!.Text = active.BaseUrl;
                    this.FindControl<TextBox>("ApiKeyBox")!.Text = active.ApiKey;
                    this.FindControl<TextBox>("ModelsBox")!.Text = string.Join(", ", active.Models);
                    var presetIndex = Presets.ToList().FindIndex(p => p.Name == active.Name);
                    if (presetIndex >= 0)
                    {
                        this.FindControl<ComboBox>("PresetBox")!.SelectedIndex = presetIndex;
                    }
                    _providerSaved = true;
                }
            }
        }
        catch
        {
            // 读取失败按全新配置处理
        }
        // 语言与提示词
        this.FindControl<ComboBox>("ReplyLangBox")!.SelectedIndex = AssistantIdentity.Language switch
        {
            "en" => 1,
            "id" => 2,
            _ => 0
        };
        this.FindControl<TextBox>("PromptBox")!.Text = AssistantIdentity.CustomPrompt;
        // 工作空间：显示当前生效目录（含覆盖配置）
        this.FindControl<TextBox>("WorkspaceBox")!.Text = UiPrefs.WorkspaceDir();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>是否需要初始化引导：无 onboarding 标记且未配置过 models.json（老安装自动跳过）。</summary>
    public static bool IsNeeded()
    {
        if (UiPrefs.Load<OnboardingState>("onboarding.json") is not null)
        {
            return false;
        }
        var modelsPath = Path.Combine(UiPrefs.WorkspaceDir(), "models.json");
        return !File.Exists(modelsPath);
    }

    private sealed record OnboardingState(string State);

    private void MarkOnboarding(string state)
        => UiPrefs.Save("onboarding.json", new OnboardingState(state));

    private void CloseOnboarding(string state)
    {
        MarkOnboarding(state);
        Close();
    }

    /// <summary>运行环境检测：结果就地渲染到列表（标题 + 说明）。</summary>
    private async Task RunEnvironmentCheckAsync()
    {
        var hint = this.FindControl<TextBlock>("CheckHint");
        var list = this.FindControl<ItemsControl>("EnvList")!;
        var btn = this.FindControl<Button>("RunCheckBtn")!;
        btn.IsEnabled = false;
        if (hint is not null)
        {
            hint.Text = I18n.T("onboarding.s5.running");
        }
        try
        {
            var results = await EnvironmentChecker.CheckAllAsync(App.Controller.Agent);
            list.ItemsSource = results;
        }
        catch (Exception ex)
        {
            if (hint is not null)
            {
                hint.Text = ex.Message;
            }
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ── 文案（跟随界面语言，切换即时刷新） ──
    /// <summary>语言切换/向导步骤变化时刷新全部文案。</summary>
    protected override void OnLanguageChanged()
    {
        Title = I18n.T("onboarding.title");
        SetB("SkipBtn", "onboarding.skip");
        SetB("BackBtn", "onboarding.back");
        SetB("NextBtn", _step == 4 ? "onboarding.finish" : "onboarding.next");
        var st = this.FindControl<TextBlock>("StepText");
        if (st is not null)
        {
            st.Text = I18n.T("onboarding.step", _step + 1, 5);
        }
        SetT("S1Welcome", "onboarding.welcome");
        SetT("S1NameLabel", "onboarding.s1.name");
        SetT("S1IconLabel", "onboarding.s1.icon");
        SetT("S1Hint", "onboarding.s1.hint");
        SetT("S2Title", "onboarding.s2.title");
        SetT("S2Desc", "onboarding.s2.desc");
        SetT("S2PresetLabel", "onboarding.s2.preset");
        SetT("S2BaseUrlLabel", "onboarding.s2.baseurl");
        SetT("S2ApiKeyLabel", "onboarding.s2.apikey");
        SetT("S2ModelsLabel", "onboarding.s2.models");
        SetT("S3Desc", "onboarding.s3.desc");
        SetT("S3LanguageLabel", "onboarding.s3.language");
        SetT("S3PromptLabel", "onboarding.s3.prompt");
        SetT("S4Title", "onboarding.s4.title");
        SetT("S4Desc", "onboarding.s4.desc");
        SetT("S4Label", "onboarding.s4.label");
        SetB("WorkspacePickBtn", "onboarding.s4.choose");
        SetT("S4Hint", "onboarding.s4.hint");
        SetT("S5Title", "onboarding.s5.title");
        SetT("S5Desc", "onboarding.s5.desc");
        SetB("RunCheckBtn", "onboarding.s5.run");
        SetT("DoneTitle", "onboarding.done.title");
        SetT("DoneDesc", "onboarding.done.desc");
        SetB("RestartBtn", "onboarding.restart");
    }

    // ── 步骤切换 ──
    private void ShowStep(int step)
    {
        _step = step;
        // 本项目 XAML 为运行时加载，不生成 x:Name 字段——控件一律 FindControl（与全仓写法一致）
        var panels = new[] { "StepModels", "StepIdentity", "Step3", "Step4", "Step5" };
        for (var i = 0; i < panels.Length; i++)
        {
            if (this.FindControl<StackPanel>(panels[i]) is { } panel)
            {
                panel.IsVisible = i == step;
            }
        }
        if (this.FindControl<StackPanel>("DonePanel") is { } done)
        {
            done.IsVisible = false;
        }
        var back = this.FindControl<Button>("BackBtn")!;
        var next = this.FindControl<Button>("NextBtn")!;
        back.IsVisible = step > 0;
        next.IsVisible = true;
        next.Content = I18n.T(step == 4 ? "onboarding.finish" : "onboarding.next");
        var stepText = this.FindControl<TextBlock>("StepText");
        if (stepText is not null)
        {
            stepText.Text = I18n.T(step == 4 ? "onboarding.step.last" : "onboarding.step", step + 1, 5);
        }
    }

    // ── 图标网格 ──
    private void BuildIconGrid()
    {
        var grid = this.FindControl<WrapPanel>("IconGrid")!;
        foreach (var icon in IconChoices)
        {
            var choice = icon;
            var btn = new ToggleButton
            {
                Content = choice,
                Classes = { "iconBtn" },
            };
            btn.Click += (_, _) =>
            {
                _icon = choice;
                foreach (ToggleButton b in grid.Children.OfType<ToggleButton>())
                {
                    b.IsChecked = ReferenceEquals(b.Content, choice) || (b.Content as string) == choice;
                }
            };
            grid.Children.Add(btn);
        }
        var first = grid.Children.OfType<ToggleButton>().FirstOrDefault();
        if (first is not null)
        {
            first.IsChecked = true;
        }
    }

    // ── 预设 → 填充端点/模型（“自定义”不覆盖手输内容） ──
    private void ApplyPreset()
    {
        var index = this.FindControl<ComboBox>("PresetBox")!.SelectedIndex;
        if (index < 0 || index >= Presets.Length)
        {
            return;
        }
        var (_, url, models) = Presets[index];
        if (!string.IsNullOrEmpty(url))
        {
            this.FindControl<TextBox>("BaseUrlBox")!.Text = url;
            this.FindControl<TextBox>("ModelsBox")!.Text = models;
        }
    }

    // ── 完成：按步骤依次落盘 ──
    private async Task FinishAsync()
    {
        // 步骤 1：身份（名称 + Helper 图标，A2A 对外身份即时生效）
        var name = this.FindControl<TextBox>("NameBox")!.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = "Brook";
        }
        try
        {
            var a2a = App.A2A?.Controller;
            if (a2a is not null)
            {
                await a2a.ApplyIconAsync(_icon);
                await a2a.ApplyDisplayNameAsync(name);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[onboarding] identity apply failed: {ex.Message}");
        }

        // 步骤 3：交流语言 + 默认提示词（含热更新模型指令）
        var replyLang = (this.FindControl<ComboBox>("ReplyLangBox")!.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh";
        var prompt = this.FindControl<TextBox>("PromptBox")!.Text?.Trim() ?? "";
        AssistantIdentity.Apply(name, AssistantIdentity.BubbleIcon, replyLang, prompt, AssistantIdentity.UiLanguage);
        try
        {
            await App.Controller.ApplyAssistantSettingsAsync(name, replyLang, prompt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[onboarding] assistant settings apply failed: {ex.Message}");
        }

        // 步骤 4：工作空间（填写了自定义目录才保存；默认目录不动）
        var workspace = this.FindControl<TextBox>("WorkspaceBox")!.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(workspace))
        {
            WorkspacePrefs.SaveOverride(workspace);
        }

        MarkOnboarding("completed");
        ShowDone();
    }

    /// <summary>把步骤 1 输入的供应商配置落盘（热应用到当前会话）。返回是否保存成功。</summary>
    private bool SaveProviderFromInputs()
    {
        var baseUrl = this.FindControl<TextBox>("BaseUrlBox")!.Text?.Trim() ?? "";
        var apiKey = this.FindControl<TextBox>("ApiKeyBox")!.Text?.Trim() ?? "";
        var modelsText = this.FindControl<TextBox>("ModelsBox")!.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return false;   // 用户跳过了大模型配置
        }
        var models = modelsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => m.Length > 0)
            .ToList();
        if (models.Count == 0)
        {
            return false;
        }
        var presetName = (this.FindControl<ComboBox>("PresetBox")!.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "自定义";
        var provider = new ProviderConfig
        {
            Name = string.IsNullOrWhiteSpace(presetName) ? "自定义" : presetName,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Models = models,
            ActiveModel = models.FirstOrDefault() ?? "",
        };
        try
        {
            App.Controller.SaveProvidersAsync([provider], provider.Name).GetAwaiter().GetResult();
            _providerSaved = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[onboarding] provider save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>「取名」：用步骤 1 配置的大模型生成一个助手名字并填入名称框。</summary>
    private async Task GenerateNameAsync(Button button)
    {
        var hint = this.FindControl<TextBlock>("S1Hint")!;
        var original = button.Content;
        // DeepSeek 服务端偶发过载（503）会挂起请求：最多 2 次尝试，间隔 2.5 秒，进度显示在提示行
        for (var namingAttempt = 1; ; namingAttempt++)
        {
            button.IsEnabled = false;
            button.Content = namingAttempt == 1
                ? I18n.T("onboarding.s1.naming.working")
                : $"{I18n.T("onboarding.s1.naming.working")}（{namingAttempt}）";
            try
            {
                await GenerateNameOnce();
                break;   // 成功
            }
            catch (Exception ex)
            {
                if (namingAttempt >= 2)
                {
                    ShowCollabHintFor(hint, I18n.T("onboarding.s1.naming.failed") + " " + ex.Message);
                    break;
                }
                ShowCollabHintFor(hint, I18n.T("onboarding.s1.naming.failed") + " " + ex.Message + "，自动重试…");
                await Task.Delay(2500);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    /// <summary>单次取名：校验 → 快速失败客户端（禁用 SDK 重试，503/错误即时暴露）→ 流式生成 → 填入名称框。</summary>
    private async Task GenerateNameOnce()
    {
        var hint = this.FindControl<TextBlock>("S1Hint")!;
        if (!SaveProviderFromInputs() && !_providerSaved)
        {
            throw new InvalidOperationException(I18n.T("onboarding.s1.naming.need.key"));
        }
        var assistant = App.Controller;
        if (assistant.Ready is false || assistant.Agent is null)
        {
            throw new InvalidOperationException(I18n.T("onboarding.s1.naming.need.key"));
        }
        var providers = assistant.Providers;
        var active = providers.FirstOrDefault(p => p.Name == assistant.CurrentProvider)
            ?? providers.FirstOrDefault(p => p.IsConfigured);
        if (active is null || !active.IsConfigured)
        {
            throw new InvalidOperationException(I18n.T("onboarding.s1.naming.need.key"));
        }
        var (model, client) = (active.ActiveModel,
            AgentFactory.CreateChatClient(active.BaseUrl, active.ApiKey, active.ActiveModel, maxRetries: 0));
        // 名字语言跟随当前界面语言（auto 模式下即系统语言）；风格每次随机
        var style = new[] { "文艺风网名：诗意满满，彰显格调", "搞笑网名：幽默诙谐，轻松有趣", "二次元风格网名：赛博未来，个性吸睛", "著名小说角色：江湖英雄，柔美清纯" }[Random.Shared.Next(4)];
        var langRule = I18n.Lang switch
        {
            "en" => "The name should be a single English word.",
            "id" => "The name should be in Bahasa Indonesia.",
            _ => "名字用中文，2-4 个字。",
        };
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
            Instructions = $"用「{style}」风格为一位桌面 AI 助手起一个名字。{langRule} 只输出名字本身。",
        };
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = "onboarding-name-gen",
            Name = "onboarding-name-gen",
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
        this.FindControl<TextBox>("NameBox")!.Text = result;
        ShowCollabHintFor(hint, I18n.T("onboarding.s1.naming.done", result));
    }

    private static void ShowCollabHintFor(TextBlock hint, string message)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => hint.Text = message);

    private void ShowDone()
    {
        foreach (var name in new[] { "Step1", "Step2", "Step3", "Step4", "Step5" })
        {
            if (this.FindControl<StackPanel>(name) is { } panel)
            {
                panel.IsVisible = false;
            }
        }
        if (this.FindControl<StackPanel>("DonePanel") is { } done)
        {
            done.IsVisible = true;
        }
        var back = this.FindControl<Button>("BackBtn")!;
        var next = this.FindControl<Button>("NextBtn")!;
        back.IsVisible = false;
        next.IsVisible = false;
        this.FindControl<Button>("SkipBtn")!.IsVisible = false;
        var stepText = this.FindControl<TextBlock>("StepText");
        if (stepText is not null)
        {
            stepText.Text = "";
        }
    }

    /// <summary>大模型连通性测试：用表单当前填写的参数直连一次对话补全（不落盘）。</summary>
    private async Task TestProviderInputsAsync()
    {
        var baseUrl = this.FindControl<TextBox>("BaseUrlBox")!.Text?.Trim() ?? "";
        var apiKey = this.FindControl<TextBox>("ApiKeyBox")!.Text?.Trim() ?? "";
        var models = (this.FindControl<TextBox>("ModelsBox")!.Text ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var hint = this.FindControl<TextBlock>("ModelTestHint");
        if (hint is not null)
        {
            hint.Text = "";
        }
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey) || models.Count == 0)
        {
            if (hint is not null)
            {
                hint.Text = "⚠ " + I18n.T("onboarding.s2.test.need");
            }
            return;
        }
        var model = models[0];
        var testBtn = this.FindControl<Button>("ModelTestBtn")!;
        testBtn.IsEnabled = false;
        // DeepSeek 服务端偶发过载（503）会挂起请求：最多 3 次尝试，间隔 2.5 秒，进度显示在提示行
        for (var attempt = 1; ; attempt++)
        {
            testBtn.Content = $"{I18n.T("onboarding.s2.testing")}（{attempt}）";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var reply = await PingModelAsync(baseUrl, apiKey, model);
                sw.Stop();
                if (hint is not null)
                {
                    var shown = string.IsNullOrWhiteSpace(reply) ? "(空回复)" : (reply.Length > 40 ? reply[..40] + "…" : reply);
                    hint.Text = $"✅ {I18n.T("ui.models.test.ok", sw.ElapsedMilliseconds / 1000.0, shown)}";
                }
                break;
            }
            catch (OperationCanceledException) when (attempt < 3)
            {
                if (hint is not null)
                {
                    hint.Text = $"⏱ 第 {attempt} 次尝试超时，自动重试…";
                }
                await Task.Delay(2500);
            }
            catch (Exception ex) when (attempt < 3)
            {
                if (hint is not null)
                {
                    hint.Text = $"⚠ {I18n.T("ui.models.test.fail", ex.Message)}（第 {attempt} 次），自动重试…";
                }
                await Task.Delay(2500);
            }
            catch (Exception ex)
            {
                if (hint is not null)
                {
                    hint.Text = "✗ " + I18n.T("ui.models.test.fail", ex.Message);
                }
                break;
            }
        }
        testBtn.IsEnabled = true;
        testBtn.Content = I18n.T("onboarding.s2.test");
    }

    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
