namespace OpenRevo.Core.Hal;
public sealed partial class HardwareSession
{
    public GpuRuntimeCapabilities SwitchHybridRuntime(bool expected,bool target)=>Serialized(()=>
    {
        Guard();if(!Active)throw new IOException("硬件接管未启用。");
        audit(new{at=DateTimeOffset.Now,operation="gpu-runtime-intent",expected,target});
        var result=GpuRuntimeCapabilities.Switch(expected,target);
        if(result.Error!=null||result.HybridIgpuSupported!=true||result.IgpuActive!=target){faulted=true;audit(new{at=DateTimeOffset.Now,operation="gpu-runtime-failed-stop",result});throw new IOException(result.Error??"运行态未确认。");}
        audit(new{at=DateTimeOffset.Now,operation="gpu-runtime-confirmed",result});return result;
    });
}
