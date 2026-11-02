using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AgentBrook.Helper.Support;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 头像制作器：选择本地图片 → 拖动定位 + 滑杆缩放框定正方形裁切区 → 导出 64×64 PNG。
/// 结果通过 <see cref="ResultIcon"/> 返回（"img:avatars/helper-avatar.png"），由设置页接管应用。
/// </summary>
public sealed partial class AvatarMakerWindow : Window
{
    private const double View = 320;          // 预览画布边长（裁剪区）
    private const int Output = 64;            // 输出像素边长
    private const string RelPath = "avatars/helper-avatar.png";

    private Bitmap? _bitmap;
    private double _baseScale = 1;            // UniformToFill 基准比例（src → 画布坐标）
    private double _zoom = 1;
    private double _offsetX, _offsetY;        // 画布坐标下图片左上角位置（≤0）
    private bool _dragging;
    private Point _dragStart;
    private double _dragOriginX, _dragOriginY;

    /// <summary>制作成功后的 icon 值（"img:..."）；取消为 null。</summary>
    public string? ResultIcon { get; private set; }

    public AvatarMakerWindow()
    {
        InitializeComponent();
        var canvas = this.FindControl<Canvas>("PreviewCanvas")!;
        var pick = this.FindControl<Button>("PickBtn")!;
        var ok = this.FindControl<Button>("OkBtn")!;
        var cancel = this.FindControl<Button>("CancelBtn")!;
        var zoom = this.FindControl<Slider>("ZoomSlider")!;

        pick.Click += async (_, _) => await PickAsync();
        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) => Save();
        zoom.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                _zoom = zoom.Value / 100.0;
                ClampOffsets();
                LayoutImage();
            }
        };

        canvas.PointerPressed += (_, e) =>
        {
            if (_bitmap is null) return;
            _dragging = true;
            _dragStart = e.GetPosition(canvas);
            _dragOriginX = _offsetX;
            _dragOriginY = _offsetY;
            e.Pointer.Capture(canvas);
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            var pos = e.GetPosition(canvas);
            _offsetX = _dragOriginX + pos.X - _dragStart.X;
            _offsetY = _dragOriginY + pos.Y - _dragStart.Y;
            ClampOffsets();
            LayoutImage();
        };
        canvas.PointerReleased += (_, _) => { _dragging = false; };
        canvas.PointerCaptureLost += (_, _) => { _dragging = false; };
        canvas.PointerWheelChanged += (_, e) =>
        {
            if (_bitmap is null) return;
            zoom.Value = Math.Clamp(zoom.Value + (e.Delta.Y > 0 ? 10 : -10), zoom.Minimum, zoom.Maximum);
        };
    }

    private async Task PickAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择头像图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }
            ],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            var bmp = new Bitmap(stream);
            if (bmp.PixelSize.Width < Output || bmp.PixelSize.Height < Output)
            {
                SetHint("图片太小：宽高至少需要 64 像素");
                return;
            }
            _bitmap = bmp;
            this.FindControl<TextBlock>("EmptyHint")!.IsVisible = false;
            FitImage();
        }
        catch (Exception ex)
        {
            SetHint($"图片读取失败：{ex.Message}");
        }
    }

    private void SetHint(string text)
    {
        var hint = this.FindControl<TextBlock>("EmptyHint")!;
        hint.Text = text;
        hint.IsVisible = true;
    }

    /// <summary>初始适配：图片按 UniformToFill 铺满画布并居中（默认裁正中）。</summary>
    private void FitImage()
    {
        var bmp = _bitmap!;
        _baseScale = Math.Max(View / bmp.PixelSize.Width, View / bmp.PixelSize.Height);
        _offsetX = (View - bmp.PixelSize.Width * _baseScale * _zoom) / 2;
        _offsetY = (View - bmp.PixelSize.Height * _baseScale * _zoom) / 2;
        ClampOffsets();
        LayoutImage();
    }

    /// <summary>约束：图片必须完整覆盖 320×320 裁剪区（offset ∈ [View-dispSize, 0]）。</summary>
    private void ClampOffsets()
    {
        if (_bitmap is null) return;
        var scale = _baseScale * _zoom;
        _offsetX = Math.Clamp(_offsetX, View - _bitmap.PixelSize.Width * scale, 0);
        _offsetY = Math.Clamp(_offsetY, View - _bitmap.PixelSize.Height * scale, 0);
    }

    private void LayoutImage()
    {
        if (_bitmap is null) return;
        var img = this.FindControl<Image>("SourceImage")!;
        var scale = _baseScale * _zoom;
        img.Source = _bitmap;
        img.Width = _bitmap.PixelSize.Width * scale;
        img.Height = _bitmap.PixelSize.Height * scale;
        Canvas.SetLeft(img, _offsetX);
        Canvas.SetTop(img, _offsetY);
    }

    /// <summary>画布裁剪区 → 源图像素矩形 → 缩放渲染为 64×64 PNG 写入工作区。</summary>
    private void Save()
    {
        if (_bitmap is null)
        {
            SetHint("请先选择图片");
            return;
        }
        try
        {
            // 所见即所得：离屏复刻预览画布的完整布局（同一 bitmap/缩放/偏移），
            // 直接按视觉 1:1 渲染 320×320，避免 srcX/srcY 反算带来的取框偏差。
            var scale = _baseScale * _zoom;
            var img = new Image
            {
                Source = _bitmap,
                Stretch = Stretch.None,
                Width = _bitmap.PixelSize.Width * scale,
                Height = _bitmap.PixelSize.Height * scale,
            };
            var canvas = new Canvas { Width = View, Height = View, ClipToBounds = true };
            canvas.Children.Add(img);
            Canvas.SetLeft(img, _offsetX);
            Canvas.SetTop(img, _offsetY);
            canvas.Measure(new Size(View, View));
            canvas.Arrange(new Rect(0, 0, View, View));
            using var scene = new RenderTargetBitmap(new PixelSize((int)View, (int)View), new Vector(96, 96));
            scene.Render(canvas);

            // 圆形透明背景：圆角 Border 裁剪场景 → 缩至 64×64 保留 alpha
            var round = new Border
            {
                Width = Output,
                Height = Output,
                CornerRadius = new CornerRadius(Output / 2.0),
                ClipToBounds = true,
                Background = Brushes.Transparent,
                Child = new Image { Source = scene, Stretch = Stretch.Fill },
            };
            round.Measure(new Size(Output, Output));
            round.Arrange(new Rect(0, 0, Output, Output));
            using var rtb = new RenderTargetBitmap(new PixelSize(Output, Output), new Vector(96, 96));
            rtb.Render(round);

            var abs = Path.Combine(UiPrefs.WorkspaceDir(), RelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            using (var fs = File.Create(abs))
            {
                rtb.Save(fs);   // RenderTargetBitmap.Save → PNG
            }
            Close($"img:{RelPath}");
        }
        catch (Exception ex)
        {
            SetHint($"保存失败：{ex.Message}");
        }
    }

    private void Close(string? result)
    {
        ResultIcon = result;
        Close();
    }
}
