using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenRevo.Core;

namespace OpenRevo.Resident;

// Created lazily and used only by the resident tray message thread.
internal sealed partial class ResidentOsd : IDisposable
{
    private const string ClassName = "OpenRevo.RevoCore.ResidentOsd";
    private const uint NoActivate = 0x0010, NoZOrder = 0x0004, ShowWindowFlag = 0x0040;
    private const uint TextFlags = 0x8824; // Single line, vertically centered, no mnemonic, end ellipsis.
    private const int HoldMilliseconds = 1600;
    private readonly WindowProcedure callback;
    private readonly nint module = GetModuleHandle(null);
    private nint window, font, monitor;
    private bool registered;
    private OsdMessage? message;
    private Rect textBounds;
    private uint accent, background;
    private int badge, padding, enter, leave;
    private byte opacity;
    private long started;

    internal ResidentOsd() => callback = OnMessage;

    internal void Show(OsdMessage next, SystemPreferences preferences)
    {
        nint foreground = GetForegroundWindow();
        nint target = MonitorFromWindow(foreground, 1); // Default to the primary screen.
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(target, ref info)) throw LastError();
        Rect area = info.Work;
        EnsureWindow(area);
        if (monitor != target)
        {
            Hide();
            if (!SetWindowPos(window, 0, area.Left, area.Top, 1, 1, NoActivate | NoZOrder)) throw LastError();
            monitor = target;
        }

        double dpi = Math.Max(96u, GetDpiForWindow(window)) / 96d;
        double scale = dpi * preferences.OsdScale / 100d;
        int Scale(double value) => Math.Max(1, (int)Math.Round(value * scale));
        string theme = preferences.OsdTheme;
        int size = theme switch { "pill" => 19, "fade" => 20, _ => 21 };
        nint nextFont = CreateFont(-Scale(size), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        if (nextFont == 0) throw LastError();
        if (font != 0) DeleteObject(font);
        font = nextFont;
        Rect measured = Measure(next.Text);
        padding = Scale(theme == "fade" ? 14 : 10);
        badge = Scale(10);
        int adornment = next.Steps.HasValue ? Scale(68) : next.Dot != null ? Scale(20) : 0;
        int width = Math.Min(area.Right - area.Left, measured.Right + 3 * padding + badge + adornment);
        int height = Math.Min(area.Bottom - area.Top, Math.Max(Scale(42), measured.Bottom + Scale(14)));
        if (width <= 0 || height <= 0) throw new IOException("屏幕提示工作区不可用。");
        int margin = (int)Math.Round(4 * dpi);
        int x = preferences.OsdPosition == "top-left"
            ? Math.Min(area.Left + margin, area.Right - width)
            : area.Left + (area.Right - area.Left - width) / 2;
        int y = preferences.OsdPosition switch
        {
            "center" => area.Top + (area.Bottom - area.Top - height) / 2,
            "bottom-center" => Math.Max(area.Top, area.Bottom - height - (int)Math.Round(24 * dpi)),
            _ => Math.Min(area.Top + margin, area.Bottom - height)
        };
        textBounds = new(2 * padding + badge, 0, width - padding - adornment, height);
        accent = Rgb(next.Accent);
        background = theme switch { "pill" => 0x463123, "fade" => 0x462f1f, _ => 0x322218 };
        opacity = theme switch { "pill" => 213, "fade" => 214, _ => 226 };
        int diameter = theme switch { "pill" => height, "fade" => Scale(8), _ => Scale(12) };
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == 0) throw LastError();
        if (SetWindowRgn(window, region, false) == 0)
        {
            DeleteObject(region);
            throw LastError();
        }

