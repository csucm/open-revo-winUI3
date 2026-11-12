using System.Text.Json;
namespace OpenRevo.Core;
public sealed record GpuModeState(int Mode,bool HardwareRead,bool Integrated,bool Hybrid,bool Discrete)
{
    public bool Known=>HardwareRead&&Mode is >=1 and <=3;
    public bool Supports(int mode)=>Known&&(mode switch{1=>Integrated,2=>Hybrid,3=>Discrete,_=>false});
    public static string Name(int mode)=>mode switch{1=>"纯集显",2=>"标准混合",3=>"独显直连",_=>"待读取"};
    public static GpuModeState Parse(JsonElement json)
    {
        var hardware=json.GetProperty("hardware");var caps=json.GetProperty("capabilities");
        int mode=hardware.GetProperty("current_mode").GetString() switch{"igpu"=>1,"hybrid"=>2,"dgpu"=>3,_=>0};
        return new(mode,hardware.GetProperty("hardware_read").GetBoolean(),caps.GetProperty("supports_igpu_only").GetBoolean(),caps.GetProperty("supports_hybrid").GetBoolean(),caps.GetProperty("supports_dgpu").GetBoolean());
    }
}
public interface IGpuModeCore {GpuModeState ReadGpuMode();bool StageGpuMode(int expected,int target);}
public sealed record GpuModeReceipt(GpuModeState Before,GpuModeState After,bool Changed);
