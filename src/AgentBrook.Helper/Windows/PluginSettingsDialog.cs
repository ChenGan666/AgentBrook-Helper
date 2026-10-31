using System.Text.Json;
using AgentBrook.Helper.Support;
using AgentBrook.Plugin.Abstractions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 插件设置对话框（doc/18 §7.1 标准）：按插件 <see cref="ISettingsContributor.SettingsSchemaJson"/>
/// 声明式渲染（toggle/slider/choice/action 四种控件），写回经 <see cref="ISettingsContributor.SetSettingsJson"/>
/// （插件内部同步真源并热更新引擎）；具名操作经 RunActionAsync 执行（进度回显状态栏）。
/// 声明了 list-terms 操作的插件附加词典编辑区（隐私脱敏）。
/// </summary>
public sealed class PluginSettingsDialog : WindowBase
{
    private static readonly string[] TermCategories = ["PERSON", "ORG", "ADDRESS", "PHONE", "EMAIL", "ID_CARD", "BANK_CARD"];

    private readonly string _pluginId;
    private readonly ISettingsContributor _contributor;
    private readonly StackPanel _contentPanel;
    private readonly TabControl _tabs = new();
    private readonly TextBlock _statusText;
    private readonly ProgressBar _actionProgress;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<TermRow> _terms = [];
    private readonly List<long> _whitelistIds = [];
    private string _termFilter = "";
    private string _termCategoryFilter = "";

    private sealed record TermRow(long Id, string Category, string Term, bool Enabled, string Source);

