using OpenRevo.Native;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class PanelContinuityChecks
{
    internal static void Run(string output)
    {
        var checks=new List<object>();
        void Check(string name,bool passed)=>checks.Add(new{name,passed});
        var full=new PanelMotionRect(-1920,40,1180,900);var mini=new PanelMotionRect(-420,300,400,640);
        foreach(var(start,end)in new[]{(full,mini),(mini,full)})
        {
            Check($"Exact endpoints {start.Width}",PanelMotionGeometry.At(start,end,0)==start&&PanelMotionGeometry.At(start,end,1)==end);
            var frames=Enumerable.Range(0,251).Select(i=>PanelMotionGeometry.At(start,end,i/250d)).ToArray();
            Check($"No overshoot or zero sizes {start.Width}",frames.All(f=>f.Width>=Math.Min(start.Width,end.Width)&&f.Width<=Math.Max(start.Width,end.Width)&&f.Height>=Math.Min(start.Height,end.Height)&&f.Height<=Math.Max(start.Height,end.Height)&&f.X>=Math.Min(start.X,end.X)&&f.X<=Math.Max(start.X,end.X)));
            Check($"Monotone width {start.Width}",frames.Zip(frames.Skip(1)).All(p=>end.Width>start.Width?p.Second.Width>=p.First.Width:p.Second.Width<=p.First.Width));
        }
        Check("Clamp invalid progress",PanelMotionGeometry.At(full,mini,-1)==full&&PanelMotionGeometry.At(full,mini,double.NaN)==mini&&PanelMotionGeometry.At(full,mini,2)==mini);
        Check("Ease-out slows near destination",PanelMotionGeometry.Ease(.25)>.25&&PanelMotionGeometry.Ease(.75)-PanelMotionGeometry.Ease(.5)<PanelMotionGeometry.Ease(.5)-PanelMotionGeometry.Ease(.25));
        nint window=CreateWindowEx(0,"STATIC","Open-Revo hidden caption check",0,0,0,100,100,0,0,0,0);
        if(window==0)throw new System.ComponentModel.Win32Exception();
        int neutralMoves=0;Hook hook=(hwnd,msg,wp,lp,id,data)=>{if(msg==0xA0&&wp==0)neutralMoves++;return DefSubclassProc(hwnd,msg,wp,lp);};
        try
        {
            bool installed=SetWindowSubclass(window,hook,1,0);GetCursorPos(out var before);var foreground=GetForegroundWindow();
            CaptionHoverState.Clear(window);CaptionHoverState.Clear(0);GetCursorPos(out var after);
            Check("Neutral NC hover reset delivered",installed&&neutralMoves==1);
            Check("No cursor motion, activation or visible fixture",before.X==after.X&&before.Y==after.Y&&foreground==GetForegroundWindow()&&!IsWindowVisible(window));
        }
        finally{DestroyWindow(window);GC.KeepAlive(hook);}
        bool passed=checks.All(c=>JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean());
        File.WriteAllText(output,JsonSerializer.Serialize(new{allPassed=passed,count=checks.Count,hardwareWrites=0,visibleReviewWindows=0,checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Panel continuity checks: {checks.Count}, passed={passed}");Environment.ExitCode=passed?0:1;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate nint Hook(nint hwnd,uint msg,nuint wp,nint lp,nuint id,nuint data);
    [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("comctl32.dll")]private static extern bool SetWindowSubclass(nint hwnd,Hook proc,nuint id,nuint data);
    [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint hwnd,uint msg,nuint wp,nint lp);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}
