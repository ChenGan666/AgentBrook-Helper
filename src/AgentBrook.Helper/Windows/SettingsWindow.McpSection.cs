using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Mcp;
using AgentBrook.Helper.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 设置窗口「MCP 管理」板块：已配置 MCP 服务器列表 + 启停/卸载。
/// 数据源与运行环境检测同源：agent.McpOptions.Servers（全量含停用）× agent.McpConnections（连接态+工具数）。
/// MCP 无安装物、纯配置型——"卸载"= 删配置：用户级条目物理删除（RemoveServer），应用级条目写
/// Removed=true 墓碑（UpsertServer，appsettings.json 全仓只读不写）；启动合并时用户级覆盖/墓碑生效。
/// 连接只在 BrookAgent.CreateAsync 建立，无运行时重连——启停/卸载保存后提示重启 Helper 生效。
/// </summary>
public partial class SettingsWindow
{
    private TextBlock? _mcpStatusText;

    private void SetMcpStatus(string text)
    {
        _mcpStatusText ??= this.FindControl<TextBlock>("McpStatusText");
        if (_mcpStatusText is not null)
        {
            _mcpStatusText.Text = text;
        }
    }

    /// <summary>重建 MCP 列表（页面激活与操作后调用；全量重建，模式同插件页）。</summary>
    private void RefreshMcpList()
    {
        var list = this.FindControl<StackPanel>("McpListPanel");
        if (list is null)
        {
            return;
        }
        list.Children.Clear();

        var agent = App.Controller.Ready ? App.Controller.Agent : null;
        if (agent is null)
        {
            list.Children.Add(new TextBlock
            {
                Text = I18n.T("ui.mcp.not.ready"),
                Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var workspaceRoot = App.Controller.Agent?.WorkspaceRoot ?? UiPrefs.WorkspaceDir();
        var userLevelNames = UserMcpConfigStore.Load(workspaceRoot)
            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var servers = agent.McpOptions.Servers;
        if (servers.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = I18n.T("ui.mcp.empty"),
                Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
                FontSize = 12.5,
            });
            return;
        }

        foreach (var server in servers)
        {
            list.Children.Add(BuildMcpRow(server, workspaceRoot, userLevelNames));
        }
    }

    /// <summary>构建一行：状态圆点 + 名称/命令摘要（左） + 启停开关 + 卸载按钮（右）。</summary>
    private Control BuildMcpRow(McpServerConfig server, string workspaceRoot, HashSet<string> userLevelNames)
    {
        var connected = App.Controller.Agent?.McpConnections
            .FirstOrDefault(c => string.Equals(c.ServerName, server.Name, StringComparison.OrdinalIgnoreCase));

        // 状态：已停用（灰）→ 已连接（绿，带工具数）→ 已启用但未连接（红）
        var (dotColor, statusText) = !server.Enabled
            ? ("#8e8e93", I18n.T("ui.mcp.status.disabled"))
            : connected is not null
                ? ("#34c759", I18n.T("ui.mcp.status.connected", connected.Tools.Count))
                : ("#ff453a", I18n.T("ui.mcp.status.not.connected"));

        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.Parse(dotColor)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameText = new TextBlock
        {
            Text = server.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(dot);
        nameRow.Children.Add(nameText);

        var commandSummary = (server.Command + " " + string.Join(" ", server.Args)).Trim();
        if (commandSummary.Length > 64)
        {
            commandSummary = commandSummary[..64] + "…";
        }

        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(nameRow);
        var cmdText = new TextBlock
        {
            Text = commandSummary,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            TextWrapping = TextWrapping.Wrap,
        };
        if (commandSummary.Length > 0)
        {
            info.Children.Add(cmdText);
        }
        var statusLine = new TextBlock
        {
            Text = statusText,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse(server.Enabled ? "#9a9a9a" : "#7a7a7a")),
        };
        info.Children.Add(statusLine);

        // 启停开关：先置初值再挂事件（避免重建列表时误触发保存）
        var toggle = new ToggleSwitch { IsChecked = server.Enabled, OnContent = "", OffContent = "" };
        toggle.IsCheckedChanged += (_, _) =>
        {
            var on = toggle.IsChecked == true;
            if (on == server.Enabled)
            {
                return;   // 与当前配置一致（重建时的初值）不动
            }
            var err = UserMcpConfigStore.UpsertServer(workspaceRoot, CloneConfig(server, enabled: on));
            SetMcpStatus(err is not null ? err : I18n.T("ui.mcp.saved.restart"));
        };

        var uninstallBtn = new Button { Content = I18n.T("ui.mcp.uninstall") };
        uninstallBtn.Classes.Add("pluginDanger");
        uninstallBtn.Click += (_, _) =>
        {
            var err = userLevelNames.Contains(server.Name)
                ? (UserMcpConfigStore.RemoveServer(workspaceRoot, server.Name) ? null : "not found")
                : UserMcpConfigStore.UpsertServer(workspaceRoot, CloneConfig(server, removed: true));   // 应用级：墓碑
            if (err is not null)
            {
                SetMcpStatus(err);
                return;
            }
            SetMcpStatus(I18n.T("ui.mcp.saved.restart"));
            RefreshMcpList();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(toggle);
        actions.Children.Add(uninstallBtn);

        var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 4) };
        dock.Children.Add(info);
        DockPanel.SetDock(info, Dock.Left);
        dock.Children.Add(actions);
        DockPanel.SetDock(actions, Dock.Right);

        var card = new Border { Padding = new Thickness(12, 10, 12, 10), Child = dock };
        card.Classes.Add("pluginCard");
        return card;
    }

    /// <summary>克隆配置用于写用户级覆盖/墓碑（Env 字典需显式拷贝，避免共享引用被后续修改）。</summary>
    private static McpServerConfig CloneConfig(McpServerConfig src, bool? enabled = null, bool removed = false) => new()
    {
        Name = src.Name,
        Enabled = enabled ?? src.Enabled,
        Command = src.Command,
        Args = [.. src.Args],
        Env = new Dictionary<string, string>(src.Env, StringComparer.OrdinalIgnoreCase),
        WorkingDirectory = src.WorkingDirectory,
        Removed = removed,
    };
}
