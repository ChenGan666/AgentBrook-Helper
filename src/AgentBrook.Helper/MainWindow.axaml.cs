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
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper;

public enum MentionItemKind
{
    Peer,   // 单个 Helper（@触发）
    All,    // @全部
    Group   // 群组（#触发）
}

/// <summary>提记弹层条目：InsertText 是写进输入框、供大模型识别路由的文本（@名称 / #群名）。</summary>
public sealed record MentionItem(string Display, string InsertText, string Icon, MentionItemKind Kind, bool Online, string StatusText)
{
    public IBrush StatusBrush => Kind switch
    {
        MentionItemKind.All => AllBrush,
        MentionItemKind.Group => GroupBrush,
        _ => Online ? OnlineBrush : OfflineBrush
    };
    private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.Parse("#34c759"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#8e8e93"));
    private static readonly IBrush AllBrush = new SolidColorBrush(Color.Parse("#af52de"));
    private static readonly IBrush GroupBrush = new SolidColorBrush(Color.Parse("#5ac8fa"));
}

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
    // ── 历史滚动加载：会话记录可视光标（从末尾计数已渲染条数）与加载状态 ──
    private int _visibleFromEnd;        // 已可见的记录条数（从末尾计；含底部直播渲染与顶部滚动加载）
    private int _lastKnownTotal;        // 上次对账时的记录总数
    private bool _historyExhausted;     // 记录已全部加载到顶
    private bool _loadingOlderHistory;  // 插入历史期间忽略 ScrollChanged，防重入
    private TextBlock? _statusText;
    private Border? _statusPill;
    private TextBox? _inputBox;
    private Button? _sendBtn;
    private TextBlock? _tokenText;

    // 输入框 @ 提记弹层：@ 后呼出已配对 Helper 列表（含 @全部），选中插入 LLM 可识别的 @名称
    private Popup? _mentionPopup;
    private ListBox? _mentionList;
    private int _mentionStart = -1;     // 活跃 @ 记号在文本中的起点；-1 = 弹层未激活
    private bool _suppressMentionUpdate; // 程序化改写输入框时暂停重算，避免闪烁

    private (long In, long Out, long Total)? _lastTurnUsage;
    private long _cumTokens;
    private DateTime _lastPasteAttempt = DateTime.MinValue;
    private ChatMessage? _lastUserMessage;

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
        _chatScroll!.ScrollChanged += OnChatScrollChanged;   // 滚动到顶 → 增量加载更早的历史记录
        _statusText = this.FindControl<TextBlock>("StatusText");
        _statusPill = this.FindControl<Border>("StatusPill");
        _inputBox = this.FindControl<TextBox>("InputBox");
        _sendBtn = this.FindControl<Button>("SendBtn");
        _tokenText = this.FindControl<TextBlock>("TokenText");

        var newBtn = this.FindControl<Button>("NewBtn");
        var a2aBtn = this.FindControl<Button>("A2ABtn");
        if (a2aBtn is not null) a2aBtn.Click += (_, _) => App.ShowA2AWindow();
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
                AddItem(I18n.T("ui.ask.every.time.safer"), !_controller.FullAccess,
                    () => { _controller.FullAccess = false; UpdateFullAccessVisual(); });
                AddItem(I18n.T("ui.full.access.auto.approve.resets.on.restart"), _controller.FullAccess,
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
                        Text = I18n.T("ui.starting"),
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
                        // 对话模型组（type<4）：点选切换主模型
                        foreach (var m in provider.Models.Where(m => provider.IsChatModel(m)))
                        {
                            var isActive = m == currentModel && provider.Name == currentProvider;
                            var model = m;
                            var label = provider.DisplayNameOf(model);
                            if (label != model) label = $"{label}（{model}）";   // 显示名≠接口名时括注真实模型名
                            var b = new Button
                            {
                                Classes = { "menuItem" },
                                Content = isActive ? "✓  " + label : "      " + label,
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
                    var manage = new Button { Classes = { "menuItem" }, Content = I18n.T("ui.manage.models") };
                    manage.Click += (_, _) =>
                    {
                        _modelPopup?.IsOpen = false;
                        ModelSettingsWindow.ShowSingle();
                    };
                    panel.Children.Add(manage);
                }
                ShowDropdown(modelChip, panel, ref _modelPopup);
            };
        }

