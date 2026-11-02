using System.Collections.Concurrent;
using AgentBrook.Helper.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 气泡上半圈的已启用插件图标（部分类，v1.1）：
/// 与下半圈局域网 Helper 头像同款交互——环绕上半圈排布、悬停放大、点击打开插件设置对话框。
/// 只显示"已启用"且携带 icon.png 的插件；启停/装卸后由 RefreshPluginOrbit 刷新。
/// </summary>
public partial class BubbleWindow
{
    private readonly System.Collections.Generic.List<(Image Img, double X, double Y)> _pluginIconHosts = [];

    /// <summary>重建上半圈插件图标（启停/装卸/换图标后调用；PointerEntered 时也会自动刷新）。</summary>
    public void RefreshPluginOrbit()
    {
        var layer = PluginIconLayer;   // x:Name 生成字段
        if (layer is null)
        {
            return;
        }
        layer.Children.Clear();
        _pluginIconHosts.Clear();

        var pm = App.Controller.Agent?.PluginManager;
        if (pm is null)
        {
            return;
        }

        // 已启用 且 带 icon.png 的插件
        var items = new List<(string Id, string Name, string Path)>();
        foreach (var (id, entry) in pm.ListInstalled())
        {
            if (!entry.Enabled)
            {
                continue;
            }
            var path = pm.GetIconPath(id);
            if (path is not null)
            {
                items.Add((id, id, path));
            }
        }
        if (items.Count == 0)
        {
            return;
        }

        var (cx, cy, _) = BubbleLayout();
        var r = BubbleHalf + 18;                       // 上半圈半径：贴气泡外沿
        var n = items.Count;
        for (var i = 0; i < n; i++)
        {
            var (id, _, path) = items[i];
            var angle = Math.PI * (1 - (i + 0.5) / n); // 上半圆均匀分布（180°→0°，从左到右）
            var x = cx + r * Math.Cos(angle) - 14;     // 图标 28×28 的左上角
            var y = cy - r * Math.Sin(angle) - 14;

            var img = new Image
            {
                Source = new Bitmap(path),
                Width = 28, Height = 28,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, y);

            // 悬停放大：28 → 36（保持圆心不动）
            img.PointerEntered += (_, _) =>
            {
                img.Width = 36; img.Height = 36;
                Canvas.SetLeft(img, x - 4);
                Canvas.SetTop(img, y - 4);
                ToolTip.SetTip(img, id);
            };
            img.PointerExited += (_, _) =>
            {
                img.Width = 28; img.Height = 28;
                Canvas.SetLeft(img, x);
                Canvas.SetTop(img, y);
            };
            img.PointerReleased += (_, _) => OpenPluginSettings(id);

            layer.Children.Add(img);
            _pluginIconHosts.Add((img, x, y));
        }
    }

    private PluginSettingsDialog? _pluginSettingsDialog;

    /// <summary>点击图标：直接打开该插件的设置对话框（模态于气泡）。</summary>
    private void OpenPluginSettings(string pluginId)
    {
        var pm = App.Controller.Agent?.PluginManager;
        var contributor = pm?.GetSettingsContributor(pluginId);
        if (contributor is null)
        {
            return;
        }
        ClosePluginSettingsDialog();
        var dialog = new PluginSettingsDialog(pluginId, pluginId, contributor, pm?.GetIconPath(pluginId));
        dialog.Build();
        dialog.Closed += (_, _) => { if (_pluginSettingsDialog == dialog) _pluginSettingsDialog = null; };
        _pluginSettingsDialog = dialog;
        _ = dialog.ShowDialog(this);
    }

    private void ClosePluginSettingsDialog()
    {
        if (_pluginSettingsDialog is { } d)
        {
            _pluginSettingsDialog = null;
            try { d.Close(); } catch { }
        }
    }
}
