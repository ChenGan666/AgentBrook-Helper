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
            newBtn.Click += async (_, _) => await _controller.NewSessionAsync();
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
                    };
                    b.Click += (_, _) =>
                    {
                        onPick();
                        _fullPopup?.IsOpen = false;
                    };
                    panel.Children.Add(b);
                }
                AddItem("每次询问（更安全）", !_controller.FullAccess,
                    () => { _controller.FullAccess = false; UpdateFullAccessVisual(); });
                AddItem("完全访问（自动批准，重启复位）", _controller.FullAccess,
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
                        Text = "（启动中…）",
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
                    var manage = new Button { Classes = { "menuItem" }, Content = "管理模型…" };
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

        Opened += (_, _) =>
        {
            SetStatus(BrookRunState.Idle, null, withModel: true);
            UpdateModelChip();
            UpdateFullAccessVisual();
            ApplyIdentityUi();
            if (_chatPanel!.Children.Count == 0)
            {
                var name = AssistantIdentity.Current;
                AddAssistantBubble($"你好，我是 {name}。\n点屏幕上的悬浮气泡打开这里，直接打字聊天。命令执行前我会先征求你的同意。");
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>把个性化设置（名字）应用到窗口标题与顶栏。</summary>
    public void ApplyIdentityUi()
    {
        var name = AssistantIdentity.Current;
        Title = $"{name} 助手";
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
            AddErrorBubble($"切换供应商失败：{ex.Message}");
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

    // ───────────────────────── 控制器事件 → 渲染 ─────────────────────────
    private int _eventSeq;

    private void OnControllerEvent(BrookEvent e)
    {
        var seq = System.Threading.Interlocked.Increment(ref _eventSeq);
        Trace($"ev#{seq} {e.GetType().Name}");
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                RenderControllerEvent(e);
            }
            catch (Exception ex)
            {
                AddErrorBubble("界面渲染异常（已拦截）：" + ex.Message);
            }
        });
    }

    private static int _renderCount;

    private static void Trace(string what, string? detail = null)
    {
        try
        {
            System.IO.File.AppendAllText("/tmp/ui_trace.log",
                $"{DateTime.Now:HH:mm:ss.fff} [{what}] {detail ?? ""}\n");
        }
        catch { }
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
                    AddUserBubble("（已开启新对话）");
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
                Trace("TurnCompleted", $"turn={_controller.TurnSeq}");
                StopThinkLine();
                CollapseExecGroup();   // 完成后收起执行过程，保持主内容清爽
                RemoveStreamBubble();  // 流式纯文本让位给 Markdown 渲染
                ShowFinalAnswer();
                AppendUsageLine();
                DumpPanel();           // 终态对账：确认面板最终子控件构成
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += (_, _) => { t.Stop(); Trace("late-dump", "3s-after"); DumpPanel(); };
                t.Start();
                break;
        }
    }

    private void OnStateChanged(BrookRunState state, string? detail)
    {
        Trace("OnStateChanged", $"called on uiThread={Dispatcher.UIThread.CheckAccess()} state={state}");
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
            _sendBtn!.IsEnabled = !busy;
            _inputBox!.IsEnabled = !busy;
        });
    }

    private DateTime _lastSendRequest = DateTime.MinValue;

    private async Task SendInputAsync()
    {
        var text = _inputBox?.Text?.Trim() ?? "";
        if (text.Length == 0 && _attachments.Count == 0)
        {
            return;
        }
        if (_controller.Busy || !_controller.Ready)
        {
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
    /// <summary>把工具调用翻译成用户看得懂的一句话提要（本地规则，无需模型参与）。</summary>
    private static string SummarizeApproval(string tool, string? argsJson)
    {
        var primary = FirstStringArg(argsJson);
        return tool switch
        {
            "run_command" => DescribeCommand(primary),
            "read_file" => string.IsNullOrEmpty(primary) ? "查看工作区文件" : $"查看文件「{Cut(primary)}」的内容",
            "append_file" => string.IsNullOrEmpty(primary) ? "往文件追加内容" : $"往文件「{Cut(primary)}」末尾追加内容",
            "write_file" => string.IsNullOrEmpty(primary) ? "写入工作区文件" : $"写入文件「{Cut(primary)}」",
            "list_dir" => string.IsNullOrEmpty(primary) || primary == "." ? "查看工作区目录" : $"查看目录「{Cut(primary)}」",
            "memory_read" => "读取 Brook 的长期记忆",
            "memory_write" or "memory_append" => "更新 Brook 的长期记忆",
            "create_skill" => string.IsNullOrEmpty(primary) ? "创建一个新技能" : $"创建新技能「{Cut(primary)}」",
            "install_skill" or "skill_market" => string.IsNullOrEmpty(primary) ? "从技能市场安装技能" : $"安装技能「{Cut(primary)}」",
            "load_skill" or "list_skills" => "加载/查看可用技能",
            "mcp_add_server" => string.IsNullOrEmpty(primary) ? "添加 MCP 插件" : $"添加 MCP 插件「{Cut(primary)}」",
            "mcp_list_servers" => "查看已安装的 MCP 插件",
            "spawn_worker" => string.IsNullOrEmpty(primary) ? "派出一个工作助手" : $"派出工作助手「{Cut(primary)}」",
            "assign_task" => "给工作助手分派任务",
            "send_message" or "send_to_worker" => "给工作助手发消息",
            "broadcast_to_workers" => "给所有工作助手广播消息",
            "check_messages" or "read_shared" or "start_project" or "list_workers" => "查看协作状态",
            "open_url" => string.IsNullOrEmpty(primary) ? "打开网页" : $"打开网页 {Cut(primary, 60)}",
            "ask_user" => "向你提问",
            _ => string.IsNullOrEmpty(primary) ? $"调用 {tool}" : $"调用 {tool}（{Cut(primary, 40)}）",
        };
    }

    /// <summary>把 shell 命令翻译成人话；识别不了就显示命令本身（截断）。</summary>
    private static string DescribeCommand(string cmd)
    {
        var c = (cmd ?? "").Trim();
        if (c.Length == 0)
        {
            return "在终端执行命令";
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
            "open" => $"打开 {Cut(arg)}",
            "ls" => $"查看目录 {Cut(arg)}",
            "cat" or "head" or "tail" or "less" or "more" => $"查看文件 {Cut(arg)}",
            "mkdir" => $"创建文件夹 {Cut(arg)}",
            "touch" => $"新建文件 {Cut(arg)}",
            "rm" => $"删除 {Cut(arg)}",
            "mv" => $"移动/重命名 {Cut(arg)}",
            "cp" => $"复制 {Cut(arg)}",
            "date" => "查看当前时间",
            "pwd" => "查看当前目录",
            "echo" => "输出一段文字",
            "curl" or "wget" => $"从网络获取 {Cut(arg, 50)}",
            "git" => $"执行 git {parts.Skip(1).FirstOrDefault() ?? "操作"}",
            "python" or "python3" or "node" => $"运行脚本 {Cut(arg)}",
            "dotnet" => $"运行 dotnet {parts.Skip(1).FirstOrDefault() ?? ""}",
            "grep" or "rg" => $"搜索 {Cut(arg)}",
            "cd" => $"切换目录 {Cut(arg)}",
            "chmod" => $"修改文件权限 {Cut(arg)}",
            _ => null,
        };
        if (body is null)
        {
            return $"在终端执行：{Cut(c, 70)}";
        }
        return more
            ? $"在终端开始执行：{body}（之后还有后续步骤）"
            : $"在终端执行：{body}";
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
            Text = "Brook 想执行一个操作，需要你批准",
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
            Text = $"操作类型：{tool}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
        });
        // 原始指令默认折叠：想看细节的用户再展开
        if (!string.IsNullOrEmpty(argsJson))
        {
            stack.Children.Add(new Expander
            {
                Classes = { "execGroup" },
                Header = new TextBlock { Text = "查看原始指令", FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")) },
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

        var reasonBox = new TextBox { Watermark = "拒绝的话可以写理由（可选）", FontSize = 13.5 };
        var ok = new Button { Content = "✓ 批准", Classes = { "actionOk" } };
        var full = new Button { Content = "完全访问", Classes = { "actionFull" } };
        var no = new Button { Content = "✗ 拒绝", Classes = { "actionNo" } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(ok);
        row.Children.Add(full);
        row.Children.Add(no);
        var hint = new TextBlock
        {
            Text = "批准后 Brook 才会真正执行这个操作。",
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
            hint.Text = "✔ 已批准";
            tcs.TrySetResult(ApprovalDecision.Approve());
        };
        full.Click += (_, _) =>
        {
            LockButtons();
            card.Classes.Add("resolved");
            hint.Text = "已开启完全访问：本次及后续操作自动批准（重启后复位）";
            _controller.FullAccess = true;
            tcs.TrySetResult(ApprovalDecision.Approve());
        };
        no.Click += (_, _) =>
        {
            LockButtons();
            var reason = reasonBox.Text?.Trim();
            card.Classes.Add("resolved");
            hint.Text = $"✘ 已拒绝{(string.IsNullOrEmpty(reason) ? "" : $"：{reason}")}";
            tcs.TrySetResult(ApprovalDecision.Reject(reason));
        };

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
            Text = "Brook 有问题想问你",
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

        var answerBox = new TextBox { Watermark = "输入你的回答…", FontSize = 15 };
        var answerBtn = new Button { Content = "回答", Classes = { "actionOk" } };
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
                Text = $"你回答了：{text}",
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
            Text = "思考",
        };
        _chatPanel!.Children.Add(_thinkLine);
        ScrollToEnd();

        _thinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _thinkTimer.Tick += (_, _) =>
        {
            var secs = (int)(DateTime.Now - _thinkStart).TotalSeconds;
            _thinkLine!.Text = $"思考 · 持续了 {secs} 秒";
        };
        _thinkTimer.Start();
    }

    /// <summary>定格思考行（保留在历史中，弱化灰显示）。</summary>
    private void StopThinkLine()
    {
        _thinkTimer?.Stop();
        _thinkTimer = null;
        if (_thinkLine is not null && _thinkLine.Text.StartsWith("思考"))
        {
            var secs = (int)(DateTime.Now - _thinkStart).TotalSeconds;
            _thinkLine.Text = $"思考 · 持续了 {secs} 秒";
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
            Text = $"Token 本轮 输入 {u.In:N0} / 输出 {u.Out:N0} / 合计 {u.Total:N0} ｜ 应用累计 {_cumTokens:N0}",
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
                        Text = $"附件：{a.Name}",
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
                    });
                }
            }
        }
        if (text.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        }
        var border = new Border { Classes = { "userBubble" }, Child = stack };
        _chatPanel!.Children.Add(border);
        ScrollToEnd();
    }

    private Control AddAssistantBubble(string? initialText = null)
    {
        // 流式期间用纯文本（高频更新稳定），回合完成后替换为 Markdown 渲染
        Control viewer = new SelectableTextBlock
        {
            Text = initialText ?? "",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#e6e6e6")),
        };
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
            Trace("stream-create", $"panelChildren={_chatPanel!.Children.Count}");
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

    /// <summary>调试：递归遍历聊天区视觉树，列出每个含文本控件的路径与长度（定位重复渲染）。</summary>
    private void DumpPanel()
    {
        var answer = _controller.FinalAnswer ?? "";
        Trace("answer", $"len={answer.Length} head20=\"{answer[..Math.Min(20, answer.Length)].Replace("\n", " ")}\" tail20=\"{answer[^Math.Min(20, answer.Length)..].Replace("\n", " ")}\"");
        DumpVisual(this, 0, "WIN");
        Trace("panel-top", string.Join(" | ", _chatPanel!.Children.Select(c =>
        {
            var d = c.GetType().Name;
            if (c is Border bb && bb.Tag is string tg) d += "#" + tg;
            return d;
        })));
    }

    private void DumpVisual(Avalonia.Visual node, int depth, string path)
    {
        if (depth > 30)
        {
            return;
        }
        string? txt = node switch
        {
            SelectableTextBlock stb => stb.Text,
            TextBlock tb => tb.Text,
            _ => null,
        };
        var desc = node.GetType().Name;
        if (node is Border b && b.Tag is string tag)
        {
            desc += $"#{tag}";
        }
        if (txt is not null && txt.Length > 0)
        {
            var head = txt.Length > 22 ? txt[..22].Replace("\n", " ") : txt;
            var tail = txt.Length > 22 ? txt[^10..].Replace("\n", " ") : "";
            Trace("node", $"{path}/{desc} len={txt.Length} head=\"{head}\" tail=\"{tail}\"");
        }
        var kids = node.GetVisualChildren().ToList();
        if (kids.Count > 0 && depth <= 2)
        {
            Trace("node", $"{path}/{desc} children={kids.Count}");
        }
        for (int i = 0; i < kids.Count; i++)
        {
            DumpVisual(kids[i], depth + 1, $"{path}/{desc}[{i}]");
        }
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
        Trace("ShowFinalAnswer", $"answerLen={answer.Length} panelChildren={_chatPanel!.Children.Count}");
        DumpPanel();
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
                Text = $"出错了：{message}",
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
        _execHeader!.Text = _execCount == 1 ? "执行过程 · 1 步" : $"执行过程 · {_execCount} 步";

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
        _execHeader = new TextBlock { Text = "执行过程" };
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
            chip.Text = _controller.Ready ? _controller.Agent!.CurrentModel : "模型";
        }
        catch
        {
            chip.Text = "模型";
        }
    }

    /// <summary>同步完全访问开关的琥珀/灰色视觉。</summary>
    private void UpdateFullAccessVisual()
    {
        var on = _controller.FullAccess;
        var color = Color.Parse(on ? "#d29922" : "#9a9a9a");
        var brush = new SolidColorBrush(color);
        var text = this.FindControl<TextBlock>("FullAccessText");
        var icon = this.FindControl<PathIcon>("FullAccessIcon");
        if (text is not null)
        {
            text.Text = on ? "完全访问·开" : "完全访问";
            text.Foreground = brush;
        }
        if (icon is not null)
        {
            icon.Foreground = brush;
        }
    }

    private void SetStatus(BrookRunState state, string? detail, bool withModel = false)
    {
        Trace("SetStatus", $"{state} ready={_controller.Ready} agentNull={_controller.Agent is null}");
        var text = state switch
        {
            BrookRunState.Idle => withModel && _controller.Ready
                ? $"空闲 · {_controller.Agent!.CurrentModel}"
                : _controller.Ready ? "空闲" : "启动中…",
            BrookRunState.Thinking => "思考中…",
            BrookRunState.CallingTool => $"干活中 · {detail}",
            BrookRunState.AwaitingApproval => $"等你批准 · {detail}",
            BrookRunState.AwaitingUserAnswer => "等你回答",
            _ => "空闲",
        };
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
            Trace("paste", $"Cmd+V detected mods={e.KeyModifiers}");
            _ = TryAttachFromClipboardAsync();
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendInputAsync();
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
            Trace("paste", $"files={files?.Count() ?? -1}");
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
            Trace("paste", $"bitmap={(bmp is not null ? "yes" : "null")}");
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
            Title = "添加附件",
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
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
