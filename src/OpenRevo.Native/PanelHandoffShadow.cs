namespace OpenRevo.Native;

// A capture-time alpha mask, not another window or animation. A small lookup
// table keeps Gaussian evaluation out of the perimeter-pixel loop. The shadow
// and content later share the same UpdateLayeredWindow constant alpha.
internal sealed class PanelHandoffShadow
{
    private readonly double halfWidth, halfHeight, radius, drop;
    private readonly byte[] ambient = [], directional = [];
    private readonly int extent;
    internal int Padding { get; }

    internal PanelHandoffShadow(int width, int height, double radius, double scale, bool enabled)
    {
        halfWidth = width / 2d; halfHeight = height / 2d;
        this.radius = Math.Min(radius, Math.Min(halfWidth, halfHeight));
        if (!enabled) return;
        drop = 5 * scale;
        Padding = (int)Math.Ceiling(33 * scale);
        extent = Padding * 2;
        ambient = Build(2 * scale, .08);
        directional = Build(7 * scale, .18);
    }

    internal byte Alpha(int x, int y)
    {
        if (Padding == 0) return 0;
        double px = x - Padding + .5, py = y - Padding + .5;
        int a = Lookup(ambient, Distance(px, py));
        int b = Lookup(directional, Distance(px, py - drop));
        return (byte)(a + b - (a * b + 127) / 255);
    }

    internal bool ContainsContent(int x, int y)
    {
        double px = x - Padding + .5, py = y - Padding + .5;
        return px >= 0 && py >= 0 && px < halfWidth * 2 && py < halfHeight * 2 && Distance(px, py) <= .5;
    }

    private double Distance(double x, double y)
    {
        double qx = Math.Abs(x - halfWidth) - (halfWidth - radius);
        double qy = Math.Abs(y - halfHeight) - (halfHeight - radius);
        double outsideX = Math.Max(qx, 0), outsideY = Math.Max(qy, 0);
        return Math.Sqrt(outsideX * outsideX + outsideY * outsideY) + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    private int Lookup(byte[] table, double distance) =>
        table[(int)Math.Clamp(Math.Round((distance + extent) * 4), 0, table.Length - 1)];

    private byte[] Build(double sigma, double opacity)
    {
        var result = new byte[extent * 8 + 1];
        for (int i = 0; i < result.Length; i++)
        {
            double z = (i / 4d - extent) / sigma;
            double x = Math.Abs(z) / Math.Sqrt(2), t = 1 / (1 + .3275911 * x);
            double erfc = (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - .284496736) * t + .254829592) * t * Math.Exp(-x * x);
            double coverage = z >= 0 ? .5 * erfc : 1 - .5 * erfc;
            result[i] = (byte)Math.Clamp(Math.Round(255 * opacity * coverage), 0, 255);
        }
        return result;
    }
}
