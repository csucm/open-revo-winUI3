using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace OpenRevo.Native;

// A short-lived, independent, non-activating HWND. It contains pixels only: no device
// controls, subscriptions, timers or application state. One uniform alpha is
// applied to the complete bitmap, including the native caption and frame.
internal sealed class PanelHandoffSurface : IDisposable
{
    private readonly nint dc, bitmap, previousBitmap;
    private nint window;
    private readonly Action<uint> command;
    private readonly RectInt32 bounds;
    private readonly PanelHandoffShadow shadow;
    private readonly int contentWidth, contentHeight;
    private readonly int offsetX, offsetY, captionHeight, buttonWidth;
    private readonly RectInt32 toggleBounds;
    private bool shown, disposed;
    private static readonly Dictionary<nint, PanelHandoffSurface> Surfaces = [];
    private static readonly WindowProc ClassCallback = Dispatch;
    private static ushort windowClass;
    private const string ClassName = "OpenRevo.PanelHandoff.v1";

    internal static async Task<PanelHandoffSurface> Capture(FrameworkElement content, nint owner,
        int nativeCaptionInset, int captionHeight, RectInt32 toggleBounds, Action<uint> command, CancellationToken stop)
    {
        if (!GetWindowRect(owner, out var outer)) ThrowNative();
        if (!GetClientRect(owner, out var client)) ThrowNative();
        var origin = new Point();
        if (!ClientToScreen(owner, ref origin)) ThrowNative();
        var image = new RenderTargetBitmap();
        content.UpdateLayout();
        double scale = content.XamlRoot.RasterizationScale;
        int targetWidth = Math.Max(1, (int)Math.Round(content.ActualWidth * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(content.ActualHeight * scale));
        // XAML's content island and the top-level Win32 client need not have
        // identical bounds. Rasterize the actual XAML extent, not a stretched
        // copy of the entire HWND client. Some WinUI/DPI combinations apply an
        // extra scale to explicit render sizes; calibrate that uniform factor.
        int requestWidth = Math.Max(1, (int)Math.Round(content.ActualWidth));
        int requestHeight = Math.Max(1, (int)Math.Round(content.ActualHeight));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await image.RenderAsync(content, requestWidth, requestHeight).AsTask(stop);
            if (Math.Abs(image.PixelWidth - targetWidth) <= 1 && Math.Abs(image.PixelHeight - targetHeight) <= 1) break;
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0) break;
            double factor = Math.Min(targetWidth / (double)image.PixelWidth, targetHeight / (double)image.PixelHeight);
            requestWidth = Math.Max(1, (int)Math.Round(requestWidth * factor));
            requestHeight = Math.Max(1, (int)Math.Round(requestHeight * factor));
        }
        var pixels = await image.GetPixelsAsync().AsTask(stop);
        stop.ThrowIfCancellationRequested();
        OpenRevo.Core.DiagnosticLog.Record("window.handoff_capture", new { clientWidth = client.Right, clientHeight = client.Bottom,
            dipWidth = content.ActualWidth, dipHeight = content.ActualHeight, scale, targetWidth, targetHeight,
            requestWidth, requestHeight, pixelWidth = image.PixelWidth, pixelHeight = image.PixelHeight });
        if (Math.Abs(image.PixelWidth - targetWidth) > 1 || Math.Abs(image.PixelHeight - targetHeight) > 1 ||
            image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
            pixels.Length < checked(image.PixelWidth * 4 * image.PixelHeight) ||
            image.PixelWidth > client.Right + 1 || image.PixelHeight > client.Bottom + 1)
            throw new InvalidOperationException($"Panel capture size mismatch: XAML {targetWidth}x{targetHeight}, bitmap {image.PixelWidth}x{image.PixelHeight}, client {client.Right}x{client.Bottom}, scale {scale}.");
        var frameBounds = outer;
        if (DwmGetWindowAttribute(owner, 9, out Rect frame, 16) == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top &&
            frame.Left >= outer.Left && frame.Top >= outer.Top && frame.Right <= outer.Right && frame.Bottom <= outer.Bottom)
            frameBounds = frame;
        // DWM's visible frame still includes non-client pixels that WM_PRINT
        // leaves black/transparent. Present only the actual XAML client extent,
        // retaining the native caption buttons that overlap this same client.
        var visible = new Rect { Left = Math.Max(frameBounds.Left, origin.X), Top = Math.Max(frameBounds.Top, origin.Y),
            Right = Math.Min(frameBounds.Right, origin.X + Math.Min(client.Right, Math.Min(targetWidth, image.PixelWidth))),
            Bottom = Math.Min(frameBounds.Bottom, origin.Y + Math.Min(client.Bottom, Math.Min(targetHeight, image.PixelHeight))) };
        return new(owner, outer, frameBounds, visible, origin, client.Right, client.Bottom, image.PixelWidth, image.PixelHeight,
            pixels, nativeCaptionInset, captionHeight, toggleBounds, command);
    }

    private PanelHandoffSurface(nint owner, Rect outer, Rect frameBounds, Rect visible, Point clientOrigin,
        int clientWidth, int clientHeight, int imageWidth, int imageHeight, IBuffer pixels, int inset, int captionHeight, RectInt32 toggleBounds, Action<uint> command)
    {
        this.command = command;
        contentWidth = visible.Right - visible.Left; contentHeight = visible.Bottom - visible.Top;
        bool maximized = IsZoomed(owner);
        double scale = GetDpiForWindow(owner) / 96d;
        bool rounded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !maximized;
        double radius = rounded ? Math.Min(8 * scale,
            Math.Min(frameBounds.Right - frameBounds.Left, frameBounds.Bottom - frameBounds.Top) / 2d) : 0;
        shadow = new(contentWidth, contentHeight, radius, scale, !maximized);
        int padding = shadow.Padding;
        bounds = new(visible.Left - padding, visible.Top - padding, contentWidth + padding * 2, contentHeight + padding * 2);
        offsetX = visible.Left - outer.Left; offsetY = visible.Top - outer.Top;
        this.captionHeight = captionHeight;
        this.toggleBounds = new(toggleBounds.X + clientOrigin.X - visible.Left, toggleBounds.Y + clientOrigin.Y - visible.Top,
            toggleBounds.Width, toggleBounds.Height);
        buttonWidth = Math.Max(1, inset / 3);
        int width = outer.Right - outer.Left, height = outer.Bottom - outer.Top;
        int nativeStride = checked(width * 4);
        _ = checked(nativeStride * height);
        nint nativeDc = 0, nativeBitmap = 0, oldNativeBitmap = 0;
        try
        {
            // RenderTargetBitmap excludes system non-client visuals. Ask the
            // owner to print its native frame/caption, then replace the XAML
            // client pixels while retaining the system caption-button strip.
            nativeDc = CreateCompatibleDC(0);
            var info = Dib(width, height);
            nativeBitmap = CreateDIBSection(nativeDc, ref info, 0, out nint nativeBits, 0, 0);
            if (nativeDc == 0 || nativeBitmap == 0) ThrowNative();
            oldNativeBitmap = SelectObject(nativeDc, nativeBitmap);
            if (!PrintWindow(owner, nativeDc, 2)) ThrowNative();
            int x0 = clientOrigin.X - outer.Left, y0 = clientOrigin.Y - outer.Top;
            // Some display/composition paths acknowledge WM_PRINT without
            // painting the DWM caption. Never animate an empty/black strip.
            if (inset > 0)
            {
                int left = Math.Max(0, x0 + clientWidth - inset + 4), right = Math.Min(width, x0 + clientWidth - 4);
                int top = Math.Max(0, y0 + 4), bottom = Math.Min(height, y0 + captionHeight - 4);
                int minimum = 255, maximum = 0;
                byte[] captionRow = new byte[Math.Max(0, right - left) * 4];
                for (int y = top; y < bottom; y++)
                {
                    Marshal.Copy(nativeBits + (y * width + left) * 4, captionRow, 0, captionRow.Length);
                    for (int i = 0; i < captionRow.Length; i += 4)
                    {
                        int luminance = (captionRow[i] + captionRow[i + 1] + captionRow[i + 2]) / 3;
                        minimum = Math.Min(minimum, luminance); maximum = Math.Max(maximum, luminance);
                    }
                }
                if (maximum - minimum < 12)
                    throw new InvalidOperationException("The native caption could not be captured; using a direct panel switch.");
            }
            // The WinRT stream shares the capture buffer. Copy one row at a
            // time to keep the temporary managed allocation off the LOH.
            using (Stream source = pixels.AsStream())
            {
                int imageStride = checked(imageWidth * 4);
                int copyWidth = Math.Min(clientWidth, imageWidth);
                int captionWidth = Math.Min(copyWidth, Math.Max(0, clientWidth - inset));
                byte[] pixelRow = new byte[checked(copyWidth * 4)];
                for (int y = 0; y < Math.Min(clientHeight, imageHeight); y++)
                {
                    // Native caption buttons overlap the extended XAML client.
                    int count = y < captionHeight ? captionWidth : copyWidth;
                    int destY = y + y0;
                    if (count == 0 || destY < 0 || destY >= height || x0 < 0 || x0 > width - count) continue;
                    source.Position = (long)y * imageStride;
                    int bytes = checked(count * 4);
                    source.ReadExactly(pixelRow, 0, bytes);
                    Marshal.Copy(pixelRow, 0, nativeBits + checked(destY * nativeStride + x0 * 4), bytes);
                }
            }
            dc = CreateCompatibleDC(0);
            info = Dib(bounds.Width, bounds.Height);
            bitmap = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
            if (dc == 0 || bitmap == 0) ThrowNative();
            previousBitmap = SelectObject(dc, bitmap);
            // Preserve the XAML premultiplied alpha. Only the native caption
            // strip is GDI-painted and needs an explicit opaque alpha. Making
            // the entire WM_PRINT frame opaque creates a black perimeter.
            byte[] row = new byte[checked(bounds.Width * 4)];
            for (int y = 0; y < bounds.Height; y++)
            {
                Array.Clear(row);
                int contentY = y - padding;
                if (contentY >= 0 && contentY < contentHeight)
                {
                    Marshal.Copy(nativeBits + ((contentY + offsetY) * width + offsetX) * 4, row, padding * 4, contentWidth * 4);
                    int clientY = visible.Top + contentY - clientOrigin.Y;
                    if (clientY >= 0 && clientY < captionHeight)
                    {
                        int first = Math.Max(0, clientOrigin.X + clientWidth - inset - visible.Left);
                        for (int x = first; x < contentWidth; x++) row[(x + padding) * 4 + 3] = 255;
                    }
                    double screenY = visible.Top + contentY + .5;
                    double dy = radius > 0 ? screenY - Math.Clamp(screenY, frameBounds.Top + radius, frameBounds.Bottom - radius) : 0;
                    if (radius > 0 && dy != 0)
                    {
                        for (int x = 0; x < contentWidth; x++)
                        {
                            double screenX = visible.Left + x + .5;
                            double dx = screenX - Math.Clamp(screenX, frameBounds.Left + radius, frameBounds.Right - radius);
                            double coverage = Math.Clamp(radius + .5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                            if (coverage >= 1) continue;
                            int i = (x + padding) * 4;
                            for (int channel = 0; channel < 4; channel++) row[i + channel] = (byte)Math.Round(row[i + channel] * coverage);
                        }
                    }
                }
                // Black premultiplied shadow behind the source: RGB stays the
                // source RGB; only destination alpha changes. Opaque content
                // skips the shadow calculation entirely.
                for (int x = 0; x < bounds.Width; x++)
                {
                    int i = x * 4 + 3, sourceAlpha = row[i];
                    if (sourceAlpha == 255) continue;
                    int shadowAlpha = shadow.Alpha(x, y);
                    row[i] = (byte)(sourceAlpha + (shadowAlpha * (255 - sourceAlpha) + 127) / 255);
                }
                Marshal.Copy(row, 0, bits + y * row.Length, row.Length);
            }
            OpenRevo.Core.DiagnosticLog.Record("window.handoff_frame_crop", new { frameLeft = frameBounds.Left, frameTop = frameBounds.Top,
                frameWidth = frameBounds.Right - frameBounds.Left, frameHeight = frameBounds.Bottom - frameBounds.Top,
                contentLeft = visible.Left, contentTop = visible.Top, contentWidth, contentHeight,
                preservesXamlAlpha = true, radius, shadowPadding = padding, shadowSharesWindowAlpha = true });
            EnsureWindowClass();
            window = CreateWindowEx(0x08080080, ClassName, "", 0x80000000,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, GetModuleHandle(null), 0);
            // An owned popup inherits its owner's DWM cloak. The live owner is
            // deliberately cloaked during the handoff, so presentation HWNDs
            // must be independent. Their lifetime/hide/close remains explicit
            // in the handoff's cancellation/finally paths.
            if (window == 0) ThrowNative();
            Surfaces.Add(window, this);
        }
        catch { Dispose(); throw; }
        finally
        {
            if (oldNativeBitmap != 0) SelectObject(nativeDc, oldNativeBitmap);
            if (nativeBitmap != 0) DeleteObject(nativeBitmap);
            if (nativeDc != 0) DeleteDC(nativeDc);
        }
    }

    internal void Paint(double alpha, double x, double y, bool acceptsInput)
    {
        if (window == 0) return;
        // The other layer is visual only. Only one layer routes caption input.
        nint style = GetWindowLongPtr(window, -20);
        long next = acceptsInput ? style.ToInt64() & ~0x20L : style.ToInt64() | 0x20L;
        if (next != style.ToInt64()) SetWindowLongPtr(window, -20, (nint)next);
        var position = new Point { X = bounds.X + (int)Math.Round(x), Y = bounds.Y + (int)Math.Round(y) };
        var size = new Point { X = bounds.Width, Y = bounds.Height };
        var source = new Point();
        var blend = new Blend { ConstantAlpha = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), AlphaFormat = 1 };
        if (!UpdateLayeredWindow(window, 0, ref position, ref size, dc, ref source, 0, ref blend, 2)) ThrowNative();
        if (!shown) { ShowWindow(window, 4); shown = true; }
    }

    internal void Hide() { if (window != 0) ShowWindow(window, 0); }
    internal void Raise() { if (window != 0) SetWindowPos(window, 0, 0, 0, 0, 0, 0x13); }
    internal void ConfirmPresentation(string layer)
    {
        int status = GetCloaked(window, 14, out int cloaked, 4);
        bool visible = IsWindowVisible(window);
        OpenRevo.Core.DiagnosticLog.Record("window.handoff_layer", new { layer, handle = window.ToInt64(),
            owner = GetWindow(window, 4).ToInt64(), visible, cloakStatus = status, cloaked });
        if (!visible || status == 0 && cloaked != 0)
            throw new InvalidOperationException($"The {layer} presentation window is hidden (DWM cloak={cloaked}).");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (window != 0) { nint handle = window; window = 0; Surfaces.Remove(handle); DestroyWindow(handle); }
        if (previousBitmap != 0 && dc != 0) SelectObject(dc, previousBitmap);
        if (bitmap != 0) DeleteObject(bitmap);
        if (dc != 0) DeleteDC(dc);
    }

    private nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x21) return 3; // MA_NOACTIVATE
        if (message == 0x84) // Shadow padding never acts as a caption/control.
        {
            var point = new Point { X = (short)((long)lParam & 0xffff), Y = (short)(((long)lParam >> 16) & 0xffff) };
            ScreenToClient(hwnd, ref point);
            return shadow.ContainsContent(point.X, point.Y) ? 1 : -1;
        }
        if (message == 0x0202)
        {
            int x = (short)((long)lParam & 0xffff) - shadow.Padding, y = (short)(((long)lParam >> 16) & 0xffff) - shadow.Padding;
            if (toggleBounds.Width > 0 && x >= toggleBounds.X && x < toggleBounds.X + toggleBounds.Width &&
                y >= toggleBounds.Y && y < toggleBounds.Y + toggleBounds.Height)
            { command(0xF030); return 0; }
            if (y + offsetY < captionHeight && y >= 0)
            {
                int right = contentWidth - x;
                if (right <= buttonWidth) command(0xF060); // close through the real owner
                else if (right <= buttonWidth * 2) command(0xF030); // reverse the handoff
                else if (right <= buttonWidth * 3) command(0xF020);
            }
            return 0;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    private static nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam) =>
        Surfaces.TryGetValue(hwnd, out var surface) ? surface.OnMessage(hwnd, message, wParam, lParam) : DefWindowProc(hwnd, message, wParam, lParam);
    private static void EnsureWindowClass()
    {
        if (windowClass != 0) return;
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = ClassCallback,
            Instance = GetModuleHandle(null), Name = ClassName, Cursor = LoadCursor(0, 32512) };
        windowClass = RegisterClassEx(ref cls);
        if (windowClass == 0) ThrowNative();
    }
    private static BitmapInfo Dib(int width, int height) => new() { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
    private static void ThrowNative() => throw new Win32Exception(Marshal.GetLastWin32Error());
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WindowProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Brush;
        public string? Menu, Name; public nint SmallIcon;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect value, uint size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int GetCloaked(nint hwnd, uint attribute, out int value, uint size);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint screenDc, ref Point position, ref Point size, nint dc, ref Point source, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