    public PluginSettingsDialog(string pluginId, string displayName, ISettingsContributor contributor)
    {
        _pluginId = pluginId;
        _contributor = contributor;

        Title = $"{displayName} · {I18n.T("ui.plugins.settings")}";

        Width = 560;
        Height = 640;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Background = new SolidColorBrush(Color.Parse("#1f1f1f"));
        FontFamily = "PingFang SC, Hiragino Sans GB, Microsoft YaHei";

        var root = new Grid
        {
            RowDefinitions = new Avalonia.Controls.RowDefinitions("*,Auto"),
            Margin = new Thickness(16, 12),
        };

        // 基础设置页内容面板（标签 1）
        _contentPanel = new StackPanel { Spacing = 8 };

        // 标签页容器（Build 时按插件能力装配：基础设置 / 敏感词词典 / 白名单）
        _tabs = new TabControl();
        Grid.SetRow(_tabs, 0);
        root.Children.Add(_tabs);

        // 底部：操作进度条（不确定态，动作运行中显示）+ 状态文本
        var bottom = new StackPanel { Spacing = 4 };
        _actionProgress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            IsVisible = false,
        };
        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        bottom.Children.Add(_actionProgress);
        bottom.Children.Add(_statusText);
        Grid.SetRow(bottom, 1);
        root.Children.Add(bottom);
        Content = root;
    }

    /// <summary>按 schema 构建设置 UI（由打开方调用一次）。</summary>
    public void Build()
    {
        var schema = ParseJson(_contributor.SettingsSchemaJson());
        if (schema.TryGetValue("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (group.TryGetProperty("title", out var title))
                {
                    _contentPanel.Children.Add(new TextBlock
                    {
                        Text = I18n.T(title.GetString() ?? ""),
                        Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
                        Margin = new Thickness(0, 8, 0, 2),
                    });
                }
                if (group.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        _contentPanel.Children.Add(BuildItem(item));
                    }
                }
            }
        }

        // 标签页装配（T5.x 修订：基础设置 / 敏感词词典 / 白名单 分页）
        _tabs.Items.Add(new TabItem
        {
            Header = I18n.T("ui.plugins.privacy.tab.basic"),
            Content = new ScrollViewer { Content = _contentPanel },
        });
        if (_contributor.Actions.Contains("list-terms"))
        {
            _tabs.Items.Add(new TabItem
            {
                Header = I18n.T("ui.plugins.privacy.tab.dict"),
                Content = new ScrollViewer { Content = BuildDictPanel() },
            });
        }
        if (_contributor.Actions.Contains("list-whitelist"))
        {
            _tabs.Items.Add(new TabItem
            {
                Header = I18n.T("ui.plugins.privacy.tab.whitelist"),
                Content = new ScrollViewer { Content = BuildWhitelistPanel() },
            });
        }
    }

    /// <summary>
    /// 识别模型行（v1.0.12 单模型部署）：模型名（固定） + 安装模型（就绪后变"已安装"并禁用） + 测试（环境自检）。
    /// 就绪状态经 readyAction 查询（异步，不阻塞 Build）。
    /// </summary>
    private Control BuildModelRow(JsonElement item)
    {
        // 两列 Grid（标签 | 右侧控件组）——右侧用水平 StackPanel 装模型名/安装/测试，
        // 规避 Grid 多列重叠问题；控件组垂直居中
        var row = new Grid
        {
            ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
        };

        row.Children.Add(new TextBlock
        {
            Text = I18n.T(ItemTitle(item)),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#e8e8e8")),
        });

        var modelName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var installAction = item.TryGetProperty("installAction", out var ia) ? ia.GetString() ?? "install-model" : "install-model";
        var checkAction = item.TryGetProperty("checkAction", out var ca) ? ca.GetString() ?? "run-self-check" : "run-self-check";
        var readyAction = item.TryGetProperty("readyAction", out var ra) ? ra.GetString() ?? "model-ready" : "model-ready";

        var modelNameText = new TextBlock
        {
            Text = modelName,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")),
        };
        var installBtn = DialogButton(I18n.T("ui.plugins.privacy.install.model"));
        var testBtn = DialogButton(I18n.T("ui.plugins.privacy.model.test"));

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        controls.Children.Add(modelNameText);
        controls.Children.Add(installBtn);
        controls.Children.Add(testBtn);
        row.Children.Add(controls);

        // 初始就绪状态：已安装 → 按钮变"已安装"并禁用（异步查询，不阻塞 Build）
        _ = Task.Run(() => _contributor.RunActionAsync(readyAction))
            .ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result.Success)
                {
                    installBtn.Content = I18n.T("ui.plugins.privacy.model.installed");
                    installBtn.IsEnabled = false;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        installBtn.Click += async (_, _) =>
        {
            installBtn.IsEnabled = false;
            _actionProgress.IsVisible = true;
            var progress = new Progress<string>(SetProgress);
            try
            {
                var result = await Task.Run(() => _contributor.RunActionAsync(installAction, null, progress));
                SetStatus(result.Message, ok: result.Success);
                if (result.Success)
                {
                    installBtn.Content = I18n.T("ui.plugins.privacy.model.installed");
                }
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                _actionProgress.IsVisible = false;
                installBtn.IsEnabled = true;
            }
        };

        testBtn.Click += async (_, _) =>
        {
            testBtn.IsEnabled = false;
            _actionProgress.IsVisible = true;
            var progress = new Progress<string>(SetProgress);
            try
            {
                var result = await Task.Run(() => _contributor.RunActionAsync(checkAction, null, progress));
                SetStatus(result.Message, ok: result.Success);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                _actionProgress.IsVisible = false;
                testBtn.IsEnabled = true;
            }
        };

        return row;
    }

    // ── schema 项 → 控件 ─────────────────────────────────────────────────

    private Control BuildItem(JsonElement item)
    {
        var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
        return type switch
        {
            "toggle" => BuildToggle(Key(item), I18n.T(ItemTitle(item))),
            "slider" => BuildSlider(item),
            "choice" => BuildChoice(item),
            "action" => BuildAction(Action(item), I18n.T(ItemTitle(item))),
            "note" => new TextBlock
            {
                Text = I18n.T(ItemTitle(item)),
                Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
            // 未知 item 类型：标题经 I18n 翻译后显示（老宿主遇新 schema 类型不裸显键名）
            _ => new TextBlock { Text = I18n.T(ItemTitle(item)), Foreground = new SolidColorBrush(Color.Parse("#e8e8e8")) },
        };
    }

    private Grid BuildToggle(string key, string label)
    {
        var row = NewRow("*", "Auto");
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#e8e8e8")),
        });
        var toggle = new ToggleSwitch
        {
            IsChecked = GetBool(key),
            OffContent = "",
            OnContent = "",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        toggle.IsCheckedChanged += async (_, _) =>
            await MergeAsync(key, JsonSerializer.SerializeToElement(toggle.IsChecked == true));
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private Grid BuildSlider(JsonElement item)
    {
        var key = Key(item);
        var min = item.TryGetProperty("min", out var mn) ? mn.GetDouble() : 0;
        var max = item.TryGetProperty("max", out var mx) ? mx.GetDouble() : 1;
        var step = item.TryGetProperty("step", out var st) ? st.GetDouble() : 0.05;

        var row = NewRow("*,Auto,Auto");
        row.Children.Add(new TextBlock
        {
            Text = I18n.T(ItemTitle(item)),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#e8e8e8")),
        });
        var valueLabel = new TextBlock
        {
            Text = GetDouble(key, (min + max) / 2).ToString("0.00"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
            Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = GetDouble(key, (min + max) / 2),
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += async (_, e) =>
        {
            var snapped = Math.Round((min + Math.Round((e.NewValue - min) / step) * step), 4);
            valueLabel.Text = snapped.ToString("0.00");
            // 拖动去抖：吸附值未变化不写回（写回→真源同步→引擎热刷新，代价高）
            if (Math.Abs(snapped - GetDouble(key, double.NaN)) > 1e-9)
            {
                await MergeAsync(key, JsonSerializer.SerializeToElement(snapped));
            }
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueLabel, 2);
        row.Children.Add(slider);
        row.Children.Add(valueLabel);
        return row;
    }

    private Grid BuildChoice(JsonElement item)
    {
        var key = Key(item);
        var row = NewRow("*", "Auto");
        row.Children.Add(new TextBlock
        {
            Text = I18n.T(ItemTitle(item)),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#e8e8e8")),
        });
        var box = new ComboBox { MinWidth = 190 };
        if (item.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in choices.EnumerateArray())
            {
                box.Items.Add(c.GetString() ?? "");
            }
        }
        var current = GetString(key) ?? (box.Items.Count > 0 ? box.Items[0]?.ToString() : null);
        var idx = current is null ? -1 : box.Items.IndexOf(current);
        box.SelectedIndex = idx >= 0 ? idx : 0;
        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedItem is string s && s.Length > 0)
            {
                await MergeAsync(key, JsonSerializer.SerializeToElement(s));
            }
        };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

    private Button BuildAction(string action, string label)
    {
        var btn = DialogButton(label);
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            var progress = new Progress<string>(SetProgress);
            _actionProgress.IsVisible = true;
            try
            {
                var result = await Task.Run(() => _contributor.RunActionAsync(action, argumentsJson: null,
                    progress: progress));
                SetStatus(result.Message, ok: result.Success);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                _actionProgress.IsVisible = false;
                btn.IsEnabled = true;
            }
        };
        return btn;
    }

    // ── 词典编辑区 v2（T5.2：添加/搜索/类别过滤/启停/别名/删除 + CSV 导入导出） ──

    private StackPanel BuildDictPanel()
    {
        var panel = new StackPanel { Spacing = 8 };

        // 添加行：类别（可自由输入，插件侧归一化）+ 词条 + 添加
        var categoryBox = new ComboBox { MinWidth = 110, IsEditable = true };
        foreach (var c in TermCategories)
        {
            categoryBox.Items.Add(c);
        }
        categoryBox.SelectedIndex = 1;   // ORG

        var addBox = new TextBox
        {
            Watermark = I18n.T("ui.plugins.privacy.dict.watermark"),
            MinWidth = 200,
        };
        var addBtn = DialogButton(I18n.T("ui.plugins.privacy.dict.add"));
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addRow.Children.Add(categoryBox);
        addRow.Children.Add(addBox);
        addRow.Children.Add(addBtn);
        panel.Children.Add(addRow);

        // 过滤行：搜索 + 类别过滤
        var searchBox = new TextBox { Watermark = I18n.T("ui.plugins.privacy.dict.search"), MinWidth = 160 };
        var filterBox = new ComboBox { MinWidth = 110 };
        filterBox.Items.Add(I18n.T("ui.plugins.privacy.dict.filter.all"));
        foreach (var c in TermCategories)
        {
            filterBox.Items.Add(c);
        }
        filterBox.SelectedIndex = 0;
        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filterRow.Children.Add(searchBox);
        filterRow.Children.Add(filterBox);
        panel.Children.Add(filterRow);

        var termList = new ListBox { MinHeight = 140, MaxHeight = 220 };
        panel.Children.Add(termList);

        // 选中操作行：启停切换 / 删除所选 / 别名添加
        var toggleBtn = DialogButton(I18n.T("ui.plugins.privacy.dict.toggle"));
        var removeBtn = DialogButton(I18n.T("ui.plugins.privacy.dict.remove"));
        var aliasBox = new TextBox { Watermark = I18n.T("ui.plugins.privacy.dict.alias.watermark"), MinWidth = 140 };
        var aliasBtn = DialogButton(I18n.T("ui.plugins.privacy.dict.alias.add"));
        var opRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        opRow.Children.Add(toggleBtn);
        opRow.Children.Add(removeBtn);
        opRow.Children.Add(aliasBox);
        opRow.Children.Add(aliasBtn);
        panel.Children.Add(opRow);

        // CSV 行
        var csvImportBtn = DialogButton(I18n.T("ui.plugins.privacy.csv.import"));
        var csvExportBtn = DialogButton(I18n.T("ui.plugins.privacy.csv.export"));
        var csvRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        csvRow.Children.Add(csvImportBtn);
        csvRow.Children.Add(csvExportBtn);
        panel.Children.Add(csvRow);

        TermRow? SelectedTerm() =>
            termList.SelectedIndex is int i && i >= 0 && i < _view.Count ? _view[i] : null;

        addBtn.Click += async (_, _) =>
        {
            var term = addBox.Text?.Trim();
            var category = (categoryBox.Text ?? categoryBox.SelectedItem as string ?? "").Trim();
            if (string.IsNullOrEmpty(term) || category.Length == 0)
            {
                return;
            }
            addBtn.IsEnabled = false;
            try
            {
                var args = JsonSerializer.Serialize(new { category, term, aliases = Array.Empty<string>(), note = "" });
                var result = await Task.Run(() => _contributor.RunActionAsync("add-term", args));
                SetStatus(result.Message, ok: result.Success);
                addBox.Text = "";
                await RefreshTermsAsync(termList);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                addBtn.IsEnabled = true;
            }
        };

        searchBox.TextChanged += (_, _) =>
        {
            _termFilter = searchBox.Text?.Trim() ?? "";
            _ = ApplyFilterAsync(termList);
        };
        filterBox.SelectionChanged += (_, _) =>
        {
            _termCategoryFilter = filterBox.SelectedIndex is int fi && fi > 0 ? filterBox.Items[fi]?.ToString() ?? "" : "";
            _ = ApplyFilterAsync(termList);
        };

        toggleBtn.Click += async (_, _) =>
        {
            var t = SelectedTerm();
            if (t is null)
            {
                return;
            }
            toggleBtn.IsEnabled = false;
            try
            {
                var args = JsonSerializer.Serialize(new { id = t.Id, enabled = !t.Enabled });
                var result = await Task.Run(() => _contributor.RunActionAsync("set-term-enabled", args));
                SetStatus(result.Message, ok: result.Success);
                await RefreshTermsAsync(termList);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                toggleBtn.IsEnabled = true;
            }
        };

        removeBtn.Click += async (_, _) =>
        {
            var t = SelectedTerm();
            if (t is null)
            {
                return;
            }
            removeBtn.IsEnabled = false;
            try
            {
                var args = JsonSerializer.Serialize(new { category = t.Category, term = t.Term });
                var result = await Task.Run(() => _contributor.RunActionAsync("remove-term", args));
                SetStatus(result.Message, ok: result.Success);
                await RefreshTermsAsync(termList);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                removeBtn.IsEnabled = true;
            }
        };

        aliasBtn.Click += async (_, _) =>
        {
            var t = SelectedTerm();
            var alias = aliasBox.Text?.Trim();
            if (t is null || string.IsNullOrEmpty(alias))
            {
                return;
            }
            aliasBtn.IsEnabled = false;
            try
            {
                var args = JsonSerializer.Serialize(new { id = t.Id, alias });
                var result = await Task.Run(() => _contributor.RunActionAsync("add-alias", args));
                SetStatus(result.Message, ok: result.Success);
                aliasBox.Text = "";
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                aliasBtn.IsEnabled = true;
            }
        };

        csvImportBtn.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            });
            if (files.Count == 0)
            {
                return;
            }
            csvImportBtn.IsEnabled = false;
            try
            {
                var csv = await File.ReadAllTextAsync(files[0].Path.LocalPath);
                var args = JsonSerializer.Serialize(new { csv });
                var result = await Task.Run(() => _contributor.RunActionAsync("import-csv", args));
                if (result.Success)
                {
                    using var doc = JsonDocument.Parse(result.Message);
                    var imported = doc.RootElement.GetProperty("imported").GetInt32();
                    var skipped = doc.RootElement.GetProperty("skipped").GetInt32();
                    SetStatus(I18n.T("ui.plugins.privacy.csv.result", imported, skipped), ok: true);
                }
                else
                {
                    SetStatus(result.Message, ok: false);
                }
                await RefreshTermsAsync(termList);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                csvImportBtn.IsEnabled = true;
            }
        };

        csvExportBtn.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                DefaultExtension = "csv",
                SuggestedFileName = "privacy-dictionary.csv",
            });
            if (file is null)
            {
                return;
            }
            csvExportBtn.IsEnabled = false;
            try
            {
                var result = await Task.Run(() => _contributor.RunActionAsync("export-csv"));
                if (result.Success)
                {
                    await File.WriteAllTextAsync(file.Path.LocalPath, result.Message);
                    SetStatus(I18n.T("ui.plugins.privacy.csv.exported", file.Name), ok: true);
                }
                else
                {
                    SetStatus(result.Message, ok: false);
                }
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                csvExportBtn.IsEnabled = true;
            }
        };

        // 首次填充词条列表
        _ = RefreshTermsAsync(termList);
        return panel;
    }

    // ── 词典/白名单数据刷新与过滤 ────────────────────────────────────────

    private async Task RefreshTermsAsync(ListBox list)
    {
        try
        {
            var result = await Task.Run(() => _contributor.RunActionAsync("list-terms"));
            if (!result.Success)
            {
                SetStatus($"list-terms: {result.Message}", ok: false);
                return;
            }
            _terms.Clear();
            using var doc = JsonDocument.Parse(result.Message);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                _terms.Add(new TermRow(
                    t.GetProperty("id").GetInt64(),
                    t.GetProperty("category").GetString() ?? "",
                    t.GetProperty("term").GetString() ?? "",
                    t.GetProperty("enabled").GetBoolean(),
                    t.GetProperty("source").GetString() ?? ""));
            }
            await ApplyFilterAsync(list);
        }
        catch
        {
            // 词典读取失败保持空列表（状态栏已有操作反馈通道）
        }
    }

    private Task ApplyFilterAsync(ListBox list)
    {
        var view = _terms.Where(t =>
            (_termCategoryFilter.Length == 0 || t.Category.Equals(_termCategoryFilter, StringComparison.OrdinalIgnoreCase))
            && (_termFilter.Length == 0 || t.Term.Contains(_termFilter, StringComparison.OrdinalIgnoreCase)));
        _view = view.ToList();
        list.Items.Clear();
        foreach (var t in _view)
        {
            var prefix = t.Enabled ? "" : I18n.T("ui.plugins.privacy.dict.disabled");
            var src = t.Source.Equals("auto", StringComparison.OrdinalIgnoreCase) ? " 🤖" : "";
            list.Items.Add($"{prefix}[{t.Category}] {t.Term}{src}");
        }
        return Task.CompletedTask;
    }

    private async Task RefreshWhitelistAsync(ListBox list)
    {
        try
        {
            var result = await Task.Run(() => _contributor.RunActionAsync("list-whitelist"));
            if (!result.Success)
            {
                SetStatus($"list-whitelist: {result.Message}", ok: false);
                return;
            }
            list.Items.Clear();
            _whitelistIds.Clear();
            using var doc = JsonDocument.Parse(result.Message);
            foreach (var w in doc.RootElement.EnumerateArray())
            {
                var id = w.GetProperty("id").GetInt64();
                var pattern = w.GetProperty("pattern").GetString() ?? "";
                _whitelistIds.Add(id);
                list.Items.Add($"#{id} {pattern}");
            }
        }
        catch
        {
            // 白名单读取失败保持空列表
        }
    }

    // ── 小工具 ───────────────────────────────────────────────────────────

    private void SetStatus(string text, bool ok = true)
    {
        _statusText.Text = text;
        _statusText.Foreground = new SolidColorBrush(Color.Parse(ok ? "#7bd97b" : "#f0b0b0"));
    }

    private static Button DialogButton(string content) => new()
    {
        Content = content,
        Background = new SolidColorBrush(Color.Parse("#333")),
        Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")),
        Padding = new Thickness(12, 6),
        CornerRadius = new Avalonia.CornerRadius(6),
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static Dictionary<string, JsonElement> ParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, JsonElement>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                result[p.Name] = p.Value.Clone();
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    // ── 白名单标签页（T5.2：豁免规则添加/移除） ─────────────────────────

    private StackPanel BuildWhitelistPanel()
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = I18n.T("ui.plugins.privacy.whitelist.hint"),
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var wlBox = new TextBox { Watermark = I18n.T("ui.plugins.privacy.whitelist.watermark"), MinWidth = 220 };
        var wlAddBtn = DialogButton(I18n.T("ui.plugins.privacy.dict.add"));
        var wlRemoveBtn = DialogButton(I18n.T("ui.plugins.privacy.dict.remove"));
        var wlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        wlRow.Children.Add(wlBox);
        wlRow.Children.Add(wlAddBtn);
        wlRow.Children.Add(wlRemoveBtn);
        panel.Children.Add(wlRow);

        var wlList = new ListBox { MinHeight = 120, MaxHeight = 260 };
        panel.Children.Add(wlList);

        wlAddBtn.Click += async (_, _) =>
        {
            var pattern = wlBox.Text?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                return;
            }
            wlAddBtn.IsEnabled = false;
            try
            {
                var args = JsonSerializer.Serialize(new { pattern, kind = "literal" });
                var result = await Task.Run(() => _contributor.RunActionAsync("add-whitelist", args));
                SetStatus(result.Message, ok: result.Success);
                wlBox.Text = "";
                await RefreshWhitelistAsync(wlList);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                wlAddBtn.IsEnabled = true;
            }
        };

        wlRemoveBtn.Click += async (_, _) =>
        {
            if (wlList.SelectedIndex is int wi && wi < _whitelistIds.Count)
            {
                var wid = _whitelistIds[wi];
                wlRemoveBtn.IsEnabled = false;
                try
                {
                    var args = JsonSerializer.Serialize(new { id = wid });
                    var result = await Task.Run(() => _contributor.RunActionAsync("remove-whitelist", args));
                    SetStatus(result.Message, ok: result.Success);
                    await RefreshWhitelistAsync(wlList);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message, ok: false);
                }
                finally
                {
                    wlRemoveBtn.IsEnabled = true;
                }
            }
        };

        // 首次填充白名单
        _ = RefreshWhitelistAsync(wlList);
        return panel;
    }

    private static string Key(JsonElement item) =>
        item.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";

    private static string ItemTitle(JsonElement item) =>
        item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";

    private static string Action(JsonElement item) =>
        item.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

    private static Grid NewRow(params string[] cols)
    {
        return new Grid
        {
            ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions(string.Join(",", cols)),
            Margin = new Thickness(0, 4),
            ColumnSpacing = 8,
        };
    }

    private bool GetBool(string key) =>
        Current(key) is { } v && v.ValueKind == JsonValueKind.True;

    private double GetDouble(string key, double fallback) =>
        Current(key) is { } v && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private string? GetString(string key) =>
        Current(key) is { } v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private JsonElement? Current(string key)
    {
        var settings = ParseJson(_contributor.GetSettingsJson());
        return settings.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>合并单键后整体写回。同步链含 SQLite 写与引擎热刷新——必须在 UI 线程外执行（卡死教训）；
    /// _writeGate 串行化避免连点开关导致并发写。</summary>
    private async Task MergeAsync(string key, JsonElement value)
    {
        if (key.Length == 0)
        {
            return;
        }
        var settings = ParseJson(_contributor.GetSettingsJson());
        settings[key] = value.Clone();
        var json = JsonSerializer.Serialize(settings);
        await _writeGate.WaitAsync();
        try
        {
            await Task.Run(() => _contributor.SetSettingsJson(json));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, ok: false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private List<TermRow> _view = [];

    private void SetProgress(string text)
    {
        _statusText.Text = text;
        _statusText.Foreground = new SolidColorBrush(Color.Parse("#9a9a9a"));
    }
}
