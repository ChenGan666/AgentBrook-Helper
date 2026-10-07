using AgentBrook.Helper;
using AgentBrook.Agent;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;

namespace AgentBrook.Helper;

/// <summary>
/// 对话窗体（默认隐藏，点击气泡打开；点关闭=隐藏）：
/// 纯渲染层 —— 聊天流（含思考动态指示、Token 用量）、状态条与 HITL 卡片；
/// 回合执行与状态由 AssistantController 驱动。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AssistantController _controller;
    private StackPanel? _chatPanel;
    private ScrollViewer? _chatScroll;
    private TextBlock? _statusText;
    private Border? _statusPill;
    private TextBox? _inputBox;
    private Button? _sendBtn;
    private TextBlock? _tokenText;

    private (long In, long Out, long Total)? _lastTurnUsage;
    private long _cumTokens;

    // 执行过程折叠组：同一回合的工具调用/结果/自动批准收进一组，默认收起
    private Expander? _execGroup;
    private TextBlock? _execHeader;
    private StackPanel? _execContent;
    private int _execCount;

    public MainWindow()
    {
        InitializeComponent();
        _controller = App.Controller;

        _chatPanel = this.FindControl<StackPanel>("ChatPanel");
        _chatScroll = this.FindControl<ScrollViewer>("ChatScroll");
        _statusText = this.FindControl<TextBlock>("StatusText");
        _statusPill = this.FindControl<Border>("StatusPill");
        _inputBox = this.FindControl<TextBox>("InputBox");
        _sendBtn = this.FindControl<Button>("SendBtn");
        _tokenText = this.FindControl<TextBlock>("TokenText");

        var newBtn = this.FindControl<Button>("NewBtn");
        var plusBtn = this.FindControl<Button>("PlusBtn");
        var fullAccessBtn = this.FindControl<Button>("FullAccessBtn");
        var modelChip = this.FindControl<Button>("ModelChip");
        if (newBtn is not null)
        {
            newBtn.Click += async (_, _) =>
            {
                await _controller.NewSessionAsync();   // 旧会话归档保留，开启新会话（清屏由 __clear__ 事件处理）
                UpdateSessionTitle();
            };
        }
        var sessionBtn = this.FindControl<Button>("SessionBtn");
        if (sessionBtn is not null)
        {
            sessionBtn.Click += (_, _) => ShowSessionMenu(sessionBtn);
        }
        _controller.SessionsChanged += () => Dispatcher.UIThread.Post(UpdateSessionTitle);
        if (_sendBtn is not null)
        {
            // 双态按钮：空闲=发送，忙碌=停止当前任务
            _sendBtn.Click += async (_, _) =>
            {
                if (_controller.Busy)
                {
                    StopTurn();
                }
                else
                {
                    await SendInputAsync();
                }
            };
        }
        if (plusBtn is not null)
        {
            plusBtn.Click += async (_, _) => await PickFilesAsync();
        }
        if (fullAccessBtn is not null)
        {
            fullAccessBtn.Click += (_, _) =>
            {
                var panel = new StackPanel { Margin = new Thickness(4) };
                void AddItem(string label, bool checkedState, Action onPick)
                {
                    var b = new Button
                    {
                        Classes = { "menuItem" },
                        Content = checkedState ? "✓  " + label : "      " + label,
                        // 选中项高亮蓝色，未选中灰色，一眼可辨当前模式
                        Foreground = new SolidColorBrush(Color.Parse(checkedState ? "#4a9eff" : "#c8c8c8")),
                    };
                    b.Click += (_, _) =>
                    {
                        onPick();
                        _fullPopup?.IsOpen = false;
                    };
                    panel.Children.Add(b);
                }
                AddItem(I18n.T("每次询问（更安全）"), !_controller.FullAccess,
                    () => { _controller.FullAccess = false; UpdateFullAccessVisual(); });
                AddItem(I18n.T("完全访问（自动批准，重启复位）"), _controller.FullAccess,
                    () => { _controller.FullAccess = true; UpdateFullAccessVisual(); });
                ShowDropdown(fullAccessBtn, panel, ref _fullPopup);
            };
        }
        if (modelChip is not null)
        {
            modelChip.Click += (_, _) =>
            {
                var panel = new StackPanel { Margin = new Thickness(4) };
                var providers = _controller.Providers;
                var currentModel = _controller.Ready ? _controller.Agent!.CurrentModel : "";
                var currentProvider = _controller.CurrentProvider;
                if (providers.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = I18n.T("（启动中…）"),
                        Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
                        FontSize = 13.5,
                        Margin = new Thickness(12, 8),
                    });
                }
                else
                {
                    foreach (var provider in providers)
                    {
                        if (provider.Models.Count == 0)
                        {
                            continue;
                        }
                        // 组头：供应商名
                        panel.Children.Add(new TextBlock
                        {
                            Text = provider.Name,
                            FontSize = 11.5,
                            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
                            Margin = new Thickness(12, 8, 12, 2),
                        });
                        foreach (var m in provider.Models)
                        {
                            var isActive = m == currentModel && provider.Name == currentProvider;
                            var model = m;
                            var b = new Button
                            {
                                Classes = { "menuItem" },
                                Content = isActive ? "✓  " + model : "      " + model,
                                Foreground = new SolidColorBrush(Color.Parse(isActive ? "#4a9eff" : "#c8c8c8")),
                            };
                            b.Click += (_, _) =>
                            {
                                if (provider.Name != currentProvider)
                                {
                                    _ = SafeSwitchProviderAsync(provider.Name);
                                }
                                else
                                {
                                    _controller.Agent!.SetModel(model);
                                }
                                UpdateModelChip();
                                _modelPopup?.IsOpen = false;
                            };
                            panel.Children.Add(b);
                        }
                    }
                    panel.Children.Add(new Separator
                    {
                        Margin = new Thickness(8, 6),
                        Background = new SolidColorBrush(Color.Parse("#3f3f3f")),
                    });
                    var manage = new Button { Classes = { "menuItem" }, Content = I18n.T("管理模型…") };
                    manage.Click += (_, _) =>
                    {
                        _modelPopup?.IsOpen = false;
                        new ModelSettingsWindow().Show();
                    };
                    panel.Children.Add(manage);
                }
                ShowDropdown(modelChip, panel, ref _modelPopup);
            };
        }

        // 事件订阅：控制器 → UI（缺失会导致状态/消息/审批全部失联）
        _controller.EventRaised -= OnControllerEvent;
        _controller.EventRaised += OnControllerEvent;
        _controller.StateChanged -= OnStateChanged;
        _controller.StateChanged += OnStateChanged;
        _controller.QueueChanged += n =>
        {
            _queuedHintCount = n;
            Dispatcher.UIThread.Post(UpdateQueueBar);
        };
        _controller.UserSaid += text =>
        {
            _lastRenderedAnswer = null;   // 新回合：允许渲染相同回答
            _streamViewer = null;         // 新回合：流式气泡重建（异常终止残留防御）
            var atts = _bubbleAttachments;
            _bubbleAttachments = new List<SentAttachment>();
            Dispatcher.UIThread.Post(() => AddUserBubble(text, atts));
        };
        _controller.ToolFeedback += text => Dispatcher.UIThread.Post(() =>
            AppendToolLine("✔ " + (text.Length > 160 ? text[..160] + "…" : text)));

        // 启动过程（连接 MCP/恢复会话/就绪）改在顶栏状态胶囊展示，不进对话流
        _controller.StartupStatus += line => Dispatcher.UIThread.Post(() => ShowStartupStatus(line));
        _controller.ReadyChanged += () => Dispatcher.UIThread.Post(UpdateSessionTitle);   // 就绪后标题才能取到真实值

        I18n.LanguageChanged += () => Dispatcher.UIThread.Post(() =>
        {
            ApplyStaticTexts();
            SetStatus(_lastState, _lastDetail, _lastWithModel);
        });

        // 窗口位置记忆：启动恢复上次位置/尺寸（带可见性校验），移动/缩放后防抖保存
        RestoreWindowBounds();
        PositionChanged += (_, _) => ScheduleSaveBounds();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WidthProperty || e.Property == HeightProperty)
            {
                ScheduleSaveBounds();
            }
        };

        Opened += (_, _) =>
        {
            ApplyStaticTexts();
            SetStatus(BrookRunState.Idle, null, withModel: true);
            UpdateModelChip();
            UpdateFullAccessVisual();
            ApplyIdentityUi();
            UpdateSessionTitle();
            if (_chatPanel!.Children.Count == 0)
            {
                var name = AssistantIdentity.Current;
                AddAssistantBubble(I18n.T("你好，我是 {0}。\n点屏幕上的悬浮气泡打开这里，直接打字聊天。命令执行前我会先征求你的同意。", name));
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>按回合状态切换发送按钮形态：空闲=发送↑，忙碌=停止■。</summary>
    private void UpdateSendStopVisual(BrookRunState state)
    {
        var busy = state is BrookRunState.Thinking or BrookRunState.CallingTool
            or BrookRunState.AwaitingApproval or BrookRunState.AwaitingUserAnswer;
        var sendIcon = this.FindControl<PathIcon>("SendIcon");
        var stopIcon = this.FindControl<PathIcon>("StopIcon");
        if (sendIcon is not null) sendIcon.IsVisible = !busy;
        if (stopIcon is not null) stopIcon.IsVisible = busy;
        var sendBtn = this.FindControl<Button>("SendBtn");
        if (sendBtn is not null)
        {
            ToolTip.SetTip(sendBtn, busy ? I18n.T("停止当前任务") : I18n.T("发送"));
        }
    }

    /// <summary>打断当前回合（停止模型流，已完成部分保留）。</summary>
    private void StopTurn()
    {
        _controller.CancelTurn();
    }

    private Popup? _sessionPopup;

    /// <summary>会话切换下拉：最近会话（当前✓蓝高亮）＋ 开启新对话 ＋ 沉淀当前会话为技能。</summary>
    private void ShowSessionMenu(Button anchor)
    {
        var panel = new StackPanel { Margin = new Thickness(4), MinWidth = 260 };
        if (_controller.Busy)
        {
            panel.Children.Add(new TextBlock
            {
                Text = I18n.T("任务进行中，结束后可切换会话"),
                Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
                FontSize = 13,
                Margin = new Thickness(12, 8),
            });
        }
        else
        {
            var listPanel = new StackPanel { Spacing = 0 };
            var sessions = _controller.Sessions;
            var currentId = _controller.Ready ? _controller.Agent!.CurrentSessionId : "";
            foreach (var m in sessions.Take(30))
            {
                var isCurrent = m.Id == currentId;
                var meta = m;
                var label = meta.Title.Length > 20 ? meta.Title[..20] + "…" : meta.Title;
                var when = meta.UpdatedAt switch
                {
                    var d when (DateTime.Now - d).TotalDays >= 1 => $"{(int)(DateTime.Now - d).TotalDays} 天前",
                    var d when (DateTime.Now - d).TotalHours >= 1 => $"{(int)(DateTime.Now - d).TotalHours} 小时前",
                    _ => "刚刚",
                };
                var b = new Button
                {
                    Classes = { "menuItem" },
                    Content = (isCurrent ? "✓  " : "      ") + label + "   ",
                    Foreground = new SolidColorBrush(Color.Parse(isCurrent ? "#4a9eff" : "#c8c8c8")),
                };
                b.Click += async (_, _) =>
                {
                    _sessionPopup?.IsOpen = false;
                    var ok = await _controller.SwitchSessionAsync(meta.Id);
                    if (ok)
                    {
                        ClearChatForSessionSwitch(meta.Title);
                    }
                };
                // 行内布局：标题 / 时间 / 删除图标
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                grid.Children.Add(b);
                var timeText = new TextBlock
                {
                    Text = when,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#7a7a7a")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                Grid.SetColumn(timeText, 1);
                grid.Children.Add(timeText);

                // 删除：首次点击进入确认态（图标变红），再次点击执行；重开菜单自动复位
                var delBtn = new Button
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(4, 2),
                    MinWidth = 30,
                    MinHeight = 30,
                    CornerRadius = new CornerRadius(6),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),   // 让开滚动条，热区更大
                    Tag = "idle",
                    Content = new PathIcon
                    {
                        Data = (StreamGeometry)Application.Current!.Resources["Icon.Close"]!,
                        Width = 14, Height = 14,
                        Foreground = new SolidColorBrush(Color.Parse("#7a7a7a")),
                    },
                };
                ToolTip.SetTip(delBtn, I18n.T("删除此会话"));
                Grid.SetColumn(delBtn, 2);
                grid.Children.Add(delBtn);
                delBtn.Click += async (_, _) =>
                {
                    if ((string)delBtn.Tag! != "armed")
                    {
                        delBtn.Tag = "armed";
                        ((PathIcon)delBtn.Content!).Foreground = new SolidColorBrush(Color.Parse("#ff6b6b"));
                        ToolTip.SetTip(delBtn, I18n.T("再点一次确认删除"));
                        return;
                    }
                    _sessionPopup?.IsOpen = false;
                    var wasCurrent = meta.Id == _controller.Agent!.CurrentSessionId;
                    await _controller.DeleteSessionAsync(meta.Id);
                    if (wasCurrent)
                    {
                        ClearChatForSessionSwitch(_controller.CurrentSessionTitle);   // 删的是当前会话：回放新当前会话
                    }
                };
                listPanel.Children.Add(new Border { Child = grid });
            }
            if (sessions.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = I18n.T("（暂无历史会话）"),
                    Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
                    FontSize = 13,
                    Margin = new Thickness(12, 8),
                });
            }
            // 会话可能很多：限制高度，超出滚动
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel,
            });
            panel.Children.Add(new Separator { Margin = new Thickness(8, 6), Background = new SolidColorBrush(Color.Parse("#3f3f3f")) });

            var newItem = new Button
            {
                Classes = { "menuItem" },
                Content = "＋  " + I18n.T("开启新对话"),
                Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")),
            };
            newItem.Click += async (_, _) =>
            {
                _sessionPopup?.IsOpen = false;
                await _controller.NewSessionAsync();   // __clear__ 事件负责清屏
                UpdateSessionTitle();
            };
            panel.Children.Add(newItem);

            var distillItem = new Button
            {
                Classes = { "menuItem" },
                Padding = new Thickness(10, 6),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 },
                Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")),
            };
            ((StackPanel)distillItem.Content!).Children.Add(new PathIcon
            {
                Data = (StreamGeometry)Application.Current!.Resources["Icon.Save"]!,
                Width = 14, Height = 14,
                Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")),
            });
            ((StackPanel)distillItem.Content!).Children.Add(new TextBlock
            {
                Text = I18n.T("把当前会话成果沉淀为技能"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            distillItem.Click += (_, _) =>
            {
                _sessionPopup?.IsOpen = false;
                var (name, error) = _controller.DistillSessionToSkill();
                AddAssistantBubble(error is null
                    ? I18n.T("✔ 当前会话成果已沉淀为技能「{0}」。其他会话中可直接让我 load_skill 调用这些成果。", name)
                    : I18n.T("沉淀失败：{0}", error));
            };
            panel.Children.Add(distillItem);
        }
        ShowDropdown(anchor, panel, ref _sessionPopup);
    }

    /// <summary>切换/新建会话后清空聊天区并回放历史（历史在会话存档中，不丢）。</summary>
    private void ClearChatForSessionSwitch(string title)
    {
        _chatPanel!.Children.Clear();
        _lastRenderedAnswer = null;
        _streamViewer = null;
        UpdateSessionTitle();

        // 回放历史消息（最近 40 条，assistant 走 Markdown 渲染）
        var replayed = 0;
        foreach (var (role, text) in _controller.GetTranscript())
        {
            if (role == "user")
            {
                AddUserBubble(text, new List<SentAttachment>());
            }
            else
            {
                Control render;
                try { render = MarkdownView.Render(text); }
                catch { render = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#e6e6e6")) }; }
                var border = new Border { Classes = { "assistantBubble" }, Child = render };
                _chatPanel!.Children.Add(border);
            }
            replayed++;
        }

        if (replayed > 0)
        {
            AddAssistantBubble(I18n.T("已切换到会话「{0}」，以上是历史消息。上下文与摘要已载入，直接继续即可。", title));
        }
        else
        {
            AddAssistantBubble(I18n.T("已切换到会话「{0}」。该会话的上下文与摘要已载入，直接继续即可。", title));
        }
        ScrollToEnd();
    }

    /// <summary>刷新顶栏会话标题。</summary>
    private void UpdateSessionTitle()
    {
        var t = this.FindControl<TextBlock>("SessionTitleText");
        if (t is null) return;
        var title = _controller.CurrentSessionTitle;
        if (title.Length > 14) title = title[..14] + "…";
        t.Text = title.Length > 0 ? "· " + title : "";
    }

    /// <summary>排队提示条。</summary>
    private void UpdateQueueBar()
    {
        var bar = this.FindControl<Border>("QueueBar");
        var text = this.FindControl<TextBlock>("QueueText");
        if (bar is null || text is null) return;
        bar.IsVisible = _queuedHintCount > 0;
        text.Text = I18n.T("已排队 {0} 条，回合结束后自动发送", _queuedHintCount);
    }
    private int _queuedHintCount;

    /// <summary>把个性化设置（名字）应用到窗口标题与顶栏。</summary>
    public void ApplyStaticTexts()
    {
        var input = this.FindControl<TextBox>("InputBox");
        if (input is not null)
        {
            input.Watermark = I18n.T("输入消息，回车发送");
        }
        var fullText = this.FindControl<TextBlock>("FullAccessText");
        if (fullText is not null)
        {
            fullText.Text = _controller.FullAccess ? I18n.T("完全访问") : I18n.T("每次询问");
        }
        var send = this.FindControl<Button>("SendBtn");
        if (send is not null)
        {
            ToolTip.SetTip(send, I18n.T("发送"));
        }
        var plus = this.FindControl<Button>("PlusBtn");
        if (plus is not null)
        {
            ToolTip.SetTip(plus, I18n.T("添加附件（或直接粘贴图片）"));
        }
        var modelChip = this.FindControl<Button>("ModelChip");
        if (modelChip is not null)
        {
            ToolTip.SetTip(modelChip, I18n.T("切换模型"));
        }
        var fullAccessBtn = this.FindControl<Button>("FullAccessBtn");
        if (fullAccessBtn is not null)
        {
            ToolTip.SetTip(fullAccessBtn, _controller.FullAccess
                ? I18n.T("完全访问：操作自动批准（重启复位）")
                : I18n.T("每次询问：执行前征求同意"));
        }
        var newBtn = this.FindControl<Button>("NewBtn");
        if (newBtn is not null)
        {
            ToolTip.SetTip(newBtn, I18n.T("开启新对话"));
        }
    }

    /// <summary>把个性化设置（名字）应用到窗口标题与顶栏。</summary>
    public void ApplyIdentityUi()
    {
        var name = AssistantIdentity.Current;
        Title = I18n.T("{0} 助手", name);
        var t = this.FindControl<TextBlock>("AgentNameText");
        if (t is not null)
        {
            t.Text = name;
        }
    }

    private async Task SafeSwitchProviderAsync(string providerName)
    {
        try
        {
            await _controller.SetProviderAsync(providerName);
            UpdateModelChip();
        }
        catch (Exception ex)
        {
            AddErrorBubble(I18n.T("切换供应商失败：{0}", ex.Message));
        }
    }

    /// <summary>测试入口：模拟粘贴（读取当前剪贴板为附件）。</summary>
    public async Task DebugPasteAsync() => await TryAttachFromClipboardAsync();

    /// <summary>供气泡打开对话窗。</summary>
    public void ShowFromBubble()
    {
        Show();
        Activate();
        _inputBox?.Focus();
    }

    private DispatcherTimer? _boundsSaveTimer;

    /// <summary>恢复上次关闭时的窗口位置与尺寸；首启或保存的位置不可见时保持 XAML 默认。</summary>
    private void RestoreWindowBounds()
    {
        var saved = UiPrefs.Load<ConvWinBounds>("conv-win.json");
        if (saved is null || saved.Width < MinWidth || saved.Height < MinHeight)
        {
            return;
        }
        Width = saved.Width;
        Height = saved.Height;
        var target = new PixelPoint(saved.X, saved.Y);
        // 可见性保护：标题栏区域必须落在任一显示器工作区内（如外接屏被拔掉时回落默认位置）
        if (Screens.All.Any(s => s.WorkingArea.Intersects(new PixelRect(target, new PixelSize(80, 40)))))
        {
            Position = target;
        }
    }

    /// <summary>移动/缩放后防抖保存（拖动过程中 PositionChanged 高频触发，避免每次写盘）。</summary>
    private void ScheduleSaveBounds()
    {
        if (_boundsSaveTimer is null)
        {
            _boundsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _boundsSaveTimer.Tick += (_, _) =>
            {
                _boundsSaveTimer!.Stop();
                SaveBoundsNow();
            };
        }
        _boundsSaveTimer.Stop();
        _boundsSaveTimer.Start();
    }

    /// <summary>把当前窗口位置/尺寸写入偏好（仅 Normal 状态；最大化时的坐标无记忆价值）。</summary>
    private void SaveBoundsNow()
    {
        if (WindowState == WindowState.Normal)
        {
            UiPrefs.Save("conv-win.json", new ConvWinBounds(Position.X, Position.Y, Width, Height));
        }
    }

    // ───────────────────────── 控制器事件 → 渲染 ─────────────────────────

    private void OnControllerEvent(BrookEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                RenderControllerEvent(e);
            }
            catch (Exception ex)
            {
                AddErrorBubble(I18n.T("界面渲染异常（已拦截）：{0}", ex.Message));
            }
        });
    }

    private void RenderControllerEvent(BrookEvent e)
    {
        switch (e)
        {
            case BrookEvent.ToolStarted tool:
                StopThinkLine();
                if (tool.Name == "__clear__")
                {
                    _chatPanel!.Children.Clear();
                    _streamViewer = null;
                    _lastRenderedAnswer = null;
                    _execGroup = null;
                    _execHeader = null;
                    _execContent = null;
                    _execCount = 0;
                    AddUserBubble(I18n.T("（已开启新对话）"));
                    return;
                }
                if (tool.Name.StartsWith("log:", StringComparison.Ordinal))
                {
                    AppendToolLine(tool.Name["log:".Length..]);
                    return;
                }
                if (tool.Name != "ask_user")
                {
                    AppendToolLine($"工具 · {tool.Name} …");
                }
                break;
            case BrookEvent.TextDelta:
                UpdateStreamText();   // 流式：把控制器全量答案覆盖到当前气泡（幂等）
                break;
            case BrookEvent.Usage u:
                _lastTurnUsage = (u.TurnInput, u.TurnOutput, u.TurnTotal);
                _cumTokens = u.CumTotal;
                UpdateTokenHeader();
                break;
            case BrookEvent.Failed fail:
                AddErrorBubble(fail.Message);
                break;
            case BrookEvent.TurnCompleted:
                StopThinkLine();
                CollapseExecGroup();   // 完成后收起执行过程，保持主内容清爽
                RemoveStreamBubble();  // 流式纯文本让位给 Markdown 渲染
                ShowFinalAnswer();
                AppendUsageLine();
                break;
        }
    }

    private void OnStateChanged(BrookRunState state, string? detail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetStatus(state, detail);

            if (state == BrookRunState.Thinking)
            {
                StartThinkLine();   // 「思考 · 持续了 N 秒」实时行
            }
            else if (state != BrookRunState.CallingTool)
            {
                StopThinkLine();
            }

            var busy = state is BrookRunState.Thinking or BrookRunState.CallingTool
                or BrookRunState.AwaitingApproval or BrookRunState.AwaitingUserAnswer;
            _inputBox!.IsEnabled = true;   // 忙碌时仍可输入补充信息（自动排队）
        });
    }

    private DateTime _lastSendRequest = DateTime.MinValue;
    private BrookRunState _lastState = BrookRunState.Idle;
    private string? _lastDetail;
    private bool _lastWithModel;

    private async Task SendInputAsync()
    {
        var text = _inputBox?.Text?.Trim() ?? "";
        if (text.Length == 0 && _attachments.Count == 0)
        {
            return;
        }
        if (!_controller.Ready)
        {
            return;
        }
        // 忙碌中：输入自动排队（补充信息），回合结束后逐条发送
        if (_controller.Busy)
        {
            _inputBox!.Text = "";
            _controller.EnqueueInput(text);
            UpdateQueueBar();
            return;
        }
        var now = DateTime.Now;
        if ((now - _lastSendRequest).TotalMilliseconds < 300)
        {
            return;   // 双触发过滤：输入法/按键重复
        }
        _lastSendRequest = now;
        _inputBox!.Text = "";

        // 附件：复制进工作区并以路径引用进消息（模型可用文件工具读取）
        var attachments = _attachments.Count > 0 ? SaveAttachmentsToWorkspace() : new List<SentAttachment>();
        var sendText = text;
        if (attachments.Count > 0)
        {
            _bubbleAttachments = attachments;
            var lines = attachments.Select(a => a.IsImage
                ? $"- 图片（二进制，请勿读取其内容）：{a.RelPath}"
                : $"- 文件（可用 read_file 查看）：{a.RelPath}");
            sendText += "\n\n【用户附加了以下附件，已保存到工作区】\n" + string.Join("\n", lines) +
                "\n注意：图片是二进制文件，read_file 无法读取其内容，也不要尝试其他方式读取；如用户希望查看图片或基于图片处理，请说明当前模型不支持视觉输入。";
        }
        await _controller.SendAsync(sendText);
    }

    // ───────────────────────── HITL 卡片（由控制器在 UI 线程调用）─────────────────────────
    /// <summary>审批提要的公开入口：气泡快捷卡复用同一套「人话翻译」规则。</summary>
    public string SummarizeApprovalPublic(string tool, string? argsJson) => SummarizeApproval(tool, argsJson);

    /// <summary>把工具调用翻译成用户看得懂的一句话提要（本地规则，无需模型参与）。</summary>
    /// <summary>spawn_worker 审批提要：带能力授予时展示所授能力（用户批准的关注点）。</summary>
    private static string SpawnWorkerSummary(string? name, string? argsJson)
    {
        var baseText = string.IsNullOrEmpty(name) ? I18n.T("派出一个工作助手") : I18n.T("派出工作助手「{0}」", Cut(name));
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson ?? "{}");
            if (doc.RootElement.TryGetProperty("capabilities", out var capsEl)
                && capsEl.ValueKind == System.Text.Json.JsonValueKind.String
                && capsEl.GetString() is { Length: > 0 } capsDesc)
            {
                baseText += I18n.T("（授予：{0}）", capsDesc);
            }
        }
        catch { }
        return baseText;
    }

    private static string SummarizeApproval(string tool, string? argsJson)
    {
        var primary = FirstStringArg(argsJson);
        return tool switch
        {
            "run_command" => DescribeCommand(primary),
            "read_file" => string.IsNullOrEmpty(primary) ? I18n.T("查看工作区文件") : I18n.T("查看文件「{0}」的内容", Cut(primary)),
            "append_file" => string.IsNullOrEmpty(primary) ? I18n.T("往文件追加内容") : I18n.T("往文件「{0}」末尾追加内容", Cut(primary)),
            "write_file" => string.IsNullOrEmpty(primary) ? I18n.T("写入工作区文件") : I18n.T("写入文件「{0}」", Cut(primary)),
            "list_dir" => string.IsNullOrEmpty(primary) || primary == "." ? I18n.T("查看工作区目录") : I18n.T("查看目录「{0}」", Cut(primary)),
            "memory_read" => I18n.T("读取 {0} 的长期记忆", AssistantIdentity.Current),
            "memory_write" or "memory_append" => I18n.T("更新 {0} 的长期记忆", AssistantIdentity.Current),
            "create_skill" => string.IsNullOrEmpty(primary) ? I18n.T("创建一个新技能") : I18n.T("创建新技能「{0}」", Cut(primary)),
            "install_skill" or "skill_market" => string.IsNullOrEmpty(primary) ? I18n.T("从技能市场安装技能") : I18n.T("安装技能「{0}」", Cut(primary)),
            "load_skill" or "list_skills" => I18n.T("加载/查看可用技能"),
            "mcp_add_server" => string.IsNullOrEmpty(primary) ? I18n.T("添加 MCP 插件") : I18n.T("添加 MCP 插件「{0}」", Cut(primary)),
            "mcp_list_servers" => I18n.T("查看已安装的 MCP 插件"),
            "spawn_worker" => SpawnWorkerSummary(primary, argsJson),
            "assign_task" => I18n.T("给工作助手分派任务"),
            "send_message" or "send_to_worker" => I18n.T("给工作助手发消息"),
            "broadcast_to_workers" => I18n.T("给所有工作助手广播消息"),
            "check_messages" or "read_shared" or "start_project" or "list_workers" => I18n.T("查看协作状态"),
            "open_url" => string.IsNullOrEmpty(primary) ? I18n.T("打开网页") : I18n.T("打开网页 {0}", Cut(primary, 60)),
            "ask_user" => I18n.T("向你提问"),
            _ => string.IsNullOrEmpty(primary) ? I18n.T("调用 {0}", tool) : I18n.T("调用 {0}（{1}）", tool, Cut(primary, 40)),
        };
    }

    /// <summary>把 shell 命令翻译成人话；识别不了就显示命令本身（截断）。</summary>
    private static string DescribeCommand(string cmd)
    {
        var c = (cmd ?? "").Trim();
        if (c.Length == 0)
        {
            return I18n.T("在终端执行命令");
        }
        // 多段命令（&& / ;）：只翻译第一段，并提示后面还有步骤
        var segments = System.Text.RegularExpressions.Regex.Split(c, @"\s*(?:&&|\|\||;)\s*");
        var seg = segments.FirstOrDefault(x => x.Trim().Length > 0)?.Trim() ?? c;
        var more = seg != c;
        var parts = seg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.FirstOrDefault()?.ToLowerInvariant() ?? "";
        var arg = parts.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "";
        string? body = verb switch
        {
            "open" => I18n.T("打开 {0}", Cut(arg)),
            "ls" => I18n.T("查看目录 {0}", Cut(arg)),
            "cat" or "head" or "tail" or "less" or "more" => I18n.T("查看文件 {0}", Cut(arg)),
            "mkdir" => I18n.T("创建文件夹 {0}", Cut(arg)),
            "touch" => I18n.T("新建文件 {0}", Cut(arg)),
            "rm" => I18n.T("删除 {0}", Cut(arg)),
            "mv" => I18n.T("移动/重命名 {0}", Cut(arg)),
            "cp" => I18n.T("复制 {0}", Cut(arg)),
            "date" => I18n.T("查看当前时间"),
            "pwd" => I18n.T("查看当前目录"),
            "echo" => I18n.T("输出一段文字"),
            "curl" or "wget" => I18n.T("从网络获取 {0}", Cut(arg, 50)),
            "git" => I18n.T("执行 git {0}", parts.Skip(1).FirstOrDefault() ?? I18n.T("操作")),
            "python" or "python3" or "node" => I18n.T("运行脚本 {0}", Cut(arg)),
            "dotnet" => $"运行 dotnet {parts.Skip(1).FirstOrDefault() ?? ""}",
            "grep" or "rg" => I18n.T("搜索 {0}", Cut(arg)),
            "cd" => I18n.T("切换目录 {0}", Cut(arg)),
            "chmod" => I18n.T("修改文件权限 {0}", Cut(arg)),
            _ => null,
        };
        if (body is null)
        {
            return I18n.T("在终端执行：{0}", Cut(c, 70));
        }
        return more
            ? I18n.T("在终端开始执行：{0}（之后还有后续步骤）", body)
            : I18n.T("在终端执行：{0}", body);
    }

    /// <summary>从参数 JSON 里取第一个字符串值（通常是主参数：路径/命令/问题等）。</summary>
    private static string FirstStringArg(string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson))
        {
            return "";
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var v = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(v))
                    {
                        return v;
                    }
                }
            }
            return "";
        }
        catch
        {
            return argsJson;
        }
    }

    /// <summary>原始指令展示：解析成「参数名 = 值」逐行（自动解码 \uXXXX 转义），失败则原样显示。</summary>
    private static string FormatArgsForApproval(string argsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var sb = new System.Text.StringBuilder();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                sb.AppendLine($"{prop.Name} = {prop.Value.ToString()}");
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return argsJson;
        }
    }

    private static string Cut(string? s, int max = 50)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    public void AddApprovalCard(string tool, string? argsJson, TaskCompletionSource<ApprovalDecision> tcs)
    {
        var card = new Border { Classes = { "card" } };
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{AssistantIdentity.Current} {I18n.T("想执行一个操作，需要你批准")}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
        });
        // 提要：用户看得懂的一句人话（本地规则翻译，详见 SummarizeApproval）
        stack.Children.Add(new TextBlock
        {
            Text = SummarizeApproval(tool, argsJson),
            FontSize = 16,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
        });
        stack.Children.Add(new TextBlock
        {
            Text = I18n.T("操作类型：{0}", tool),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
        });
        // 原始指令默认折叠：想看细节的用户再展开
        if (!string.IsNullOrEmpty(argsJson))
        {
            stack.Children.Add(new Expander
            {
                Classes = { "execGroup" },
                Header = new TextBlock { Text = I18n.T("查看原始指令"), FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")) },
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#333333")),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(9),
                    Child = new SelectableTextBlock
                    {
                        Text = FormatArgsForApproval(argsJson),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new FontFamily("Menlo, monospace"),
                        Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")),
                    },
                },
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2),
            });
        }

        var reasonBox = new TextBox { Watermark = I18n.T("拒绝的话可以写理由（可选）"), FontSize = 13.5 };
        var ok = new Button { Content = I18n.T("✓ 批准"), Classes = { "actionOk" } };
        var full = new Button { Content = I18n.T("完全访问"), Classes = { "actionFull" } };
        var no = new Button { Content = I18n.T("✗ 拒绝"), Classes = { "actionNo" } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(ok);
        row.Children.Add(full);
        row.Children.Add(no);
        var hint = new TextBlock
        {
            Text = I18n.T("批准后 {0} 才会真正执行这个操作。", AssistantIdentity.Current),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
        };

        void LockButtons()
        {
            ok.IsEnabled = false;
            full.IsEnabled = false;
            no.IsEnabled = false;
        }
        ok.Click += (_, _) =>
        {
            LockButtons();
            card.Classes.Add("resolved");
            hint.Text = I18n.T("✔ 已批准");
            tcs.TrySetResult(ApprovalDecision.Approve());
        };
        full.Click += (_, _) =>
        {
            LockButtons();
            card.Classes.Add("resolved");
            hint.Text = I18n.T("已开启完全访问：本次及后续操作自动批准（重启后复位）");
            _controller.FullAccess = true;
            UpdateFullAccessVisual();   // 芯片与 tooltip 同步
            tcs.TrySetResult(ApprovalDecision.Approve());
        };
        no.Click += (_, _) =>
        {
            LockButtons();
            var reason = reasonBox.Text?.Trim();
            card.Classes.Add("resolved");
            hint.Text = I18n.T("✘ 已拒绝{0}", string.IsNullOrEmpty(reason) ? "" : $"：{reason}");
            tcs.TrySetResult(ApprovalDecision.Reject(reason));
        };
        // 回合被用户停止时：卡片作废标注（TrySetCanceled 由控制器触发）
        tcs.Task.ContinueWith(t =>
        {
            if (!t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                LockButtons();
                card.Classes.Add("resolved");
                hint.Text = I18n.T("⏹ 任务已停止，此操作未执行");
            });
        }, TaskScheduler.Default);
        // 在气泡快捷卡上被决议（外部 TrySetResult）时：同步锁定并标注本卡状态
        tcs.Task.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var d = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                LockButtons();
                card.Classes.Add("resolved");
                hint.Text = d.Approved
                    ? I18n.T("✔ 已批准")
                    : I18n.T("✘ 已拒绝：{0}", d.Reason ?? "");
            });
        }, TaskScheduler.Default);

        stack.Children.Add(reasonBox);
        stack.Children.Add(row);
        stack.Children.Add(hint);
        card.Child = stack;
        _chatPanel!.Children.Add(card);
        ScrollToEnd();
    }

    public void AddAskCard(string question, string? choices, TaskCompletionSource<string> tcs)
    {


        var card = new Border { Classes = { "card", "ask" } };
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{AssistantIdentity.Current} {I18n.T("有问题想问你")}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#d29922")),
        });
        stack.Children.Add(new TextBlock
        {
            Text = question,
            FontSize = 16,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
        });

        var answerBox = new TextBox { Watermark = I18n.T("输入你的回答…"), FontSize = 15 };
        var answerBtn = new Button { Content = I18n.T("回答"), Classes = { "actionOk" } };
        var choicesRow = new WrapPanel { Orientation = Orientation.Horizontal };

        void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            card.Classes.Add("resolved");
            stack.Children.Add(new TextBlock
            {
                Text = I18n.T("你回答了：{0}", text),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            });
            choicesRow.Children.Clear();
            answerBox.IsEnabled = false;
            answerBtn.IsEnabled = false;
            tcs.TrySetResult(text);
        }

        if (!string.IsNullOrWhiteSpace(choices))
        {
            foreach (var choice in choices.Split('|', StringSplitOptions.TrimEntries))
            {
                if (choice.Length == 0)
                {
                    continue;
                }
                var chip = new Button { Content = choice, Classes = { "choiceChip" }, Margin = new Thickness(0, 0, 8, 8) };
                chip.Click += (_, _) => Submit(choice);
                choicesRow.Children.Add(chip);
            }
        }

        answerBtn.Click += (_, _) => Submit(answerBox.Text ?? "");
        answerBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit(answerBox.Text ?? "");
            }
        };

        stack.Children.Add(choicesRow);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(answerBox);
        row.Children.Add(answerBtn);
        stack.Children.Add(row);
        card.Child = stack;
        _chatPanel!.Children.Add(card);
        ScrollToEnd();
    }

    // ───────────────────────── 思考动态指示（ZCode 风格）─────────────────────────
    private TextBlock? _thinkLine;
    private DispatcherTimer? _thinkTimer;
    private DateTime _thinkStart;

    /// <summary>显示「思考 · 持续了 N 秒」行，随时间实时更新；工具开始或回合结束时定格。</summary>
    private void StartThinkLine()
    {
        if (_thinkLine is not null)
        {
            return;
        }
        _thinkStart = DateTime.Now;
        _thinkLine = new TextBlock
        {
            Classes = { "procLine" },
            Text = I18n.T("思考"),
        };
        _chatPanel!.Children.Add(_thinkLine);
        ScrollToEnd();

        _thinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _thinkTimer.Tick += (_, _) =>
        {
            var secs = (int)(DateTime.Now - _thinkStart).TotalSeconds;
            _thinkLine!.Text = I18n.T("思考 · 持续了 {0} 秒", secs);
        };
        _thinkTimer.Start();
    }

    /// <summary>定格思考行（保留在历史中，弱化灰显示）。</summary>
    private void StopThinkLine()
    {
        _thinkTimer?.Stop();
        _thinkTimer = null;
        if (_thinkLine is not null && _thinkLine.Text.StartsWith(I18n.T("思考"), StringComparison.Ordinal))
        {
            var secs = (int)(DateTime.Now - _thinkStart).TotalSeconds;
            _thinkLine.Text = I18n.T("思考 · 持续了 {0} 秒", secs);
        }
        _thinkLine = null;
    }

    private void UpdateTokenHeader()
    {
        if (_tokenText is not null)
        {
            _tokenText.Text = $"Ⓣ {_cumTokens:N0}";
            _tokenText.IsVisible = true;
        }
    }

    private void AppendUsageLine()
    {
        if (_lastTurnUsage is not { } u)
        {
            return;
        }
        var line = new TextBlock
        {
            Classes = { "procLine" },
            Text = I18n.T("Token 本轮 输入 {0} / 输出 {1} / 合计 {2} ｜ 应用累计 {3}",
                u.In.ToString("N0"), u.Out.ToString("N0"), u.Total.ToString("N0"), _cumTokens.ToString("N0")),
        };
        _chatPanel!.Children.Add(line);
        ScrollToEnd();
        _lastTurnUsage = null;
    }

    // ───────────────────────── 聊天流渲染 ─────────────────────────
    private void AddUserBubble(string text) => AddUserBubble(text, null);

    private void AddUserBubble(string text, List<SentAttachment>? attachments)
    {
        CollapseExecGroup();   // 新回合开始，收起上一组执行过程
        var stack = new StackPanel { Spacing = 6 };
        if (attachments is { Count: > 0 })
        {
            foreach (var a in attachments)
            {
                if (a.IsImage && File.Exists(a.AbsPath))
                {
                    try
                    {
                        stack.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(8),
                            ClipToBounds = true,
                            MaxHeight = 140,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Child = new Image { Source = new Bitmap(a.AbsPath), MaxHeight = 140 },
                        });
                    }
                    catch { }
                }
                else
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = I18n.T("附件：{0}", a.Name),
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
                    });
                }
            }
        }
        if (text.Length > 0)
        {
            stack.Children.Add(MarkdownView.Selectable(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap }));
        }
        var border = new Border { Classes = { "userBubble" }, Child = stack };
        _chatPanel!.Children.Add(border);
        ScrollToEnd();
    }

    private Control AddAssistantBubble(string? initialText = null)
    {
        // 流式期间用纯文本（高频更新稳定），回合完成后替换为 Markdown 渲染
        Control viewer = MarkdownView.Selectable(new SelectableTextBlock
        {
            Text = initialText ?? "",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#e6e6e6")),
        });
        var border = new Border
        {
            Classes = { "assistantBubble" },
            Child = viewer,
        };
        _chatPanel!.Children.Add(border);
        ScrollToEnd();
        return viewer;
    }

    private Control? _streamViewer;

    /// <summary>流式期间：把控制器维护的当前回合全量答案覆盖到流式气泡（幂等，重复事件无副作用）。</summary>
    private void UpdateStreamText()
    {
        var text = _controller.FinalAnswer;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (_streamViewer is null)
        {
            _streamViewer = AddAssistantBubble();
            if (_streamViewer.Parent is Border bb)
            {
                bb.Tag = "stream";
            }
        }
        if (_streamViewer is SelectableTextBlock tb)
        {
            tb.Text = text;
            ScrollToEnd();
        }
    }

    /// <summary>回合完成：移除全部流式纯文本气泡，位置让给最终 Markdown 渲染。</summary>
    private void RemoveStreamBubble()
    {
        for (int i = _chatPanel!.Children.Count - 1; i >= 0; i--)
        {
            if (_chatPanel.Children[i] is Border { Tag: "stream" })
            {
                _chatPanel.Children.RemoveAt(i);
            }
        }
        _streamViewer = null;
    }

    /// <summary>回合完成：以 Markdown 渲染最终回答。按内容指纹去重（同一回答只渲染一次）。</summary>
    private string? _lastRenderedAnswer;

    private void ShowFinalAnswer()
    {
        var answer = _controller.FinalAnswer;
        if (string.IsNullOrWhiteSpace(answer))
        {
            return;
        }
        if (answer == _lastRenderedAnswer)
        {
            return;   // 同一回答已渲染过，忽略重复的完成事件
        }
        _lastRenderedAnswer = answer;

        Control render;
        try
        {
            render = MarkdownView.Render(RewriteWorkspaceImages(answer));
        }
        catch
        {
            render = new SelectableTextBlock
            {
                Text = answer,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#e6e6e6")),
            };
        }
        // 跨块多行复制：最终回答由多个文本块组成（选择无法跨块），每个块的右键菜单
        // 追加"复制全文"——一键复制整条回答的纯文本（保留多行结构）
        foreach (var block in render.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            MarkdownView.AppendMenuItem(block, "复制全文", () => MarkdownView.ToPlainText(answer));
        }
        if (render is SelectableTextBlock direct)
        {
            MarkdownView.AppendMenuItem(direct, "复制全文", () => MarkdownView.ToPlainText(answer));
        }
        _chatPanel!.Children.Add(render);
        ScrollToEnd();
    }


    /// <summary>把 Markdown 图片里的工作区相对路径改写为绝对 file URI，让本地图片可渲染。</summary>
    private string RewriteWorkspaceImages(string markdown)
    {
        var root = _controller.Agent?.WorkspaceRoot;
        if (string.IsNullOrEmpty(root))
        {
            return markdown;
        }
        return System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"!\[([^\]]*)\]\((?!https?://|file:///|data:)([^)]+)\)",
            m =>
            {
                var src = m.Groups[2].Value.Trim().Replace(" ", "%20");
                return $"![{m.Groups[1].Value}](file://{root}/{src})";
            });
    }

    private void AddErrorBubble(string message)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#ffe9e7")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = I18n.T("出错了：{0}", message),
                FontSize = 13.5,
                Foreground = new SolidColorBrush(Color.Parse("#b3261e")),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        _chatPanel!.Children.Add(border);
        ScrollToEnd();
    }

    /// <summary>执行过程行：收进当前回合的折叠组（无组则先创建），不挤占主内容区。</summary>
    private void AppendToolLine(string text)
    {
        BeginExecGroupIfNeeded();
        _execCount++;
        _execHeader!.Text = I18n.T("执行过程 · {0} 步", _execCount);

        var line = new TextBlock
        {
            Text = text.Length > 160 ? text[..160] + "…" : text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };
        _execContent!.Children.Add(line);
        ScrollToEnd();
    }

    private void BeginExecGroupIfNeeded()
    {
        if (_execGroup is not null)
        {
            return;
        }
        _execHeader = new TextBlock { Text = I18n.T("执行过程") };
        _execContent = new StackPanel { Spacing = 3 };
        _execGroup = new Expander
        {
            Classes = { "execGroup" },
            Header = _execHeader,
            Content = _execContent,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 40, 0),
        };
        _chatPanel!.Children.Add(_execGroup);
        ScrollToEnd();
    }

    private void CollapseExecGroup()
    {
        if (_execGroup is not null)
        {
            _execGroup.IsExpanded = false;
        }
        _execGroup = null;
        _execHeader = null;
        _execContent = null;
        _execCount = 0;
    }

    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => _chatScroll?.ScrollToEnd(), DispatcherPriority.Background);
    }

    // ───────────────────────── 状态条 ─────────────────────────
    private Popup? _modelPopup;
    private Popup? _fullPopup;

    /// <summary>在目标按钮下方弹出手写下拉面板；点外部自动关闭。</summary>
    private void ShowDropdown(Button target, StackPanel panel, ref Popup? store)
    {
        store?.IsOpen = false;
        var popup = new Popup
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            PlacementTarget = target,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2b2b2b")),
                BorderBrush = new SolidColorBrush(Color.Parse("#4a4a4a")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
                MinWidth = 200,
                Child = panel,
            },
            IsLightDismissEnabled = true,
        };
        ((Panel)Content!).Children.Add(popup);
        store = popup;
        popup.IsOpen = true;
    }

    /// <summary>同步输入区模型标签文字（Agent 未就绪时保持占位）。</summary>
    private void UpdateModelChip()
    {
        var chip = this.FindControl<TextBlock>("ModelChipText");
        if (chip is null)
        {
            return;
        }
        try
        {
            chip.Text = _controller.Ready ? _controller.Agent!.CurrentModel : I18n.T("模型");
        }
        catch
        {
            chip.Text = I18n.T("模型");
        }
    }

    /// <summary>同步完全访问开关的琥珀/灰色视觉。</summary>
    /// <summary>刷新完全访问芯片：文字、颜色与 tooltip 随模式联动（所有切换入口都走这里）。</summary>
    public void UpdateFullAccessVisual()
    {
        var on = _controller.FullAccess;
        var color = Color.Parse(on ? "#d29922" : "#9a9a9a");
        var brush = new SolidColorBrush(color);
        var text = this.FindControl<TextBlock>("FullAccessText");
        var icon = this.FindControl<PathIcon>("FullAccessIcon");
        if (text is not null)
        {
            text.Text = on ? I18n.T("完全访问") : I18n.T("每次询问");   // 胶囊显示当前选中的模式
            text.Foreground = brush;
        }
        if (icon is not null)
        {
            icon.Foreground = brush;
        }
        var btn = this.FindControl<Button>("FullAccessBtn");
        if (btn is not null)
        {
            ToolTip.SetTip(btn, on
                ? I18n.T("完全访问：操作自动批准（重启复位）")
                : I18n.T("每次询问：执行前征求同意"));
        }
    }

    // 启动状态胶囊覆盖：显示「启动中 · …」/「就绪 · N 工具」，到期自动回落到回合状态
    private string? _pillOverride;
    private DispatcherTimer? _pillOverrideTimer;

    /// <summary>把启动日志行翻译成状态胶囊短文案（对话区不出现）。</summary>
    private void ShowStartupStatus(string line)
    {
        if (line.Contains('✖') || line.Contains("失败"))
        {
            return;   // 失败行由对话流展示，胶囊维持原状
        }
        if (line.StartsWith("就绪", StringComparison.Ordinal) || line.StartsWith("Ready", StringComparison.Ordinal))
        {
            var count = System.Text.RegularExpressions.Regex.Match(line, @"工具 (\d+)");
            ShowPillOverride(count.Success
                ? I18n.T("就绪 · {0} 工具", count.Groups[1].Value)
                : I18n.T("就绪"), autoClearSeconds: 6);
        }
        else if (line.Contains("连接 MCP"))
        {
            ShowPillOverride(I18n.T("启动中 · 连接 MCP…"));
        }
        else if (line.Contains("恢复上次会话"))
        {
            ShowPillOverride(I18n.T("启动中 · 恢复会话…"));
        }
        // 其余行（MCP 已连接、工具清单等）太细碎，不占用胶囊
    }

    private void ShowPillOverride(string text, int autoClearSeconds = 0)
    {
        _pillOverride = text;
        _pillOverrideTimer?.Stop();
        if (autoClearSeconds > 0)
        {
            _pillOverrideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoClearSeconds) };
            _pillOverrideTimer.Tick += (_, _) =>
            {
                _pillOverrideTimer!.Stop();
                _pillOverrideTimer = null;
                _pillOverride = null;
                SetStatus(_lastState, _lastDetail, _lastWithModel);   // 回落到回合状态
            };
            _pillOverrideTimer.Start();
        }
        SetStatus(_lastState, _lastDetail, _lastWithModel);
    }

    private void SetStatus(BrookRunState state, string? detail, bool withModel = false)
    {
        _lastState = state;
        _lastDetail = detail;
        _lastWithModel = withModel;
        UpdateSendStopVisual(state);
        var text = state switch
        {
            BrookRunState.Idle => withModel && _controller.Ready
                ? I18n.T("空闲 · {0}", _controller.Agent!.CurrentModel)
                : _controller.Ready ? I18n.T("空闲") : I18n.T("启动中…"),
            BrookRunState.Thinking => I18n.T("思考中…"),
            BrookRunState.CallingTool => I18n.T("干活中 · {0}", detail),
            BrookRunState.AwaitingApproval => I18n.T("等你批准 · {0}", detail),
            BrookRunState.AwaitingUserAnswer => I18n.T("等你回答"),
            _ => I18n.T("空闲"),
        };
        if (_pillOverride is not null)
        {
            text = _pillOverride;   // 启动状态覆盖（如「就绪 · 26 工具」），到期自动恢复
        }
        _statusText!.Text = text;
        UpdateModelChip();
        _statusPill!.Classes.Remove("pillBusy");
        _statusPill.Classes.Remove("pillWait");
        if (state is BrookRunState.Thinking or BrookRunState.CallingTool)
        {
            _statusPill.Classes.Add("pillBusy");
        }
        else if (state is BrookRunState.AwaitingApproval or BrookRunState.AwaitingUserAnswer)
        {
            _statusPill.Classes.Add("pillWait");
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Cmd+V：剪贴板里有图片/文件时转为附件（文本仍走默认粘贴，无副作用）
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            _ = TryAttachFromClipboardAsync();
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendInputAsync();
        }
        else if (e.Key == Key.Escape && _controller.Busy)
        {
            e.Handled = true;
            StopTurn();   // Esc 快捷打断当前回合
        }
    }

    // ───────────────────────── 附件 ─────────────────────────
    private sealed record PendingAttachment(string SourcePath, string DisplayName, bool IsImage);
    private sealed record SentAttachment(string AbsPath, string RelPath, bool IsImage, string Name);
    private readonly List<PendingAttachment> _attachments = new();
    private List<SentAttachment> _bubbleAttachments = new();

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    /// <summary>剪贴板转附件：优先文件（Finder 复制），其次图片位图。</summary>
    private async Task TryAttachFromClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }
            var files = await clipboard.TryGetFilesAsync();
            if (files is not null)
            {
                var added = false;
                foreach (var f in files)
                {
                    var p = f.Path.LocalPath;
                    if (File.Exists(p))
                    {
                        AddAttachment(p);
                        added = true;
                    }
                }
                if (added)
                {
                    return;
                }
            }
            var bmp = await clipboard.TryGetBitmapAsync();
            if (bmp is not null)
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                bmp.Save(tmp);
                AddAttachment(tmp);
            }
        }
        catch
        {
            // 剪贴板读取失败时静默：不影响默认粘贴行为
        }
    }

    /// <summary>文件选择器添加附件。</summary>
    private async Task PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = I18n.T("添加附件"),
            AllowMultiple = true,
        });
        foreach (var f in files)
        {
            AddAttachment(f.Path.LocalPath);
        }
    }

    private void AddAttachment(string path)
    {
        if (_attachments.Count >= 8 || !File.Exists(path))
        {
            return;
        }
        _attachments.Add(new PendingAttachment(path, Path.GetFileName(path), IsImageFile(path)));
        RenderAttachmentBar();
    }

    /// <summary>重建附件预览条（图片缩略 + 名称 + 移除）。</summary>
    private void RenderAttachmentBar()
    {
        var bar = this.FindControl<StackPanel>("AttachmentBar");
        if (bar is null)
        {
            return;
        }
        bar.Children.Clear();
        bar.IsVisible = _attachments.Count > 0;
        foreach (var att in _attachments)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (att.IsImage && File.Exists(att.SourcePath))
            {
                try
                {
                    row.Children.Add(new Image
                    {
                        Source = new Bitmap(att.SourcePath),
                        Width = 32,
                        Height = 32,
                    });
                }
                catch { }
            }
            row.Children.Add(new TextBlock
            {
                Text = Cut(att.DisplayName, 20),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#d5d5d5")),
            });
            var close = new Button
            {
                Content = "×",
                Classes = { "toolChip" },
                Padding = new Thickness(4, 0),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
            };
            var attRef = att;
            close.Click += (_, _) =>
            {
                _attachments.Remove(attRef);
                RenderAttachmentBar();
            };
            row.Children.Add(close);
            bar.Children.Add(new Border { Classes = { "attachChip" }, Child = row });
        }
    }

    /// <summary>把待发附件复制到工作区 attachments/，返回引用信息并清空待发列表。</summary>
    private List<SentAttachment> SaveAttachmentsToWorkspace()
    {
        var result = new List<SentAttachment>();
        var wsRoot = _controller.Agent?.WorkspaceRoot;
        if (string.IsNullOrEmpty(wsRoot))
        {
            _attachments.Clear();
            RenderAttachmentBar();
            return result;
        }
        var dir = Path.Combine(wsRoot, "attachments");
        Directory.CreateDirectory(dir);
        foreach (var a in _attachments)
        {
            try
            {
                var stored = $"{DateTime.Now:MMdd-HHmmss}-{a.DisplayName}";
                var dest = Path.Combine(dir, stored);
                File.Copy(a.SourcePath, dest, true);
                result.Add(new SentAttachment(dest, $"attachments/{stored}", a.IsImage, a.DisplayName));
            }
            catch { }
        }
        _attachments.Clear();
        RenderAttachmentBar();
        return result;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 点关闭 = 隐藏（气泡仍常驻；退出走气泡右键菜单）
        SaveBoundsNow();
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
