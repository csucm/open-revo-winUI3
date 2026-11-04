using System.Text.Json;

namespace OpenRevo.Core.Hal;

public sealed record DeclaredCpuTuningLimits(int Pl1Min,int Pl1Max,int Pl2Min,int Pl2Max,bool FanCurve)
{
    internal static DeclaredCpuTuningLimits? Parse(JsonElement json)
    {
        if(!json.TryGetProperty("declared_tuning",out var value)||value.ValueKind!=JsonValueKind.Object)return null;
        var pl1=value.GetProperty("pl1");
        var pl2=value.GetProperty("pl2");
        return new(pl1.GetProperty("min").GetInt32(),pl1.GetProperty("max").GetInt32(),
            pl2.GetProperty("min").GetInt32(),pl2.GetProperty("max").GetInt32(),value.GetProperty("fan_curve").GetBoolean());
    }
}

public sealed record DeclaredTuningPreset(string Manifest,int Pl1,int Pl2,int Temperature,bool CustomFans,
    FanPoint[] CpuCurve,FanPoint[] GpuCurve,bool RestoreOnAc);

public sealed partial class HardwareSession
{
    public DeclaredCpuTuningLimits? DeclaredCpuTuning => CoreDelegated?Admission.CpuTuning:null;

    public DeclaredTuningPreset? SavedDeclaredTuning
    {
        get
        {
            var preset=AppData.Load().DeclaredTuning;
            return DeclaredCpuTuning!=null&&preset?.Manifest==Admission.Manifest?preset:null;
        }
    }

    public object ApplyDeclaredCpuTuning(DeclaredTuningPreset preset) => Serialized(() =>
    {
        Guard();
        if(!Active||DeclaredCpuTuning==null||preset.Manifest!=Admission.Manifest)throw new InvalidOperationException("Core 未声明 CPU 调校。");
        if(!AllowBatteryPerformance&&acOnline()!=true)throw new IOException("CPU 调校需要交流供电。");
        var (_,pl1,pl2,temperature,customFans,cpuCurve,gpuCurve,_)=preset;
        audit(new{operation="core-cpu-profile-intent",pl1,pl2,temperature});
        try
        {
            CoreMachine.Library.ExecuteFeature(12,new{pl1,pl2,temperature,custom_fans=customFans,
                cpu_curve=cpuCurve.Select(point=>new{temp=point.Celsius,duty=point.Percent}).ToArray(),
                gpu_curve=gpuCurve.Select(point=>new{temp=point.Celsius,duty=point.Percent}).ToArray()});
        }
        catch
        {
            faulted=true;
            throw;
        }
        var receipt=new{operation="core-cpu-profile-sent",pl1,pl2,temperature};
        audit(receipt);
        return receipt;
    });
}
