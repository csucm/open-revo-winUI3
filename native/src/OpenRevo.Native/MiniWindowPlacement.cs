using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private const double MiniEdgeMarginDip = 2;
    private const uint DwmExtendedFrameBounds = 9;

    private double MiniPlacementScale()
    {
        uint dpi = GetDpiForWindow(captionWindow);
        return dpi == 0 ? 1 : dpi / 96d;
    }

    private void PositionMiniAtBottomRight()
    {
        if (closed || !mini) return;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var point = MiniCorner(area, AppWindow.Size, MiniPlacementScale());
        // Re-anchor only on entry/wake, using the actual final window size. Native dragging
        // remains free; no position-change callback or idle timer moves the window back.
        var current = AppWindow.Position;
        if (point.X != current.X || point.Y != current.Y) AppWindow.Move(point);
    }

    private PointInt32 MiniCorner(RectInt32 area, SizeInt32 size, double scale)
    {
        int margin = (int)Math.Round(MiniEdgeMarginDip * scale);
        int rightInset = 0, bottomInset = 0;
        // AppWindow moves the outer HWND. Compensate for the part Windows does not draw.
        if (GetWindowRect(captionWindow, out var outer) &&
            DwmGetWindowAttribute(captionWindow, DwmExtendedFrameBounds, out var visible, 16) == 0 &&
            visible.Right > visible.Left && visible.Bottom > visible.Top &&
            visible.Left >= outer.Left && visible.Top >= outer.Top &&
            visible.Right <= outer.Right && visible.Bottom <= outer.Bottom)
        {
            rightInset = outer.Right - visible.Right;
            bottomInset = outer.Bottom - visible.Bottom;
        }
        return new PointInt32(Math.Max(area.X, area.X + area.Width - size.Width - margin + rightInset),
            Math.Max(area.Y, area.Y + area.Height - size.Height - margin + bottomInset));
    }
}
