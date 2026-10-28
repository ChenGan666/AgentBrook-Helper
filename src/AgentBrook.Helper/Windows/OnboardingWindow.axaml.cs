using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
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
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 首次启动初始化向导（5 步）：Helper 身份 → 大模型 → 语言与提示词 → 工作空间 → 环境检测。
/// 仅新安装弹出（无 onboarding 标记且未配置 models.json）；跳过/完成后写入标记不再出现。
/// 全部文案走 I18n（默认跟随系统语言），语言切换即时刷新。
/// </summary>
public partial class OnboardingWindow : WindowBase
{
    /// <summary>向导用 Helper 图标（emoji 子集，完整 84 个在设置页可选）。</summary>
    private static readonly string[] IconChoices =
    [
        "🤖", "🧑‍🚀", "🦊", "🐱", "🐼", "🦉", "🦄", "🚀", "⭐", "💡", "🎯", "⚡",
        "🧠", "🔮", "🔥", "🛸", "👑", "🌈", "🍀", "💎", "🎧", "🧩", "✨", "🌏",
    ];

    private static OnboardingWindow? _current;

    public static OnboardingWindow ShowSingle()
    {
        if (_current is null)
        {
            _current = new OnboardingWindow();
            _current.Closed += (_, _) => _current = null;
        }
        if (!_current.IsVisible) _current.Show();
        _current.Activate();
        _current.Focus();
        return _current;
    }

    private int _step;
    private string _icon = "🦊";
    private string? _loadedProviderName;
    private bool _providerSaved;   // 步骤 1 的大模型是否已落盘（取名按钮依赖它激活模型）
    private string? _trialKey;     // 试用卡 Key（申请成功后填充）
    private bool _trialFilled;    // 试用卡已自动填充大模型配置（保存时类型名固定 AgentBrook）
    private int _catalogFetchSeq;              // 拉模型防串号：拉取期间表单被编辑/切换预设则结果作废不回写
    private ProviderConfig? _catalogProvider;  // 按当前端点+Key 拉到的完整模型元数据（保存沿用类型/名称/端点/参数）
    private string? _catalogKey;               // _catalogProvider 对应的表单指纹（预设+端点+Key）

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
        presets.SelectionChanged += (_, _) => ApplyPreset();
        presets.SelectedIndex = Array.FindIndex(Presets, p => p.Name == "AgentBrook");

