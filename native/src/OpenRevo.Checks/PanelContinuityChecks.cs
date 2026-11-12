using OpenRevo.Native;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics;

internal static class PanelContinuityChecks
{
    internal static void Run(string output)
    {
        var checks=new List<object>();
        void Check(string name,bool passed)=>checks.Add(new{name,passed});
        var full=new RectInt32(-1920,40,1180,900);var mini=new RectInt32(-420,300,400,640);
        Check("Approved 220 ms timeline",PanelHandoffGeometry.DurationMilliseconds==220);
        Check("Exact opacity endpoints",PanelHandoffGeometry.Opacity(0)==(1d,0d)&&PanelHandoffGeometry.Opacity(1)==(0d,1d));
        var frames=Enumerable.Range(0,221).Select(i=>PanelHandoffGeometry.Opacity(i/220d)).ToArray();
        Check("Bounded overlapping handoff",frames.All(f=>f.Large is >=0 and <=1&&f.Mini is >=0 and <=1&&f.Large+f.Mini>0));
        Check("Reversible monotone opacity",frames.Zip(frames.Skip(1)).All(p=>p.Second.Large<=p.First.Large&&p.Second.Mini>=p.First.Mini));
        foreach(double scale in new[]{1d,1.5,2d})
        {
            var travel=PanelHandoffGeometry.Travel(full,mini,scale);
            var reverse=PanelHandoffGeometry.Travel(mini,full,scale);
            Check($"DPI {scale} travel capped at 16 DIP",Math.Sqrt(travel.X*travel.X+travel.Y*travel.Y)<=16*scale+1e-8);
            Check($"DPI {scale} reverse direction",Math.Abs(travel.X+reverse.X)<1e-8&&Math.Abs(travel.Y+reverse.Y)<1e-8);
        }
        Check("Coincident centers do not move",PanelHandoffGeometry.Travel(full,full,1)==(0d,0d));
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
