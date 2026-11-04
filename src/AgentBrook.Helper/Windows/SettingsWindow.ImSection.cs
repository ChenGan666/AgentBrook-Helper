using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Text;
using AgentBrook.Agent.Plugins;
using AgentBrook.Helper.Core.Im;
using AgentBrook.Helper.Support;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 设置窗口「IM 连接」板块（S8 任务2/4）：服务端目录中 category=im 的插件显示于此，
/// 安装后引导完成 Telegram Bot Token 设置（@BotFather 步骤 + 测试连接 + 保存启用）。
/// UI 只经 ImComposition 静态入口操作，不触 DB/文件/平台细节。
/// </summary>
public partial class SettingsWindow
{
    private bool _imNavWired;
    private bool _telegramTokenVerified;
    private bool _imSectionBuilding;   // 防并发重入：两次异步调用各 Clear+Append 导致卡片翻倍

    private void WireImNav()
    {
        if (_imNavWired) return;
        _imNavWired = true;
        NavIm.IsCheckedChanged += (_, _) =>
        {
            if (NavIm.IsChecked == true)
            {
                ShowPage(PageIm);
            }
        };
    }

    private void SetImStatus(string text)
    {
        var status = this.FindControl<TextBlock>("ImStatus");
        if (status is not null) status.Text = text;
    }

    /// <summary>刷新 IM 连接板块（PageIm 激活/语言切换/安装完成后调用）。</summary>
    private async Task RefreshImSectionAsync()
    {
        if (_imSectionBuilding) return;   // 已在构建中——跳过本次（防异步竞态卡片翻倍）
        _imSectionBuilding = true;
        try
        {
            await RefreshImSectionCoreAsync();
        }
        finally
        {
            _imSectionBuilding = false;
        }
    }

