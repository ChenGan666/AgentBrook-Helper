using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.Skills;
using AgentBrook.Helper.Update;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

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

    public ModelSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => CenterOnScreen();
        LoadFromController();
        BuildProviderList();
        BuildPresets();

        _skillsHint = this.FindControl<TextBlock>("SkillsHint");
        BuildSkillsList();

        var currentVersionText = this.FindControl<TextBlock>("CurrentVersionText");
        if (currentVersionText is not null)
        {
            currentVersionText.Text = I18n.T("当前版本：{0}", AutoUpdater.CurrentVersion.ToString());
        }
        var checkUpdateBtn = this.FindControl<Button>("CheckUpdateBtn");
        if (checkUpdateBtn is not null) checkUpdateBtn.Click += async (_, _) => await CheckUpdateAsync();
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (upgradeBtn is not null) upgradeBtn.Click += async (_, _) => await UpgradeAsync();
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

    private void ShowPage(StackPanel page)
    {
        PageGeneral.IsVisible = page == PageGeneral;
        PageAppearance.IsVisible = page == PageAppearance;
        PageModels.IsVisible = page == PageModels;
        PageSkills.IsVisible = page == PageSkills;
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

    private async Task CheckUpdateAsync()
    {
        var updateHint = this.FindControl<TextBlock>("UpdateHint");
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (updateHint is not null) updateHint.Text = I18n.T("正在检查更新…");
        if (upgradeBtn is not null) upgradeBtn.IsVisible = false;
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
                ? I18n.T("无")
                : info.ReleaseNotes;
        }
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
            return;
        }

        if (updateHint is not null) updateHint.Text = I18n.T("下载完成，即将关闭并升级。");
        if (progressText is not null) progressText.Text = "100%";
        await Task.Delay(800);
        Environment.Exit(0);
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
        var saveUrlBtn = this.FindControl<Button>("SaveUpdateUrlBtn");
        if (saveUrlBtn is not null) saveUrlBtn.Content = I18n.T("保存地址");
        var releaseNotesTitle = this.FindControl<TextBlock>("UpdateReleaseNotesTitle");
        if (releaseNotesTitle is not null) releaseNotesTitle.Text = I18n.T("更新说明");
        SetT("SkillsTitle", "Skill管理");
        SetT("SkillsDesc", "管理已安装的技能，删除后立即生效。");
        SetT("LabelProviderName", "供应商名称");
        SetT("LabelApiEndpoint", "API 地址（OpenAI 兼容端点）");
        SetT("LabelApiKey", "API Key");
        SetT("LabelModelsCsv", "模型清单（逗号分隔，第一个为默认）");
        }
        catch
        {
            // 语言应用失败不阻塞窗口
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
}