        double motion = preferences.MotionSpeed switch { "fast" => .325, "slow" => .75, "off" => 0, _ => .5 };
        if (SystemParametersInfo(0x1042, 0, out int enabled, 0) && enabled == 0) motion = 0;
        bool alreadyVisible = message != null;
        enter = alreadyVisible ? 0 : (int)Math.Round(180 * motion);
        leave = (int)Math.Round(150 * motion);
        message = next;
        started = Stopwatch.GetTimestamp();
        if (!SetLayeredWindowAttributes(window, 0, enter == 0 ? opacity : (byte)0, 2) ||
            !SetWindowPos(window, -1, x, y, width, height, NoActivate | ShowWindowFlag))
            throw LastError();
        InvalidateRect(window, 0, false);
        UpdateWindow(window);
        if (SetTimer(window, 1, enter == 0 && leave == 0 ? HoldMilliseconds : 16u, 0) == 0)
            throw LastError();
    }

    private void EnsureWindow(Rect work)
    {
        if (window != 0) return;
        if (!registered)
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = module, Name = ClassName,
                Procedure = Marshal.GetFunctionPointerForDelegate(callback)
            };
            if (RegisterClassEx(in windowClass) == 0) throw LastError();
            registered = true;
        }
        const uint layeredClickThroughToolWindow = 0x080800A0;
        window = CreateWindowEx(layeredClickThroughToolWindow, ClassName, "Open-Revo OSD", 0x80000000,
            work.Left, work.Top, 1, 1, 0, 0, module, 0);
        if (window == 0) throw LastError();
    }

    private Rect Measure(string text)
    {
        nint dc = GetDC(window);
        if (dc == 0) throw LastError();
        nint previous = SelectObject(dc, font);
        try
        {
            var size = new Rect();
            DrawText(dc, text, text.Length, ref size, 0xC20); // Calculate a single line, including literal ampersands.
            return size;
        }
        finally
        {
            SelectObject(dc, previous);
            ReleaseDC(window, dc);
        }
    }

    private nint OnMessage(nint hwnd, uint code, nuint wParam, nint lParam)
    {
        switch (code)
        {
            case 0x000F: // WM_PAINT
                Paint(hwnd);
                return 0;
            case 0x0113 when wParam == 1: // WM_TIMER
                Animate();
                return 0;
            case 0x0021: // WM_MOUSEACTIVATE
                return 3; // MA_NOACTIVATE
        }
        return DefWindowProc(hwnd, code, wParam, lParam);
    }

    private void Paint(nint hwnd)
    {
        nint dc = BeginPaint(hwnd, out var paint);
        try
        {
            if (dc == 0 || message is not { } current || !GetClientRect(hwnd, out var client)) return;
            Fill(dc, client, background);
            int middle = client.Bottom / 2;
            Fill(dc, new(padding, middle - badge / 2, padding + badge, middle + (badge + 1) / 2), accent);
            nint previous = SelectObject(dc, font);
            try
            {
                SetBkMode(dc, 1);
                SetTextColor(dc, 0xFFFFFF);
                var bounds = textBounds;
                DrawText(dc, current.Text, current.Text.Length, ref bounds, TextFlags);
            }
            finally { SelectObject(dc, previous); }
            if (current.Dot is { } dot)
            {
                int right = client.Right - padding;
                Fill(dc, new(right - badge, middle - badge / 2, right, middle + (badge + 1) / 2),
                    dot switch { "on" => 0x81B910, "off" => 0x5E3FF4, _ => 0xB8A394 });
            }
            if (current.Steps is int steps)
            {
                int spacing = Math.Max(1, badge / 3);
                int start = client.Right - padding - 4 * badge - 3 * spacing;
                for (int i = 0; i < 4; i++)
                {
                    int left = start + i * (badge + spacing);
                    Fill(dc, new(left, middle - spacing, left + badge, middle + spacing), i < steps ? accent : 0x83746C);
                }
            }
        }
        finally { EndPaint(hwnd, in paint); }
    }

    private static void Fill(nint dc, Rect rect, uint color)
    {
        nint brush = CreateSolidBrush(color);
        if (brush == 0) return;
        FillRect(dc, in rect, brush);
        DeleteObject(brush);
    }

    private static uint Rgb(string hex)
    {
        uint color = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((color & 0xff) << 16) | (color & 0xff00) | ((color >> 16) & 0xff);
    }

    private void Animate()
    {
        if (message == null) return;
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double untilExit = enter + HoldMilliseconds;
        if (elapsed >= untilExit + leave)
        {
            Hide();
            return;
        }
        double factor = elapsed < enter ? 1 - Math.Pow(1 - elapsed / enter, 3)
            : elapsed >= untilExit ? 1 - Math.Pow((elapsed - untilExit) / leave, 2)
            : 1;
        SetLayeredWindowAttributes(window, 0, (byte)Math.Round(factor * opacity), 2);
        if (elapsed >= enter && elapsed < untilExit)
            SetTimer(window, 1, (uint)Math.Max(16, Math.Ceiling(untilExit - elapsed)), 0);
        else if (elapsed >= untilExit)
            SetTimer(window, 1, 16, 0);
    }

    internal void Hide()
    {
        if (window == 0) return;
        KillTimer(window, 1);
        ShowWindow(window, 0);
        message = null;
    }

    public void Dispose()
    {
        Hide();
        if (window != 0) DestroyWindow(window);
        if (font != 0) DeleteObject(font);
        if (registered) UnregisterClass(ClassName, module);
        window = font = monitor = 0;
        registered = false;
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());
}
