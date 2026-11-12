namespace OpenRevo.Core.Hal;

public sealed partial class HardwareSession
{
    public string? ModeIndicatorWarning {get;private set;}
    private void SynchronizePerformanceIndicator(int mode)
    {
        ModeIndicatorWarning=null;
        if(!UsesProductionClassicCore)return;
        try
        {
            Guard();CoreMachine.Library.SetPerformanceIndicator(mode==4);
            DiagnosticLog.Record("performance.indicator.confirmed",new{mode,customWhite=mode==4});
        }
        catch(Exception error)
        {
            // A lamp failure does not invalidate an already confirmed performance transaction.
            ModeIndicatorWarning="性能档位已确认，但模式指示灯未同步："+error.Message;
            DiagnosticLog.Failure("performance.indicator.failed",error,new{mode});
        }
    }
}