        // 试用卡由按钮手动触发（仅 AgentBrook 预设显示该按钮）
        this.FindControl<Button>("TrialBtn")!.Click += async (_, _) => await RequestTrialAsync();
        this.FindControl<Button>("BuyBtn")!.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(TokenPurchaseUrl) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                var hint = this.FindControl<TextBlock>("TrialHint");
                if (hint is not null) hint.Text = I18n.T("ui.token.open.fail", ex.Message);
            }
        };

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
        this.FindControl<Button>("RepairEnvironmentBtn")!.Click += async (_, _) => await RepairEnvironmentAsync();

        this.FindControl<Button>("ModelTestBtn")!.Click += async (_, _) => await TestProviderInputsAsync();

        var back = this.FindControl<Button>("BackBtn")!;
        var next = this.FindControl<Button>("NextBtn")!;
        back.Click += (_, _) => { if (_step > 0) { _step--; ShowStep(_step); } };
        next.Click += async (_, _) =>
        {
            if (_step == 0)
            {
                next.IsEnabled = false;
                var saved = false;
                try
                {
                    saved = await SaveProviderFromInputsAsync();   // 大模型先行落盘：第 2 步的「取名」才能调用模型
                }
                finally
                {
                    next.IsEnabled = true;
                }
                if (!saved)
                {
                    return;   // 配置不完整/模型拉取失败：提示已显示在 ModelTestHint，停在本步
                }
                _step = 1;
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
        UpdateTrialButtonVisibility();
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
            var settings = AssistantController.LoadProviders(AssistantController.LoadStartupConfig(), UiPrefs.WorkspaceDir());
            var active = settings.Active;
            if (active is not null && active.IsConfigured && !ProviderConfig.IsPlaceholderApiKey(active.ApiKey))
            {
                var presetIndex = Presets.ToList().FindIndex(p => p.Name == active.Name);
                this.FindControl<ComboBox>("PresetBox")!.SelectedIndex = presetIndex >= 0 ? presetIndex : Presets.Length - 1;
                _loadedProviderName = active.Name;
                this.FindControl<TextBox>("BaseUrlBox")!.Text = active.BaseUrl;
                this.FindControl<TextBox>("ApiKeyBox")!.Text = active.ApiKey;
                this.FindControl<TextBox>("ModelsBox")!.Text = string.Join(", ", active.Models);
                _providerSaved = AssistantController.HasUsableChatModel(settings);
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
        try
        {
            return !AssistantController.HasUsableChatModel(AssistantController.LoadProviders(
                AssistantController.LoadStartupConfig(), UiPrefs.WorkspaceDir()));
        }
        catch
        {
            return false;
        }
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
    private List<EnvironmentCheckResult> _environmentResults = new();
    private bool _environmentBusy;
    private bool _environmentChecked;
    private bool _environmentRepaired;

    private void RefreshEnvironmentView()
    {
        EnvironmentResultView.Render(this.FindControl<StackPanel>("EnvList")!, _environmentResults,
            item => RepairEnvironmentAsync(item), busy: _environmentBusy);
        this.FindControl<Button>("RunCheckBtn")!.IsEnabled = !_environmentBusy;
        SetB("RunCheckBtn", _environmentChecked ? "ui.recheck" : "onboarding.s5.run");
        this.FindControl<Button>("RepairEnvironmentBtn")!.IsEnabled = !_environmentBusy && _environmentResults.Any(r => r.CanRepair);
        this.FindControl<TextBlock>("CheckSummary")!.Text = _environmentBusy
            ? I18n.T(_environmentRepaired ? "ui.repairing" : "onboarding.s5.running")
            : _environmentChecked ? EnvironmentResultView.Summary(_environmentResults, _environmentRepaired) : "";
    }

    private async Task RunEnvironmentCheckAsync()
    {
        if (_environmentBusy) return;
        _environmentBusy = true;
        _environmentRepaired = false;
        _environmentChecked = false;
        this.FindControl<TextBlock>("CheckHint")!.Text = "";
        _environmentResults.Clear();
        RefreshEnvironmentView();
        try
        {
            _environmentResults = await EnvironmentChecker.CheckAllAsync(App.Controller.Ready ? App.Controller.Agent : null);
            _environmentChecked = true;
        }
        catch (Exception ex)
        {
            this.FindControl<TextBlock>("CheckHint")!.Text = ex.Message;
        }
        finally
        {
            _environmentBusy = false;
            RefreshEnvironmentView();
        }
    }

    private async Task RepairEnvironmentAsync(EnvironmentCheckResult? item = null)
    {
        if (_environmentBusy) return;
        _environmentBusy = true;
        _environmentRepaired = true;
        this.FindControl<TextBlock>("CheckHint")!.Text = "";
        RefreshEnvironmentView();
        try
        {
            var items = item is null ? _environmentResults.Where(r => r.CanRepair).ToList() : new List<EnvironmentCheckResult> { item };
            foreach (var current in items)
            {
                var result = await EnvironmentChecker.RepairAsync(current);
                var index = _environmentResults.FindIndex(r => r.Id == result.Id);
                if (index >= 0) _environmentResults[index] = result;
            }
            this.FindControl<TextBlock>("CheckHint")!.Text = I18n.T("ui.tried.to.repair.value.item.s.please.follow.the.tips.if.issues.remain", items.Count);
        }
        catch (Exception ex)
        {
            this.FindControl<TextBlock>("CheckHint")!.Text = ex.Message;
        }
        finally
        {
            _environmentBusy = false;
            RefreshEnvironmentView();
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
            st.Text = I18n.T(_step == 4 ? "onboarding.step.last" : "onboarding.step", _step + 1, 5);
        }
        SetT("S1Welcome", "onboarding.welcome");
        SetT("S1NameLabel", "onboarding.s1.name");
        SetT("S1IconLabel", "onboarding.s1.icon");
        SetT("S1Hint", "onboarding.s1.hint");
        SetT("S2Title", "onboarding.s2.title");
        SetT("S2Desc", "onboarding.s2.desc");
        SetT("TitleText", "onboarding.title");
        SetB("NameGenBtn", "ui.naming");
        SetB("ModelTestBtn", "onboarding.s2.test");
        this.FindControl<TextBox>("PromptBox")!.Watermark = I18n.T("onboarding.s3.placeholder");
        SetT("S2PresetLabel", "onboarding.s2.preset");
        SetT("S2BaseUrlLabel", "onboarding.s2.baseurl");
        SetT("S2ApiKeyLabel", "onboarding.s2.apikey");
        SetT("S2ModelsLabel", "onboarding.s2.models");
        SetB("TrialBtn", "onboarding.trial.get");
        SetB("BuyBtn", "onboarding.token.buy");
        SetT("S3Desc", "onboarding.s3.desc");
        SetT("S3LanguageLabel", "onboarding.s3.language");
        SetT("S3PromptLabel", "onboarding.s3.prompt");
        SetT("S4Title", "onboarding.s4.title");
        SetT("S4Desc", "onboarding.s4.desc");
        SetT("S4Label", "onboarding.s4.label");
        SetB("WorkspacePickBtn", "onboarding.s4.choose");
        SetT("WorkspaceHint", "onboarding.s4.hint");
        SetT("S5Title", "onboarding.s5.title");
        SetT("S5Desc", "onboarding.s5.desc");
        SetB("RunCheckBtn", _environmentChecked ? "ui.recheck" : "onboarding.s5.run");
        SetB("RepairEnvironmentBtn", "ui.repair.all");
        RefreshEnvironmentView();
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

    // ── 试用卡：自动申请并填充大模型配置（类型 AgentBrook，端点为中转网关） ──

    private static (string ServiceUrl, string RelayPublicUrl) LoadTokenConfig()
    {
        try
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables("AGENTBROOK_")
                .Build();
            var serviceUrl = configuration["Token:ServiceUrl"];
            var relayUrl = configuration["Token:RelayPublicUrl"];
            return (string.IsNullOrWhiteSpace(serviceUrl) ? "https://helper.agentbrook.com" : serviceUrl,
                    string.IsNullOrWhiteSpace(relayUrl) ? "https://api.agentbrook.com" : relayUrl);
        }
        catch
        {
            return ("https://helper.agentbrook.com", "https://api.agentbrook.com");
        }
    }

    /// <summary>仅当预设为 AgentBrook 时显示「获取体验卡 / 购买Token卡」按钮（其他供应商无此功能）。</summary>
    private void UpdateTrialButtonVisibility()
    {
        var idx = this.FindControl<ComboBox>("PresetBox")!.SelectedIndex;
        var row = this.FindControl<StackPanel>("TokenRow");
        if (row is not null)
        {
            row.IsVisible = idx >= 0 && idx < Presets.Length && Presets[idx].Name == "AgentBrook";
        }
    }

    private async Task RequestTrialAsync()
    {
        var hint = this.FindControl<TextBlock>("TrialHint");
        var btn = this.FindControl<Button>("TrialBtn");
        if (btn is not null) btn.IsEnabled = false;
        if (hint is not null) hint.Text = I18n.T("onboarding.trial.loading");
        try
        {
            var (serviceUrl, relayPublicUrl) = LoadTokenConfig();
            var (fingerprint, hostname, osInfo) = ClientFingerprint.Get();
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { fingerprint, hostname, os = osInfo }),
                Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync($"{serviceUrl.TrimEnd('/')}/Token/Api/Trial", content);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var code = doc.RootElement.GetProperty("code").GetInt32();
            var message = doc.RootElement.GetProperty("message").GetString() ?? "";

            if (code != 0)
            {
                if (hint is not null)
                    hint.Text = I18n.T("onboarding.trial.dup", message);
                return;
            }

            var data = doc.RootElement.GetProperty("data");
            _trialKey = data.GetProperty("key").GetString() ?? "";
            var baseUrl = data.GetProperty("base_url").GetString() ?? relayPublicUrl;
            var models = data.GetProperty("models").EnumerateArray()
                .Select(m => m.GetString() ?? "").Where(m => m.Length > 0).ToList();
            var expire = data.GetProperty("expire").GetString();

            // 自动填充大模型配置：类型 AgentBrook、端点为中转网关、Key 为试用卡 Key
            this.FindControl<TextBox>("BaseUrlBox")!.Text = baseUrl;
            this.FindControl<TextBox>("ApiKeyBox")!.Text = _trialKey;
            this.FindControl<TextBox>("ModelsBox")!.Text = string.Join(", ", models);
            var presetBox = this.FindControl<ComboBox>("PresetBox")!;
            var idx = Presets.ToList().FindIndex(p => p.Name == "AgentBrook");
            if (idx >= 0) presetBox.SelectedIndex = idx;   // 会触发 ApplyPreset，下面重新覆盖动态值
            this.FindControl<TextBox>("BaseUrlBox")!.Text = baseUrl;
            this.FindControl<TextBox>("ApiKeyBox")!.Text = _trialKey;
            this.FindControl<TextBox>("ModelsBox")!.Text = string.Join(", ", models);

            _trialFilled = true;
            if (hint is not null)
                hint.Text = string.IsNullOrWhiteSpace(message)
                    ? I18n.T("onboarding.trial.ok", expire)
                    : message;
        }
        catch (HttpRequestException)
        {
            // 计费服务未部署/不可达属正常场景（空响应或连接失败均走这里），给友好提示
            if (hint is not null)
                hint.Text = I18n.T("onboarding.trial.not.available");
        }
        catch (Exception ex)
        {
            if (hint is not null)
                hint.Text = I18n.T("onboarding.trial.unreachable", ex.Message);
        }
        finally
        {
            if (btn is not null) btn.IsEnabled = true;
        }
    }

    private async Task SaveTrialProviderAsync(string baseUrl, string modelsText)
    {
        var provider = new ProviderConfig
        {
            Name = "AgentBrook",
            BaseUrl = baseUrl,
            ApiKey = _trialKey ?? "",
            Models = modelsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            ActiveModel = "",
        };
        provider.ActiveModel = provider.Models.FirstOrDefault() ?? "";
        var existing = App.Controller.Providers.Select(ModelCatalog.Clone).ToList();
        var index = existing.FindIndex(p => p.Name == provider.Name);
        if (index >= 0) existing[index] = provider; else existing.Add(provider);
        await App.Controller.SaveProvidersAsync(existing, provider.Name);
        _providerSaved = true;
    }

    // ── 预设 → 填充端点/模型（“自定义”不覆盖手输内容） ──
    private void ApplyPreset()
    {
        _loadedProviderName = null;
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
        UpdateTrialButtonVisibility();
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

    /// <summary>当前表单对应的供应商类型名（试用卡固定 AgentBrook，已加载配置优先原名）。</summary>
    private string CurrentPresetName() => _trialFilled ? "AgentBrook"
        : _loadedProviderName ?? (this.FindControl<ComboBox>("PresetBox")!.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "自定义";

    /// <summary>表单指纹：预设+端点+Key；任一变化即视为缓存元数据失效。</summary>
    private string FormCatalogKey() =>
        $"{CurrentPresetName()}\n{this.FindControl<TextBox>("BaseUrlBox")!.Text?.Trim()}\n{this.FindControl<TextBox>("ApiKeyBox")!.Text?.Trim()}";

    /// <summary>表单指纹未变时返回已拉取的模型元数据缓存，否则 null（编辑字段后自动失效）。</summary>
    private ProviderConfig? FormCatalog() =>
        _catalogProvider is not null && _catalogKey == FormCatalogKey() ? _catalogProvider : null;

    /// <summary>错误信息脱敏：抹掉可能回显的 API Key。</summary>
    private static string MaskKey(string? message, string apiKey) =>
        string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(message)
            ? message ?? ""
            : message.Replace(apiKey, "***", StringComparison.Ordinal);

    /// <summary>端点+Key 已填但模型清单为空时，复用 ModelCatalog.FetchAsync/Apply 拉取并填充 ModelsBox，
    /// 同时缓存完整元数据（类型/名称/端点/参数）供测试选对话模型与保存沿用。
    /// 拉取期间表单被编辑或切换预设则结果作废、不回写。返回是否已有可用模型清单。</summary>
    private async Task<bool> FetchModelsIntoFormAsync(TextBlock? hint)
    {
        var modelsBox = this.FindControl<TextBox>("ModelsBox")!;
        var preset = CurrentPresetName();
        var baseUrl = this.FindControl<TextBox>("BaseUrlBox")!.Text?.Trim() ?? "";
        var apiKey = this.FindControl<TextBox>("ApiKeyBox")!.Text?.Trim() ?? "";
        var modelsBefore = modelsBox.Text ?? "";
        var seq = ++_catalogFetchSeq;
        var probe = new ProviderConfig { Name = preset, BaseUrl = baseUrl, ApiKey = apiKey };
        List<ModelCatalog.Entry> entries;
        try
        {
            entries = await ModelCatalog.FetchAsync(probe);
        }
        catch (Exception ex)
        {
            if (seq == _catalogFetchSeq && hint is not null)
            {
                hint.Text = "⚠ " + I18n.T("ui.models.fetch.fail", FriendlyRelayError(MaskKey(ex.Message, apiKey)));
            }
            return false;
        }
        // 拉取期间用户改了表单/切换了预设：结果作废，不覆盖当前输入
        if (seq != _catalogFetchSeq
            || FormCatalogKey() != $"{preset}\n{baseUrl}\n{apiKey}"
            || (modelsBox.Text ?? "") != modelsBefore)
        {
            return false;
        }
        if (entries.Count == 0)
        {
            if (hint is not null)
            {
                hint.Text = "⚠ " + I18n.T("ui.models.fetch.empty");
            }
            return false;
        }
        ModelCatalog.Apply(probe, entries);   // 全量入列并记录类型/名称/端点/参数
        _catalogProvider = probe;
        _catalogKey = FormCatalogKey();
        modelsBox.Text = string.Join(", ", probe.Models);
        return true;
    }

    /// <summary>把步骤 1 输入的供应商配置落盘（热应用到当前会话）。返回是否可进入下一步：
    /// 地址与 Key 全空视为用户跳过（放行）；填写了但缺项/模型拉取失败/无对话模型则提示并返回 false。</summary>
    private async Task<bool> SaveProviderFromInputsAsync()
    {
        var hint = this.FindControl<TextBlock>("ModelTestHint");
        var baseUrl = this.FindControl<TextBox>("BaseUrlBox")!.Text?.Trim() ?? "";
        var apiKey = this.FindControl<TextBox>("ApiKeyBox")!.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(baseUrl) && string.IsNullOrEmpty(apiKey))
        {
            return true;   // 用户跳过了大模型配置
        }
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            if (hint is not null)
            {
                hint.Text = "⚠ " + I18n.T("ui.models.fetch.need.url.key");
            }
            return false;
        }
        var models = (this.FindControl<TextBox>("ModelsBox")!.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => m.Length > 0)
            .ToList();
        if (models.Count == 0)
        {
            // 未点「测试」直接下一步：同样自动拉取模型清单，失败则留在本步
            if (!await FetchModelsIntoFormAsync(hint))
            {
                return false;
            }
            models = (this.FindControl<TextBox>("ModelsBox")!.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(m => m.Length > 0)
                .ToList();
            if (models.Count == 0)
            {
                return false;
            }
        }
        var presetName = CurrentPresetName();
        var catalog = FormCatalog();
        var provider = catalog is not null ? ModelCatalog.Clone(catalog) : new ProviderConfig();
        provider.Name = string.IsNullOrWhiteSpace(presetName) ? "自定义" : presetName;
        provider.BaseUrl = baseUrl;
        provider.ApiKey = apiKey;
        provider.Models = models;
        // 元数据按表单清单取交集（用户可能删减了部分模型）；无缓存时保持空，未知模型按文本处理
        provider.ModelTypes = provider.ModelTypes.Where(kv => models.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        provider.ModelNames = provider.ModelNames.Where(kv => models.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        provider.ModelEndpoints = provider.ModelEndpoints.Where(kv => models.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        provider.ModelParams = provider.ModelParams.Where(kv => models.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        provider.ActiveModel = models.FirstOrDefault(provider.IsChatModel) ?? "";
        if (provider.ActiveModel.Length == 0)
        {
            if (hint is not null)
            {
                hint.Text = "⚠ " + I18n.T("ui.models.fetch.only.multimodal", models.Count);
            }
            return false;
        }
        try
        {
            // 合并保存：保留其他已有供应商，仅替换/追加当前这一个（避免覆盖用户原有配置）
            var existing = App.Controller.Providers.Select(ModelCatalog.Clone).ToList();
            var idx = existing.FindIndex(p => p.Name == provider.Name);
            if (idx >= 0)
            {
                var saved = existing[idx];
                saved.BaseUrl = provider.BaseUrl;
                saved.ApiKey = provider.ApiKey;
                saved.Models = provider.Models;
                if (catalog is not null)
                {
                    // 有本次拉取的元数据则刷新；否则保留已存配置，避免清空类型/名称/端点/参数
                    saved.ModelTypes = provider.ModelTypes;
                    saved.ModelNames = provider.ModelNames;
                    saved.ModelEndpoints = provider.ModelEndpoints;
                    saved.ModelParams = provider.ModelParams;
                }
                if (!saved.Models.Contains(saved.ActiveModel) || !saved.IsChatModel(saved.ActiveModel))
                    saved.ActiveModel = saved.Models.FirstOrDefault(saved.IsChatModel) ?? "";
                if (!saved.Models.Contains(saved.ActiveMediaModel) || saved.IsChatModel(saved.ActiveMediaModel))
                    saved.ActiveMediaModel = "";
            }
            else existing.Add(provider);
            await App.Controller.SaveProvidersAsync(existing, provider.Name);
            _providerSaved = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[onboarding] provider save failed: {ex.Message}");
            if (hint is not null)
            {
                hint.Text = "✗ " + I18n.T("ui.save.failed.value", MaskKey(ex.Message, apiKey));
            }
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
                    PostHint(hint, I18n.T("onboarding.s1.naming.failed") + " " + ex.Message);
                    break;
                }
                PostHint(hint, I18n.T("onboarding.s1.naming.failed") + " " + ex.Message + " " + I18n.T("onboarding.retry"));
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
        if (!await SaveProviderFromInputsAsync() && !_providerSaved)
        {
            throw new InvalidOperationException(I18n.T("onboarding.s1.naming.need.key"));
        }
        var assistant = App.Controller;
        var settings = new ProviderSettings { Providers = assistant.Providers.ToList(), ActiveProvider = assistant.CurrentProvider };
        if (!AssistantController.HasUsableChatModel(settings))
        {
            throw new InvalidOperationException(I18n.T("onboarding.s1.naming.need.key"));
        }
        var active = settings.Active!;
        var model = string.IsNullOrEmpty(active.ActiveModel) ? active.Models[0] : active.ActiveModel;
        var client = AgentFactory.CreateChatClient(active.BaseUrl, active.ApiKey, model, maxRetries: 0);
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
        PostHint(hint, I18n.T("onboarding.s1.naming.done", result));
    }

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

    /// <summary>大模型连通性测试：用表单当前填写的参数直连一次对话补全（不落盘）。
    /// 地址+Key 已填但模型为空时先自动拉取清单并缓存元数据；测试优先挑对话模型，避免落到图像/视频。</summary>
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
        var testBtn = this.FindControl<Button>("ModelTestBtn")!;
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            if (hint is not null)
            {
                hint.Text = "⚠ " + I18n.T("onboarding.s2.test.need");
            }
            return;
        }
        testBtn.IsEnabled = false;
        if (models.Count == 0)
        {
            // 地址与 Key 已填但模型清单为空（如 AgentBrook 预设默认空）：先拉取，失败直接提示原因
            testBtn.Content = I18n.T("onboarding.s2.fetching");
            if (!await FetchModelsIntoFormAsync(hint))
            {
                testBtn.IsEnabled = true;
                testBtn.Content = I18n.T("onboarding.s2.test");
                return;
            }
            models = (this.FindControl<TextBox>("ModelsBox")!.Text ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
        var catalog = FormCatalog();
        var probe = catalog is not null ? ModelCatalog.Clone(catalog) : new ProviderConfig();
        probe.Name = CurrentPresetName();
        probe.BaseUrl = baseUrl;
        probe.ApiKey = apiKey;
        probe.Models = models;
        var model = models.FirstOrDefault(probe.IsChatModel) ?? "";
        if (model.Length == 0)
        {
            if (hint is not null)
            {
                hint.Text = "⚠ " + I18n.T("ui.models.fetch.only.multimodal", models.Count);
            }
            testBtn.IsEnabled = true;
            testBtn.Content = I18n.T("onboarding.s2.test");
            return;
        }
        var apiBase = ModelCatalog.EffectiveApiBase(probe);
        // DeepSeek 服务端偶发过载（503）会挂起请求：最多 3 次尝试，间隔 2.5 秒，进度显示在提示行
        for (var attempt = 1; ; attempt++)
        {
            testBtn.Content = $"{I18n.T("onboarding.s2.testing")}（{attempt}）";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var reply = await PingModelAsync(apiBase, apiKey, model);
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
                    hint.Text = $"⚠ {I18n.T("ui.models.test.fail", FriendlyRelayError(MaskKey(ex.Message, apiKey)))} ({I18n.T("onboarding.attempt", attempt)}) {I18n.T("onboarding.retry")}";
                }
                await Task.Delay(2500);
            }
            catch (Exception ex)
            {
                if (hint is not null)
                {
                    hint.Text = "✗ " + I18n.T("ui.models.test.fail", FriendlyRelayError(MaskKey(ex.Message, apiKey)));
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
