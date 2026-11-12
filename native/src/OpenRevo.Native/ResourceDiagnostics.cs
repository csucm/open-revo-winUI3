using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private long lastResourceSample;
    private int renderCount;
    private void SampleResources()
    {
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        if(lastResourceSample!=0&&System.Diagnostics.Stopwatch.GetElapsedTime(lastResourceSample,now)<TimeSpan.FromSeconds(30))return;
        lastResourceSample=now;
        try{DiagnosticLog.Record("process.resources",new{resources=ProcessResources.Read(),mini,currentPage,renderCount,visible=IsVisibleNow(),lowRefreshOnBattery,state=ExitDiagnosticState()});}
        catch(Exception error){DiagnosticLog.Failure("process.resources.failed",error,throttle:true);}
    }
}
