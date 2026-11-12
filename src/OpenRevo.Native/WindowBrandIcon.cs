using System.Runtime.InteropServices;

namespace OpenRevo.Native;

// Explicit HWND icons avoid reliance on the WinUI class icon. Own only loaded handles.
internal sealed class WindowBrandIcon : IDisposable
{
    internal const string ApplicationId = "OpenRevo.Native.ControlPanel";
    private readonly nint window;
    private nint small, large;
    private bool disposed;
    internal WindowBrandIcon(nint window, string path)
    {
        this.window = window;
        uint dpi = GetDpiForWindow(window);
        int smallSize = Math.Max(16, (int)(16 * dpi / 96));
        int largeSize = Math.Max(32, (int)(32 * dpi / 96));
        small = LoadImage(0, path, 1, smallSize, smallSize, 0x10);
        large = LoadImage(0, path, 1, largeSize, largeSize, 0x10);
        if(small == 0 || large == 0) { Dispose(); throw new IOException("Cannot load window brand icons."); }
        Apply();
    }
    internal static void SetProcessIdentity() => SetCurrentProcessExplicitAppUserModelID(ApplicationId);
    internal void Apply()
    {
        if(disposed||!IsWindow(window))return;
        if(SendMessage(window,0x7F,0,0)!=small)SendMessage(window,0x80,0,small);
        if(SendMessage(window,0x7F,1,0)!=large)SendMessage(window,0x80,1,large);
    }
    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        if(IsWindow(window)){
            if(SendMessage(window,0x7F,0,0)==small)SendMessage(window,0x80,0,0);
            if(SendMessage(window,0x7F,1,0)==large)SendMessage(window,0x80,1,0);
        }
        if(small!=0)DestroyIcon(small);
        if(large!=0)DestroyIcon(large);
        small=large=0;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint msg,nuint wp,nint lp);
    [DllImport("user32.dll",EntryPoint="LoadImageW",CharSet=CharSet.Unicode)]private static extern nint LoadImage(nint instance,string path,uint type,int cx,int cy,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
}

