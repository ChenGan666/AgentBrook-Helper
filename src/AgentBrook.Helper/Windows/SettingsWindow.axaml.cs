using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.Plugins;
using AgentBrook.Agent.Skills;
using AgentBrook.Helper.EnvironmentCheck;
using AgentBrook.Helper.Update;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 模型设置：管理多个 OpenAI 兼容供应商（名称/端点/Key/模型清单），
/// 保存后热应用到会话（切换供应商时保留对话上下文）。
/// </summary>
public partial class SettingsWindow : WindowBase
{
    /// <summary>单例窗口：已打开则聚焦复用，未打开则新建（菜单/对话窗/回环回填统一走此入口）。</summary>
    private static SettingsWindow? _current;

    public static SettingsWindow ShowSingle()
    {
        if (_current is null)
        {
            _current = new SettingsWindow();
            _current.Closed += (_, _) => _current = null;
        }
        if (!_current.IsVisible) _current.Show();
        _current.Activate();
        _current.Focus();
        return _current;
    }

    private List<ProviderConfig> _working = new();
    private string _activeName = "";
    private int _selectedIndex = -1;
    private bool _dirty;
    private TextBlock? _skillsHint;
    private UpdateInfo? _pendingUpdate;
    private List<EnvironmentCheckResult> _environmentResults = new();
    private Button? _repairAllBtn;

    public SettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => CenterOnScreen();
        LoadFromController();
        BuildProviderList();
        BuildPresets();

        _skillsHint = this.FindControl<TextBlock>("SkillsHint");
        _pluginStatusText = this.FindControl<TextBlock>("PluginStatusText");
        BuildSkillsList();
        WireCollaborationAndUpdateSettings();
        this.FindControl<Button>("SetupWizardBtn")?.Click += (_, _) => OnboardingWindow.ShowSingle();

        var currentVersionText = this.FindControl<TextBlock>("CurrentVersionText");
        if (currentVersionText is not null)
        {
            currentVersionText.Text = I18n.T("ui.current.version.value", AutoUpdater.CurrentVersion.ToString());
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

        WatchLanguageChanges();
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
        // 所有静态控件和个人设置均初始化后再统一刷新，避免 AXAML fallback 覆盖已翻译文案。
        OnLanguageChanged();
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
                    Title = I18n.T("ui.choose.the.main.workspace.folder"),
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
                    if (hint is not null) hint.Text = I18n.T("ui.enter.or.choose.a.workspace.folder");
                    return;
                }
                var err = WorkspacePrefs.SaveOverride(path);
                if (hint is not null)
                {
                    hint.Text = err is null
                        ? I18n.T("ui.saved.takes.effect.after.app.restart")
                        : I18n.T("ui.save.failed.value", err);
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
                        ? I18n.T("ui.default.workspace.restored.takes.effect.after.app.restart")
                        : I18n.T("ui.save.failed.value", err);
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
                        ? I18n.T(on ? "ui.enabled.will.start.on.next.login" : "ui.auto.start.disabled")
                        : I18n.T("ui.failed.to.apply.value", err);
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
        NavPlugins.IsCheckedChanged += (_, _) =>
        {
            if (NavPlugins.IsChecked == true) ShowPage(PagePlugins);
        };
        this.FindControl<Button>("PluginRefreshBtn")!.Click += async (_, _) =>
        {
            this.FindControl<Button>("PluginRefreshBtn")!.IsEnabled = false;
            try { await RefreshPluginLists(); }
            finally { this.FindControl<Button>("PluginRefreshBtn")!.IsEnabled = true; }
        };
        ShowPage(PageGeneral);   // 与 NavGeneral 默认选中一致
        this.FindControl<Button>("SaveApplyBtn")!.Click += async (_, _) => await SaveAndApplyAsync();
        this.FindControl<Button>("CloseBtn")!.Click += (_, _) => Close();
        this.FindControl<Button>("DeleteBtn")!.Click += (_, _) => DeleteSelected();
        this.FindControl<Button>("UpdateBtn")!.Click += async (_, _) => await UpdateProviderAsync();
        this.FindControl<Button>("TokenRechargeBtn")!.Click += (_, _) => OpenTokenPurchasePage(recharge: true);
        NameBox.TextChanged += (_, _) => RefreshProviderSpecificUi();
        // 助手名字「取名」：用当前激活大模型生成，填入名称框（点「保存个人设置」后生效）
        this.FindControl<Button>("NameGenBtn")!.Click += async (_, _) =>
        {
            var genBtn = this.FindControl<Button>("NameGenBtn")!;
            genBtn.IsEnabled = false;
            genBtn.Content = I18n.T("ui.naming.working");
            try
            {
                var nameBox = this.FindControl<TextBox>("PersonalNameBox")!;
                nameBox.Text = await AssistantNameGenerator.GenerateAsync(AssistantIdentity.Language);
                Hint(I18n.T("ui.naming.done", nameBox.Text));
            }
            catch (Exception ex)
            {
                Hint(I18n.T("ui.naming.failed", ex.Message));
            }
            finally
            {
                genBtn.IsEnabled = true;
                genBtn.Content = I18n.T("ui.naming");
            }
        };
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
                Hint(I18n.T("ui.unsaved.changes.were.discarded"));
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
            Hint(I18n.T("ui.helper.value.2", icon));
        }
        catch (Exception ex)
        {
            Hint(I18n.T("ui.helper.value", ex.Message));
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
        langBox.SelectedIndex = AssistantIdentity.Language switch
        {
            "en" => 1,
            "id" => 2,
            _ => 0,
        };
        uiLangBox.SelectedIndex = AssistantIdentity.UiLanguage switch
        {
            "zh" => 1,
            "en" => 2,
            "id" => 3,
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
            Hint(I18n.T("ui.personalization.saved.applied.model.instructions.hot.updated.conversation.preserved"));
        }
        catch (Exception ex)
        {
            Hint(I18n.T("ui.personalization.applied.to.ui.failed.to.update.model.instructions.value", ex.Message));
        }
    }

