using System.Text.Json;
namespace OpenRevo.Core.Hal;

public sealed record CoreCompatibility(bool Supported,bool Ready,bool ClassicEc,string Channel,string Driver,string? Manifest,int? Project,string Reason,int? Mode,bool Amd,int TjMax,bool Performance,bool Battery,bool FanBoost,bool FnLock,bool Rgb,bool CustomTuning)
{
    public static CoreCompatibility Unknown(string reason)=>new(false,false,false,"Unknown","Unknown",null,null,reason,null,false,100,false,false,false,false,false,false);
    public static CoreCompatibility Parse(JsonElement json)
    {
        bool Flag(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
        string Text(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
        int? Number(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int n)?n:null;
        var caps=json.GetProperty("capabilities");bool Cap(string key)=>caps.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
        bool ready=Flag("supported")&&Flag("ready")&&Text("driver") is not("Mock" or "Unknown" or "");
        return new(Flag("supported"),ready,Flag("classic_ec"),Text("channel"),Text("driver"),Text("manifest"),Number("project"),Text("reason"),Number("mode"),Text("cpu_encoding")=="amd_celsius",Number("tj_max")??100,
            ready&&Cap("performance"),ready&&Cap("battery"),ready&&Cap("fan_boost"),ready&&Cap("fn_lock"),ready&&Cap("rgb"),ready&&Cap("custom_tuning"));
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
            try{current=CoreCompatibility.Parse(Library.Read(20));DiagnosticLog.Record("admission.result",current);return current;}
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
