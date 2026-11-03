using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRevo.Native;

internal static class NativePanelMotion
{
    // A short-lived worker owns frame pacing, never XAML objects. SetWindowPos
    // synchronizes native window messages with their owner thread.
    internal static Task<int> Run(nint window,PanelMotionRect start,PanelMotionRect end,CancellationToken stop,int duration=PanelMotionGeometry.DurationMilliseconds)=>Task.Run(()=>
    {
        int frames=0;var clock=Stopwatch.StartNew();
        while(!stop.IsCancellationRequested&&IsWindow(window)&&clock.Elapsed.TotalMilliseconds<duration)
        {
            var rect=PanelMotionGeometry.At(start,end,clock.Elapsed.TotalMilliseconds/duration);
            if(!SetWindowPos(window,0,rect.X,rect.Y,rect.Width,rect.Height,0x0214))
            {if(stop.IsCancellationRequested||!IsWindow(window))break;throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
            frames++;
            long before=clock.ElapsedMilliseconds;
            Flush(); // Wait for DWM, not the UI-thread Rendering event.
            if(clock.ElapsedMilliseconds-before<2)Thread.Sleep(4); // Hidden/disabled DWM must not busy-spin.
        }
        return frames;
    });
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool IsWindow(nint window);
    [DllImport("dwmapi.dll",EntryPoint="DwmFlush")]private static extern int Flush();
}