    private void ShowPage(Control page)
    {
        PageGeneral.IsVisible = page == PageGeneral;
        PageAppearance.IsVisible = page == PageAppearance;
        PageModels.IsVisible = page == PageModels;
        PageSkills.IsVisible = page == PageSkills;
        PageEnvironment.IsVisible = page == PageEnvironment;
        PagePlugins.IsVisible = page == PagePlugins;
        if (page == PagePlugins) _ = RefreshPluginLists();
        if (page == PageEnvironment && _environmentResults.Count == 0)
        {
            _ = CheckEnvironmentAsync();
        }
    }

    private TextBlock? _pluginStatusText;

    private void SetPluginStatus(string text)
    {
        if (_pluginStatusText is not null)
        {
            _pluginStatusText.Text = text;
        }
    }

    /// <summary>当前打开的插件设置对话框（卸载时关闭：对话框持有插件实例，会阻碍 ALC 回收与目录删除）。</summary>
    private PluginSettingsDialog? _openPluginDialog;

    private void ClosePluginDialog()
    {
        if (_openPluginDialog is { } d)
        {
            _openPluginDialog = null;
            try { d.Close(); } catch { /* 已关闭则忽略 */ }
        }
    }

    /// <summary>刷新插件列表（PagePlugins 激活/手动刷新时调用）：已装 + 服务端可用；所有状态（未就绪/空/错误）页面内可见。</summary>
    private async Task RefreshPluginLists()
    {
        var installedPanel = this.FindControl<StackPanel>("PluginInstalledList");
        var availablePanel = this.FindControl<StackPanel>("PluginAvailableList");
        if (installedPanel is null || availablePanel is null)
        {
            Console.Error.WriteLine("[plugins] 页面控件缺失：PluginInstalledList/PluginAvailableList");
            return;
        }
        installedPanel.Children.Clear();
        availablePanel.Children.Clear();
        SetPluginStatus("");

        var plugins = App.Controller.Agent?.PluginManager;
        if (plugins is null)
        {
            availablePanel.Children.Add(new TextBlock
            {
                Text = I18n.T("ui.plugins.not.ready"),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f0b0b0")),
            });
            return;
        }

        // 已装插件（空则显示占位说明）
        var installed = plugins.ListInstalled();
        if (installed.Count == 0)
        {
            installedPanel.Children.Add(new TextBlock
            {
                Text = I18n.T("ui.plugins.none"),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8a8a8a")),
            });
        }
        foreach (var (id, entry) in installed)
        {
            var card = new Border
            {
                Classes = { "pluginCard" },
                Padding = new Thickness(10, 8),
                CornerRadius = new Avalonia.CornerRadius(8),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#252525")),
            };
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock
            {
                Text = $"{id} · {entry.Version}",
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e8e8e8")),
            });
            var toggle = new ToggleSwitch
            {
                IsChecked = entry.Enabled,
                OffContent = "",
                OnContent = "",
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            toggle.IsCheckedChanged += async (_, _) =>
            {
                try { await plugins.SetEnabledAsync(id, toggle.IsChecked == true); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[plugins] SetEnabled: {ex.Message}");
                    SetPluginStatus(ex.Message);
                }
            };
            stack.Children.Add(toggle);

            // 插件设置对话框（doc/18 §7.1：具体插件在弹出单独的设置界面对话框）
            var contributor = plugins.GetSettingsContributor(id);
            if (contributor is not null)
            {
                var settingsBtn = new Button
                {
                    Content = I18n.T("ui.plugins.settings"),
                    Classes = { "pluginSecondary" },
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                };
                settingsBtn.Click += (_, _) =>
                {
                    ClosePluginDialog();
                    var dialog = new PluginSettingsDialog(id, id, contributor);
                    dialog.Build();
                    dialog.Closed += (_, _) => { if (_openPluginDialog == dialog) _openPluginDialog = null; };
                    _openPluginDialog = dialog;
                    // 模态：弹出后设置窗口不可点（ShowDialog 非阻塞，返回 Task 不 await）
                    _ = dialog.ShowDialog(this);
                };
                stack.Children.Add(settingsBtn);
            }
            var uninstallBtn = new Button
            {
                Content = I18n.T("ui.plugins.uninstall"),
                Classes = { "pluginDanger" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            uninstallBtn.Click += async (_, _) =>
            {
                try
                {
                    ClosePluginDialog();   // 对话框持有插件实例，先关（热插拔回收需要）
                    var fullyRemoved = await plugins.UninstallAsync(id);
                    SetPluginStatus(I18n.T(fullyRemoved ? "ui.plugins.uninstalled" : "ui.plugins.uninstall.restarted", id));
                    _ = RefreshPluginLists();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[plugins] Uninstall: {ex.Message}");
                    SetPluginStatus(ex.Message);
                }
            };
            stack.Children.Add(uninstallBtn);
            card.Child = stack;
            installedPanel.Children.Add(card);
        }

        // 服务端可用插件
        try
        {
            var available = await plugins.FetchAvailableAsync();
            if (available.Count == 0)
            {
                availablePanel.Children.Add(new TextBlock
                {
                    Text = I18n.T("ui.plugins.none"),
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8a8a8a")),
                });
            }
            foreach (var available_entry in available)
            {
                if (plugins.ListInstalled().ContainsKey(available_entry.Id))
                {
                    continue;   // 已装
                }
                var card = new Border
                {
                    Classes = { "pluginCard" },
                    Padding = new Thickness(10, 8),
                    CornerRadius = new Avalonia.CornerRadius(8),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#252525")),
                };
                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(new TextBlock
                {
                    Text = $"{available_entry.Name} ({available_entry.Latest})",
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e8e8e8")),
                });
                if (!string.IsNullOrEmpty(available_entry.Description))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = available_entry.Description,
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                var installBtn = new Button
                {
                    Content = I18n.T("ui.plugins.install"),
                    Classes = { "pluginPrimary" },
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                };
                // 安装进度条（下载=确定态百分比；校验/解压/加载=不确定态）+ 字节文案
                var installBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Height = 4,
                    IsVisible = false,
                };
                var installStage = new TextBlock
                {
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
                    FontSize = 12,
                    IsVisible = false,
                    TextWrapping = TextWrapping.Wrap,
                };
                stack.Children.Add(installBar);
                stack.Children.Add(installStage);
                var captured = available_entry;
                installBtn.Click += async (_, _) =>
                {
                    installBtn.IsEnabled = false;
                    installBar.IsVisible = true;
                    installBar.IsIndeterminate = true;
                    installStage.IsVisible = true;
                    installStage.Text = I18n.T("ui.plugins.installing", captured.Name);
                    var progress = new Progress<PluginInstallProgress>(p =>
                    {
                        if (p.Stage == PluginInstallProgress.Download)
                        {
                            installBar.IsIndeterminate = p.Total is not > 0;
                            if (p.Total is > 0)
                            {
                                installBar.Value = p.Bytes * 100.0 / p.Total.Value;
                            }
                            installStage.Text = $"{I18n.T("ui.plugins.downloading")} {p.Bytes * 100.0 / Math.Max(p.Total ?? 1, 1):0}%（{p.Bytes / 1048576.0:0.0}/{(p.Total ?? 0) / 1048576.0:0.0} MB）";
                        }
                        else if (p.Stage == PluginInstallProgress.Verify)
                        {
                            installBar.IsIndeterminate = true;
                            installStage.Text = I18n.T("ui.plugins.verifying");
                        }
                        else if (p.Stage == PluginInstallProgress.Extract)
                        {
                            installBar.IsIndeterminate = true;
                            installStage.Text = I18n.T("ui.plugins.extracting");
                        }
                        else if (p.Stage == PluginInstallProgress.Done)
                        {
                            installBar.IsIndeterminate = true;
                            installStage.Text = I18n.T("ui.plugins.enabling");
                        }
                    });
                    try
                    {
                        // 校验/解压/插件初始化是同步 CPU 阶段——整体放后台，UI 不卡死
                        await Task.Run(() => plugins.InstallAsync(captured, progress: progress));
                        await RefreshPluginLists();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[plugins] Install: {ex.Message}");
                        SetPluginStatus($"{I18n.T("ui.plugins.install.failed")}：{ex.Message}");
                        installStage.Text = $"{I18n.T("ui.plugins.install.failed")}：{ex.Message}";
                    }
                    finally
                    {
                        installBar.IsVisible = false;
                        installStage.IsVisible = false;
                        installBtn.IsEnabled = true;
                    }
                };
                stack.Children.Add(installBtn);
                card.Child = stack;
                availablePanel.Children.Add(card);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugins] FetchAvailable: {ex.Message}");
            SetPluginStatus(I18n.T("ui.plugins.server.unreachable", ex.Message));
            availablePanel.Children.Add(new TextBlock
            {
                Text = I18n.T("ui.plugins.server.unreachable", ex.Message),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f0b0b0")),
                TextWrapping = TextWrapping.Wrap,
            });
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
        Hint(I18n.T("ui.bubble.position.has.been.reset"));
    }

    // ───────────────────────── Skill 管理 ─────────────────────────
    private void BuildSkillsList()
    {
        if (SkillsListPanel is null) return;
        SkillsListPanel.Children.Clear();
        var skills = App.Controller.GetSkills(includeDisabled: true);
        if (skills.Count == 0)
        {
            SkillsListPanel.Children.Add(new TextBlock
            {
                Text = I18n.T("ui.no.installed.skills"),
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
            var name = skill.Name;
            var actions = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            // 启用开关：停用不删文件，仅从技能清单/主索引中屏蔽（减少预加载 token）
            var toggle = new ToggleSwitch
            {
                IsChecked = skill.Enabled,
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
            };
            var isMaster = string.Equals(name, AgentBrook.Agent.Skills.MasterIndex.SkillName, StringComparison.OrdinalIgnoreCase);
            ToolTip.SetTip(toggle, I18n.T(isMaster ? "ui.skill.master.tip" : "ui.skill.toggle.tip"));
            if (isMaster)
            {
                toggle.IsEnabled = false;
            }
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (isMaster) return;
                var on = toggle.IsChecked == true;
                try
                {
                    if (App.Controller.SetSkillEnabled(name, on))
                    {
                        card.Opacity = on ? 1 : 0.55;
                        if (_skillsHint is not null) _skillsHint.Text =
                            I18n.T(on ? "ui.skill.enabled.value" : "ui.skill.disabled.value", name);
                    }
                    else if (_skillsHint is not null)
                    {
                        _skillsHint.Text = I18n.T("ui.skill.not.found");
                    }
                }
                catch (Exception ex)
                {
                    if (_skillsHint is not null) _skillsHint.Text = ex.Message;
                }
            };
            var delBtn = new Button
            {
                Content = I18n.T("ui.delete.skill"),
                Classes = { "dangerBtn" },
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            delBtn.Click += (_, _) => DeleteSkill(name);
            actions.Children.Add(toggle);
            actions.Children.Add(delBtn);
            Grid.SetColumn(actions, 1);
            if (!skill.Enabled) card.Opacity = 0.55;
            grid.Children.Add(textPanel);
            grid.Children.Add(actions);
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
                if (_skillsHint is not null) _skillsHint.Text = I18n.T("ui.deleted.skill.value", name);
                BuildSkillsList();
            }
            else
            {
                if (_skillsHint is not null) _skillsHint.Text = I18n.T("ui.delete.failed.value", I18n.T("ui.skill.not.found"));
            }
        }
        catch (Exception ex)
        {
            if (_skillsHint is not null) _skillsHint.Text = I18n.T("ui.delete.failed.value", ex.Message);
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
        Hint(I18n.T("ui.address.saved"));
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

        PostHint(hint, a2a.Config.Enabled ? I18n.T("ui.a2a.collaboration.enabled") : I18n.T("ui.a2a.collaboration.disabled"));

        enabled.IsCheckedChanged += async (_, _) =>
        {
            var on = enabled.IsChecked == true;
            a2a.Config.Enabled = on;
            a2a.SaveConfig();
            PostHint(hint, on ? I18n.T("ui.enabled.starting.a2a") : I18n.T("ui.disabled.disconnecting.a2a"));
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
                PostHint(hint, on ? I18n.T("ui.enabled.a2a.online") : I18n.T("ui.disabled.a2a.disconnected.tick.again.to.restore"));
            }
            catch (Exception ex)
            {
                PostHint(hint, I18n.T("ui.operation.failed.value", ex.Message));
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

    private async Task CheckUpdateAsync()
    {
        var updateHint = this.FindControl<TextBlock>("UpdateHint");
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (updateHint is not null) updateHint.Text = I18n.T("ui.checking.for.updates");
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
            if (updateHint is not null) updateHint.Text = I18n.T("ui.cannot.connect.to.update.service.please.make.sure.the.service.is.running");
            return;
        }

        if (!AutoUpdater.IsNewer(info.Version))
        {
            if (updateHint is not null) updateHint.Text = I18n.T("ui.you.are.using.the.latest.version");
            return;
        }

        _pendingUpdate = info;
        if (updateHint is not null) updateHint.Text = I18n.T("ui.new.version.available.value", info.Version);
        if (upgradeBtn is not null) upgradeBtn.IsVisible = true;
        ShowUpdateDetails(info);
    }

    private void ShowUpdateDetails(UpdateInfo? info)
    {
        var detailsPanel = this.FindControl<Border>("UpdateDetailsPanel");
        if (detailsPanel is not null) detailsPanel.IsVisible = info is not null;
        if (info is null) return;

        var platformText = this.FindControl<TextBlock>("UpdatePlatformText");
        if (platformText is not null) platformText.Text = I18n.T("ui.target.platform.value", info.Platform);

        var fileInfoText = this.FindControl<TextBlock>("UpdateFileInfoText");
        if (fileInfoText is not null)
        {
            fileInfoText.Text = I18n.T("ui.file.value.size.value.mb", info.FileName, info.Size / (1024.0 * 1024.0));
        }

        var notesText = this.FindControl<TextBlock>("UpdateReleaseNotesText");
        if (notesText is not null)
        {
            notesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? I18n.T("ui.no.release.notes")
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
                hint.Text = I18n.T("ui.new.version.value.available.click.upgrade.now.to.start", info.Version);
            }
            return;
        }
        if (hint is not null)
        {
            hint.Text = I18n.T("ui.new.version.value.updating.automatically", info.Version);
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

        if (updateHint is not null) updateHint.Text = I18n.T("ui.downloading.update");
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
            if (updateHint is not null) updateHint.Text = I18n.T("ui.upgrade.failed.value", error);
            if (progressPanel is not null) progressPanel.IsVisible = false;
            ShowDownloadLink(_pendingUpdate.DownloadUrl);
            return;
        }

        if (updateHint is not null) updateHint.Text = I18n.T("ui.download.complete.the.app.will.close.to.upgrade");
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
            Hint(I18n.T("ui.value.4", ex.Message));
        }
    }

    /// <summary>按当前语言设置窗口标题与导航文案。</summary>
    /// <summary>语言切换回调：刷新本窗口全部静态文案。</summary>
    protected override void OnLanguageChanged()
    {
        try
        {
        Title = I18n.T("ui.settings");
        var ngb = this.FindControl<TextBlock>("NavGroupBasic");
        if (ngb is not null) ngb.Text = I18n.T("ui.basic");
        var ngm = this.FindControl<TextBlock>("NavGroupModels");
        if (ngm is not null) ngm.Text = I18n.T("ui.model");
        var nga = this.FindControl<TextBlock>("NavGroupAgent");
        if (nga is not null) nga.Text = I18n.T("ui.agent");
        var nst = this.FindControl<TextBlock>("NavSkillsText");
        if (nst is not null) nst.Text = I18n.T("ui.skill.management");
        var pt = this.FindControl<TextBlock>("PersonalTitle");
        if (pt is not null) pt.Text = I18n.T("ui.personalization");
        var ph = this.FindControl<TextBlock>("PresetHeader");
        if (ph is not null) ph.Text = I18n.T("ui.quick.add.templates");
        var ng = this.FindControl<TextBlock>("NavGeneralText");
        if (ng is not null) ng.Text = I18n.T("ui.general");
        var na = this.FindControl<TextBlock>("NavAppearanceText");
        if (na is not null) na.Text = I18n.T("ui.appearance");
        var nm = this.FindControl<TextBlock>("NavModelsText");
        if (nm is not null) nm.Text = I18n.T("ui.models");
        var ngs = this.FindControl<TextBlock>("NavGroupSystem");
        if (ngs is not null) ngs.Text = I18n.T("ui.system");
        var nenv = this.FindControl<TextBlock>("NavEnvironmentText");
        if (nenv is not null) nenv.Text = I18n.T("ui.environment");
        var saveBtn = this.FindControl<Button>("SavePersonalBtn");
        if (saveBtn is not null) saveBtn.Content = I18n.T("ui.save.general");
        var promptBox2 = this.FindControl<TextBox>("CustomPromptBox");
        if (promptBox2 is not null) promptBox2.Watermark = I18n.T("ui.e.g.keep.answers.brief.prefer.tables.for.data");
        // 模型设置：更新（取模型+余额+测试）/Token卡充值
        var updateBtnL = this.FindControl<Button>("UpdateBtn");
        if (updateBtnL is not null) updateBtnL.Content = I18n.T("ui.models.update");
        var rechargeBtnL = this.FindControl<Button>("TokenRechargeBtn");
        if (rechargeBtnL is not null) rechargeBtnL.Content = I18n.T("ui.token.recharge.btn");
        var rechargeLabelL = this.FindControl<TextBlock>("TokenRechargeLabel");
        if (rechargeLabelL is not null) rechargeLabelL.Text = I18n.T("ui.token.recharge.label");
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
        if (saveApply is not null) saveApply.Content = I18n.T("ui.save.apply");
        var closeBtn = this.FindControl<Button>("CloseBtn");
        if (closeBtn is not null) closeBtn.Content = I18n.T("ui.off");
        var addBtn = this.FindControl<Button>("AddBtn");
        if (addBtn is not null) addBtn.Content = I18n.T("ui.add.provider");
        var delBtn = this.FindControl<Button>("DeleteBtn");
        if (delBtn is not null) delBtn.Content = I18n.T("ui.delete");
        // 页面元素（空安全，缺 x:Name 的跳过）
        SetT("GeneralTitle", "ui.general");
        SetT("GeneralDesc", "ui.assistant.identity.reply.preferences");
        SetT("LabelAssistantName", "ui.assistant.name");
        SetT("LabelReplyLang", "ui.reply.language.affects.model.output");
        SetT("LabelCustomPrompt", "ui.custom.prompt.appended.to.system.instructions.applies.every.turn");
        SetT("LabelUiLang", "ui.ui.language.full.effect.after.app.restart");
        SetT("LabelAutoStart", "ui.launch.at.login");
        SetT("AutoStartDesc", "ui.start.automatically.after.login.the.bubble.stays.resident");
        SetT("LabelWorkspace", "ui.main.workspace");
        SetT("WorkspaceDesc", "ui.message.8");
        var wsPickBtn = this.FindControl<Button>("WorkspacePickBtn");
        if (wsPickBtn is not null) wsPickBtn.Content = I18n.T("ui.choose.folder");
        var wsSaveBtn = this.FindControl<Button>("WorkspaceSaveBtn");
        if (wsSaveBtn is not null) wsSaveBtn.Content = I18n.T("ui.save.workspace");
        var wsResetBtn = this.FindControl<Button>("WorkspaceResetBtn");
        if (wsResetBtn is not null) wsResetBtn.Content = I18n.T("ui.reset.to.default");
        SetT("LabelNotifySound", "ui.notification.sound");
        SetT("NotifySoundDesc", "ui.plays.when.a.task.finishes.while.the.chat.window.is.in.the.background");
        var nsBox = this.FindControl<ComboBox>("NotifySoundBox");
        if (nsBox is not null)
        {
            var items = nsBox.Items.OfType<ComboBoxItem>().ToList();
            var names = new[] { I18n.T("ui.ding"), I18n.T("ui.glass"), I18n.T("ui.chord") };
            for (var k = 0; k < items.Count && k < names.Length; k++)
            {
                items[k].Content = names[k];
            }
        }
        SetT("AppearanceTitle", "ui.appearance");
        SetT("AppearanceDesc", "ui.personalize.the.floating.bubble");
        SetT("LabelBubbleIcon", "ui.bubble.icon");
        SetT("LabelBubblePos", "ui.bubble.position");
        var resetPosBtn = this.FindControl<Button>("ResetPosBtn");
        if (resetPosBtn is not null) resetPosBtn.Content = I18n.T("ui.reset.to.default.position.top.right");
        SetT("ModelsTitle", "ui.models");
        SetT("ModelsDesc", "ui.manage.model.providers.switch.from.the.bottom.bar");
        var checkUpdateBtn = this.FindControl<Button>("CheckUpdateBtn");
        if (checkUpdateBtn is not null) checkUpdateBtn.Content = I18n.T("ui.check.for.updates");
        var upgradeBtn = this.FindControl<Button>("UpgradeBtn");
        if (upgradeBtn is not null) upgradeBtn.Content = I18n.T("ui.upgrade.now");
        SetT("LabelUpdate", "ui.version.update");
        var currentVersionText = this.FindControl<TextBlock>("CurrentVersionText");
        if (currentVersionText is not null) currentVersionText.Text = I18n.T("ui.current.version.value", AutoUpdater.CurrentVersion.ToString());
        SetT("LabelUpdateServiceUrl", "ui.update.service.url");
        SetT("LabelCollab", "ui.collaboration.a2a.lan");
        SetT("A2aEnabledDesc", "ui.enable.a2a.lan.collaboration.off.disconnects.discovery.pairing.groups.on.reconnects.immedi");
        SetT("PeerAutoProcessDesc", "ui.auto.process.direct.messages.from.other.helpers.off.keeps.them.in.inbox.for.manual.handlin");
        SetT("GroupAutoTalkDesc", "ui.group.auto.talk.default.can.be.disabled.per.group.in.the.a2a.session.center");
        SetT("ContextCompressDesc", "ui.auto.compress.group.context.every");
        SetT("CompressRoundsSuffix", "ui.rounds.and.sync.to.the.whole.group");
        SetT("LabelGroupNotify", "ui.group.message.alerts");
        SetT("GroupNotifyDesc", "ui.bubble.alert.on.new.group.messages.click.to.open.the.a2a.session.center");
        SetT("SetupWizardText", "ui.setup.wizard");
        SetT("DailyUpdateCheckDesc", "ui.check.for.updates.on.the.first.launch.each.day");
        SetT("AutoDownloadUpdateDesc", "ui.download.and.install.automatically.after.the.reminder");
        // ComboBox 下拉项不是 TextBlock，SetT 覆盖不到，逐项设置 Content
        var notifyOff = this.FindControl<ComboBoxItem>("GroupNotifyOff");
        if (notifyOff is not null) notifyOff.Content = I18n.T("ui.off");
        var notifyMention = this.FindControl<ComboBoxItem>("GroupNotifyMention");
        if (notifyMention is not null) notifyMention.Content = I18n.T("ui.only.me");
        var notifyAll = this.FindControl<ComboBoxItem>("GroupNotifyAll");
        if (notifyAll is not null) notifyAll.Content = I18n.T("ui.all.messages");
        var soundDing = this.FindControl<ComboBoxItem>("NotifySoundDing");
        if (soundDing is not null) soundDing.Content = I18n.T("ui.ding");
        var soundGlass = this.FindControl<ComboBoxItem>("NotifySoundGlass");
        if (soundGlass is not null) soundGlass.Content = I18n.T("ui.glass");
        var soundChord = this.FindControl<ComboBoxItem>("NotifySoundChord");
        if (soundChord is not null) soundChord.Content = I18n.T("ui.chord");
        var saveUrlBtn = this.FindControl<Button>("SaveUpdateUrlBtn");
        if (saveUrlBtn is not null) saveUrlBtn.Content = I18n.T("ui.save.address");
        var releaseNotesTitle = this.FindControl<TextBlock>("UpdateReleaseNotesTitle");
        if (releaseNotesTitle is not null) releaseNotesTitle.Text = I18n.T("ui.release.notes");
        SetT("SkillsTitle", "ui.skill.management");
        SetT("SkillsDesc", "ui.message.9");
        SetT("EnvironmentTitle", "ui.environment.check");
        SetT("EnvironmentDesc", "ui.check.whether.the.system.environment.is.ready.for.agentbrook.helper");
        var checkEnvBtn = this.FindControl<Button>("CheckEnvironmentBtn");
        if (checkEnvBtn is not null) checkEnvBtn.Content = I18n.T("ui.recheck");
        var repairAllBtn = this.FindControl<Button>("RepairAllBtn");
        if (repairAllBtn is not null) repairAllBtn.Content = I18n.T("ui.repair.all");
        SetT("LabelProviderName", "ui.provider.name");
        SetT("LabelApiEndpoint", "ui.api.endpoint.openai.compatible");
        SetT("LabelApiKey", "ui.api.key");
        SetT("LabelModelsCsv", "ui.models.comma.separated.first.is.default");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[i18n] ApplyWindowLanguage failed: {ex}");
        }
    }

    /// <summary>测试入口：程序化选择界面语言（触发真实 SelectionChanged → 热切换链路）。</summary>
    public void DebugSelectUiLanguage(string lang)
    {
        UiLanguageBox.SelectedIndex = lang switch { "zh" => 1, "en" => 2, "id" => 3, _ => 0 };
    }

    /// <summary>外部回填后展示：重载最新供应商列表，切到「模型设置」页并选中指定供应商。</summary>
    public void ShowModelsPage(string? providerName = null)
    {
        LoadFromController();
        BuildProviderList();
        NavModels.IsChecked = true;
        ShowPage(PageModels);
        if (!string.IsNullOrEmpty(providerName))
        {
            var idx = _working.FindIndex(p => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) ProviderList.SelectedIndex = idx;
        }
    }

    private void LoadFromController()
    {
        // Agent 未就绪（启动初始化中/失败）时 Controller 返回空列表：兜底直读 models.json
        if (!App.Controller.Ready || App.Controller.Providers.Count == 0)
        {
            var loaded = ProviderSettings.Load(ModelsJsonPath);
            if (loaded.Providers.Count > 0)
            {
                _working = loaded.Providers;
                _activeName = loaded.ActiveProvider;
                return;
            }
        }
        _working = App.Controller.Providers.Select(p => new ProviderConfig
        {
            Name = p.Name,
            Protocol = p.Protocol,
            BaseUrl = p.BaseUrl,
            ApiKey = p.ApiKey,
            Models = [.. p.Models],
            ActiveModel = p.ActiveModel,
            ActiveMediaModel = p.ActiveMediaModel,
            ModelTypes = new Dictionary<string, int>(p.ModelTypes),
            ModelNames = new Dictionary<string, string>(p.ModelNames),
            ModelEndpoints = new Dictionary<string, string>(p.ModelEndpoints),
            ModelParams = new Dictionary<string, string>(p.ModelParams),
        }).ToList();
        _activeName = App.Controller.CurrentProvider;
    }

    /// <summary>workspace/models.json 路径（与 Agent 宿主同一解析规则）。</summary>
    private static string ModelsJsonPath
    {
        get
        {
            var dir = UiPrefs.WorkspaceDir();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "models.json");
        }
    }

    /// <summary>把当前 _working 写入 models.json（Agent 未就绪时的保存兜底）。</summary>
    private static void SaveModelsJson(List<ProviderConfig> providers, string activeProvider)
    {
        new AgentBrook.Agent.Infrastructure.ProviderSettings
        {
            Providers = providers,
            ActiveProvider = activeProvider,
        }.Save(ModelsJsonPath);
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
                Text = displayName + (p.Name == _activeName ? I18n.T("ui.in.use") : ""),
                FontSize = 13.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            ProviderList.Items.Add(panel);
        }
        if (_selectedIndex >= _working.Count)
        {
            _selectedIndex = _working.Count - 1;
        }
        if (_selectedIndex < 0 && _working.Count > 0)
        {
            _selectedIndex = 0;   // 打开窗口默认选中第一个供应商，表单即显示已有配置
        }
        ProviderList.SelectedIndex = _selectedIndex;
        // SelectedIndex 未变化时 SelectionChanged 不触发（如删到只剩一项仍选 0）：
        // 主动加载表单，保证唯一供应商的参数正确回显
        LoadSelectedIntoForm();
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
                Content = name,
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
        Hint(I18n.T("ui.added.value.fill.in.the.api.key.and.click.save.apply.endpoint.availability.is.not.verified", name));
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
        _ = RefreshBalanceAsync();
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
        if (p.ActiveModel is null || p.ActiveModel.Length == 0 || !p.Models.Contains(p.ActiveModel) || !p.IsChatModel(p.ActiveModel))
        {
            p.ActiveModel = p.Models.FirstOrDefault(p.IsChatModel) ?? "";
        }
        if (!p.Models.Contains(p.ActiveMediaModel) || p.IsChatModel(p.ActiveMediaModel)) p.ActiveMediaModel = "";
    }

    private async Task SaveAndApplyAsync()
    {
        if (!App.Controller.Ready)
        {
            // Agent 未就绪：仅持久化到 models.json，就绪后自动加载
            SyncFormIntoWorking();
            SaveModelsJson([.. _working], _activeName);
            Hint(I18n.T("ui.models.saved.offline"));
            return;
        }
       SyncFormIntoWorking();
        // 名称去重
        var dup = _working.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            Hint(I18n.T("ui.provider.name.value.is.duplicated.rename.it.before.saving", dup.Key));
            return;
        }
        if (_working.Any(p => p.Name.Length == 0))
        {
            Hint(I18n.T("ui.there.is.an.unnamed.provider"));
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
            Hint(I18n.T("ui.saved.applied.conversation.migrated.to.the.new.model.client"));
        }
        catch (Exception ex)
        {
            Hint(I18n.T("ui.save.failed.value", ex.Message));
        }
    }

