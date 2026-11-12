using System.Text.Json;
namespace OpenRevo.Core.Hal;

public sealed record CoreCompatibility(bool Supported,bool Ready,bool ClassicEc,string Channel,string Driver,string? Manifest,int? Project,string Reason,int? Mode,bool Amd,int TjMax,bool Performance,bool Battery,bool FanBoost,bool FnLock,bool Rgb,bool CustomTuning)
{
    public bool FnLockViaEc {get;init;}
    public bool FnLockReadback {get;init;}
    public bool IntegratedOnly {get;init;}
    public bool? DiscreteGpuDeclared {get;init;}
    public bool TouchpadControl {get;init;}
    public bool DeclaredRgb {get;init;}
    public DeclaredCpuTuningLimits? CpuTuning {get;init;}
    public bool ClassicCpuTuning {get;init;}=true;
    public int? FactoryPl1 {get;init;}
    public bool MonochromeControl {get;init;}
    public int[]? MonochromeLevels {get;init;}
    internal bool Gm7tg0mModeProfile { get; init; }
    public static CoreCompatibility Unknown(string reason)=>new(false,false,false,"Unknown","Unknown",null,null,reason,null,false,100,false,false,false,false,false,false);
    public static CoreCompatibility Parse(JsonElement json) => Parse(json, false);

    internal static CoreCompatibility Parse(JsonElement json, bool matchesGm7tg0m)
    {
        bool Flag(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
        string Text(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
        int? Number(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int n)?n:null;
        int? mode=Number("mode");
        int? project = Number("project");
        bool ready=Flag("supported")&&Flag("ready")&&Text("driver") is not("Mock" or "Unknown" or "");
        bool gm7tg0mModeProfile = matchesGm7tg0m && ready && Flag("classic_ec") && project == Gm7tg0mFactoryMode.Project;
        if (gm7tg0mModeProfile)
            mode = Gm7tg0mFactoryMode.Decode(project, Number("oem_mode_platform"),
                Number("oem_mode_custom"), Number("oem_mode_clamp"), Number("oem_mode_index"),
                Number("oem_mode_isolated")) ?? mode;
        string? factoryKey=mode switch{1=>"office",2=>"balance",3=>"turbo",_=>null};
        int? factoryPl1=factoryKey!=null&&json.TryGetProperty("power_mode_watts",out var watts)&&watts.ValueKind==JsonValueKind.Object
            &&watts.TryGetProperty(factoryKey,out var wattsValue)&&wattsValue.TryGetInt32(out int factoryWatts)&&factoryWatts>0?factoryWatts:null;
        var caps=json.GetProperty("capabilities");bool Cap(string key)=>caps.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
        int[]? levels=null;
        if(json.TryGetProperty("keyboard",out var keyboard)&&keyboard.ValueKind==JsonValueKind.Object
            && keyboard.TryGetProperty("supported",out var supported)&&supported.ValueKind==JsonValueKind.True
            && keyboard.TryGetProperty("type",out var kind)&&kind.ValueKind==JsonValueKind.String&&kind.GetString()=="monochrome_stepped")
        {
            levels=[0];
            if(keyboard.TryGetProperty("steps",out var steps)&&steps.ValueKind==JsonValueKind.Array)
                levels=new[]{0}.Concat(steps.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int n)&&n is >0 and <=255).Select(v=>v.GetInt32())).Distinct().Order().ToArray();
        }
        bool? discrete = json.TryGetProperty("discrete_gpu", out var dg) && dg.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? dg.GetBoolean() : null;
        return new(Flag("supported"),ready,Flag("classic_ec"),Text("channel"),Text("driver"),Text("manifest"),project,Text("reason"),mode,Text("cpu_encoding")=="amd_celsius",Number("tj_max")??100,
            ready&&Cap("performance"),ready&&Cap("battery"),ready&&Cap("fan_boost"),ready&&Cap("fn_lock"),ready&&Cap("rgb"),ready&&Cap("custom_tuning"))
            {FnLockViaEc=ready&&Flag("fn_lock_via_ec"),FnLockReadback=ready&&Flag("fn_lock_readback"),IntegratedOnly=discrete==false,DiscreteGpuDeclared=discrete,TouchpadControl=ready&&Flag("touchpad_control"),DeclaredRgb=ready&&Flag("declared_rgb"),CpuTuning=ready?DeclaredCpuTuningLimits.Parse(json):null,FactoryPl1=factoryPl1,MonochromeControl=ready&&Flag("monochrome_control"),MonochromeLevels=levels,
                ClassicCpuTuning=ready&&Flag("classic_ec")&&(!json.TryGetProperty("classic_cpu_tuning",out var classicCpu)||classicCpu.ValueKind==JsonValueKind.True),
                Gm7tg0mModeProfile = gm7tg0mModeProfile};
    }
}

public static class CoreMachine
{
    private static readonly object gate=new();
    private static UpstreamCoreLibrary? core;
    private static CoreCompatibility? current;
    public static UpstreamCoreLibrary Library {get{lock(gate)return core??=new(Path.Combine(AppContext.BaseDirectory,"openrevo_native_bridge.dll"));}}
    public static CoreCompatibility Current {get{lock(gate)return current??Refresh();}}
    public static CoreCompatibility Refresh()
    {
        lock(gate)
        {
            try
            {
                bool matchesGm7tg0m = Gm7tg0mFactoryMode.MatchesMachine;
                current = CoreCompatibility.Parse(Library.Read(20, matchesGm7tg0m ? 1u : 0u), matchesGm7tg0m);
                DiagnosticLog.Record("admission.result", current);
                return current;
            }
            catch(Exception error){DiagnosticLog.Failure("admission.probe",error);return current=CoreCompatibility.Unknown("Core机型探测失败："+error.Message);}
        }
    }
    public static EcSnapshot ReadSnapshot()
    {
        try
        {
            var value=Library.Read(20);
            int? Number(string name)=>value.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int n)?n:null;
            bool? boost=value.TryGetProperty("fan_boost",out var b)&&b.ValueKind is JsonValueKind.True or JsonValueKind.False?b.GetBoolean():null;
            return new(Number("cpu_fan"),Number("gpu_fan"),null,boost,value.GetProperty("reason").GetString()??"");
        }
        catch(Exception error){return new(null,null,null,null,error.Message);}
    }
}

internal sealed class CoreEcTransport:IHardwareTransport
{
    public byte? Read(ushort address)=>CoreMachine.Library.ReadNativeEc(address);
    public bool Write(ushort address,byte value)=>CoreMachine.Library.WriteNativeEc(address,value);
    public void Dispose(){}
}


