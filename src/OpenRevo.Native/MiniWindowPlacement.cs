using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private const double MiniEdgeMarginDip = 6;

    private void PositionMiniAtBottomRight()
    {
        if(closed||!mini)return;
        var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
        double scale=GetDpiForWindow(captionWindow)/96d;if(scale<=0)scale=1;
        int margin=(int)Math.Round(MiniEdgeMarginDip*scale);
        var size=AppWindow.Size;
        var point=new PointInt32(Math.Max(area.X,area.X+area.Width-size.Width-margin),
            Math.Max(area.Y,area.Y+area.Height-size.Height-margin));
        // Re-anchor only on entry/wake, using the actual final window size. Native dragging
        // remains free; no position-change callback or idle timer moves the window back.
        AppWindow.Move(point);
    }
}
