using Windows.Graphics;

namespace OpenRevo.Native;

// The approved C timeline. Screen coordinates are physical pixels; the travel
// cap is converted once from DIP using the source window's rasterization scale.
internal static class PanelHandoffGeometry
{
    internal const int DurationMilliseconds = 220;
    internal static double Smooth(double value)
    {
        double t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }
    internal static (double X, double Y) Travel(RectInt32 large, RectInt32 small, double scale)
    {
        double x = small.X + small.Width / 2d - large.X - large.Width / 2d;
        double y = small.Y + small.Height / 2d - large.Y - large.Height / 2d;
        double limit = 16 * scale, length = Math.Sqrt(x * x + y * y);
        double factor = length > limit ? limit / length : 1;
        return (x * factor, y * factor);
    }
    internal static (double Large, double Mini) Opacity(double progress) =>
        (1 - Smooth(progress / .67), Smooth((progress - .36) / .64));
}
