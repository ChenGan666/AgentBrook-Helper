using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.Skills;
using AgentBrook.Helper.EnvironmentCheck;
using AgentBrook.Helper.Update;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;

namespace AgentBrook.Helper;

/// <summary>
/// 模型设置：管理多个 OpenAI 兼容供应商（名称/端点/Key/模型清单），
/// 保存后热应用到会话（切换供应商时保留对话上下文）。
/// </summary>
public partial class ModelSettingsWindow : Window
{
    /// <summary>预设模板：名称 →（默认端点，默认模型 CSV）。</summary>
    private static readonly (string Name, string BaseUrl, string Models)[] Presets =
    [
        ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat, deepseek-reasoner"),
        ("OpenAI", "https://api.openai.com/v1", "gpt-4o, gpt-4o-mini"),
        ("Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k, moonshot-v1-32k"),
        ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.5-flash"),
        ("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini"),
        ("自定义", "", ""),
    ];

    private List<ProviderConfig> _working = new();
    private string _activeName = "";
    private int _selectedIndex = -1;
    private bool _dirty;
    private TextBlock? _skillsHint;
    private UpdateInfo? _pendingUpdate;
    private List<EnvironmentCheckResult> _environmentResults = new();
    private Button? _repairAllBtn;

    public ModelSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => CenterOnScreen();
        LoadFromController();
        BuildProviderList();
        BuildPresets();

        _skillsHint = this.FindControl<TextBlock>("SkillsHint");
        BuildSkillsList();
        WireCollaborationAndUpdateSettings();

        var currentVersionText = this.FindControl<TextBlock>("CurrentVersionText");
        if (currentVersionText is not null)
        {
            currentVersionText.Text = I18n.T("当前版本：{0}", AutoUpdater.CurrentVersion.ToString());
        }
        var checkUpdateBtn = this.FindControl<Button>("CheckUpdateBtn");
        if (checkUpdateBtn is not null) checkUpdateBtn.Click += async (_, _) => await CheckUpdateAsync();
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (upgradeBtn is not null) upgradeBtn.Click += async (_, _) => await UpgradeAsync();
        var downloadLinkBtn = this.FindControl<Button>("UpdateDownloadLinkBtn");
        if (downloadLinkBtn is not null) downloadLinkBtn.Click += (_, _) => OpenDownloadLinkInBrowser();
        var updateUrlBox = this.FindControl<TextBox>("UpdateServiceUrlBox");
        if (updateUrlBox is not null) updateUrlBox.Text = LoadUpdateServiceUrl();
        var saveUpdateUrlBtn = this.FindControl<Button>("SaveUpdateUrlBtn");
        if (saveUpdateUrlBtn is not null) saveUpdateUrlBtn.Click += (_, _) => SaveUpdateServiceUrl();