    private async Task RefreshImSectionCoreAsync()
    {
        var list = this.FindControl<StackPanel>("ImChannelList");
        if (list is null) return;
        list.Children.Clear();

        var settings = ImSettings.Load(UiPrefs.WorkspaceDir());
        IReadOnlyList<PluginIndexEntry> available = [];
        try
        {
            if (App.Controller.Agent?.PluginManager is { } pm)
            {
                available = await pm.FetchAvailableAsync();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[im-settings] 服务端目录拉取失败：{ex.Message}");
        }

        var telegramEntry = available.FirstOrDefault(e => e.IsImChannel && e.Id == "im-telegram");
        var telegramInstalled = App.Controller.Agent?.PluginManager?.ListInstalled().ContainsKey("im-telegram") == true;
        var telegramChannel = settings.Channels.FirstOrDefault(c => c.Type == "telegram");
        var telegramEnabled = settings.ImEnabled && (telegramChannel?.Enabled ?? false);

        // 运行态行（config 开启 ≠ 通道真在轮询）：组合未启动 / 运行中 + 已绑定通道列表
        var comp = ImComposition.Instance;
        var boundIds = comp?.BoundInternal.Select(b => b.ChannelId).ToList();
        var runtimeText = comp is null
            ? I18n.T("ui.im.runtime.stopped")
            : string.Format(I18n.T("ui.im.runtime.running"),
                boundIds?.Count ?? 0, boundIds is { Count: > 0 } ? string.Join("、", boundIds) : "-");
        var enabledAny = settings.ImEnabled && settings.Channels.Any(c => c.Enabled);
        var runtimeWarning = (comp is null && settings.ImEnabled)
            || (comp is not null && enabledAny && (boundIds?.Count ?? 0) == 0);   // 开了没起来 / 起来了没绑上
        list.Children.Add(new TextBlock
        {
            Text = runtimeText,
            FontSize = 12,
            Margin = new Thickness(2, 0, 0, 4),
            Foreground = runtimeWarning
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e0b060"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        // 最近回合失败原因（无回复/抖动类问题的第一现场）
        if (!string.IsNullOrEmpty(ImComposition.LastTurnError))
        {
            list.Children.Add(new TextBlock
            {
                Text = $"⚠ 最近回合失败：{ImComposition.LastTurnError}",
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 4),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }

        // 绑定失败明细（包加载/通道启动异常原样可见——不用翻 VS 输出）
        foreach (var err in ImComposition.BindErrors)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"⚠ {err}",
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 4),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }

        list.Children.Add(BuildTelegramCard(settings, telegramEntry, telegramInstalled, telegramEnabled));

        // 桥通道插件（im-bridge）同一形态自动出现；P0 暂无服务端条目时静默
        var bridgeEntry = available.FirstOrDefault(e => e.IsImChannel && e.Id == "im-bridge");
        if (bridgeEntry is not null)
        {
            list.Children.Add(BuildBridgeCard(settings, bridgeEntry));
        }
    }

    private Control BuildTelegramCard(ImSettings settings,
        PluginIndexEntry? entry, bool installed, bool enabled)
    {
        var card = new Border
        {
            Classes = { "pluginCard" },
            Padding = new Thickness(12, 10),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#252525")),
        };
        var stack = new StackPanel { Spacing = 8 };

        // 头部：图标 + 名称 + 状态
        var head = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = "💬 Telegram",
            FontSize = 15,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e8e8e8")),
        });
        var stateText = enabled ? I18n.T("ui.im.state.enabled")
            : installed ? I18n.T("ui.im.state.installed")
            : I18n.T("ui.im.state.not.installed");
        head.Children.Add(new TextBlock
        {
            Text = stateText,
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = enabled
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7ec97e"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
        });
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = I18n.T("ui.im.telegram.desc"),
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        // 未安装 → 安装按钮（下载 + 校验 + 经 IM 宿主加载）
        if (!installed && entry is not null)
        {
            var installBtn = new Button
            {
                Content = I18n.T("ui.im.install"),
                Classes = { "pluginPrimary" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            var installStatus = new TextBlock
            {
                FontSize = 12,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
                IsVisible = false,
            };
            installBtn.Click += async (_, _) =>
            {
                installBtn.IsEnabled = false;
                installStatus.IsVisible = true;
                installStatus.Text = I18n.T("ui.im.installing");
                try
                {
                    // 先确保 IM 组合启动（实例创建 + 类别加载器注册——安装分流到 IM 宿主）
                    await ImComposition.EnsureStartedAsync();
                    if (entry is not null)
                    {
                        await (App.Controller.Agent?.PluginManager?.InstallAsync(entry) ?? Task.CompletedTask);
                    }
                    installStatus.Text = I18n.T("ui.im.installed");
                    await RefreshImSectionAsync();
                }
                catch (Exception ex)
                {
                    installBtn.IsEnabled = true;
                    installStatus.Text = $"{I18n.T("ui.im.install.failed")}：{ex.Message}";
                }
            };
            stack.Children.Add(installBtn);
            stack.Children.Add(installStatus);
            card.Child = stack;
            return card;
        }

        // 已安装：引导设置区
        var guide = new TextBlock
        {
            Text = I18n.T("ui.im.botfather.guide"),
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#c8c8c8")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        stack.Children.Add(guide);

        var tokenBox = new TextBox
        {
            Watermark = I18n.T("ui.im.token.watermark"),
            FontSize = 13,
            Width = 420,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        // 已配置时回显占位（不回显明文——secret 宿主持有）
        if (enabled) tokenBox.Watermark = I18n.T("ui.im.token.configured");
        stack.Children.Add(tokenBox);

        var tokenActions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var testBtn = new Button { Content = I18n.T("ui.im.test"), Classes = { "pluginSecondary" } };
        var saveBtn = new Button
        {
            Content = I18n.T("ui.im.save.enable"),
            Classes = { "pluginPrimary" },
        };
        tokenActions.Children.Add(testBtn);
        tokenActions.Children.Add(saveBtn);
        stack.Children.Add(tokenActions);

        var feedback = new TextBlock
        {
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        stack.Children.Add(feedback);

        testBtn.Click += async (_, _) =>
        {
            var token = tokenBox.Text?.Trim() ?? "";
            if (token.Length == 0)
            {
                feedback.Text = I18n.T("ui.im.token.empty");
                return;
            }
            testBtn.IsEnabled = true;
            feedback.Text = I18n.T("ui.im.testing");
            var (ok, detail) = await ImComposition.VerifyTelegramTokenAsync(token);
            feedback.Text = ok
                ? $"{I18n.T("ui.im.test.ok")} {detail}"
                : $"{I18n.T("ui.im.test.fail")} {detail}";
            feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(ok ? "#7ec97e" : "#e08080"));
            _telegramTokenVerified = ok;
        };

        saveBtn.Click += async (_, _) =>
        {
            var token = tokenBox.Text?.Trim() ?? "";
            if (token.Length == 0)
            {
                feedback.Text = I18n.T("ui.im.token.empty");
                return;
            }
            saveBtn.IsEnabled = false;
            feedback.Text = I18n.T("ui.im.saving");
            try
            {
                var state = await ImComposition.SetupTelegramAsync(token);
                feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7ec97e"));
                feedback.Text = $"{I18n.T("ui.im.saved")} {state}";
                await RefreshImSectionAsync();
            }
            catch (Exception ex)
            {
                saveBtn.IsEnabled = true;
                feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080"));
                feedback.Text = $"{I18n.T("ui.im.save.failed")}：{ex.Message}";
            }
        };

        card.Child = stack;
        return card;
    }

    private Control BuildBridgeCard(ImSettings settings, PluginIndexEntry entry)
    {
        var installed = App.Controller.Agent?.PluginManager?.ListInstalled().ContainsKey("im-bridge") == true;
        var card = new Border
        {
            Classes = { "pluginCard" },
            Padding = new Thickness(12, 10),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#252525")),
        };
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = $"🔗 {entry.Name}",
            FontSize = 15,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e8e8e8")),
        });
        stack.Children.Add(new TextBlock
        {
            Text = I18n.T("ui.im.bridge.desc"),
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var state = installed ? I18n.T("ui.im.state.installed") : I18n.T("ui.im.state.not.installed");
        stack.Children.Add(new TextBlock
        {
            Text = state,
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
        });

        // 未安装 → 安装按钮（下载 + 校验 + 经 IM 宿主加载；与 Telegram 卡同款分流）
        if (!installed)
        {
            var installBtn = new Button
            {
                Content = I18n.T("ui.im.install"),
                Classes = { "pluginPrimary" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            var installStatus = new TextBlock
            {
                FontSize = 12,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
                IsVisible = false,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            installBtn.Click += async (_, _) =>
            {
                installBtn.IsEnabled = false;
                installStatus.IsVisible = true;
                installStatus.Text = I18n.T("ui.im.installing");
                try
                {
                    await ImComposition.EnsureStartedAsync();
                    await (App.Controller.Agent?.PluginManager?.InstallAsync(entry) ?? Task.CompletedTask);
                    installStatus.Text = I18n.T("ui.im.installed");
                    await RefreshImSectionAsync();
                }
                catch (Exception ex)
                {
                    installBtn.IsEnabled = true;
                    installStatus.Text = $"{I18n.T("ui.im.install.failed")}：{ex.Message}";
                }
            };
            stack.Children.Add(installBtn);
            stack.Children.Add(installStatus);
        }
        _ = settings;
        card.Child = stack;
        return card;
    }
}
