using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AgentBrook.Helper.Support;

namespace AgentBrook.Helper.Views;

/// <summary>
/// Helper / Peer 图标编解码：本地 Config.Icon 存 emoji 或 "img:<工作区相对路径>"；
/// 出站（profile / senderIcon）时图片图标转 data-URL，复用现有字符串通道无需改协议。
/// </summary>
public static class PeerIconCodec
{
    public const string LocalPrefix = "img:";           // 本地存储形：img:avatars/helper-avatar.png
    public const string WirePrefix = "data:image/";     // 出站形：data:image/png;base64,...

    public static bool IsImageIcon(string? icon) =>
        icon?.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase) == true ||
        icon?.StartsWith(WirePrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>文本上下文兜底：图片图标无法塞进拼接字符串时用该占位 emoji。</summary>
    public static string ForText(string? icon) =>
        IsImageIcon(icon) ? "🖼️" : (string.IsNullOrWhiteSpace(icon) ? "🤖" : icon!);

    /// <summary>img: 相对路径 → 工作区绝对路径（不存在返回 null）。</summary>
    public static string? ResolveLocalPath(string icon)
    {
        if (!icon.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = icon[LocalPrefix.Length..];
        var abs = Path.IsPathRooted(rel) ? rel : Path.Combine(UiPrefs.WorkspaceDir(), rel);
        return File.Exists(abs) ? abs : null;
    }

    /// <summary>本地 icon → 出站 icon：img: 读文件转 data-URL；读不到回退 emoji。</summary>
    public static string ToWire(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "🤖";
        if (!icon.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase)) return icon;
        var abs = ResolveLocalPath(icon);
        if (abs is null) return "🤖";
        try
        {
            var bytes = File.ReadAllBytes(abs);
            return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return "🤖";
        }
    }

    /// <summary>icon 字符串 → 可绘制位图（img: 文件 / data: 内联），失败返回 null。</summary>
    public static Bitmap? TryDecode(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        try
        {
            var abs = ResolveLocalPath(icon);
            if (abs is not null) return new Bitmap(abs);
            if (icon.StartsWith(WirePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var comma = icon.IndexOf(',');
                if (comma > 0)
                {
                    var bytes = Convert.FromBase64String(icon[(comma + 1)..]);
                    return new Bitmap(new MemoryStream(bytes));
                }
            }
        }
        catch
        {
            // 图片损坏/格式不支持：交给调用方回退 emoji
        }
        return null;
    }
}

/// <summary>
/// 图标渲染控件：emoji → TextBlock；img:/data: 图片 → Image（自动回退 emoji 占位）。
/// 用法：<views:PeerIconView Icon="{Binding Icon}" Size="16"/>。
/// </summary>
public sealed class PeerIconView : ContentControl
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<PeerIconView, string?>(nameof(Icon));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<PeerIconView, double>(nameof(Size), 16d);

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    static PeerIconView()
    {
        IconProperty.Changed.AddClassHandler<PeerIconView>((v, _) => v.Rebuild());
        SizeProperty.Changed.AddClassHandler<PeerIconView>((v, _) => v.Rebuild());
    }

    private void Rebuild()
    {
        var icon = Icon;
        var size = Size;
        var bmp = PeerIconCodec.TryDecode(icon);
        if (bmp is not null)
        {
            // 圆形头像：圆角 Border 裁剪图片，与导出端（圆 PNG）/气泡头像语义一致
            Content = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2.0),
                ClipToBounds = true,
                Child = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }
        Content = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(icon) ? "🤖" : icon,
            FontSize = size * 0.9,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }
}
