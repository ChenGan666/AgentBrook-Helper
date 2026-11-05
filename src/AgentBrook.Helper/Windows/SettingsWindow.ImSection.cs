using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Text;
using AgentBrook.Agent.Plugins;
using AgentBrook.Messaging.Abstractions;
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

        // 通道运行态原因（Degraded/Failed 时显示，如 getupdates ret=… / 需重新扫码）
        foreach (var (id, state, reason) in ImComposition.ChannelStatuses)
        {
            if (string.IsNullOrWhiteSpace(reason)) continue;
            list.Children.Add(new TextBlock
            {
                Text = $"⚠ {id}：{state} · {reason}",
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 4),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e0a060")),
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

        // 重新绑定按钮：强制对配置中的启用通道重跑绑定（诊断/恢复用，无需重启）
        var rebindRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(2, 0, 0, 6),
        };
        var rebindBtn = new Button
        {
            Content = I18n.T("ui.im.rebind"),
            Classes = { "pluginSecondary" },
            FontSize = 12,
        };
        var rebindStatus = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        rebindBtn.Click += async (_, _) =>
        {
            rebindBtn.IsEnabled = false;
            rebindStatus.Text = I18n.T("ui.im.rebinding");
            try
            {
                var lines = await ImComposition.RebindConfiguredChannelsAsync();
                rebindStatus.Text = string.Join("；", lines);
                await RefreshImSectionAsync();
            }
            catch (Exception ex)
            {
                rebindStatus.Text = $"{I18n.T("ui.im.install.failed")}：{ex.Message}";
            }
            finally
            {
                rebindBtn.IsEnabled = true;
            }
        };
        var openLogBtn = new Button
        {
            Content = I18n.T("ui.im.open.log"),
            Classes = { "pluginSecondary" },
            FontSize = 12,
        };
        openLogBtn.Click += (_, _) =>
        {
            try
            {
                var ws = UiPrefs.WorkspaceDir();
                var imDir = Path.Combine(ws, "im");
                // 显式点击才建目录（U01：IM 未启用时启动不产生任何文件副作用）
                Directory.CreateDirectory(imDir);
                var diag = Path.Combine(imDir, "host-log.txt");
                var audit = Path.Combine(imDir, "audit.jsonl");
                var target = File.Exists(diag) ? diag : File.Exists(audit) ? audit : imDir;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[im] 打开日志失败：{ex.Message}"); }
        };
        rebindRow.Children.Add(openLogBtn);
        rebindRow.Children.Add(rebindBtn);
        rebindRow.Children.Add(rebindStatus);
        list.Children.Add(rebindRow);

        list.Children.Add(BuildTelegramCard(settings, telegramEntry, telegramInstalled, telegramEnabled));

        // 桥通道插件（im-bridge）同一形态自动出现；P0 暂无服务端条目时静默
        var bridgeEntry = available.FirstOrDefault(e => e.IsImChannel && e.Id == "im-bridge");
        if (bridgeEntry is not null)
        {
            list.Children.Add(BuildBridgeCard(settings, bridgeEntry));
        }

        // 微信通道（iLink/ClawBot，扫码登录）：服务端条目缺失时也显示（自带登录流程）
        var weixinEntry = available.FirstOrDefault(e => e.IsImChannel && e.Id == "im-weixin");
        list.Children.Add(BuildWeixinCard(settings, weixinEntry));
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
        var bridgeChannel = settings.Channels.FirstOrDefault(c => c.Type == "bridge");
        var bridgeEnabled = settings.ImEnabled && (bridgeChannel?.Enabled ?? false);
        var stateText = bridgeEnabled ? I18n.T("ui.im.state.enabled")
            : installed ? I18n.T("ui.im.state.installed")
            : I18n.T("ui.im.state.not.installed");
        stack.Children.Add(new TextBlock
        {
            Text = stateText,
            FontSize = 12,
            Foreground = bridgeEnabled
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7ec97e"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
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
        else
        {
            // 已安装 → 配置区（桥地址 + Token + 测试连接 + 保存并启用），与 Telegram 卡同款交互
            var guide = new TextBlock
            {
                Text = I18n.T("ui.im.bridge.guide"),
                FontSize = 12,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#c8c8c8")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            stack.Children.Add(guide);

            var urlBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(bridgeChannel?.BridgeUrl) ? "http://127.0.0.1:38100" : bridgeChannel!.BridgeUrl,
                Watermark = I18n.T("ui.im.bridge.url.watermark"),
                FontSize = 13,
                Width = 420,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            stack.Children.Add(urlBox);

            var tokenBox = new TextBox
            {
                Watermark = bridgeEnabled ? I18n.T("ui.im.token.configured") : I18n.T("ui.im.bridge.token.watermark"),
                FontSize = 13,
                Width = 420,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            stack.Children.Add(tokenBox);

            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            var testBtn = new Button { Content = I18n.T("ui.im.test"), Classes = { "pluginSecondary" } };
            var saveBtn = new Button { Content = I18n.T("ui.im.save.enable"), Classes = { "pluginPrimary" } };
            actions.Children.Add(testBtn);
            actions.Children.Add(saveBtn);
            stack.Children.Add(actions);

            var feedback = new TextBlock
            {
                FontSize = 12,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            stack.Children.Add(feedback);

            testBtn.Click += async (_, _) =>
            {
                var url = urlBox.Text?.Trim() ?? "";
                var token = tokenBox.Text?.Trim() ?? "";
                if (url.Length == 0 || token.Length == 0)
                {
                    feedback.Text = I18n.T("ui.im.bridge.need.url.token");
                    feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080"));
                    return;
                }
                testBtn.IsEnabled = false;
                feedback.Text = I18n.T("ui.im.testing");
                var (ok, detail) = await ImComposition.VerifyBridgeAsync(url, token);
                testBtn.IsEnabled = true;
                feedback.Text = ok ? $"{I18n.T("ui.im.test.ok")} {detail}" : $"{I18n.T("ui.im.test.fail")} {detail}";
                feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(ok ? "#7ec97e" : "#e08080"));
            };

            saveBtn.Click += async (_, _) =>
            {
                var url = urlBox.Text?.Trim() ?? "";
                var token = tokenBox.Text?.Trim() ?? "";
                if (url.Length == 0 || token.Length == 0)
                {
                    feedback.Text = I18n.T("ui.im.bridge.need.url.token");
                    feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080"));
                    return;
                }
                saveBtn.IsEnabled = false;
                feedback.Text = I18n.T("ui.im.saving");
                try
                {
                    var state = await ImComposition.SetupBridgeAsync(url, token);
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
        }
        card.Child = stack;
        return card;
    }

    /// <summary>微信（iLink/ClawBot）卡：扫码登录 → 自动落库并热绑定。二维码由本地把 qrcode_img_content 编码成图。</summary>
    private Control BuildWeixinCard(ImSettings settings, PluginIndexEntry? entry)
    {
        var installed = entry is null
            || App.Controller.Agent?.PluginManager?.ListInstalled().ContainsKey("im-weixin") == true;
        var channel = settings.Channels.FirstOrDefault(c => c.Type == "weixin");
        var enabled = settings.ImEnabled && (channel?.Enabled ?? false);

        var card = new Border
        {
            Classes = { "pluginCard" },
            Padding = new Thickness(12, 10),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#252525")),
        };
        var stack = new StackPanel { Spacing = 8 };
        // 标题行：微信图标（插件安装目录 icon.png——与"已安装插件"列表同源；缺失时回退矢量 Icon.Weixin）
        var titleRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var iconPath = App.Controller.Agent?.PluginManager?.GetIconPath("im-weixin");
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            try
            {
                titleRow.Children.Add(new Avalonia.Controls.Image
                {
                    Source = new Avalonia.Media.Imaging.Bitmap(iconPath),
                    Width = 20, Height = 20,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
            }
            catch { /* 图片损坏降级矢量 */ }
        }
        if (titleRow.Children.Count == 0)
        {
            titleRow.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = App.Current!.FindResource("Icon.Weixin") as Avalonia.Media.StreamGeometry,
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2dc100")),
                Width = 20, Height = 20,
                Stretch = Avalonia.Media.Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }
        titleRow.Children.Add(new TextBlock
        {
            Text = "微信（iLink 官方通道）",
            FontSize = 15,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e8e8e8")),
        });
        stack.Children.Add(titleRow);
        stack.Children.Add(new TextBlock
        {
            Text = I18n.T("ui.im.weixin.desc"),
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        var stateText = enabled ? I18n.T("ui.im.state.enabled")
            : installed ? I18n.T("ui.im.state.installed")
            : I18n.T("ui.im.state.not.installed");
        stack.Children.Add(new TextBlock
        {
            Text = stateText,
            FontSize = 12,
            Foreground = enabled
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7ec97e"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
        });

        // 未安装 → 安装按钮（与其它通道同款分流）
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
                    await (App.Controller.Agent?.PluginManager?.InstallAsync(entry!) ?? Task.CompletedTask);
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

        // 已安装：扫码登录区（二维码图 + 状态/配对码 + 刷新）
        var qrImage = new Image
        {
            Width = 200, Height = 200,
            Stretch = Avalonia.Media.Stretch.Uniform,
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        stack.Children.Add(qrImage);

        var verifyBox = new TextBox
        {
            Watermark = I18n.T("ui.im.weixin.verify.watermark"),
            FontSize = 13, Width = 200,
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        stack.Children.Add(verifyBox);

        var feedback = new TextBlock
        {
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        stack.Children.Add(feedback);

        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var loginBtn = new Button
        {
            Content = enabled ? I18n.T("ui.im.weixin.relogin") : I18n.T("ui.im.weixin.login"),
            Classes = { "pluginPrimary" },
        };
        actions.Children.Add(loginBtn);
        stack.Children.Add(actions);

        // 扫码轮询（一次登录会话一个后台循环；卡片重建/完成即取消）。
        // 协议在插件内：宿主经 IInteractiveLoginProvider 契约驱动，不感知 iLink 细节。
        CancellationTokenSource? cts = null;
        var finished = false;

        loginBtn.Click += async (_, _) =>
        {
            if (finished) return;
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            loginBtn.IsEnabled = false;
            verifyBox.IsVisible = false;
            feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a9a9a"));
            feedback.Text = I18n.T("ui.im.weixin.requesting");
            try
            {
                // 登录入口域名随通道类型固定由插件声明；宿主只需放行该域
                var hosts = new List<string> { "ilinkai.weixin.qq.com" };
                if (channel?.BaseUrl is { } bu && Uri.TryCreate(bu, UriKind.Absolute, out var buUri))
                    hosts.Add(buUri.Host);
                var provider = await ImComposition.StartLoginProviderAsync("weixin", "inst-1", hosts, ct);
                if (provider is null)
                {
                    feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080"));
                    feedback.Text = I18n.T("ui.im.weixin.provider.missing");
                    loginBtn.IsEnabled = true;
                    return;
                }

                var challenge = await provider.BeginAsync(ct);
                Console.Error.WriteLine($"[im-ui] 扫码挑战已获取：id={challenge.Id[..Math.Min(8, challenge.Id.Length)]}… qr={challenge.QrContent[..Math.Min(60, challenge.QrContent.Length)]}");
                var qrBitmap = RenderQr(challenge.QrContent);
                if (qrBitmap is null)
                {
                    feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080"));
                    feedback.Text = "二维码渲染失败（QRCoder）——请把此提示截图反馈";
                    loginBtn.IsEnabled = true;
                    return;
                }
                qrImage.Source = qrBitmap;
                qrImage.IsVisible = true;
                feedback.Text = string.IsNullOrWhiteSpace(challenge.Instructions)
                    ? I18n.T("ui.im.weixin.scan") : challenge.Instructions;

                string? verifyCode = null;
                while (!ct.IsCancellationRequested && !finished)
                {
                    var poll = await provider.PollAsync(challenge.Id, verifyCode, ct);
                    Console.Error.WriteLine($"[im-ui] 扫码轮询：{poll.Stage}" + (poll.Detail is null ? "" : $" · {poll.Detail}"));
                    switch (poll.Stage)
                    {
                        case LoginStage.Waiting:
                            await Task.Delay(1500, ct);
                            break;
                        case LoginStage.Scanned:
                            feedback.Text = I18n.T("ui.im.weixin.scanned");
                            await Task.Delay(1200, ct);
                            break;
                        case LoginStage.NeedVerification:
                            verifyBox.IsVisible = true;
                            feedback.Text = I18n.T("ui.im.weixin.need.code");
                            var waited = 0;
                            while (verifyBox.IsVisible && !ct.IsCancellationRequested && waited < 180_000)
                            {
                                if (!string.IsNullOrWhiteSpace(verifyBox.Text))
                                {
                                    verifyCode = verifyBox.Text!.Trim();
                                    verifyBox.IsVisible = false;
                                    break;
                                }
                                await Task.Delay(400, ct);
                                waited += 400;
                            }
                            break;
                        case LoginStage.Redirected:
                            // 登录节点切换：重建提供者实例（放行新域），挑战 ID（qrcode）不变可继续轮询
                            if (poll.RedirectBaseUrl is { } redirect
                                && Uri.TryCreate(redirect, UriKind.Absolute, out var ru))
                            {
                                hosts = [.. hosts, ru.Host];
                                provider = await ImComposition.StartLoginProviderAsync("weixin", "inst-1", hosts, ct)
                                    ?? provider;
                            }
                            await Task.Delay(500, ct);
                            break;
                        case LoginStage.Blocked:
                        case LoginStage.Expired:
                        case LoginStage.Failed:
                            feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080"));
                            feedback.Text = $"{I18n.T("ui.im.test.fail")} {poll.Detail}";
                            finished = true;
                            break;
                        case LoginStage.Confirmed:
                        {
                            feedback.Text = I18n.T("ui.im.saving");
                            var completion = poll.Completion
                                ?? throw new InvalidOperationException("登录完成但缺少凭据信息。");
                            var state = await ImComposition.ApplyLoginCompletionAsync(
                                "weixin", completion, displayName: "微信", channelId: "weixin", instanceId: "inst-1");
                            feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7ec97e"));
                            feedback.Text = $"{I18n.T("ui.im.saved")} {state}";
                            qrImage.IsVisible = false;
                            finished = true;
                            await RefreshImSectionAsync();
                            break;
                        }
                    }
                    if (finished) break;
                }
            }
            catch (OperationCanceledException) { /* 卡片重建/关闭 */ }
            catch (Exception ex)
            {
                feedback.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e08080"));
                feedback.Text = $"{I18n.T("ui.im.save.failed")}：{ex.Message}";
                finished = true;
            }
            finally
            {
                loginBtn.IsEnabled = true;
            }
        };

        _ = settings;
        card.Child = stack;
        return card;
    }

    /// <summary>把二维码内容编码成位图（QRCoder；内容为 iLink 返回的二维码 URL）。</summary>
    private static Avalonia.Media.Imaging.Bitmap? RenderQr(string content)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
            using var ms = new MemoryStream(png);
            return new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[im] 二维码渲染失败：{ex.Message}");
            return null;
        }
    }
}
