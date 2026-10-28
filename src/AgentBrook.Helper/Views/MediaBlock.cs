using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System.Net.Http;

namespace AgentBrook.Helper.Views;

/// <summary>
/// 对话内媒体块：图片（本地/远程，自适应大小）/ 视频 / 音频卡片。
/// 远程媒体先下载到 {workspace}/media/ 缓存；视频/音频走系统默认播放器（零依赖）。
/// 统一提供"打开"与"另存为"。
/// </summary>
public static class MediaBlock
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    private static readonly string[] VideoExts = [".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v"];
    private static readonly string[] AudioExts = [".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac"];

    public enum Kind { None, Image, Video, Audio }

    /// <summary>按扩展名识别媒体类型（URL/本地路径皆可；data: 前缀视为图片）</summary>
    public static Kind Detect(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return Kind.None;
        if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return Kind.Image;
        var path = src;
        var q = path.IndexOfAny(['?', '#']);
        if (q >= 0) path = path[..q];
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExts.Contains(ext)) return Kind.Image;
        if (VideoExts.Contains(ext)) return Kind.Video;
        if (AudioExts.Contains(ext)) return Kind.Audio;
        return Kind.None;
    }

    /// <summary>是否为 http(s) 远程资源</summary>
    public static bool IsRemote(string src) =>
        src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>媒体本地缓存目录：{workspace}/media/；workspace 不可用时退系统临时目录</summary>
    public static string CacheDir()
    {
        var ws = App.Controller.Agent?.WorkspaceRoot;
        var dir = string.IsNullOrWhiteSpace(ws)
            ? Path.Combine(Path.GetTempPath(), "agentbrook_media")
            : Path.Combine(ws, "media");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>远程资源下载到缓存（重名用 URL hash 区分），本地路径直接返回</summary>
    public static async Task<string?> EnsureLocalAsync(string src)
    {
        if (!IsRemote(src)) return File.Exists(src) ? src : null;
        try
        {
            var url = new Uri(src);
            var name = Path.GetFileName(url.LocalPath);
            if (string.IsNullOrWhiteSpace(name) || !name.Contains('.')) name = "media.bin";
            var abs = Path.Combine(CacheDir(), $"{Math.Abs(src.GetHashCode()):x8}_{name}");
            if (!File.Exists(abs))
            {
                var tmp = abs + ".part";
                var bytes = await Http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, abs, overwrite: true);
            }
            return abs;
        }
        catch { return null; }
    }

    /// <summary>构建媒体块控件：图片自适应显示；视频/音频渲染卡片（打开/另存为）</summary>
    public static Control Build(string src)
    {
        var kind = Detect(src);
        return kind switch
        {
            Kind.Image => BuildImage(src),
            Kind.Video or Kind.Audio => BuildMediaCard(src, kind),
            _ => BuildFallback(src),
        };
    }

    // ---------------- 图片：自适应 + 点击打开 + 另存为 ----------------

    private static Control BuildImage(string src)
    {
        var image = new Image
        {
            // Uniform 按原比例缩放；MaxWidth 与气泡宽度对齐，超大图等比缩小、小图不放大
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = 380,
            Margin = new Thickness(0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = image,
        };

        var local = new TaskCompletionSource<string?>();
        if (IsRemote(src))
        {
            // 远程图：先后台下载缓存，完成后回填 Source（不占 UI 线程）
            _ = Task.Run(async () =>
            {
                var path = await EnsureLocalAsync(src);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (path != null) image.Source = new Bitmap(path);
                    else image.Source = null;
                    local.TrySetResult(path);
                });
            });
        }
        else if (File.Exists(src))
        {
            image.Source = new Bitmap(src);
            local.TrySetResult(src);
        }
        else
        {
            local.TrySetResult(null);
        }

        ToolTip.SetTip(border, "点击打开原图 / 右键另存为");
        border.PointerReleased += (_, _) => _ = local.Task.ContinueWith(
            t => MarkdownView.OpenTarget(t.Result ?? src), TaskScheduler.Default);
        border.ContextMenu = BuildMenu(src, local.Task);
        return border;
    }

    // ---------------- 视频/音频：卡片 + 打开/另存为 ----------------

    private static Control BuildMediaCard(string src, Kind kind)
    {
        var icon = kind == Kind.Video ? "🎬" : "🎵";
        var label = kind == Kind.Video ? "视频" : "音频";
        var name = FileNameOf(src);

        var local = IsRemote(src) ? null : (File.Exists(src) ? src : null);
        var localTask = new TaskCompletionSource<string?>();
        if (local != null) localTask.TrySetResult(local);
        else _ = Task.Run(async () =>
        {
            var path = await EnsureLocalAsync(src);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => localTask.TrySetResult(path));
        });

        var openBtn = new Button
        {
            Content = "▶ 打开",
            Padding = new Thickness(10, 4),
            Background = new SolidColorBrush(Color.Parse("#3a6ea5")),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        openBtn.Click += async (_, _) =>
        {
            var path = await localTask.Task;
            MarkdownView.OpenTarget(path ?? src);
        };

        var saveBtn = new Button
        {
            Content = "💾 另存为",
            Padding = new Thickness(10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#3d3d3d")),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        saveBtn.Click += async (_, _) => await SaveAsAsync(saveBtn, src, await localTask.Task);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#242a33")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = icon, FontSize = 26, VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = $"{label}：{name}", Foreground = new SolidColorBrush(Color.Parse("#e6e6e6")), FontSize = 13.5, MaxWidth = 300, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0), Children = { openBtn, saveBtn } },
                        },
                    },
                },
            },
        };
        card.ContextMenu = BuildMenu(src, localTask.Task);
        return card;
    }

    private static Control BuildFallback(string src) => new TextBlock
    {
        Text = $"[媒体：{src}]",
        Foreground = new SolidColorBrush(Color.Parse("#6cb6ff")),
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    // ---------------- 另存为 ----------------

    /// <summary>右键菜单：打开 / 另存为</summary>
    private static ContextMenu BuildMenu(string src, Task<string?> localTask)
    {
        var open = new MenuItem { Header = "打开" };
        open.Click += async (_, _) => MarkdownView.OpenTarget(await localTask ?? src);
        var save = new MenuItem { Header = "另存为…" };
        save.Click += async (_, _) => await SaveAsAsync(null, src, await localTask);
        return new ContextMenu { Items = { open, save } };
    }

    private static async Task SaveAsAsync(Control? anchor, string src, string? localPath)
    {
        try
        {
            var top = anchor != null ? TopLevel.GetTopLevel(anchor) : null;
            top ??= Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                ? TopLevel.GetTopLevel(d.MainWindow) : null;
            if (top?.StorageProvider is not { CanSave: true } sp) return;

            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为",
                SuggestedFileName = FileNameOf(src),
            });
            if (file == null) return;

            var bytes = localPath != null && File.Exists(localPath)
                ? await File.ReadAllBytesAsync(localPath)
                : await Http.GetByteArrayAsync(src);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
        }
        catch { }
    }

    private static string FileNameOf(string src)
    {
        try
        {
            var path = IsRemote(src) ? new Uri(src).LocalPath : src;
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? "media" : name;
        }
        catch { return "media"; }
    }
}
