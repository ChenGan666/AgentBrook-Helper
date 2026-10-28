using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using System.Runtime.InteropServices;

namespace AgentBrook.Helper.Windows;

// Windows 透明区域点击穿透：命中区限定为气泡本体（含光晕/轨道/伙伴头像）与提示卡区域。
public partial class BubbleWindow
{
    /// <summary>
    /// 将气泡窗口的命中区域限定为可见的气泡本体（含光晕/轨道）和提示卡区域，
    /// 其余透明区域不再拦截鼠标，使气泡下方的桌面程序可被正常点击。
    /// 仅在 Windows 生效；macOS 的透明窗口默认已可穿透。
    /// </summary>
    private void UpdateClickThroughRegion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            var scale = RenderScaling;
            // 气泡本体 + 光晕 + 轨道图标大致覆盖范围：中心随贴边平移（_rootShift），半径 70
            var cxd = WindowWidth / 2 + _rootShift.X;
            var cyd = BubbleOffsetY + BubbleHalf + _rootShift.Y;
            var bubbleLeft = (int)Math.Round((cxd - 70) * scale);
            var bubbleTop = (int)Math.Round((cyd - 70) * scale);
            var bubbleRight = (int)Math.Round((cxd + 70) * scale);
            var bubbleBottom = (int)Math.Round((cyd + 70) * scale);
            var hRgn = CreateEllipticRgn(bubbleLeft, bubbleTop, bubbleRight, bubbleBottom);

            // 提示卡区域：居中 228 宽，从 y=110 开始（气泡缩小后上贴；有伙伴头像时下移），高度取实际渲染高度
            var toastHeight = _toastPanel.Bounds.Height;
            if (toastHeight > 0)
            {
                const double toastWidth = 228;
                var toastTopDip = _toastPanel.Margin.Top;
                const double toastLeftDip = (WindowWidth - toastWidth) / 2.0;
                var toastLeft = (int)Math.Round(toastLeftDip * scale);
                var toastTop = (int)Math.Round(toastTopDip * scale);
                var toastRight = (int)Math.Round((toastLeftDip + toastWidth) * scale);
                var toastBottom = (int)Math.Round((toastTopDip + toastHeight) * scale);
                var hRect = CreateRectRgn(toastLeft, toastTop, toastRight, toastBottom);
                CombineRgn(hRgn, hRgn, hRect, RGN_OR);
                DeleteObject(hRect);
            }

            // 伙伴气泡展开区：展开/收拢动画期间并入圆形命中区（收拢态头像在原半径 70 范围内）
            var peerRadius = _peerBubbles?.HitRadius ?? 0;
            if (peerRadius > 0)
            {
                var hPeer = CreateEllipticRgn(
                    (int)Math.Round((cxd - peerRadius) * scale), (int)Math.Round((cyd - peerRadius) * scale),
                    (int)Math.Round((cxd + peerRadius) * scale), (int)Math.Round((cyd + peerRadius) * scale));
                CombineRgn(hRgn, hRgn, hPeer, RGN_OR);
                DeleteObject(hPeer);
            }

            SetWindowRgn(hwnd, hRgn, true);
            // SetWindowRgn 接管 HRGN 所有权，此处不再 DeleteObject(hRgn)
        }
        catch
        {
            // 平台 API 失败时回退到原始矩形窗口，不阻断主流程
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    private const int RGN_OR = 2;
}
