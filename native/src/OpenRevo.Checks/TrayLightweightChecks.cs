using OpenRevo.Core;
using OpenRevo.Native;
using System.Runtime.InteropServices;
using System.Text.Json;
internal static class TrayLightweightChecks
{
    public static async Task Run(string report,string icon)
    {
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);}
        var clock=new ManualClock{Now=DateTimeOffset.UtcNow};
        var cache=new TrayTelemetryCache(clock);int reads=0;
        var completion=new TaskCompletionSource<TrayTelemetrySample>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TrayTelemetrySample> Read(){reads++;return completion.Task;}
        var a=cache.RequestAsync(Read);var b=cache.RequestAsync(Read);
        Check(ReferenceEquals(a,b),"in-flight hover requests coalesce");
        completion.SetResult(new(clock.Now,55,12,60,0,true));
        var first=await a;await b;Check(reads==1,"one actual sample for concurrent requests");
        clock.Now+=TimeSpan.FromSeconds(4);var reused=await cache.RequestAsync(()=>throw new Exception());
        Check(ReferenceEquals(first,reused),"fresh samples reused without extra reads");
        clock.Now+=TimeSpan.FromSeconds(2);
        var second=await cache.RequestAsync(()=>Task.FromResult(new TrayTelemetrySample(clock.Now,54,0,null,null,false)));
        Check(second.At==clock.Now&&second.CpuTemperature==54,"expired cache refreshed");
        Check(second.ToTooltip().Contains("0%")&&second.ToTooltip().Contains("节能限制"),"real zero usage retained and skipped GPU explained");
        clock.Now+=TimeSpan.FromSeconds(6);
        var failed=await cache.RequestAsync(()=>throw new IOException("fixture"));
        Check(failed.CpuTemperature==null&&failed.GpuTemperature==null,"read failure never returns stale values");
        var invalid=new TrayTelemetrySample(clock.Now,999,double.NaN,-1,double.PositiveInfinity,true);
        string tip=invalid.ToTooltip();
        Check(!tip.Contains("999")&&!tip.Contains("NaN")&&!tip.Contains("Infinity"),"invalid values remain unknown");
        Check(tip.Length<128&&tip.Contains("CPU 温区")&&tip.Contains("GPU"),"native tooltip limit and source label");
        Check(new TrayTelemetrySample(DateTimeOffset.MinValue,null,null,null,null,false).ToTooltip().Contains("待采样"),"startup tooltip not invented");
        nint window=CreateWindowEx(0,"STATIC","isolated-icon-check",0,0,0,40,40,0,0,0,0);
        Check(window!=0,"hidden icon check window created");
        try
        {
            using(var warm=new WindowBrandIcon(window,icon)){}
            uint beforeGdi=GetGuiResources(GetCurrentProcess(),0),beforeUser=GetGuiResources(GetCurrentProcess(),1);
            for(int i=0;i<50;i++)
            {
                var binding=new WindowBrandIcon(window,icon);
                nint small=SendMessage(window,0x7F,0,0),large=SendMessage(window,0x7F,1,0);
                Check(small!=0&&large!=0,"small and big HWND icons bound "+i);
                for(int repeat=0;repeat<10;repeat++)binding.Apply();
                Check(SendMessage(window,0x7F,0,0)==small&&SendMessage(window,0x7F,1,0)==large,"reapply reuses icon handles "+i);
                binding.Dispose();binding.Dispose();
                Check(SendMessage(window,0x7F,0,0)==0&&SendMessage(window,0x7F,1,0)==0,"disposed icons detached "+i);
            }
            Check(GetGuiResources(GetCurrentProcess(),0)<=beforeGdi+1&&GetGuiResources(GetCurrentProcess(),1)<=beforeUser+1,"no growing GDI or USER icon handles");
        }
        finally{DestroyWindow(window);}
        File.WriteAllText(report,JsonSerializer.Serialize(new{allPassed=true,count=checks.Count,checks,visibleWindows=0,hardwareWrites=0,driverSamples=0},new JsonSerializerOptions{WriteIndented=true}));
    }
    private sealed class ManualClock:TimeProvider{public DateTimeOffset Now;public override DateTimeOffset GetUtcNow()=>Now;}
    [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")]private static extern nint SendMessage(nint hwnd,uint msg,nuint wp,nint lp);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern uint GetGuiResources(nint process,uint flags);
    [DllImport("kernel32.dll")]private static extern nint GetCurrentProcess();
}