        // 多模态独立下拉：只列 type≥4 模型 + “无”，点选即生效 ActiveMediaModel
        var mediaChip = this.FindControl<Button>("MediaChip");
        if (mediaChip is not null)
        {
            mediaChip.Click += (_, _) =>
            {
                var panel = new StackPanel { Margin = new Thickness(4) };
                var providers = _controller.Providers;
                var currentProvider = _controller.CurrentProvider;
                var any = false;
                foreach (var provider in providers)
                {
                    var mmModels = provider.Models.Where(m => !provider.IsChatModel(m)).ToList();
                    if (mmModels.Count == 0) continue;
                    any = true;
                    panel.Children.Add(new TextBlock
                    {
                        Text = provider.Name,
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
                        Margin = new Thickness(12, 8, 12, 2),
                    });
                    // “无”项：不启用多模态子能力
                    var noneActive = provider.Name == currentProvider && string.IsNullOrEmpty(provider.ActiveMediaModel);
                    var noneBtn = new Button
                    {
                        Classes = { "menuItem" },
                        Content = (noneActive ? "✓  " : "      ") + "无",
                        Foreground = new SolidColorBrush(Color.Parse(noneActive ? "#4a9eff" : "#c8c8c8")),
                    };
                    noneBtn.Click += (_, _) =>
                    {
                        SelectMediaModel(provider, "");
                        _mediaPopup?.IsOpen = false;
                    };
                    panel.Children.Add(noneBtn);
                    foreach (var m in mmModels)
                    {
                        var isActive = provider.Name == currentProvider && provider.ActiveMediaModel == m;
                        var mm = m;
                        var label = provider.DisplayNameOf(mm);
                        var typeTag = provider.TypeOf(mm) is >= 6 ? "视频" : "图像";
                        if (label != mm) label = $"{label}（{mm}）";
                        var mb = new Button
                        {
                            Classes = { "menuItem" },
                            Content = (isActive ? "✓  " : "      ") + label + $"〔{typeTag}〕",
                            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#d29922" : "#c8c8c8")),
                        };
                        mb.Click += (_, _) =>
                        {
                            provider.ActiveMediaModel = mm;
                            _ = PersistProvidersAsync();
                            UpdateMediaChip();
                            _mediaPopup?.IsOpen = false;
                        };
                        panel.Children.Add(mb);
                    }
                }
                if (!any)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "无多模态模型（在模型设置中拉取）",
                        Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
                        FontSize = 13.5,
                        Margin = new Thickness(12, 8),
                    });
                }
                ShowDropdown(mediaChip, panel, ref _mediaPopup);
            };
        }

        // 拖入文件到附件
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);

        // 事件订阅：控制器 → UI（缺失会导致状态/消息/审批全部失联）
        _controller.EventRaised -= OnControllerEvent;
        _controller.EventRaised += OnControllerEvent;
        _controller.StateChanged -= OnStateChanged;
        _controller.StateChanged += OnStateChanged;
        _controller.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncHistoryCursor);   // 回合结束对账记录增量（滚动加载历史的游标）
        _controller.QueueChanged += n =>
        {
            _queuedHintCount = n;
            this.FindControl<Button>("QueueClearBtn")!.Click += (_, _) => ClearQueuedMessages();
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
        _controller.ReadyChanged += () => Dispatcher.UIThread.Post(OnReadyForHistory);   // 就绪后标题取真实值；并回放当前会话最近记录

        I18n.LanguageChanged += () => Dispatcher.UIThread.Post(() =>
        {
            ApplyStaticTexts();
            SetStatus(_lastState, _lastDetail, _lastWithModel);
        });

        // 全局粘贴拦截：隧道阶段抢在输入框之前处理 Ctrl/Cmd+V，
        // 剪贴板含图片/文件时转成附件；纯文本时仍放行给输入框默认粘贴。
        // 注册在 InputBox 上而不是 Window 上：更接近事件源，兼容 macOS/Windows 的事件路由差异。
        if (_inputBox is not null)
        {
            _inputBox.AddHandler(InputElement.KeyDownEvent, OnInputKeyDownTunnel,
                RoutingStrategies.Tunnel, handledEventsToo: true);

            // @ 提记：文本/光标变化时重算弹层（输入、删改、光标移出记号都会收起）
            _mentionPopup = this.FindControl<Popup>("MentionPopup");
            _mentionList = this.FindControl<ListBox>("MentionList");
            if (_mentionList is not null)
            {
                _mentionList.AddHandler(TappedEvent, OnMentionListTapped, RoutingStrategies.Bubble);
            }
            _inputBox.PropertyChanged += (_, e) =>
            {
                if (!_suppressMentionUpdate &&
                    (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty))
                {
                    UpdateMentionPopup();
                }
            };
        }

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
                AddAssistantBubble(I18n.T("ui.value.n", name));
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
            ToolTip.SetTip(sendBtn, busy ? I18n.T("ui.stop.the.current.task") : I18n.T("ui.send"));
        }
    }

    /// <summary>打断当前回合（停止模型流，已完成部分保留）。</summary>
    private void StopTurn()
    {
        _controller.CancelTurn();
    }

    private Popup? _sessionPopup;
    private Popup? _mediaPopup;

    /// <summary>会话切换下拉：最近会话（当前✓蓝高亮）＋ 开启新对话 ＋ 沉淀当前会话为技能。</summary>
    private void ShowSessionMenu(Button anchor)
    {
        var panel = new StackPanel { Margin = new Thickness(4), MinWidth = 260 };
        if (_controller.Busy)
        {
            panel.Children.Add(new TextBlock
            {
                Text = I18n.T("ui.busy.switch.sessions.after.the.current.task"),
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
                ToolTip.SetTip(delBtn, I18n.T("ui.message.4"));
                Grid.SetColumn(delBtn, 2);
                grid.Children.Add(delBtn);
                delBtn.Click += async (_, _) =>
                {
                    if ((string)delBtn.Tag! != "armed")
                    {
                        delBtn.Tag = "armed";
                        ((PathIcon)delBtn.Content!).Foreground = new SolidColorBrush(Color.Parse("#ff6b6b"));
                        ToolTip.SetTip(delBtn, I18n.T("ui.message.7"));
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
                    Text = I18n.T("ui.no.archived.sessions.yet"),
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
                Content = "＋  " + I18n.T("ui.new.chat"),
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
                Text = I18n.T("ui.distill.this.session.into.a.skill"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            distillItem.Click += (_, _) =>
            {
                _sessionPopup?.IsOpen = false;
                var (name, error) = _controller.DistillSessionToSkill();
                AddAssistantBubble(error is null
                    ? I18n.T("ui.session.distilled.into.skill.value.in.any.other.session.just.ask.me.to.load.skill.it", name)
                    : I18n.T("ui.distill.failed.value", error));
            };
            panel.Children.Add(distillItem);
        }
        ShowDropdown(anchor, panel, ref _sessionPopup);
    }

    private bool _startupReplayed;

    /// <summary>就绪后回放当前会话最近记录：启动时聊天区为空没有滚动空间，滚动加载需要初始内容；
    /// 只在首次就绪执行一次（后续就绪/重连由会话切换流程负责重建）。</summary>
    private void OnReadyForHistory()
    {
        UpdateSessionTitle();
        if (_startupReplayed || !_controller.Ready)
        {
            return;
        }
        _startupReplayed = true;
        var transcript = _controller.GetTranscript();
        if (transcript.Count == 0)
        {
            return;   // 全新会话：保持问候引导
        }
        foreach (var (role, text) in transcript)
        {
            _chatPanel!.Children.Add(BuildTranscriptItem(role, text));
        }
        _lastKnownTotal = _controller.TranscriptCount;
        _visibleFromEnd = transcript.Count;
        ScrollToEnd();
    }

    /// <summary>切换/新建会话后清空聊天区并回放历史（历史在会话存档中，不丢）。</summary>
    private void ClearChatForSessionSwitch(string title)
    {
        _chatPanel!.Children.Clear();
        _lastRenderedAnswer = null;
        _streamViewer = null;
        UpdateSessionTitle();

        // 回放历史消息（最近 40 条，assistant 走 Markdown 渲染）；光标对账供后续滚动加载
        var transcript = _controller.GetTranscript();
        foreach (var (role, text) in transcript)
        {
            _chatPanel!.Children.Add(BuildTranscriptItem(role, text));
        }
        _lastKnownTotal = _controller.TranscriptCount;
        _visibleFromEnd = transcript.Count;
        _historyExhausted = false;
        var replayed = transcript.Count;

        if (replayed > 0)
        {
            AddAssistantBubble(I18n.T("ui.value.6", title));
        }
        else
        {
            AddAssistantBubble(I18n.T("ui.switched.to.session.value.its.context.and.summary.are.loaded.just.continue", title));
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
        text.Text = I18n.T("ui.value.queued.will.send.after.the.current.turn", _queuedHintCount);
    }
    private int _queuedHintCount;

    /// <summary>删除排队中的消息（清空队列）。</summary>
    private void ClearQueuedMessages()
    {
        _controller.ClearQueuedInput();
        _queuedHintCount = 0;
        UpdateQueueBar();
    }

    /// <summary>把个性化设置（名字）应用到窗口标题与顶栏。</summary>
    public void ApplyStaticTexts()
    {
        var input = this.FindControl<TextBox>("InputBox");
        if (input is not null)
        {
            var watermarkKey = OperatingSystem.IsMacOS()
                ? "输入消息，Cmd+Enter 发送"
                : "输入消息，Ctrl+Enter 发送";
            input.Watermark = I18n.T(watermarkKey);
        }
        var fullText = this.FindControl<TextBlock>("FullAccessText");
        if (fullText is not null)
        {
            fullText.Text = _controller.FullAccess ? I18n.T("ui.full.access") : I18n.T("ui.ask.every.time");
        }
        var send = this.FindControl<Button>("SendBtn");
        if (send is not null)
        {
            ToolTip.SetTip(send, I18n.T("ui.send"));
        }
        var plus = this.FindControl<Button>("PlusBtn");
        if (plus is not null)
        {
            ToolTip.SetTip(plus, I18n.T("ui.add.attachment.or.paste.image"));
        }
        var modelChip = this.FindControl<Button>("ModelChip");
        if (modelChip is not null)
        {
            ToolTip.SetTip(modelChip, I18n.T("ui.switch.model"));
        }
        var fullAccessBtn = this.FindControl<Button>("FullAccessBtn");
        if (fullAccessBtn is not null)
        {
            ToolTip.SetTip(fullAccessBtn, _controller.FullAccess
                ? I18n.T("ui.full.access.auto.approve.resets.on.restart.2")
                : I18n.T("ui.ask.every.time.confirm.before.running"));
        }
        var newBtn = this.FindControl<Button>("NewBtn");
        if (newBtn is not null)
        {
            ToolTip.SetTip(newBtn, I18n.T("ui.new.chat"));
        }
    }

    /// <summary>把个性化设置（名字）应用到窗口标题与顶栏。</summary>
    public void ApplyIdentityUi()
    {
        var name = AssistantIdentity.Current;
        Title = I18n.T("ui.value.assistant", name);
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
            AddErrorBubble(I18n.T("ui.provider.switch.failed.value", ex.Message));
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
                AddErrorBubble(I18n.T("ui.ui.render.error.intercepted.value", ex.Message));
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
                    _lastKnownTotal = 0;   // 新会话：历史光标归零
                    _visibleFromEnd = 0;
                    _historyExhausted = false;
                    AddUserBubble(I18n.T("ui.new.conversation.started"));
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

    /// <summary>从外部（如设置窗）向对话输入并发送一条消息。</summary>
    public async Task SendMessageAsync(string text)
    {
        if (_inputBox is null) return;
        _inputBox.Text = text;
        await SendInputAsync();
    }

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

        // 附件：复制进工作区，图片同时作为 DataContent 直接送进模型视觉输入
        var attachments = _attachments.Count > 0 ? SaveAttachmentsToWorkspace() : new List<SentAttachment>();
        var message = BuildUserMessage(text, attachments);

        // 忙碌中：输入自动排队（补充信息），回合结束后逐条发送
        if (_controller.Busy)
        {
            _inputBox!.Text = "";
            _controller.EnqueueInput(message);
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

        if (attachments.Count > 0)
        {
            _bubbleAttachments = attachments;
        }
        _lastUserMessage = message;
        await _controller.SendAsync(message);
    }

    /// <summary>构造用户消息：文本 + 非图片文件说明 + 图片 DataContent（多模态输入）。</summary>
    private ChatMessage BuildUserMessage(string text, List<SentAttachment> attachments)
    {
        var imageAttachments = attachments.Where(a => a.IsImage).ToList();
        var fileAttachments = attachments.Where(a => !a.IsImage).ToList();

        var sb = new StringBuilder(text);
        if (fileAttachments.Count > 0)
        {
            sb.Append("\n\n【用户附加了以下文件，已保存到工作区】\n");
            foreach (var a in fileAttachments)
            {
                sb.AppendLine($"- {a.RelPath}");
            }
        }
        if (imageAttachments.Count > 0)
        {
            sb.Append($"\n\n【用户附加了 {imageAttachments.Count} 张图片，已作为视觉输入一并提供】");
        }

        var contents = new List<AIContent> { new TextContent(sb.ToString()) };
        foreach (var img in imageAttachments)
        {
            try
            {
                var bytes = File.ReadAllBytes(img.AbsPath);
                var mime = GetMimeType(img.AbsPath);
                contents.Add(new DataContent(bytes, mime));
                Console.Error.WriteLine($"[send] 附加图片 {img.RelPath} ({bytes.Length}B, {mime})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[send] ✖ 读取图片失败 {img.AbsPath}: {ex.Message}");
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    // ───────────────────────── HITL 卡片（由控制器在 UI 线程调用）─────────────────────────
    /// <summary>审批提要的公开入口：气泡快捷卡复用同一套「人话翻译」规则。</summary>
    public string SummarizeApprovalPublic(string tool, string? argsJson) => SummarizeApproval(tool, argsJson);

    /// <summary>把工具调用翻译成用户看得懂的一句话提要（本地规则，无需模型参与）。</summary>
    /// <summary>spawn_worker 审批提要：带能力授予时展示所授能力（用户批准的关注点）。</summary>
    private static string SpawnWorkerSummary(string? name, string? argsJson)
    {
        var baseText = string.IsNullOrEmpty(name) ? I18n.T("ui.spawn.a.worker.agent") : I18n.T("ui.spawn.worker.value", Cut(name));
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson ?? "{}");
            if (doc.RootElement.TryGetProperty("capabilities", out var capsEl)
                && capsEl.ValueKind == System.Text.Json.JsonValueKind.String
                && capsEl.GetString() is { Length: > 0 } capsDesc)
            {
                baseText += I18n.T("ui.value", capsDesc);
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
            "read_file" => string.IsNullOrEmpty(primary) ? I18n.T("ui.view.a.workspace.file") : I18n.T("ui.view.contents.of.value", Cut(primary)),
            "append_file" => string.IsNullOrEmpty(primary) ? I18n.T("ui.append.to.a.workspace.file") : I18n.T("ui.append.to.value", Cut(primary)),
            "write_file" => string.IsNullOrEmpty(primary) ? I18n.T("ui.write.a.workspace.file") : I18n.T("ui.write.file.value", Cut(primary)),
            "list_dir" => string.IsNullOrEmpty(primary) || primary == "." ? I18n.T("ui.list.the.workspace.directory") : I18n.T("ui.list.directory.value.2", Cut(primary)),
            "memory_read" => I18n.T("ui.read.value.s.long.term.memory", AssistantIdentity.Current),
            "memory_write" or "memory_append" => I18n.T("ui.update.value.s.long.term.memory", AssistantIdentity.Current),
            "create_skill" => string.IsNullOrEmpty(primary) ? I18n.T("ui.create.a.new.skill") : I18n.T("ui.create.skill.value", Cut(primary)),
            "install_skill" or "skill_market" => string.IsNullOrEmpty(primary) ? I18n.T("ui.install.a.skill.from.the.market") : I18n.T("ui.install.skill.value", Cut(primary)),
            "load_skill" or "list_skills" => I18n.T("ui.load.list.available.skills"),
            "mcp_add_server" => string.IsNullOrEmpty(primary) ? I18n.T("ui.add.an.mcp.plugin") : I18n.T("ui.add.mcp.plugin.value", Cut(primary)),
            "mcp_list_servers" => I18n.T("ui.list.installed.mcp.plugins"),
            "spawn_worker" => SpawnWorkerSummary(primary, argsJson),
            "assign_task" => I18n.T("ui.assign.a.task.to.a.worker"),
            "send_message" or "send_to_worker" => I18n.T("ui.message.a.worker"),
            "broadcast_to_workers" => I18n.T("ui.broadcast.to.all.workers"),
            "check_messages" or "read_shared" or "start_project" or "list_workers" => I18n.T("ui.check.team.status"),
            "open_url" => string.IsNullOrEmpty(primary) ? I18n.T("ui.open.a.web.page") : I18n.T("ui.open.web.page.value", Cut(primary, 60)),
            "ask_user" => I18n.T("ui.ask.you.a.question"),
            _ => string.IsNullOrEmpty(primary) ? I18n.T("ui.invoke.value", tool) : I18n.T("ui.invoke.value.value", tool, Cut(primary, 40)),
        };
    }

    /// <summary>把 shell 命令翻译成人话；识别不了就显示命令本身（截断）。</summary>
    private static string DescribeCommand(string cmd)
    {
        var c = (cmd ?? "").Trim();
        if (c.Length == 0)
        {
            return I18n.T("ui.run.a.terminal.command");
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
            "open" => I18n.T("ui.open.value", Cut(arg)),
            "ls" => I18n.T("ui.list.directory.value", Cut(arg)),
            "cat" or "head" or "tail" or "less" or "more" => I18n.T("ui.view.file.value", Cut(arg)),
            "mkdir" => I18n.T("ui.create.folder.value", Cut(arg)),
            "touch" => I18n.T("ui.create.file.value", Cut(arg)),
            "rm" => I18n.T("ui.delete.value", Cut(arg)),
            "mv" => I18n.T("ui.move.rename.value", Cut(arg)),
            "cp" => I18n.T("ui.copy.value", Cut(arg)),
            "date" => I18n.T("ui.check.the.current.time"),
            "pwd" => I18n.T("ui.show.current.directory"),
            "echo" => I18n.T("ui.print.a.piece.of.text"),
            "curl" or "wget" => I18n.T("ui.fetch.from.the.web.value", Cut(arg, 50)),
            "git" => I18n.T("ui.run.git.value", parts.Skip(1).FirstOrDefault() ?? I18n.T("ui.action")),
            "python" or "python3" or "node" => I18n.T("ui.run.script.value", Cut(arg)),
            "dotnet" => $"运行 dotnet {parts.Skip(1).FirstOrDefault() ?? ""}",
            "grep" or "rg" => I18n.T("ui.search.for.value", Cut(arg)),
            "cd" => I18n.T("ui.change.directory.to.value", Cut(arg)),
            "chmod" => I18n.T("ui.change.permissions.of.value", Cut(arg)),
            _ => null,
        };
        if (body is null)
        {
            return I18n.T("ui.in.terminal.value", Cut(c, 70));
        }
        return more
            ? I18n.T("ui.in.terminal.starting.with.value.more.steps.follow", body)
            : I18n.T("ui.in.terminal.value", body);
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
            Text = $"{AssistantIdentity.Current} {I18n.T("ui.wants.to.perform.an.action.and.needs.your.approval")}",
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
            Text = I18n.T("ui.action.type.value", tool),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
        });
        // 原始指令默认折叠：想看细节的用户再展开
        Expander? argsExpander = null;
        if (!string.IsNullOrEmpty(argsJson))
        {
            argsExpander = new Expander
            {
                Classes = { "execGroup" },
                Header = new TextBlock { Text = I18n.T("ui.view.raw.command"), FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")) },
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#333333")),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6),
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
            };
            stack.Children.Add(argsExpander);
        }

        // 固定宽度自动换行：拒绝原因可能很长，单行横向滚动看不全，宽度受卡片约束、长文自动折行增高
        var reasonBox = new TextBox
        {
            Watermark = I18n.T("ui.reason.for.rejection.optional"),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var ok = new Button { Content = I18n.T("ui.approve"), Classes = { "actionOk" } };
        var full = new Button { Content = I18n.T("ui.full.access"), Classes = { "actionFull" } };
        var no = new Button { Content = I18n.T("ui.reject"), Classes = { "actionNo" } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(ok);
        row.Children.Add(full);
        row.Children.Add(no);
        var hint = new TextBlock
        {
            Text = I18n.T("ui.value.will.only.run.this.after.your.approval", AssistantIdentity.Current),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
        };

        void LockButtons()
        {
            ok.IsEnabled = false;
            full.IsEnabled = false;
            no.IsEnabled = false;
        }
        void FoldCard()
        {
            LockButtons();
            if (argsExpander is not null) argsExpander.IsExpanded = false;
            reasonBox.IsVisible = false;
            row.IsVisible = false;
        }
        ok.Click += (_, _) =>
        {
            FoldCard();
            card.Classes.Add("resolved");
            hint.Text = I18n.T("ui.approved");
            tcs.TrySetResult(ApprovalDecision.Approve());
        };
        full.Click += (_, _) =>
        {
            FoldCard();
            card.Classes.Add("resolved");
            hint.Text = I18n.T("ui.full.access.granted.actions.auto.approved.this.session.resets.on.restart");
            _controller.FullAccess = true;
            UpdateFullAccessVisual();   // 芯片与 tooltip 同步
            tcs.TrySetResult(ApprovalDecision.Approve());
        };
        no.Click += (_, _) =>
        {
            FoldCard();
            var reason = reasonBox.Text?.Trim();
            card.Classes.Add("resolved");
            hint.Text = I18n.T("ui.value.2", string.IsNullOrEmpty(reason) ? "" : $"：{reason}");
            tcs.TrySetResult(ApprovalDecision.Reject(reason));
        };
        // 回合被用户停止时：卡片作废标注（TrySetCanceled 由控制器触发）
        tcs.Task.ContinueWith(t =>
        {
            if (!t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                FoldCard();
                card.Classes.Add("resolved");
                hint.Text = I18n.T("ui.message.2");
            });
        }, TaskScheduler.Default);
        // 在气泡快捷卡上被决议（外部 TrySetResult）时：同步锁定并标注本卡状态
        tcs.Task.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var d = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                FoldCard();
                card.Classes.Add("resolved");
                hint.Text = d.Approved
                    ? I18n.T("ui.approved")
                    : I18n.T("ui.rejected.value", d.Reason ?? "");
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
            Text = $"{AssistantIdentity.Current} {I18n.T("ui.has.a.question.for.you")}",
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

        // 固定宽度自动换行：长回答自动折行增高，不横向溢出
        var answerBox = new TextBox
        {
            Watermark = I18n.T("ui.type.your.answer"),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        };
        var answerBtn = new Button { Content = I18n.T("ui.answer"), Classes = { "actionOk" } };
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
                Text = I18n.T("ui.you.answered.value", text),
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
        // 输入框占满剩余宽度（横排 StackPanel 给子项无限宽度，长输入会把行撑出卡片边界）
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        answerBtn.Margin = new Thickness(10, 0, 0, 0);
        answerBtn.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(answerBox, 0);
        Grid.SetColumn(answerBtn, 1);
        row.Children.Add(answerBox);
        row.Children.Add(answerBtn);
        stack.Children.Add(row);
        card.Child = stack;
        _chatPanel!.Children.Add(card);
        ScrollToEnd();
    }

    /// <summary>测试钩子：向审批/提问卡的补充输入框灌入超长文本（验证固定宽度自动换行不横向溢出）。</summary>
    public void DebugSeedCardInputs()
    {
        var longText = "这是一段超长的补充说明，验证输入框在固定宽度下自动换行：用户可能输入很长的拒绝原因或回答，" +
                       "例如 /very/long/path/reports/gold/gold-report-20260930-临时备份目录/segment_without_spaces " +
                       "这类中英混合的长路径也必须折行显示，而不是横向撑出卡片边界。";
        var boxes = _chatPanel!.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>()
            .Where(b => b.Name != "InputBox").ToList();
        if (boxes.Count > 0) boxes[0].Text = longText;
        if (boxes.Count > 1) boxes[1].Text = longText;
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
            Text = I18n.T("ui.thinking"),
        };
        _chatPanel!.Children.Add(_thinkLine);
        ScrollToEnd();

        _thinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _thinkTimer.Tick += (_, _) =>
        {
            var secs = (int)(DateTime.Now - _thinkStart).TotalSeconds;
            _thinkLine!.Text = I18n.T("ui.thinking.values", secs);
        };
        _thinkTimer.Start();
    }

    /// <summary>定格思考行（保留在历史中，弱化灰显示）。</summary>
    private void StopThinkLine()
    {
        _thinkTimer?.Stop();
        _thinkTimer = null;
        if (_thinkLine is not null && _thinkLine.Text.StartsWith(I18n.T("ui.thinking"), StringComparison.Ordinal))
        {
            var secs = (int)(DateTime.Now - _thinkStart).TotalSeconds;
            _thinkLine.Text = I18n.T("ui.thinking.values", secs);
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
            Text = I18n.T("ui.tokens.this.turn.in.value.out.value.total.value.app.total.value",
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
                        Text = I18n.T("ui.attachment.value", a.Name),
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
        var border = new Border { Classes = { "userBubble" }, Child = stack, Tag = "msg" };
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

        // 操作按钮：复制全文、重试/重新生成
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 2),
        };
        var copyBtn = CreateIconButton("Icon.Copy", I18n.T("ui.copy"));
        copyBtn.Click += async (_, _) =>
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is null) return;
            await clip.SetTextAsync(MarkdownView.ToPlainText(answer));
        };
        var retryBtn = CreateIconButton("Icon.Refresh", I18n.T("ui.retry"));
        retryBtn.Click += async (_, _) =>
        {
            if (_controller.Busy || _lastUserMessage is null) return;
            await _controller.SendAsync(_lastUserMessage);
        };
        buttonRow.Children.Add(copyBtn);
        buttonRow.Children.Add(retryBtn);

        var wrapper = new StackPanel { Spacing = 2, Tag = "msg" };
        wrapper.Children.Add(render);
        wrapper.Children.Add(buttonRow);
        _chatPanel!.Children.Add(wrapper);
        ScrollToEnd();
    }

    /// <summary>构造一个带矢量图标和文字的轻量按钮；图标与文字统一使用同一套中性色，hover 变白。</summary>
    private static Button CreateIconButton(string iconKey, string label)
    {
        var iconBrush = new SolidColorBrush(Color.Parse("#b0b8c4"));
        var icon = new PathIcon
        {
            Data = (StreamGeometry)Application.Current!.Resources[iconKey]!,
            Width = 14,
            Height = 14,
            Foreground = iconBrush,
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = iconBrush,
        };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(text);
        var btn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = stack,
        };
        btn.PointerEntered += (_, _) =>
        {
            var hover = new SolidColorBrush(Color.Parse("#e6e6e6"));
            icon.Foreground = hover;
            text.Foreground = hover;
        };
        btn.PointerExited += (_, _) =>
        {
            icon.Foreground = iconBrush;
            text.Foreground = iconBrush;
        };
        return btn;
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
                Text = I18n.T("ui.error.value", message),
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
        _execHeader!.Text = I18n.T("ui.process.value.steps", _execCount);

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
        _execHeader = new TextBlock { Text = I18n.T("ui.process") };
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
        TrimChatHistory();
        Dispatcher.UIThread.Post(() => _chatScroll?.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>按角色构造对话记录条目（回放/滚动加载共用）：user 气泡、assistant 走 Markdown 渲染。
    /// 统一标记 Tag="history"：不计入直播裁剪上限（总量受会话记录窗口约束，有界）。</summary>
    private Control BuildTranscriptItem(string role, string text)
    {
        if (role == "user")
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(MarkdownView.Selectable(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap }));
            return new Border { Classes = { "userBubble" }, Child = stack, Tag = "history" };
        }
        Control render;
        try { render = MarkdownView.Render(text); }
        catch { render = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#e6e6e6")) }; }
        return new Border { Classes = { "assistantBubble" }, Child = render, Tag = "history" };
    }

    /// <summary>回合结束时对账：会话记录新增的条数计入可视光标（新增条目都已在底部直播渲染）。</summary>
    private void SyncHistoryCursor()
    {
        if (!_controller.Ready)
        {
            return;
        }
        var total = _controller.TranscriptCount;
        if (total > _lastKnownTotal)
        {
            _visibleFromEnd += total - _lastKnownTotal;
            _lastKnownTotal = total;
            _historyExhausted = false;
        }
        else if (total < _lastKnownTotal)
        {
            // 总量回退（会话切换/清空兜底）：以新总数为基线，可见数收敛到不越界
            _lastKnownTotal = total;
            _visibleFromEnd = Math.Min(_visibleFromEnd, total);
        }
    }

    /// <summary>滚动到顶（留 24dip 余量）时增量加载更早的历史记录。</summary>
    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = _chatScroll!;
        if (_loadingOlderHistory || _historyExhausted)
        {
            return;
        }
        if (sv.Extent.Height <= sv.Viewport.Height)
        {
            return;   // 内容不足一屏，无滚动空间
        }
        if (sv.Offset.Y > 24)
        {
            return;
        }
        LoadOlderHistory();
    }

    /// <summary>向上滚动加载：取可视光标之前的一批记录插入聊天区顶部，并补偿滚动偏移保持视口不跳。</summary>
    private void LoadOlderHistory()
    {
        var total = _controller.TranscriptCount;
        var end = total - _visibleFromEnd;   // 最早一条未渲染记录的位置（从末尾计）
        if (end <= 0)
        {
            _historyExhausted = true;
            return;
        }
        var batch = _controller.GetTranscriptRange(_visibleFromEnd, Math.Min(40, end));
        if (batch.Count == 0)
        {
            _historyExhausted = true;
            return;
        }
        _loadingOlderHistory = true;
        try
        {
            var prevExtent = _chatScroll!.Extent.Height;
            for (var i = 0; i < batch.Count; i++)
            {
                _chatPanel!.Children.Insert(i, BuildTranscriptItem(batch[i].Role, batch[i].Text));
            }
            _visibleFromEnd += batch.Count;
            if (end - batch.Count <= 0)
            {
                _historyExhausted = true;
                _chatPanel!.Children.Insert(0, new TextBlock
                {
                    Text = I18n.T("ui.history.all.loaded"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 12,
                    Margin = new Thickness(0, 4),
                    Foreground = new SolidColorBrush(Color.Parse("#6f6f6f")),
                    Tag = "history",
                });
            }
            // 插入在顶部会把内容整体下推：把高度增量补偿进滚动偏移，视口停留在原内容上
            Dispatcher.UIThread.Post(() =>
            {
                var grew = _chatScroll!.Extent.Height - prevExtent;
                if (grew > 0)
                {
                    _chatScroll.Offset = _chatScroll.Offset.WithY(_chatScroll.Offset.Y + grew);
                }
                _loadingOlderHistory = false;
            }, DispatcherPriority.Background);
        }
        catch
        {
            _loadingOlderHistory = false;
        }
    }

    /// <summary>聊天历史裁剪：直播区控件树只进不出会让长会话/长驻进程累积大量视觉对象；
    /// 直播子节点超过上限时从最旧端移除（被裁的记录条目回到"未渲染"，滚动到顶可重新加载）；
    /// 回放/滚动加载的历史条目（Tag="history"）不计入、不裁剪；未决议卡片与活动思考行保留。</summary>
    private void TrimChatHistory()
    {
        const int MaxLiveChildren = 300;
        var children = _chatPanel!.Children;
        var liveCount = 0;
        foreach (var child in children)
        {
            if (child.Tag as string != "history")
            {
                liveCount++;
            }
        }
        var allowed = liveCount - MaxLiveChildren;
        if (allowed <= 0)
        {
            return;
        }
        List<Control>? remove = null;
        foreach (var child in children)
        {
            if (remove is not null && remove.Count >= allowed)
            {
                break;
            }
            if (child.Tag as string == "history")
            {
                continue;   // 历史条目保留
            }
            if (child is Border { Classes: var cls } && cls.Contains("card") && !cls.Contains("resolved"))
            {
                continue;   // 未决议卡片保留
            }
            if (ReferenceEquals(child, _thinkLine))
            {
                continue;   // 活动中的思考行保留
            }
            (remove ??= new List<Control>()).Add(child);
        }
        if (remove is not null)
        {
            foreach (var c in remove)
            {
                children.Remove(c);
                if (c.Tag as string == "msg")
                {
                    _visibleFromEnd = Math.Max(0, _visibleFromEnd - 1);
                }
            }
        }
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

    /// <summary>持久化供应商配置（多模态选择等即时生效，Agent.Core 侧 SaveProvidersAsync 负责写盘+热应用）。</summary>
    private async Task PersistProvidersAsync(string? activeProvider = null)
    {
        try
        {
            if (_controller.Ready)
                await _controller.SaveProvidersAsync(
                    _controller.Providers.ToList(), activeProvider ?? _controller.CurrentProvider);
        }
        catch { /* 持久化失败不阻断 UI 选择 */ }
    }

    /// <summary>选择多模态模型（空串=无）；与对话模型供应商无关，可跨供应商自由组合；全局唯一，清除其他供应商的选择。</summary>
    private void SelectMediaModel(AgentBrook.Agent.Infrastructure.ProviderConfig provider, string mediaModel)
    {
        foreach (var p in _controller.Providers)
        {
            p.ActiveMediaModel = p.Name == provider.Name ? mediaModel : "";
        }
        _ = PersistProvidersAsync();   // 激活供应商不变
        UpdateMediaChip();
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
            var model = _controller.Ready ? _controller.Agent!.CurrentModel : "";
            var provider = _controller.Providers.FirstOrDefault(p => p.Name == _controller.CurrentProvider);
            chip.Text = model.Length == 0
                ? I18n.T("ui.model")
                : provider?.DisplayNameOf(model) ?? model;
            // 显示名≠接口名时 tooltip 标注真实模型名（参数用）
            if (provider != null && provider.DisplayNameOf(model) != model)
            {
                ToolTip.SetTip(chip.Parent as Control, $"model = {model}");
            }
        }
        catch
        {
            chip.Text = I18n.T("ui.model");
        }
        UpdateMediaChip();
    }

    /// <summary>同步多模态芯片：显示已选 media_generate 模型（未选显示“🎬无”，无多模态模型时隐藏）。</summary>
    private void UpdateMediaChip()
    {
        var chip = this.FindControl<TextBlock>("MediaChipText");
        if (chip is null) return;
        try
        {
            var provider = _controller.Providers.FirstOrDefault(p => !string.IsNullOrEmpty(p.ActiveMediaModel));
            var mm = provider?.ActiveMediaModel ?? "";
            chip.Text = mm.Length == 0
                ? "多媒体: 无"
                : $"多媒体: {provider!.DisplayNameOf(mm)}";
            var hasAny = _controller.Providers.Any(p => p.Models.Any(m => !p.IsChatModel(m)));
            (chip.Parent?.Parent as Control)?.IsVisible = hasAny;
        }
        catch
        {
            chip.Text = "多媒体: 无";
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
            text.Text = on ? I18n.T("ui.full.access") : I18n.T("ui.ask.every.time");   // 胶囊显示当前选中的模式
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
                ? I18n.T("ui.full.access.auto.approve.resets.on.restart.2")
                : I18n.T("ui.ask.every.time.confirm.before.running"));
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
                ? I18n.T("ui.ready.value.tools", count.Groups[1].Value)
                : I18n.T("ui.ready"), autoClearSeconds: 6);
        }
        else if (line.Contains("连接 MCP"))
        {
            ShowPillOverride(I18n.T("ui.starting.connecting.mcp"));
        }
        else if (line.Contains("恢复上次会话"))
        {
            ShowPillOverride(I18n.T("ui.starting.restoring.session"));
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
                ? I18n.T("ui.idle.value", _controller.Agent!.CurrentModel)
                : _controller.Ready ? I18n.T("ui.idle") : I18n.T("ui.starting.2"),
            BrookRunState.Thinking => I18n.T("ui.thinking.2"),
            BrookRunState.CallingTool => I18n.T("ui.working.value", detail),
            BrookRunState.AwaitingApproval => I18n.T("ui.awaiting.approval.value", detail),
            BrookRunState.AwaitingUserAnswer => I18n.T("ui.awaiting.your.answer"),
            _ => I18n.T("ui.idle"),
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
        if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            _ = SendInputAsync();
        }
        else if (e.Key == Key.Escape && _controller.Busy)
        {
            e.Handled = true;
            StopTurn();   // Esc 快捷打断当前回合
        }
        else if (IsPasteCombo(e) && TryMarkPasteAttempt())
        {
            // 兜底：如果隧道阶段被输入框默认粘贴吞掉，直接阶段再试一次。
            // 200ms 防抖避免隧道与直接阶段重复附加同一张图片（跨平台兼容）。
            Console.Error.WriteLine("[paste-direct] 在输入框直接阶段兜底粘贴");
            _ = TryAttachFromClipboardAsync();
        }
    }

    /// <summary>测试钩子：模拟一次 Ctrl/Cmd+V 按键路由事件（tunnel + handledEventsToo 全链路）。</summary>
    public void RaisePasteKeyEvent()
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control };
        Console.Error.WriteLine("[paste-tunnel] 模拟按键路由事件");
        RaiseEvent(args);
    }

    /// <summary>
    /// 隧道阶段拦截 Ctrl/Cmd+V 与 Ctrl/Cmd+Enter：必须用 Tunnel——输入框的类处理（粘贴/回车换行）
    /// 会把按键标记为已处理，冒泡阶段到达窗口时事件已被吞掉（图片粘贴和 Ctrl+Enter 发送失效的根因）。
    /// 剪贴板有图片/文件时异步转附件；纯文本时不做任何事，输入框默认粘贴不受影响。
    /// </summary>
    private void OnInputKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        // @ 提记弹层打开时优先消费导航/选中键（隧道阶段拦截，避免被输入框类处理如回车换行抢走）
        if (_mentionPopup is { IsOpen: true } && HandleMentionKey(e))
        {
            e.Handled = true;
            return;
        }
        if (IsPasteCombo(e) && TryMarkPasteAttempt())
        {
            Console.Error.WriteLine("[paste-tunnel] 拦截到 Ctrl/Cmd+V");
            _ = TryAttachFromClipboardAsync();
        }
        else if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            _ = SendInputAsync();
        }
    }

    private static bool IsPasteCombo(KeyEventArgs e)
        => e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));

    private bool TryMarkPasteAttempt()
    {
        var now = DateTime.Now;
        if ((now - _lastPasteAttempt).TotalMilliseconds < 200)
        {
            return false;
        }
        _lastPasteAttempt = now;
        return true;
    }

    // ───────────────────────── 输入框 @ 提记（呼出已配对 Helper） ─────────────────────────
    /// <summary>文本/光标变化后重算提记弹层：光标处于活跃 @/# 记号中则按触发符构建列表（@=Helper、#=群组）并弹出，否则收起。</summary>
    private void UpdateMentionPopup()
    {
        if (_inputBox is null || _mentionPopup is null || _mentionList is null)
        {
            return;
        }
        var text = _inputBox.Text ?? "";
        var caret = _inputBox.CaretIndex;
        var start = FindMentionStart(text, caret);
        if (start < 0)
        {
            CloseMentionPopup();
            return;
        }

        var filter = text[(start + 1)..caret];
        var items = new List<MentionItem>();
        switch (text[start])
        {
            case '@':
                if (filter.Length == 0 || "全部".StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new MentionItem("@全部", "@全部", "📣", MentionItemKind.All, Online: true, "所有 Helper"));
                }
                items.AddRange(A2AAgentTools.GetMentionablePeers()
                    .Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Online)
                    .Select(p => new MentionItem($"@{p.Name}", $"@{p.Name}", p.Icon, MentionItemKind.Peer, p.Online, p.Online ? "在线" : "离线")));
                break;

            case '#':
                items.AddRange(A2AAgentTools.GetGroups()
                    .Where(g => g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .Select(g => new MentionItem($"#{g.Name}", $"#{g.Name}", "👥", MentionItemKind.Group, Online: true, $"{g.MemberCount} 人")));
                break;
        }
        if (items.Count == 0)
        {
            CloseMentionPopup();
            return;
        }

        _mentionStart = start;
        _mentionList.ItemsSource = items;
        _mentionList.SelectedIndex = 0;
        if (!_mentionPopup.IsOpen)
        {
            _mentionPopup.Open();
        }
    }

    /// <summary>从光标向前找活跃记号起点：@ 或 # 与光标之间无空白（限 25 字符）即有效，
    /// 允许直接跟在文字后（如「你好@」）；找不到返回 -1。</summary>
    private static int FindMentionStart(string text, int caret)
    {
        var end = Math.Min(caret, text.Length);
        for (var i = end - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch is '@' or '#')
            {
                return end - i <= 25 ? i : -1;
            }
            if (char.IsWhiteSpace(ch))
            {
                return -1;
            }
        }
        return -1;
    }

    /// <summary>弹层打开时的按键路由：↑↓ 选择、Enter/空格/Tab 选中插入、Esc 关闭。返回 true 表示已消费该键。</summary>
    private bool HandleMentionKey(KeyEventArgs e)
    {
        if (_mentionList is null)
        {
            return false;
        }
        switch (e.Key)
        {
            case Key.Up:
                MoveMentionSelection(-1);
                return true;
            case Key.Down:
                MoveMentionSelection(1);
                return true;
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Control)
                             && !e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                AcceptMention();   // Ctrl/Cmd+Enter 不拦截：照常走发送
                return true;
            case Key.Space or Key.Tab:
                AcceptMention();
                return true;
            case Key.Escape:
                CloseMentionPopup();
                return true;
            default:
                return false;
        }
    }

    private void MoveMentionSelection(int delta)
    {
        if (_mentionList is null || _mentionList.ItemCount == 0)
        {
            return;
        }
        var next = Math.Max(0, Math.Min(_mentionList.ItemCount - 1, _mentionList.SelectedIndex + delta));
        _mentionList.SelectedIndex = next;
        _mentionList.ScrollIntoView(next);
    }

    /// <summary>选中项写回输入框：替换活跃 @ 记号段为「@名称 」，光标落在其后，弹层收起。
    /// 「@名称」正是系统提示词约定的大模型路由指令（a2a_send_message / a2a_send_all）。</summary>
    private void AcceptMention()
    {
        if (_inputBox is null || _mentionList?.SelectedItem is not MentionItem item || _mentionStart < 0)
        {
            return;
        }
        var text = _inputBox.Text ?? "";
        var caret = Math.Min(_inputBox.CaretIndex, text.Length);
        var head = text[.._mentionStart] + item.InsertText + " ";
        var tail = text[caret..];
        _suppressMentionUpdate = true;
        try
        {
            _inputBox.Text = head + tail;
            _inputBox.CaretIndex = head.Length;
        }
        finally
        {
            _suppressMentionUpdate = false;
        }
        CloseMentionPopup();
    }

    private void CloseMentionPopup()
    {
        _mentionStart = -1;
        if (_mentionPopup is { IsOpen: true })
        {
            _mentionPopup.Close();
        }
    }

    /// <summary>点击弹层项直接选中并插入。</summary>
    private void OnMentionListTapped(object? sender, TappedEventArgs e)
    {
        if (_mentionList is null || e.Source is not Visual visual)
        {
            return;
        }
        var el = visual;
        while (el is not null && el is not ListBoxItem)
        {
            el = el.GetVisualParent();
        }
        if (el is ListBoxItem { DataContext: MentionItem item })
        {
            if (_mentionList.SelectedItem != item)
            {
                _mentionList.SelectedItem = item;
            }
            AcceptMention();
        }
    }

    // ───────────────────────── 附件 ─────────────────────────
    private sealed record PendingAttachment(string SourcePath, string DisplayName, bool IsImage);
    private sealed record SentAttachment(string AbsPath, string RelPath, bool IsImage, string Name);
    private readonly List<PendingAttachment> _attachments = new();
    private List<SentAttachment> _bubbleAttachments = new();

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    /// <summary>剪贴板转附件：优先文件（Finder 复制），其次图片位图（多格式回退 + 平台兜底）。</summary>
    private async Task TryAttachFromClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Console.Error.WriteLine("[paste] ✖ Clipboard 为空");
                return;
            }
            var files = await clipboard.TryGetFilesAsync();
            Console.Error.WriteLine($"[paste] TryGetFilesAsync → {(files is null ? "null" : files.Count() + " 个文件")}");
            if (files is not null)
            {
                var added = false;
                foreach (var f in files)
                {
                    var p = f.Path.LocalPath;
                    if (File.Exists(p))
                    {
                        Console.Error.WriteLine($"[paste] 添加文件附件：{p}");
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
            Console.Error.WriteLine($"[paste] TryGetBitmapAsync → {(bmp is not null ? "已读到位图" : "null")}");
            if (bmp is not null)
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                try
                {
                    bmp.Save(tmp);
                    Console.Error.WriteLine($"[paste] 已保存 {tmp} ({new FileInfo(tmp).Length}B)");
                }
                catch (Exception saveEx)
                {
                    Console.Error.WriteLine($"[paste] ✖ Save 失败：{saveEx.Message}");
                }
                AddAttachment(tmp);
                return;
            }
            else
            {
                // 平台兜底：TryGetBitmapAsync 读不到的剪贴板位图（截图工具/聊天软件只放 DIB/TIFF/PNGf 的场景），
                // macOS 经 osascript 写出 PNGf；Windows 经 PowerShell 读位图
                Console.Error.WriteLine("[paste] 进入平台兜底读取剪贴板图片");
                var tmp = Path.Combine(Path.GetTempPath(), $"clip-{Guid.NewGuid():N}.png");
                if (OperatingSystem.IsMacOS())
                {
                    var script =
                        "set pngData to (the clipboard as «class PNGf»)\n" +
                        $"set f to open for access POSIX file \"{tmp}\" with write permission\n" +
                        "set eof f to 0\n" +
                        "write pngData to f\n" +
                        "close access f";
                    var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript", $"-e \"{script.Replace("\"", "\\\"")}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(4000);
                }
                else if (OperatingSystem.IsWindows())
                {
                    var script =
                        "Add-Type -AssemblyName System.Windows.Forms; " +
                        "Add-Type -AssemblyName System.Drawing; " +
                        "$i = [System.Windows.Forms.Clipboard]::GetImage(); " +
                        "$i.Save('" + tmp + "', [System.Drawing.Imaging.ImageFormat]::Png)";
                    var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(4000);
                }
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                {
                    Console.Error.WriteLine("[paste] 平台兜底已读取图片");
                    AddAttachment(tmp);
                }
                else
                {
                    Console.Error.WriteLine("[paste] 平台兜底未读到图片");
                    try { File.Delete(tmp); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            // 剪贴板读取失败时静默：不影响默认粘贴行为
            Console.Error.WriteLine($"[paste] ✖ 剪贴板读取异常：{ex.Message}");
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            return;
        }
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return;
        }
        foreach (var file in files.OfType<Avalonia.Platform.Storage.IStorageFile>())
        {
            AddAttachment(file.Path.LocalPath);
        }
    }

    /// <summary>文件选择器添加附件。</summary>
    private async Task PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = I18n.T("ui.add.attachments"),
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

    /// <summary>由气泡拖入文件时调用：确保窗口可见并批量加入附件。</summary>
    public void AddAttachmentFromDrop(IEnumerable<string> paths)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
            {
                Show();
            }
            Activate();
            foreach (var path in paths)
            {
                AddAttachment(path);
            }
        });
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
        // 点关闭 = 隐藏（气泡仍常驻；退出走气泡右键菜单）；系统退出请求（App 已判定空闲）放行真正关闭
        SaveBoundsNow();
        if (App.IsQuitting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