    /// <summary>模型连通性测试：用表单当前填写的参数直连一次对话补全（不落盘、不影响已保存配置）。</summary>
    private async Task TestProviderAsync(bool fetchIfEmpty = true)
    {
        var testHint = this.FindControl<TextBlock>("ModelTestHint");
        if (testHint is not null)
        {
            testHint.Text = "";
        }
        SyncFormIntoWorking();
        if (_selectedIndex < 0 || _selectedIndex >= _working.Count)
        {
            return;
        }
        var p = _working[_selectedIndex];
        if (fetchIfEmpty && p.Models.Count == 0)
        {
            // 自动获取模型列表与参数（AgentBrook.API /models，OpenAI 兼容）
            try
            {
                var models = await FetchRelayModelsAsync(p);
                ModelCatalog.Apply(p, models); // 记录类型（含多模态）
                if (models.Count > 0)
                {
                    // 全量入列
                    // 默认激活优先文本模型，避免默认落到图像/视频
                    ModelsBox.Text = string.Join(", ", p.Models);
                }
            }
            catch { /* 拉取失败：按原逻辑提示未配置 */ }
        }
        if (!p.IsConfigured)
        {
            if (testHint is not null)
            {
                testHint.Text = I18n.T("ui.models.test.need.config");
            }
            return;
        }
        var model = p.ActiveModel;
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await PingModelAsync(EffectiveApiBase(p), p.ApiKey, model);
            sw.Stop();
            reply = reply.Trim();
            if (testHint is not null)
            {
                testHint.Text = I18n.T("ui.models.test.ok", sw.ElapsedMilliseconds / 1000.0,
                    string.IsNullOrWhiteSpace(reply) ? "(空回复)" : (reply.Length > 60 ? reply[..60] + "…" : reply));
            }
        }
        catch (OperationCanceledException)
        {
            if (testHint is not null)
            {
                testHint.Text = I18n.T("ui.models.test.timeout");
            }
        }
        catch (Exception ex)
        {
            if (testHint is not null)
            {
                testHint.Text = I18n.T("ui.models.test.fail", FriendlyRelayError(ex.Message));
            }
        }
    }

    /// <summary>「更新」一键三连：拉取模型列表 → 刷新余额 → 连通性测试（复用各环节自身提示）。</summary>
    private async Task UpdateProviderAsync()
    {
        var btn = this.FindControl<Button>("UpdateBtn");
        if (btn is not null)
        {
            btn.IsEnabled = false;
            btn.Content = I18n.T("ui.models.updating");
        }
        try
        {
            await FetchModelsBtnAsync();
            await RefreshBalanceAsync();
            await TestProviderAsync(fetchIfEmpty: false);
        }
        finally
        {
            if (btn is not null)
            {
                btn.IsEnabled = true;
                btn.Content = I18n.T("ui.models.update");
            }
        }
    }

    // ---------------- Token卡：模型列表自动获取 / 一键激活 ----------------

    /// <summary>AgentBrook 中转端点规范化：补 /v1 前缀（OpenAI SDK 只按 Endpoint 直拼 /chat/completions）。</summary>
    private static string EffectiveApiBase(ProviderConfig p)
    {
        return ModelCatalog.EffectiveApiBase(p);
    }

    private static readonly HttpClient TokenHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    

    /// <summary>调 OpenAI 兼容 /models 获取模型列表与参数。</summary>
    private async Task<List<ModelCatalog.Entry>> FetchRelayModelsAsync(ProviderConfig p)
    {
        // 附加一次性参数避开网关/nginx 的 GET 缓存（缓存键含 query，请求头不一定生效）
        var before = ModelCatalog.Fingerprint(p);
        var entries = await ModelCatalog.FetchAsync(ModelCatalog.Clone(p));
        SyncFormIntoWorking();
        if (_selectedIndex < 0 || _selectedIndex >= _working.Count || !ReferenceEquals(p, _working[_selectedIndex])
            || before != ModelCatalog.Fingerprint(p))
            throw new InvalidOperationException("供应商配置已变化，请重新更新模型列表。");
        return entries;
    }

    /// <summary>
    /// 按供应商名称切换 Token 区与「获取模型列表」按钮：
    /// AgentBrook → 显示充值按钮（Key 即当前 API Key，无需重填）；
    /// DeepSeek → 显示「获取模型列表」（官方 /models 支持）；
    /// 其他供应商 → 等拿到各自的模型列表接口后再显示。
    /// </summary>
    private void RefreshProviderSpecificUi()
    {
        var name = NameBox.Text?.Trim() ?? "";
        var isAgentBrook = name == "AgentBrook";
        var supportsFetch = isAgentBrook || name == "DeepSeek";

        var rechargePanel = this.FindControl<StackPanel>("TokenRechargePanel");
        if (rechargePanel is not null) rechargePanel.IsVisible = isAgentBrook;
        var balanceText = this.FindControl<TextBlock>("BalanceText");
        if (balanceText is not null) balanceText.IsVisible = supportsFetch;
    }

    /// <summary>打开 Token卡购买/充值页；充值时带上当前 AgentBrook 供应商的 Key 预填。</summary>
    private void OpenTokenPurchasePage(bool recharge = false)
    {
        try
        {
            var url = TokenPurchaseUrl;
            if (recharge)
            {
                SyncFormIntoWorking();
                var p = _selectedIndex >= 0 && _selectedIndex < _working.Count ? _working[_selectedIndex] : null;
                var key = p?.ApiKey ?? "";
                if (p?.Name == "AgentBrook" && !string.IsNullOrWhiteSpace(key))
                    url += "?mode=2&key=" + Uri.EscapeDataString(key);
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            var hint = this.FindControl<TextBlock>("TokenHint");
            if (hint is not null)
                hint.Text = recharge
                    ? I18n.T("ui.token.recharge.opened")
                    : I18n.T("ui.token.buy.opened");
        }
        catch (Exception ex)
        {
            Hint(I18n.T("ui.token.open.fail", ex.Message));
        }
    }

    /// <summary>「获取模型列表」：用表单当前的 API 地址 + Key 拉取并填充模型清单，附参数摘要。</summary>
    private async Task FetchModelsBtnAsync()
    {
        SyncFormIntoWorking();
        if (_selectedIndex < 0 || _selectedIndex >= _working.Count) return;
        var p = _working[_selectedIndex];
        if (string.IsNullOrWhiteSpace(p.BaseUrl) || string.IsNullOrWhiteSpace(p.ApiKey))
        {
            Hint(I18n.T("ui.models.fetch.need.url.key"));
            return;
        }
        try
        {
            var all = await FetchRelayModelsAsync(p);
            // 记录类型映射（发送选接口）与显示名映射（UI 展示）；全部模型入列
            ModelCatalog.Apply(p, all);
            var models = all;                    // 文本+多模态全部保留
            var mmCount = all.Count(m => m.ModelType >= 4);  // 多模态数（仅提示用）
            if (models.Count == 0)
            {
                Hint(I18n.T("ui.models.fetch.empty"));
                return;
            }
            // 默认激活优先文本模型；多模态模型发对话时由 ValidateActiveProvider 拦截提示
            ModelsBox.Text = string.Join(", ", p.Models);
            var first = models[0];
            var extra = first.ContextWindow.HasValue || first.MaxOutput.HasValue
                ? I18n.T("ui.models.fetch.params", first.Id, first.ContextWindow?.ToString() ?? "-", first.MaxOutput?.ToString() ?? "-")
                : "";
            var okMsg = I18n.T("ui.models.fetch.ok", models.Count, extra);
            if (mmCount > 0) okMsg += I18n.T("ui.models.fetch.skipped.multimodal", mmCount);
            Hint(okMsg);
        }
        catch (Exception ex)
        {
            Hint(I18n.T("ui.models.fetch.fail", ex.Message));
        }
    }

    /// <summary>
    /// 显示所选供应商余额（红色粗体 14 号）：仅 DeepSeek / AgentBrook 支持余额接口，
    /// 两者返回结构一致（balance_infos[0].total_balance），共用解析。
    /// </summary>
    private async Task RefreshBalanceAsync()
    {
        var balanceText = this.FindControl<TextBlock>("BalanceText");
        if (balanceText is null) return;
        var p = _selectedIndex >= 0 && _selectedIndex < _working.Count ? _working[_selectedIndex] : null;
        if (p is null || string.IsNullOrWhiteSpace(p.ApiKey) || string.IsNullOrWhiteSpace(p.BaseUrl))
        {
            balanceText.Text = "";
            return;
        }

        // 余额端点不带 /v1 前缀（DeepSeek: /user/balance；AgentBrook: /api/get-card-balance）
        var root = p.BaseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        var url = p.Name == "DeepSeek" ? root + "/user/balance" : root + "/api/get-card-balance";
        // 附加一次性参数避开网关/nginx 的 GET 缓存
        url += "?_=" + DateTimeOffset.Now.ToUnixTimeMilliseconds();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var resp = await TokenHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                balanceText.Text = I18n.T("ui.models.balance.fail");
                return;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var info = doc.RootElement.GetProperty("balance_infos")[0];
            var total = info.GetProperty("total_balance").GetString() ?? "0";
            var currency = info.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "";
            var shown = currency.Equals("CNY", StringComparison.OrdinalIgnoreCase) ? "¥" + total
                : string.IsNullOrEmpty(currency) ? total : currency + " " + total;
            balanceText.Text = I18n.T("ui.models.balance", shown);
        }
        catch
        {
            balanceText.Text = I18n.T("ui.models.balance.fail");
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
            Hint(I18n.T("ui.keep.at.least.one.provider.edit.the.existing.one.instead"));
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
        Hint(I18n.T("ui.deleted.value.takes.effect.when.you.click.save.apply", removedName));
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
        if (summary is not null) summary.Text = I18n.T("ui.checking");
        if (hint is not null) hint.Text = "";

        var agent = App.Controller.Ready ? App.Controller.Agent : null;
        _environmentResults = await EnvironmentChecker.CheckAllAsync(agent);
        Dispatcher.UIThread.Post(() => BuildEnvironmentList(_environmentResults));

        var errorCount = _environmentResults.Count(r => r.Status == CheckStatus.Error);
        var warningCount = _environmentResults.Count(r => r.Status == CheckStatus.Warning);
        if (summary is not null)
        {
            summary.Text = EnvironmentResultView.Summary(_environmentResults);
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
        if (hint is not null) hint.Text = I18n.T("ui.repairing");

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
            summary.Text = EnvironmentResultView.Summary(_environmentResults, repaired: true);
        }
        if (hint is not null)
        {
            if (repaired.Count > 0)
            {
                hint.Text = I18n.T("ui.tried.to.repair.value.item.s.please.follow.the.tips.if.issues.remain", repaired.Count);
            }
            else if (stillError > 0 || stillWarning > 0)
            {
                hint.Text = I18n.T("ui.no.auto.repairable.items.please.follow.the.tips.above.to.fix.manually");
            }
            else
            {
                hint.Text = I18n.T("ui.no.auto.repairable.items");
            }
        }
        if (_repairAllBtn is not null) _repairAllBtn.IsEnabled = stillError > 0 || stillWarning > 0;
    }

    private void BuildEnvironmentList(List<EnvironmentCheckResult> results)
    {
        var list = this.FindControl<StackPanel>("EnvironmentListPanel");
        if (list is null) return;
        list.Children.Clear();

        EnvironmentResultView.Render(list, results, RepairSingleAsync, async r =>
        {
            if (r.Id.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
                await RepairMcpViaChatAsync(r.Title, r.Extra);
            else
                await RepairCommandViaChatAsync(r.Title, r.Message, r.Extra);
        });
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
            summary.Text = EnvironmentResultView.Summary(_environmentResults);
        }
        if (_repairAllBtn is not null) _repairAllBtn.IsEnabled = errorCount > 0 || warningCount > 0;
    }

    private async Task RepairMcpViaChatAsync(string title, string? command)
    {
        var main = App.Controller.ConversationWindow;
        if (main is null || !App.Controller.Ready)
        {
            Hint(I18n.T("ui.agent.is.not.ready.yet.please.try.again.later"));
            return;
        }

        var serverName = title.StartsWith("MCP: ", StringComparison.OrdinalIgnoreCase) ? title[5..] : title;
        var prompt = I18n.T("ui.please.fix.the.connection.of.mcp.server.value.the.start.command.is.value.it.failed.to.conn", serverName, command ?? "");
        await ShowConversationAndSendAsync(main, prompt);
        Close();
    }

    private async Task RepairCommandViaChatAsync(string title, string message, string? command)
    {
        var main = App.Controller.ConversationWindow;
        if (main is null || !App.Controller.Ready)
        {
            Hint(I18n.T("ui.agent.is.not.ready.yet.please.try.again.later"));
            return;
        }

        var prompt = I18n.T("ui.please.fix.the.external.command.execution.environment.check.item.value.probe.command.value", title, command ?? "", message);
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
