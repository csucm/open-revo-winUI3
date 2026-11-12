using System.Runtime.InteropServices;
namespace OpenRevo.Native;

internal static class CaptionHoverState
{
    // Route a neutral region through WinUI's pointer source. NCLEAVE alone can be dropped.
    internal static void Clear(nint window)
    {
        if(window==0)return;
        GetCursorPos(out var point);
        uint packed=((uint)(ushort)point.Y<<16)|(ushort)point.X;
        SendMessage(window,0x00A0,0,unchecked((nint)(int)packed));
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern nint SendMessage(nint window,uint msg,nuint wp,nint lp);
}
