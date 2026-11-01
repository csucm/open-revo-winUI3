using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenRevo.Core;

// These are the upstream read-only ACPI queries, NOT its write commands 0/1.
// A positive answer concerns hybrid<->iGPU only; it does not prove live MUX support.
public sealed record GpuRuntimeCapabilities(bool? HybridIgpuSupported,bool? IgpuActive,ulong? SupportRaw,ulong? StateRaw,string? Error)
{
    public static bool? Decode(ulong? value)=>(value&255) switch{0x55=>true,0xAA=>false,_=>null};
    public string Description(int initialMode)=>HybridIgpuSupported switch
    {
        false=>"固件未提供混合/纯集显热切换接口；切换需重启",
        true when initialMode==3=>"已识别混合/纯集显热切换接口；本次以独显直连启动，切换仍需重启",
        true=>"已识别混合/纯集显热切换接口，当前机型实际热切换尚未验证；暂保存后重启",
        _=>"未能确认热切换能力；暂保存后重启"
    };
    public static GpuRuntimeCapabilities Probe()
    {
        if(Hal.HardwareSession.CheckMachineIdentity() is string blocked)return new(null,null,null,null,blocked);
        object? locator=null,service=null,rows=null,selected=null,signature=null,methods=null,method=null,cls=null;
        string phase="connect";
        try
        {
            locator=Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service=((dynamic)locator!).ConnectServer(".",@"root\wmi");
            phase="enumerate";rows=((dynamic)service).ExecQuery("SELECT __RELPATH, InstanceName FROM AcpiTest_MULong","WQL",48);
            foreach(object row in (System.Collections.IEnumerable)rows)
            {
                string instance=Convert.ToString(((dynamic)row).InstanceName,CultureInfo.InvariantCulture)??"";
                if(selected==null){selected=row;if(instance.Contains("1_1"))break;}
                else if(instance.Contains("1_1")){Release(selected);selected=row;break;}
                else Release(row);
            }
            if(selected==null)return new(null,null,null,null,"AcpiTest_MULong unavailable");
            phase="signature";string path=((dynamic)selected).Path_.Path;
            cls=((dynamic)service).Get("AcpiTest_MULong");methods=((dynamic)cls).Methods_;
            method=((dynamic)methods).Item("GetSetULong");signature=((dynamic)method).InParameters;
            ulong? Query(ulong command)
            {
                object? input=null,output=null;
                try
                {
                    input=((dynamic)signature).SpawnInstance_();
                    ((dynamic)input).Data=command.ToString(CultureInfo.InvariantCulture);
                    output=((dynamic)service).ExecMethod(path,"GetSetULong",input);
                    string raw=Convert.ToString(((dynamic)output).Properties_.Item("Return").Value,CultureInfo.InvariantCulture)??"";
                    return ulong.TryParse(raw,NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong number)?number:null;
                }
                finally{Release(output);Release(input);}
            }
            phase="support-query";ulong? support=Query(0x30000000003);
            phase="state-query";ulong? state=Query(0x30000000002);
            return new(Decode(support),Decode(state),support,state,null);
        }
        catch(Exception error){return new(null,null,null,null,$"{phase}: {error.GetType().Name} 0x{error.HResult:X8}: {error.Message}");}
        finally{Release(signature);Release(method);Release(methods);Release(cls);Release(selected);Release(rows);Release(service);Release(locator);}
    }
    private static void Release(object? value){if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
}