        ApplyWindowLanguage();
        I18n.LanguageChanged += ApplyWindowLanguage;
        this.FindControl<ComboBox>("UiLanguageBox")!.SelectionChanged += (_, _) =>
        {
            var uiLang = (this.FindControl<ComboBox>("UiLanguageBox")!.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            if (uiLang != AssistantIdentity.UiLanguage)
            {
                AssistantIdentity.Apply(AssistantIdentity.Current, AssistantIdentity.BubbleIcon,
                    AssistantIdentity.Language, AssistantIdentity.CustomPrompt, uiLang);
                // ApplyWindowLanguage 由 LanguageChanged 事件触发
            }
        };
        this.FindControl<Button>("AddBtn")!.Click += (_, _) => AddProvider();
        BuildIconPicker();
        LoadPersonal();
        this.FindControl<Button>("SavePersonalBtn")!.Click += async (_, _) => await SavePersonalAsync();
        this.FindControl<Button>("ResetPosBtn")!.Click += (_, _) => ResetBubblePosition();

        // 主工作空间：回显当前生效目录；保存/恢复写覆盖配置（重启应用生效）
        var wsBox = this.FindControl<TextBox>("WorkspaceBox");
        var wsSave = this.FindControl<Button>("WorkspaceSaveBtn");
        var wsReset = this.FindControl<Button>("WorkspaceResetBtn");
        var wsPick = this.FindControl<Button>("WorkspacePickBtn");
        if (wsBox is not null && wsSave is not null && wsReset is not null && wsPick is not null)
        {
            wsBox.Text = UiPrefs.WorkspaceDir();
            wsPick.Click += async (_, _) =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = I18n.T("选择主工作空间目录"),
                    AllowMultiple = false,
                });
                if (folders.Count > 0)
                {
                    wsBox.Text = folders[0].Path.LocalPath;
                }
            };
            wsSave.Click += (_, _) =>
            {
                var hint = this.FindControl<TextBlock>("WorkspaceHint");
                var path = (wsBox.Text ?? "").Trim();
                if (path.Length == 0)
                {
                    if (hint is not null) hint.Text = I18n.T("请输入或选择工作空间目录");
                    return;
                }
                var err = WorkspacePrefs.SaveOverride(path);
                if (hint is not null)
                {
                    hint.Text = err is null
                        ? I18n.T("✔ 已保存，重启应用后生效")
                        : I18n.T("保存失败：{0}", err);
                }
            };
            wsReset.Click += (_, _) =>
            {
                var err = WorkspacePrefs.SaveOverride(null);
                var hint = this.FindControl<TextBlock>("WorkspaceHint");
                wsBox.Text = UiPrefs.WorkspaceDir();
                if (hint is not null)
                {
                    hint.Text = err is null
                        ? I18n.T("已恢复默认工作空间，重启应用后生效")
                        : I18n.T("保存失败：{0}", err);
                }
            };
        }

        // 开机自启动：状态回显读系统侧实际值（注册表/plist），切换即生效（不经过"保存"按钮）
        var autoStartSwitch = this.FindControl<ToggleSwitch>("AutoStartSwitch");
        if (autoStartSwitch is not null)
        {
            autoStartSwitch.IsEnabled = AutoStart.Supported;
            autoStartSwitch.IsChecked = AutoStart.IsEnabled();   // 先赋初值再订阅，避免回显触发保存
            autoStartSwitch.IsCheckedChanged += (_, _) =>
            {
                var on = autoStartSwitch.IsChecked == true;
                var err = AutoStart.SetEnabled(on);
                var hint = this.FindControl<TextBlock>("AutoStartHint");
                if (hint is not null)
                {
                    hint.Text = err is null
                        ? I18n.T(on ? "✔ 已开启：下次登录自动启动" : "已关闭开机自启动")
                        : I18n.T("设置失败：{0}", err);
                }
            };
        }

        // 只响应「选中」态：互斥切换时旧项的取消事件不参与页面决定
        NavGeneral.IsCheckedChanged += (_, _) =>
        {
            if (NavGeneral.IsChecked == true) ShowPage(PageGeneral);
        };
        NavAppearance.IsCheckedChanged += (_, _) =>
        {
            if (NavAppearance.IsChecked == true) ShowPage(PageAppearance);
        };
        NavModels.IsCheckedChanged += (_, _) =>
        {
            if (NavModels.IsChecked == true) ShowPage(PageModels);
        };
        NavSkills.IsCheckedChanged += (_, _) =>
        {
            if (NavSkills.IsChecked == true) ShowPage(PageSkills);
        };
        NavEnvironment.IsCheckedChanged += (_, _) =>
        {
            if (NavEnvironment.IsChecked == true) ShowPage(PageEnvironment);
        };
        ShowPage(PageGeneral);   // 与 NavGeneral 默认选中一致
        this.FindControl<Button>("SaveApplyBtn")!.Click += async (_, _) => await SaveAndApplyAsync();
        this.FindControl<Button>("CloseBtn")!.Click += (_, _) => Close();
        this.FindControl<Button>("DeleteBtn")!.Click += (_, _) => DeleteSelected();
        ProviderList.SelectionChanged += (_, _) =>
        {
            _selectedIndex = ProviderList.SelectedIndex;
            LoadSelectedIntoForm();
        };
        foreach (var box in new[] { NameBox, BaseUrlBox, ApiKeyBox, ModelsBox })
        {
            box.TextChanged += (_, _) => _dirty = true;
        }

        // 环境检测页
        var checkEnvBtn = this.FindControl<Button>("CheckEnvironmentBtn");
        _repairAllBtn = this.FindControl<Button>("RepairAllBtn");
        if (_repairAllBtn is not null) _repairAllBtn.IsEnabled = false;
        if (checkEnvBtn is not null) checkEnvBtn.Click += async (_, _) => await CheckEnvironmentAsync();
        if (_repairAllBtn is not null) _repairAllBtn.Click += async (_, _) => await RepairAllAsync();

        Closed += (_, _) =>
        {
            if (_dirty)
            {
                Hint(I18n.T("有未保存的修改已丢弃（可重新打开编辑）。"));
            }
        };
    }

    // ───────────────────────── 个性化 ─────────────────────────
    private string _selectedIcon = "Icon.Sparkle";
    private string _uiLanguage = "auto";
    private static readonly string[] IconChoices =
        ["Icon.Sparkle", "Icon.Settings", "Icon.Shield", "Icon.Question", "Icon.Team", "Icon.Compose", "Icon.Swap", "Icon.Send"];

    private void BuildIconPicker()
    {
        IconPicker.Children.Clear();   // 重建前清空，避免点击后重复追加
        foreach (var key in IconChoices)
        {
            var k = key;
            var btn = new Button
            {
                Classes = { "presetBtn" },
                Padding = new Avalonia.Thickness(8, 6),
                Content = new PathIcon
                {
                    Width = 16, Height = 16,
                    Data = this.TryFindResource(k, out var geo) && geo is StreamGeometry sg ? sg : null,
                    Foreground = new SolidColorBrush(k == _selectedIcon ? Color.Parse("#2f7bff") : Color.Parse("#c8c8c8")),
                },
            };
            btn.Click += (_, _) =>
            {
                _selectedIcon = k;
                BuildIconPicker();   // 重建以刷新选中态
                // 即点即生效：保存偏好并同步气泡图形
                AssistantIdentity.Apply(AssistantIdentity.Current, k,
                    AssistantIdentity.Language, AssistantIdentity.CustomPrompt, AssistantIdentity.UiLanguage);
                if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow is BubbleWindow bubble)
                {
                    bubble.ApplyIdentityUi();
                }
            };
            btn.Tag = k;
            IconPicker.Children.Add(btn);
        }
    }

    private string _selectedHelperIcon = "🤖";
    private bool _helperIconUiBuilt;
    private Flyout? _helperIconFlyout;

    /// <summary>构建 Helper 图标选择弹层（设置页一次性初始化）。84 个预设图标，带滚动条。</summary>
    private void BuildHelperIconPicker()
    {
        var button = this.FindControl<Button>("HelperIconPickerButton")!;
        var wrap = new WrapPanel { MaxWidth = 268 };
        string[] choices =
        [
            // 人物与角色
            "🤖", "🧑‍🚀", "👩‍💻", "🧑‍💻", "🧑‍💼", "🛡️", "👨‍🎤", "🕵️", "🧙", "🦸", "🧚", "👻",
            "🧛", "🧜", "🧝", "🧞", "🤴", "👸", "🥷", "🧑‍🎨", "🧑‍🔬", "🧑‍⚕️", "🧑‍🏫", "👮",
            // 动物
            "🦊", "🐱", "🐶", "🐼", "🦉", "🐙", "🦁", "🐯", "🐸", "🐵", "🐳", "🦋",
            "🐝", "🦔", "🐧", "🦅", "🦇", "🐺", "🐗", "🦌", "🐨", "🐹", "🐰", "🐭",
            // 奇幻与器物
            "🦄", "🚀", "⭐", "💡", "🔧", "🎭", "👑", "🌈", "🎯", "⚡", "🌊", "🍀",
            "🔮", "💎", "🔥", "🛰️", "📡", "🔭", "🧲", "🧩", "♟️", "🥇", "🏆", "🎖️",
            // 自然与生活
            "🌸", "🌻", "🌙", "☕", "🍵", "🍎", "🎧", "🎨", "🏔️", "🍀", "🎈", "🎁",
            "🌴", "🌵", "🍁", "🍉", "🍇", "🥑", "🍩", "🧊", "🛸", "🧸", "🪐", "❄️",
            // 扩展与符号
            "🧠", "🫶", "👍", "🙌", "💫", "✨", "☸️", "☯️", "⚛️", "🧿", "🔴", "🟢",
            "🔵", "🟣", "🟡", "⭕", "❤️", "💙", "💚", "🧡", "💜", "🖤", "🤍", "🩵"
        ];
        foreach (var icon in choices)
        {
            var choice = icon;
            var btn = new Button
            {
                Content = choice,
                Width = 44,
                Height = 44,
                FontSize = 20,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#333333")),
                Foreground = Brushes.White,
            };
            btn.Click += (_, _) =>
            {
                _selectedHelperIcon = choice;
                button.Content = choice;
                _helperIconFlyout?.Hide();
                _ = ApplyHelperIconAsync(choice);
            };
            wrap.Children.Add(btn);
        }
        // 弹层内容套滚动条：图标多时不撑破屏幕，滚轮直接滚动。
        var scroll = new ScrollViewer
        {
            Content = wrap,
            MaxHeight = 390,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _helperIconFlyout = new Flyout { Content = scroll };
        button.Flyout = _helperIconFlyout;   // 附加后点击按钮自动弹出
    }

    private async Task ApplyHelperIconAsync(string icon)
    {
        var controller = App.A2A?.Controller;
        if (controller is null) return;
        try
        {
            await controller.ApplyIconAsync(icon);
            Hint(I18n.T("Helper 图标已更新：{0}（对端将在资料刷新后显示）", icon));
        }
        catch (Exception ex)
        {
            Hint(I18n.T("Helper 图标已保存；热更新失败（未启动或对端离线）：{0}", ex.Message));
        }
    }

    private void LoadPersonal()
    {
        var nameBox = this.FindControl<TextBox>("PersonalNameBox")!;
        var promptBox = this.FindControl<TextBox>("CustomPromptBox")!;
        var langBox = this.FindControl<ComboBox>("LanguageBox")!;
        var uiLangBox = this.FindControl<ComboBox>("UiLanguageBox")!;
        nameBox.Text = AssistantIdentity.Current == "Brook" ? "" : AssistantIdentity.Current;
        promptBox.Text = AssistantIdentity.CustomPrompt;
        langBox.SelectedIndex = AssistantIdentity.Language == "en" ? 1 : 0;
        uiLangBox.SelectedIndex = AssistantIdentity.UiLanguage switch
        {
            "zh" => 1,
            "en" => 2,
            _ => 0,
        };
        _selectedIcon = AssistantIdentity.BubbleIcon;
        BuildIconPicker();

        // Helper 图标（A2A 对外展示）：读取当前配置并构建选择弹层（仅初始化一次）。
        _selectedHelperIcon = App.A2A?.Controller.Config.Icon ?? "🤖";
        var helperIconBtn = this.FindControl<Button>("HelperIconPickerButton")!;
        helperIconBtn.Content = _selectedHelperIcon;
        if (!_helperIconUiBuilt)
        {
            _helperIconUiBuilt = true;
            BuildHelperIconPicker();
        }
    }

    private async Task SavePersonalAsync()
    {
        var name = this.FindControl<TextBox>("PersonalNameBox")!.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            name = "Brook";   // 留空恢复默认名
        }
        var lang = (this.FindControl<ComboBox>("LanguageBox")!.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh";
        var uiLang = (this.FindControl<ComboBox>("UiLanguageBox")!.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        _uiLanguage = uiLang;
        var prompt = this.FindControl<TextBox>("CustomPromptBox")!.Text?.Trim() ?? "";
        AssistantIdentity.Apply(name, _selectedIcon, lang, prompt, _uiLanguage);

        // 同步更新 A2A 对外显示名，使对方 Helper 的 Peers 列表也能看到新名称
        try
        {
            var a2aController = App.A2A?.Controller;
            if (a2aController is not null)
            {
                await a2aController.ApplyDisplayNameAsync(name);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[settings] 同步 A2A 显示名失败: {ex.Message}");
        }

        // UI 立即刷新
        if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is BubbleWindow bubble)
        {
            bubble.ApplyIdentityUi();
        }
        App.Controller.ConversationWindow?.ApplyIdentityUi();

        try
        {
            await App.Controller.ApplyAssistantSettingsAsync(name, lang, prompt);
            Hint(I18n.T("个性化已保存并生效（模型指令已热更新，对话上下文保留）。"));
        }
        catch (Exception ex)
        {
            Hint(I18n.T("个性化已应用到界面；模型指令应用失败：{0}", ex.Message));
        }
    }

    private void ShowPage(Control page)
    {
        PageGeneral.IsVisible = page == PageGeneral;
        PageAppearance.IsVisible = page == PageAppearance;
        PageModels.IsVisible = page == PageModels;
        PageSkills.IsVisible = page == PageSkills;
        PageEnvironment.IsVisible = page == PageEnvironment;
        if (page == PageEnvironment && _environmentResults.Count == 0)
        {
            _ = CheckEnvironmentAsync();
        }
    }

    private void CenterOnScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }
        var scale = screen.Scaling;
        var width = ClientSize.Width * scale;
        var height = ClientSize.Height * scale;
        var x = screen.WorkingArea.X + (screen.WorkingArea.Width - width) / 2;
        var y = screen.WorkingArea.Y + (screen.WorkingArea.Height - height) / 2;
        Position = new PixelPoint((int)x, (int)y);
    }

    /// <summary>删除位置偏好并通知气泡窗口回到默认位置。</summary>
    private void ResetBubblePosition()
    {
        UiPrefs.Delete("bubble-pos.json");
        if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is BubbleWindow bubble)
        {
            bubble.ResetPositionToDefault();
        }
        Hint(I18n.T("气泡位置已重置。"));
    }

    // ───────────────────────── Skill 管理 ─────────────────────────
    private void BuildSkillsList()
    {
        if (SkillsListPanel is null) return;
        SkillsListPanel.Children.Clear();
        var skills = App.Controller.GetSkills();
        if (skills.Count == 0)
        {
            SkillsListPanel.Children.Add(new TextBlock
            {
                Text = I18n.T("暂无已安装的技能。"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            });
            return;
        }
        foreach (var skill in skills)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2b2b2b")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10),
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var textPanel = new StackPanel { Spacing = 4 };
            textPanel.Children.Add(new TextBlock
            {
                Text = skill.Name,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
            });
            if (!string.IsNullOrWhiteSpace(skill.Description))
            {
                textPanel.Children.Add(new TextBlock
                {
                    Text = skill.Description,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
                });
            }
            Grid.SetColumn(textPanel, 0);
            var delBtn = new Button
            {
                Content = I18n.T("删除技能"),
                Classes = { "dangerBtn" },
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var name = skill.Name;
            delBtn.Click += (_, _) => DeleteSkill(name);
            Grid.SetColumn(delBtn, 1);
            grid.Children.Add(textPanel);
            grid.Children.Add(delBtn);
            card.Child = grid;
            SkillsListPanel.Children.Add(card);
        }
    }

    private void DeleteSkill(string name)
    {
        try
        {
            if (App.Controller.DeleteSkill(name))
            {
                if (_skillsHint is not null) _skillsHint.Text = I18n.T("已删除技能「{0}」", name);
                BuildSkillsList();
            }
            else
            {
                if (_skillsHint is not null) _skillsHint.Text = I18n.T("删除失败：{0}", I18n.T("未找到该技能"));
            }
        }
        catch (Exception ex)
        {
            if (_skillsHint is not null) _skillsHint.Text = I18n.T("删除失败：{0}", ex.Message);
        }
    }

    private string BuildVersionUrl(string baseUrl)
    {
        var url = (baseUrl ?? "http://helper.agentbrook.com").Trim().TrimEnd('/');
        if (url.EndsWith("/api/update/version", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/api/update/version".Length].TrimEnd('/');
        }
        var (os, arch) = AutoUpdater.CurrentPlatform;
        return $"{url}/api/update/version?os={os}&arch={arch}";
    }

    private string LoadUpdateServiceUrl()
    {
        var saved = UiPrefs.Load<string>("update-service-url.json");
        var url = string.IsNullOrWhiteSpace(saved) ? "http://helper.agentbrook.com" : saved.Trim().TrimEnd('/');
        return url;
    }

    private void SaveUpdateServiceUrl()
    {
        var box = this.FindControl<TextBox>("UpdateServiceUrlBox");
        var url = box?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "http://helper.agentbrook.com";
        }
        UiPrefs.Save("update-service-url.json", url.TrimEnd('/'));
        Hint(I18n.T("地址已保存"));
    }

    /// <summary>协作（A2A/群组）与更新偏好：加载配置并即时保存（改动即写配置文件，无需保存按钮）。</summary>
    private void WireCollaborationAndUpdateSettings()
    {
        var a2a = App.A2A?.Controller;
        var enabled = this.FindControl<ToggleSwitch>("A2aEnabledSwitch");
        var peerAuto = this.FindControl<ToggleSwitch>("PeerAutoProcessSwitch");
        var autoTalk = this.FindControl<ToggleSwitch>("GroupAutoTalkDefaultSwitch");
        var compress = this.FindControl<ToggleSwitch>("ContextCompressSwitch");
        var rounds = this.FindControl<TextBox>("CompressRoundsBox");
        var notify = this.FindControl<ComboBox>("GroupNotifyBox");
        var hint = this.FindControl<TextBlock>("CollabHint");
        var daily = this.FindControl<ToggleSwitch>("DailyUpdateCheckSwitch");
        var autoDownload = this.FindControl<ToggleSwitch>("AutoDownloadUpdateSwitch");
        if (a2a is null || enabled is null || peerAuto is null || autoTalk is null
            || compress is null || rounds is null || notify is null)
        {
            return;
        }

        enabled.IsChecked = a2a.Config.Enabled;
        peerAuto.IsChecked = a2a.Config.AutoProcessPeerMessages;
        autoTalk.IsChecked = a2a.Config.GroupAutoTalkDefault;
        compress.IsChecked = a2a.Config.GroupContextCompress;
        rounds.Text = Math.Max(3, a2a.Config.GroupCompressRounds).ToString();
        notify.SelectedIndex = a2a.Config.GroupNotifyMode switch { "all" => 2, "off" => 0, _ => 1 };

        var updatePrefs = UiPrefs.Load<UpdatePrefs>("update-prefs.json") ?? new UpdatePrefs();
        if (daily is not null)
        {
            daily.IsChecked = updatePrefs.DailyCheck;
            daily.IsCheckedChanged += (_, _) =>
                UiPrefs.Save("update-prefs.json", new UpdatePrefs(daily.IsChecked == true, autoDownload?.IsChecked != false));
        }
        if (autoDownload is not null)
        {
            autoDownload.IsChecked = updatePrefs.AutoDownload;
            autoDownload.IsCheckedChanged += (_, _) =>
                UiPrefs.Save("update-prefs.json", new UpdatePrefs(daily?.IsChecked != false, autoDownload.IsChecked == true));
        }

        ShowCollabHint(hint, a2a.Config.Enabled ? I18n.T("A2A 协作已启用") : I18n.T("A2A 协作已关闭"));

        enabled.IsCheckedChanged += async (_, _) =>
        {
            var on = enabled.IsChecked == true;
            a2a.Config.Enabled = on;
            a2a.SaveConfig();
            ShowCollabHint(hint, on ? I18n.T("已启用，正在启动 A2A…") : I18n.T("已关闭，正在断开 A2A…"));
            try
            {
                if (on)
                {
                    await a2a.StartAsync();
                }
                else
                {
                    await a2a.StopAsync();
                }
                ShowCollabHint(hint, on ? I18n.T("已启用，A2A 在线") : I18n.T("已关闭，A2A 已断开（重新勾选即可恢复）"));
            }
            catch (Exception ex)
            {
                ShowCollabHint(hint, I18n.T("操作失败：{0}", ex.Message));
            }
        };
        peerAuto.IsCheckedChanged += (_, _) =>
        {
            a2a.Config.AutoProcessPeerMessages = peerAuto.IsChecked == true;
            a2a.SaveConfig();
        };
        autoTalk.IsCheckedChanged += (_, _) =>
        {
            a2a.Config.GroupAutoTalkDefault = autoTalk.IsChecked == true;
            a2a.SaveConfig();
        };
        compress.IsCheckedChanged += (_, _) =>
        {
            a2a.Config.GroupContextCompress = compress.IsChecked == true;
            a2a.SaveConfig();
        };
        rounds.LostFocus += (_, _) =>
        {
            if (int.TryParse(rounds.Text, out var n))
            {
                a2a.Config.GroupCompressRounds = Math.Clamp(n, 3, 99);
            }
            rounds.Text = a2a.Config.GroupCompressRounds.ToString();
            a2a.SaveConfig();
        };
        notify.SelectionChanged += (_, _) =>
        {
            a2a.Config.GroupNotifyMode = notify.SelectedIndex switch
            {
                0 => "off",
                2 => "all",
                _ => "mention"
            };
            a2a.SaveConfig();
        };
    }

    private static void ShowCollabHint(TextBlock? hint, string message)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (hint is not null)
            {
                hint.Text = message;
            }
        });

    private async Task CheckUpdateAsync()
    {
        var updateHint = this.FindControl<TextBlock>("UpdateHint");
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (updateHint is not null) updateHint.Text = I18n.T("正在检查更新…");
        if (upgradeBtn is not null) upgradeBtn.IsVisible = false;
        ShowDownloadLink(null);
        ShowUpdateDetails(null);
        _pendingUpdate = null;

        var urlBox = this.FindControl<TextBox>("UpdateServiceUrlBox");
        var baseUrl = urlBox?.Text?.Trim();
        var versionUrl = BuildVersionUrl(baseUrl!);

        var info = await AutoUpdater.CheckAsync(versionUrl);
        if (info is null)
        {
            if (updateHint is not null) updateHint.Text = I18n.T("无法连接更新服务，请确认服务已启动。");
            return;
        }

        if (!AutoUpdater.IsNewer(info.Version))
        {
            if (updateHint is not null) updateHint.Text = I18n.T("已是最新版本。");
            return;
        }

        _pendingUpdate = info;
        if (updateHint is not null) updateHint.Text = I18n.T("发现新版本：{0}", info.Version);
        if (upgradeBtn is not null) upgradeBtn.IsVisible = true;
        ShowUpdateDetails(info);
    }

    private void ShowUpdateDetails(UpdateInfo? info)
    {
        var detailsPanel = this.FindControl<Border>("UpdateDetailsPanel");
        if (detailsPanel is not null) detailsPanel.IsVisible = info is not null;
        if (info is null) return;

        var platformText = this.FindControl<TextBlock>("UpdatePlatformText");
        if (platformText is not null) platformText.Text = I18n.T("目标平台：{0}", info.Platform);

        var fileInfoText = this.FindControl<TextBlock>("UpdateFileInfoText");
        if (fileInfoText is not null)
        {
            fileInfoText.Text = I18n.T("文件：{0}，大小：{1:F2} MB", info.FileName, info.Size / (1024.0 * 1024.0));
        }

        var notesText = this.FindControl<TextBlock>("UpdateReleaseNotesText");
        if (notesText is not null)
        {
            notesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? I18n.T("（发布方未提供本次更新的说明）")
                : info.ReleaseNotes;
        }
    }

    /// <summary>外部入口（气泡"立即更新"提醒）：定位到版本更新区并自动开始更新流程。</summary>
    internal async Task StartUpdateFlowAsync(UpdateInfo info, bool autoStart = true)
    {
        this.FindControl<TextBlock>("LabelUpdate")?.BringIntoView();   // 定位到版本更新区
        _pendingUpdate = info;
        var hint = this.FindControl<TextBlock>("UpdateHint");
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (upgradeBtn is not null)
        {
            upgradeBtn.IsVisible = true;
        }
        ShowUpdateDetails(info);
        ShowDownloadLink(info.DownloadUrl);
        if (!autoStart)
        {
            if (hint is not null)
            {
                hint.Text = I18n.T("发现新版本：{0}，点击「立即升级」开始更新。", info.Version);
            }
            return;
        }
        if (hint is not null)
        {
            hint.Text = I18n.T("发现新版本：{0}，正在自动更新…", info.Version);
        }
        if (upgradeBtn is not null)
        {
            upgradeBtn.IsVisible = false;
        }
        await UpgradeAsync();
    }

    private async Task UpgradeAsync()
    {
        if (_pendingUpdate is null) return;
        var updateHint = this.FindControl<TextBlock>("UpdateHint");
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        var progressPanel = this.FindControl<StackPanel>("UpdateProgressPanel");
        var progressBar = this.FindControl<ProgressBar>("UpdateProgressBar");
        var progressText = this.FindControl<TextBlock>("UpdateProgressText");

        if (updateHint is not null) updateHint.Text = I18n.T("正在下载更新…");
        if (upgradeBtn is not null) upgradeBtn.IsVisible = false;
        if (progressPanel is not null) progressPanel.IsVisible = true;
        if (progressBar is not null) progressBar.Value = 0;
        if (progressText is not null) progressText.Text = "0%";

        var progress = new Progress<double>(p =>
        {
            var percent = p * 100;
            if (progressBar is not null) progressBar.Value = percent;
            if (progressText is not null) progressText.Text = $"{percent:F0}%";
        });

        var error = await AutoUpdater.UpgradeAsync(_pendingUpdate, progress);
        if (!string.IsNullOrEmpty(error))
        {
            if (updateHint is not null) updateHint.Text = I18n.T("升级失败：{0}", error);
            if (progressPanel is not null) progressPanel.IsVisible = false;
            ShowDownloadLink(_pendingUpdate.DownloadUrl);
            return;
        }

        if (updateHint is not null) updateHint.Text = I18n.T("下载完成，即将关闭并升级。");
        if (progressText is not null) progressText.Text = "100%";
        await Task.Delay(800);
        Environment.Exit(0);
    }

    private void ShowDownloadLink(string? url)
    {
        var downloadLinkBtn = this.FindControl<Button>("UpdateDownloadLinkBtn");
        if (downloadLinkBtn is null) return;
        downloadLinkBtn.IsVisible = !string.IsNullOrWhiteSpace(url);
        ToolTip.SetTip(downloadLinkBtn, url ?? "");
    }

    private void OpenDownloadLinkInBrowser()
    {
        if (_pendingUpdate is null) return;
        var url = _pendingUpdate.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Hint(I18n.T("无法打开浏览器：{0}", ex.Message));
        }
    }

    /// <summary>按当前语言设置窗口标题与导航文案。</summary>
    private void ApplyWindowLanguage()
    {
        try
        {
        Title = I18n.T("设置");
        var ngb = this.FindControl<TextBlock>("NavGroupBasic");
        if (ngb is not null) ngb.Text = I18n.T("基础设置");
        var ngm = this.FindControl<TextBlock>("NavGroupModels");
        if (ngm is not null) ngm.Text = I18n.T("模型");
        var nga = this.FindControl<TextBlock>("NavGroupAgent");
        if (nga is not null) nga.Text = I18n.T("智能体");
        var nst = this.FindControl<TextBlock>("NavSkillsText");
        if (nst is not null) nst.Text = I18n.T("Skill管理");
        var pt = this.FindControl<TextBlock>("PersonalTitle");
        if (pt is not null) pt.Text = I18n.T("个性化");
        var ph = this.FindControl<TextBlock>("PresetHeader");
        if (ph is not null) ph.Text = I18n.T("快速添加（点选模板）");
        var ng = this.FindControl<TextBlock>("NavGeneralText");
        if (ng is not null) ng.Text = I18n.T("常规");
        var na = this.FindControl<TextBlock>("NavAppearanceText");
        if (na is not null) na.Text = I18n.T("外观");
        var nm = this.FindControl<TextBlock>("NavModelsText");
        if (nm is not null) nm.Text = I18n.T("模型设置");
        var ngs = this.FindControl<TextBlock>("NavGroupSystem");
        if (ngs is not null) ngs.Text = I18n.T("系统");
        var nenv = this.FindControl<TextBlock>("NavEnvironmentText");
        if (nenv is not null) nenv.Text = I18n.T("运行环境检测");
        var saveBtn = this.FindControl<Button>("SavePersonalBtn");
        if (saveBtn is not null) saveBtn.Content = I18n.T("保存常规设置");
        var promptBox2 = this.FindControl<TextBox>("CustomPromptBox");
        if (promptBox2 is not null) promptBox2.Watermark = I18n.T("例如：回答尽量简短；优先用表格展示数据…");
        // 提示音偏好：开关/音色切换即保存并试听（不经过"保存"按钮）
        var notifySwitch = this.FindControl<ToggleSwitch>("NotifySoundSwitch");
        var notifyBox = this.FindControl<ComboBox>("NotifySoundBox");
        if (notifySwitch is not null && notifyBox is not null)
        {
            notifySwitch.IsChecked = AssistantIdentity.NotifySoundOn;
            notifyBox.SelectedIndex = AssistantIdentity.NotifySoundId switch
            {
                "glass" => 1,
                "hero" => 2,
                _ => 0,
            };
            notifyBox.IsEnabled = AssistantIdentity.NotifySoundOn;
            notifySwitch.IsCheckedChanged += (_, _) =>
            {
                var on = notifySwitch.IsChecked == true;
                AssistantIdentity.SaveNotifyPrefs(on, AssistantIdentity.NotifySoundId);
                notifyBox.IsEnabled = on;
                if (on)
                {
                    NotifySound.Play(AssistantIdentity.NotifySoundId);   // 开启时试听
                }
            };
            notifyBox.SelectionChanged += (_, _) =>
            {
                var id = (notifyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ding";
                AssistantIdentity.SaveNotifyPrefs(AssistantIdentity.NotifySoundOn, id);
                NotifySound.Play(id);   // 换音色即试听
            };
        }

        var saveApply = this.FindControl<Button>("SaveApplyBtn");
        if (saveApply is not null) saveApply.Content = I18n.T("保存并应用");
        var closeBtn = this.FindControl<Button>("CloseBtn");
        if (closeBtn is not null) closeBtn.Content = I18n.T("关闭");
        var addBtn = this.FindControl<Button>("AddBtn");
        if (addBtn is not null) addBtn.Content = I18n.T("＋ 添加供应商");
        var delBtn = this.FindControl<Button>("DeleteBtn");
        if (delBtn is not null) delBtn.Content = I18n.T("删除此供应商");
        // 页面元素（空安全，缺 x:Name 的跳过）
        void SetT(string name, string key)
        {
            var c = this.FindControl<TextBlock>(name);
            if (c is not null) c.Text = I18n.T(key);
        }
        SetT("GeneralTitle", "常规");
        SetT("GeneralDesc", "助手身份与交流偏好。");
        SetT("LabelAssistantName", "助手名字");
        SetT("LabelReplyLang", "交流语言（影响模型回复语言）");
        SetT("LabelCustomPrompt", "默认提示词（附加到系统指令，每轮生效）");
        SetT("LabelUiLang", "界面语言（UI 显示；切换后重启应用完全生效）");
        SetT("LabelAutoStart", "开机自启动");
        SetT("AutoStartDesc", "登录系统后自动启动，气泡常驻待命");
        SetT("LabelWorkspace", "主工作空间");
        SetT("WorkspaceDesc", "智能体的文件、脚本与记忆所在目录；修改保存后重启应用生效，助手设置随工作空间保存");
        var wsPickBtn = this.FindControl<Button>("WorkspacePickBtn");
        if (wsPickBtn is not null) wsPickBtn.Content = I18n.T("选择目录…");
        var wsSaveBtn = this.FindControl<Button>("WorkspaceSaveBtn");
        if (wsSaveBtn is not null) wsSaveBtn.Content = I18n.T("保存工作空间");
        var wsResetBtn = this.FindControl<Button>("WorkspaceResetBtn");
        if (wsResetBtn is not null) wsResetBtn.Content = I18n.T("恢复默认");
        SetT("LabelNotifySound", "提示音");
        SetT("NotifySoundDesc", "任务完成且对话窗不在前台时播放");
        var nsBox = this.FindControl<ComboBox>("NotifySoundBox");
        if (nsBox is not null)
        {
            var items = nsBox.Items.OfType<ComboBoxItem>().ToList();
            var names = new[] { I18n.T("叮"), I18n.T("玻璃"), I18n.T("和弦") };
            for (var k = 0; k < items.Count && k < names.Length; k++)
            {
                items[k].Content = names[k];
            }
        }
        SetT("AppearanceTitle", "外观");
        SetT("AppearanceDesc", "悬浮气泡的个性化显示。");
        SetT("LabelBubbleIcon", "气泡图标");
        SetT("LabelBubblePos", "气泡位置");
        var resetPosBtn = this.FindControl<Button>("ResetPosBtn");
        if (resetPosBtn is not null) resetPosBtn.Content = I18n.T("重置到默认位置（右上角）");
        SetT("ModelsTitle", "模型设置");
        SetT("ModelsDesc", "管理模型供应商，配置后即可在底部切换使用。");
        var checkUpdateBtn = this.FindControl<Button>("CheckUpdateBtn");
        if (checkUpdateBtn is not null) checkUpdateBtn.Content = I18n.T("检查更新");
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (upgradeBtn is not null) upgradeBtn.Content = I18n.T("立即升级");
        SetT("LabelUpdate", "版本更新");
        var currentVersionText = this.FindControl<TextBlock>("CurrentVersionText");
        if (currentVersionText is not null) currentVersionText.Text = I18n.T("当前版本：{0}", AutoUpdater.CurrentVersion.ToString());
        SetT("LabelUpdateServiceUrl", "更新服务地址");
        SetT("LabelCollab", "协作（A2A 局域网）");
        SetT("A2aEnabledDesc", "启用 A2A 局域网协作（关闭即断开发现/配对/群组，开启即时恢复）");
        SetT("PeerAutoProcessDesc", "自动处理其他 Helper 发来的点对点消息（关闭后仅存 Inbox 等人工处理）");
        SetT("GroupAutoTalkDesc", "群组自动交流（默认；可在 A2A 会话中心按群单独关闭）");
        SetT("ContextCompressDesc", "群上下文自动压缩，每");
        SetT("CompressRoundsSuffix", "轮一次并同步全群");
        SetT("LabelGroupNotify", "群消息提醒");
        SetT("GroupNotifyDesc", "群内有新消息时气泡提醒，点击打开 A2A 会话中心");
        SetT("GroupNotifyOff", "关闭");
        SetT("GroupNotifyMention", "仅 @我");
        SetT("GroupNotifyAll", "所有消息");
        SetT("DailyUpdateCheckDesc", "每日首次启动自动检查更新");
        SetT("AutoDownloadUpdateDesc", "提醒后自动下载并安装");
        // ComboBox 下拉项不是 TextBlock，SetT 覆盖不到，逐项设置 Content
        var notifyOff = this.FindControl<ComboBoxItem>("GroupNotifyOff");
        if (notifyOff is not null) notifyOff.Content = I18n.T("关闭");
        var notifyMention = this.FindControl<ComboBoxItem>("GroupNotifyMention");
        if (notifyMention is not null) notifyMention.Content = I18n.T("仅 @我");
        var notifyAll = this.FindControl<ComboBoxItem>("GroupNotifyAll");
        if (notifyAll is not null) notifyAll.Content = I18n.T("所有消息");
        var soundDing = this.FindControl<ComboBoxItem>("NotifySoundDing");
        if (soundDing is not null) soundDing.Content = I18n.T("叮");
        var soundGlass = this.FindControl<ComboBoxItem>("NotifySoundGlass");
        if (soundGlass is not null) soundGlass.Content = I18n.T("玻璃");
        var soundChord = this.FindControl<ComboBoxItem>("NotifySoundChord");
        if (soundChord is not null) soundChord.Content = I18n.T("和弦");
        var saveUrlBtn = this.FindControl<Button>("SaveUpdateUrlBtn");
        if (saveUrlBtn is not null) saveUrlBtn.Content = I18n.T("保存地址");
        var releaseNotesTitle = this.FindControl<TextBlock>("UpdateReleaseNotesTitle");
        if (releaseNotesTitle is not null) releaseNotesTitle.Text = I18n.T("更新说明");
        SetT("SkillsTitle", "Skill管理");
        SetT("SkillsDesc", "管理已安装的技能，删除后立即生效。");
        SetT("EnvironmentTitle", "环境检测");
        SetT("EnvironmentDesc", "检查系统环境是否满足 AgentBrook Helper 正常运行。");
        var checkEnvBtn = this.FindControl<Button>("CheckEnvironmentBtn");
        if (checkEnvBtn is not null) checkEnvBtn.Content = I18n.T("重新检测");
        var repairAllBtn = this.FindControl<Button>("RepairAllBtn");
        if (repairAllBtn is not null) repairAllBtn.Content = I18n.T("一键修复");
        SetT("LabelProviderName", "供应商名称");
        SetT("LabelApiEndpoint", "API 地址（OpenAI 兼容端点）");
        SetT("LabelApiKey", "API Key");
        SetT("LabelModelsCsv", "模型清单（逗号分隔，第一个为默认）");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[i18n] ApplyWindowLanguage failed: {ex}");
        }
    }

    /// <summary>测试入口：程序化选择界面语言（触发真实 SelectionChanged → 热切换链路）。</summary>
    public void DebugSelectUiLanguage(string lang)
    {
        UiLanguageBox.SelectedIndex = lang == "en" ? 2 : lang == "zh" ? 1 : 0;
    }

    private void LoadFromController()
    {
        _working = App.Controller.Providers.Select(p => new ProviderConfig
        {
            Name = p.Name,
            BaseUrl = p.BaseUrl,
            ApiKey = p.ApiKey,
            Models = [.. p.Models],
            ActiveModel = p.ActiveModel,
        }).ToList();
        _activeName = App.Controller.CurrentProvider;
    }

    private void BuildProviderList()
    {
        ProviderList.Items.Clear();
        foreach (var p in _working)
        {
            var dotColor = p.IsConfigured ? "#4cc38a" : "#e0a030";
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new Avalonia.CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse(dotColor)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var displayName = p.Name == "默认" && I18n.Lang == "en" ? "Default" : p.Name;
            panel.Children.Add(new TextBlock
            {
                Text = displayName + (p.Name == _activeName ? I18n.T("（使用中）") : ""),
                FontSize = 13.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            ProviderList.Items.Add(panel);
        }
        if (_selectedIndex >= _working.Count)
        {
            _selectedIndex = _working.Count - 1;
        }
        ProviderList.SelectedIndex = _selectedIndex;
        FormPanel.IsEnabled = _working.Count > 0;
    }

    private void BuildPresets()
    {
        foreach (var (name, baseUrl, models) in Presets)
        {
            var presetName = name;
            var b = new Button
            {
                Classes = { "presetBtn" },
                Content = name == "自定义" ? I18n.T("＋ 创建自定义供应商") : name,
            };
            b.Click += (_, _) => AddProvider(presetName, baseUrl, models);
            PresetPanel.Children.Add(b);
        }
    }

    private void AddProvider(string? presetName = null, string presetUrl = "", string presetModels = "")
    {
        var name = presetName ?? "";
        var n = 2;
        var baseName = string.IsNullOrEmpty(name) ? "新供应商" : name;
        name = baseName;
        while (_working.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {n++}";
        }
        _working.Add(new ProviderConfig
        {
            Name = name,
            BaseUrl = presetUrl,
            Models = presetModels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
        });
        _selectedIndex = _working.Count - 1;
        BuildProviderList();
        Hint(I18n.T("已添加「{0}」，填写 API Key 后点「保存并应用」。（模型管理功能不校验端点可用性，配置错误会在对话时报错）", name));
    }

    private void LoadSelectedIntoForm()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _working.Count)
        {
            FormPanel.IsEnabled = false;
            return;
        }
        FormPanel.IsEnabled = true;
        var p = _working[_selectedIndex];
        NameBox.Text = p.Name;
        BaseUrlBox.Text = p.BaseUrl;
        ApiKeyBox.Text = p.ApiKey;
        ModelsBox.Text = string.Join(", ", p.Models);
    }

    private void SyncFormIntoWorking()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _working.Count)
        {
            return;
        }
        var p = _working[_selectedIndex];
        p.Name = NameBox.Text?.Trim() ?? "";
        p.BaseUrl = BaseUrlBox.Text?.Trim() ?? "";
        p.ApiKey = ApiKeyBox.Text?.Trim() ?? "";
        p.Models = (ModelsBox.Text ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (p.ActiveModel is null || p.ActiveModel.Length == 0 || !p.Models.Contains(p.ActiveModel))
        {
            p.ActiveModel = p.Models.FirstOrDefault() ?? "";
        }
    }

    private async Task SaveAndApplyAsync()
    {
        SyncFormIntoWorking();
        // 名称去重
        var dup = _working.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            Hint(I18n.T("供应商名称「{0}」重复，请改名后再保存。", dup.Key));
            return;
        }
        if (_working.Any(p => p.Name.Length == 0))
        {
            Hint(I18n.T("存在未命名的供应商。"));
            return;
        }
        var active = _activeName;
        if (_working.All(p => p.Name != active))
        {
            active = _working.FirstOrDefault(p => p.IsConfigured)?.Name ?? _working.FirstOrDefault()?.Name ?? "";
        }
        try
        {
            await App.Controller.SaveProvidersAsync(_working, active);
            _activeName = App.Controller.CurrentProvider;
            _dirty = false;
            BuildProviderList();
            Hint(I18n.T("已保存并热应用：当前对话上下文已迁移到新的模型客户端。"));
        }
        catch (Exception ex)
        {
            Hint(I18n.T("保存失败：{0}", ex.Message));
        }
    }

    private void DeleteSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _working.Count)
        {
            return;
        }
        if (_working.Count == 1)
        {
            Hint(I18n.T("至少保留一个供应商（可改为编辑现有项）。"));
            return;
        }
        var removedName = _working[_selectedIndex].Name;
        _working.RemoveAt(_selectedIndex);
        if (_activeName == removedName)
        {
            _activeName = _working.FirstOrDefault(p => p.IsConfigured)?.Name ?? _working.FirstOrDefault()?.Name ?? "";
        }
        _selectedIndex = Math.Min(_selectedIndex, _working.Count - 1);
        _dirty = true;
        BuildProviderList();
        Hint(I18n.T("已删除「{0}」（点「保存并应用」生效）。", removedName));
    }

    private void Hint(string text)
    {
        PersonalHint.Text = text;
    }

    // ───────────────────────── 环境检测 ─────────────────────────
    private async Task CheckEnvironmentAsync()
    {
        var list = this.FindControl<StackPanel>("EnvironmentListPanel");
        var summary = this.FindControl<TextBlock>("EnvironmentSummary");
        var hint = this.FindControl<TextBlock>("EnvironmentHint");
        var checkBtn = this.FindControl<Button>("CheckEnvironmentBtn");
        if (list is null) return;

        if (checkBtn is not null) checkBtn.IsEnabled = false;
        list.Children.Clear();
        if (summary is not null) summary.Text = I18n.T("正在检测…");
        if (hint is not null) hint.Text = "";

        var agent = App.Controller.Ready ? App.Controller.Agent : null;
        _environmentResults = await EnvironmentChecker.CheckAllAsync(agent);
        Dispatcher.UIThread.Post(() => BuildEnvironmentList(_environmentResults));

        var errorCount = _environmentResults.Count(r => r.Status == CheckStatus.Error);
        var warningCount = _environmentResults.Count(r => r.Status == CheckStatus.Warning);
        if (summary is not null)
        {
            summary.Text = errorCount == 0 && warningCount == 0
                ? I18n.T("✔ 全部通过")
                : I18n.T("检测完成：{0} 项异常，{1} 项警告", errorCount, warningCount);
        }
        if (_repairAllBtn is not null) _repairAllBtn.IsEnabled = errorCount > 0 || warningCount > 0;
        if (checkBtn is not null) checkBtn.IsEnabled = true;
    }

    private async Task RepairAllAsync()
    {
        if (_environmentResults.Count == 0) return;
        var list = this.FindControl<StackPanel>("EnvironmentListPanel");
        var summary = this.FindControl<TextBlock>("EnvironmentSummary");
        var hint = this.FindControl<TextBlock>("EnvironmentHint");
        if (_repairAllBtn is not null) _repairAllBtn.IsEnabled = false;
        if (hint is not null) hint.Text = I18n.T("正在修复…");

        var repaired = new List<EnvironmentCheckResult>();
        foreach (var item in _environmentResults.Where(r => r.CanRepair))
        {
            var result = await EnvironmentChecker.RepairAsync(item);
            repaired.Add(result);
        }

        // 更新结果列表
        for (var i = 0; i < _environmentResults.Count; i++)
        {
            var existing = _environmentResults[i];
            var update = repaired.FirstOrDefault(r => r.Id == existing.Id);
            if (update is not null) _environmentResults[i] = update;
        }

        Dispatcher.UIThread.Post(() => BuildEnvironmentList(_environmentResults));

        var stillError = _environmentResults.Count(r => r.Status == CheckStatus.Error);
        var stillWarning = _environmentResults.Count(r => r.Status == CheckStatus.Warning);
        if (summary is not null)
        {
            summary.Text = stillError == 0 && stillWarning == 0
                ? I18n.T("✔ 修复后全部通过")
                : I18n.T("修复后：{0} 项异常，{1} 项警告", stillError, stillWarning);
        }
        if (hint is not null)
        {
            if (repaired.Count > 0)
            {
                hint.Text = I18n.T("已尝试修复 {0} 项，若仍异常请按提示手动处理。", repaired.Count);
            }
            else if (stillError > 0 || stillWarning > 0)
            {
                hint.Text = I18n.T("没有可自动修复的项，请按上方提示手动处理。");
            }
            else
            {
                hint.Text = I18n.T("没有可自动修复的项。");
            }
        }
        if (_repairAllBtn is not null) _repairAllBtn.IsEnabled = stillError > 0 || stillWarning > 0;
    }

    private void BuildEnvironmentList(List<EnvironmentCheckResult> results)
    {
        var list = this.FindControl<StackPanel>("EnvironmentListPanel");
        if (list is null) return;
        list.Children.Clear();

        foreach (var r in results)
        {
            var (dotColor, statusText) = r.Status switch
            {
                CheckStatus.Ok => ("#4cc38a", I18n.T("正常")),
                CheckStatus.Warning => ("#e0a030", I18n.T("警告")),
                CheckStatus.Error => ("#ff6b6b", I18n.T("异常")),
                _ => ("#8a8a8a", I18n.T("未知")),
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var dot = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.Parse(dotColor)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            var content = new StackPanel { Spacing = 2 };
            Grid.SetColumn(content, 1);
            content.Children.Add(new TextBlock
            {
                Text = r.Title,
                FontSize = 13.5,
                Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
                FontWeight = FontWeight.SemiBold,
            });
            content.Children.Add(new TextBlock
            {
                Text = r.Message,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#b0b0b0")),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(r.Solution))
            {
                content.Children.Add(new TextBlock
                {
                    Text = I18n.T("解决方案：{0}", r.Solution),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.Parse("#d29922")),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            grid.Children.Add(content);

            if (r.CanRepair)
            {
                var repairBtn = new Button
                {
                    Classes = { "ghostBtn" },
                    Content = I18n.T("修复"),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                repairBtn.Click += async (_, _) => await RepairSingleAsync(r);
                Grid.SetColumn(repairBtn, 2);
                grid.Children.Add(repairBtn);
            }
            else if (r.Status == CheckStatus.Error && r.Id.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
            {
                var mcpRepairBtn = new Button
                {
                    Classes = { "ghostBtn" },
                    Content = I18n.T("修复"),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                mcpRepairBtn.Click += async (_, _) => await RepairMcpViaChatAsync(r.Title, r.Extra);
                Grid.SetColumn(mcpRepairBtn, 2);
                grid.Children.Add(mcpRepairBtn);
            }
            else if (r.Status == CheckStatus.Error && r.Id == "CommandExecution")
            {
                var cmdRepairBtn = new Button
                {
                    Classes = { "ghostBtn" },
                    Content = I18n.T("修复"),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                cmdRepairBtn.Click += async (_, _) => await RepairCommandViaChatAsync(r.Title, r.Message, r.Extra);
                Grid.SetColumn(cmdRepairBtn, 2);
                grid.Children.Add(cmdRepairBtn);
            }

            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#333333")), Margin = new Thickness(0, 6, 0, 0) });
        }
    }

    private async Task RepairSingleAsync(EnvironmentCheckResult item)
    {
        var result = await EnvironmentChecker.RepairAsync(item);
        var idx = _environmentResults.FindIndex(r => r.Id == result.Id);
        if (idx >= 0) _environmentResults[idx] = result;
        Dispatcher.UIThread.Post(() => BuildEnvironmentList(_environmentResults));

        var summary = this.FindControl<TextBlock>("EnvironmentSummary");
        var errorCount = _environmentResults.Count(r => r.Status == CheckStatus.Error);
        var warningCount = _environmentResults.Count(r => r.Status == CheckStatus.Warning);
        if (summary is not null)
        {
            summary.Text = errorCount == 0 && warningCount == 0
                ? I18n.T("✔ 全部通过")
                : I18n.T("检测完成：{0} 项异常，{1} 项警告", errorCount, warningCount);
        }
        if (_repairAllBtn is not null) _repairAllBtn.IsEnabled = errorCount > 0 || warningCount > 0;
    }

    private async Task RepairMcpViaChatAsync(string title, string? command)
    {
        var main = App.Controller.ConversationWindow;
        if (main is null || !App.Controller.Ready)
        {
            Hint(I18n.T("Agent 尚未就绪，请稍后再试。"));
            return;
        }

        var serverName = title.StartsWith("MCP: ", StringComparison.OrdinalIgnoreCase) ? title[5..] : title;
        var prompt = I18n.T("请帮我修复 MCP 服务器 {0} 的连接。启动命令是：{1}。当前连接失败，请检查配置、依赖、工作目录并尝试修复。", serverName, command ?? "");
        await ShowConversationAndSendAsync(main, prompt);
        Close();
    }

    private async Task RepairCommandViaChatAsync(string title, string message, string? command)
    {
        var main = App.Controller.ConversationWindow;
        if (main is null || !App.Controller.Ready)
        {
            Hint(I18n.T("Agent 尚未就绪，请稍后再试。"));
            return;
        }

        var prompt = I18n.T("请帮我修复外部命令执行环境。检测项：{0}。探针命令：{1}。错误信息：{2}。请检查系统 PATH 与进程权限，并尝试修复。", title, command ?? "", message);
        await ShowConversationAndSendAsync(main, prompt);
        Close();
    }

    private static async Task ShowConversationAndSendAsync(MainWindow main, string prompt)
    {
        if (!main.IsVisible)
        {
            main.ShowFromBubble();
        }
        else
        {
            main.Activate();
            main.WindowState = WindowState.Normal;
        }
        await main.SendMessageAsync(prompt);
    }
}
