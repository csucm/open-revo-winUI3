namespace OpenRevo.Core.Hal;

public sealed record FanPoint(int Celsius,int Percent);
public sealed record TuningProfile(int Pl1,int Pl2,int Pl4,bool TemperatureEnabled,int TemperatureC,
    bool GpuSuperPerformance,bool GpuBoostEnabled,int GpuBoostWatts,int GpuBaseWatts,ClockOffsets? Offsets,
    bool CustomFans,FanPoint[] CpuCurve,FanPoint[] GpuCurve,bool SmoothFans,int FanStepMs)
{
    public static FanPoint[] DefaultCurve()=>[new(40,22),new(50,30),new(60,45),new(68,55),new(75,65),new(82,75),new(88,85),new(95,100)];
    public TuningProfile Copy()=>this with{CpuCurve=CpuCurve.ToArray(),GpuCurve=GpuCurve.ToArray()};
}
public sealed record TuningRegisters(byte Pl1,byte Pl2,byte Pl4,byte Tcc,byte DbControl,byte DbFlag,byte DbWatts,byte TgpOffset,
    byte TurboPl1,byte TurboPl2,byte TurboPl4,byte TurboTcc,byte OfficePl1,byte OfficePl2,byte OfficePl4,byte BalancePl1,byte BalancePl2,byte BalancePl4);

public enum CpuPowerLimit { Pl1, Pl2, Pl4 }
public sealed record CpuPowerCeilings(int? Pl1=null,int? Pl2=null,int? Pl4=null);

public sealed class TuningFactoryLimitsException(TuningRegisters registers)
    : IOException("工厂功耗边界读取无效，未开放调校。")
{
    public TuningRegisters Registers { get; } = registers;
}

public sealed record TuningSnapshot(PerformanceSnapshot Performance,TuningRegisters Registers,GpuTuningState Gpu,int TjMax,bool Amd=false)
{
    public const int Pl4Headroom=20;
    public bool CpuTuning {get;init;}=true;
    public CpuPowerCeilings? CpuCeilings {get;init;}
    public int Pl1Max=>Math.Min(CpuCeilingRange(CpuPowerLimit.Pl1).Value,Pl2Max);
    public int Pl2Max=>Math.Min(CpuCeilingRange(CpuPowerLimit.Pl2).Value,Pl4Max-Pl4Headroom);
    public int Pl4Max=>CpuCeilingRange(CpuPowerLimit.Pl4).Value;

    private static int FactoryPl4Max(TuningRegisters registers)=>Math.Min(255,registers.TurboPl4+50);

    public (int Min,int Value,int Max) CpuCeilingRange(CpuPowerLimit limit)
    {
        var (minimum,defaultValue,maximum,configured)=limit switch
        {
            CpuPowerLimit.Pl1 => (Registers.TurboPl1,Registers.TurboPl1+25,Registers.TurboPl1+50,CpuCeilings?.Pl1),
            CpuPowerLimit.Pl2 => (Registers.TurboPl2,Registers.TurboPl2+25,Registers.TurboPl2+50,CpuCeilings?.Pl2),
            CpuPowerLimit.Pl4 => (Math.Max(Registers.TurboPl4+20,Registers.TurboPl2+Pl4Headroom),FactoryPl4Max(Registers),Registers.TurboPl4+80,CpuCeilings?.Pl4),
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        int representable=limit==CpuPowerLimit.Pl4?byte.MaxValue:byte.MaxValue-Pl4Headroom;
        int max=Math.Min(representable,maximum),min=Math.Min(max,minimum);
        return (min,Math.Clamp(configured??defaultValue,min,max),max);
    }

    internal static bool HasUsableFactoryPowerLimits(TuningRegisters registers)
    {
        // Factory defaults can have less headroom than a custom write requires.
        // Only adapt readable values inside the existing device-derived envelope.
        return registers.TurboPl1 is >=15 and <=235 &&
            registers.TurboPl2>=15 &&
            registers.TurboPl4>=15+Pl4Headroom &&
            registers.TurboPl2+Pl4Headroom<=FactoryPl4Max(registers);
    }

    internal (int Pl1,int Pl2,int Pl4) AdaptCpuLimits(int pl1,int pl2,int pl4) =>
        AdaptCpuLimits(pl1,pl2,pl4,Pl1Max,Pl2Max,Pl4Max);

    private static (int Pl1,int Pl2,int Pl4) AdaptCpuLimits(int pl1,int pl2,int pl4,int pl1Max,int pl2Max,int pl4Max)
    {
        int shortLimit=Math.Clamp(pl2,15,pl2Max);
        int sustainedLimit=Math.Clamp(pl1,15,Math.Min(pl1Max,shortLimit));
        int peakLimit=Math.Clamp(pl4,shortLimit+Pl4Headroom,pl4Max);
        return (sustainedLimit,shortLimit,peakLimit);
    }

    public TuningProfile FitCpuDraft(TuningProfile draft) =>
        CpuTuning ? FitCpuDraft(draft,Pl1Max,Pl2Max,Pl4Max) : draft;

    public static TuningProfile FitCpuDraft(TuningProfile draft,int pl1Max,int pl2Max,int pl4Max)
    {
        var (pl1,pl2,pl4)=AdaptCpuLimits(draft.Pl1,draft.Pl2,draft.Pl4,pl1Max,pl2Max,pl4Max);
        return draft with{Pl1=pl1,Pl2=pl2,Pl4=pl4};
    }

    public TuningProfile EditCpuDraft(TuningProfile draft,CpuPowerLimit limit,int value)
        => FitCpuDraft(EditCpuValue(draft,limit,value));

    public static TuningProfile EditCpuDraft(TuningProfile draft,CpuPowerLimit limit,int value,int pl1Max,int pl2Max,int pl4Max)
        => FitCpuDraft(EditCpuValue(draft,limit,value),pl1Max,pl2Max,pl4Max);

    private static TuningProfile EditCpuValue(TuningProfile draft,CpuPowerLimit limit,int value)
    {
        return limit switch
        {
            CpuPowerLimit.Pl1 => draft with{Pl1=value,Pl2=Math.Max(value,draft.Pl2)},
            CpuPowerLimit.Pl2 => draft with{Pl2=value},
            CpuPowerLimit.Pl4 => draft with{Pl4=value,Pl2=Math.Min(draft.Pl2,value-Pl4Headroom)},
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
    }

    public TuningProfile CreateDraft()
    {
        bool custom=Performance.Mode==4;
        var r=Registers;var c=Gpu.Capabilities;
        var factory=Performance.Mode switch{1=>(r.OfficePl1,r.OfficePl2,r.OfficePl4),2=>(r.BalancePl1,r.BalancePl2,r.BalancePl4),_=>(r.TurboPl1,r.TurboPl2,r.TurboPl4)};
        FanPoint[] Curve(int[] duty)=>new[]{40,50,60,68,75,82,88,95}.Select(t=>new FanPoint(t,Math.Clamp(duty[Math.Clamp(1+(t-30)/5,1,15)]/2,0,100))).ToArray();
        var cpu=custom&&(Performance.Fans.Isolated&128)!=0?Curve(Performance.Fans.CpuDuty):TuningProfile.DefaultCurve();
        var gpu=custom&&(Performance.Fans.Isolated&128)!=0?Curve(Performance.Fans.GpuDutyAndControl):TuningProfile.DefaultCurve();
        cpu[^1]=cpu[^1] with{Percent=100};gpu[^1]=gpu[^1] with{Percent=100};
        var cpuLimits=custom||!CpuTuning?(Pl1:(int)r.Pl1,Pl2:(int)r.Pl2,Pl4:(int)r.Pl4):AdaptCpuLimits(factory.Item1,factory.Item2,factory.Item3);
        return new(cpuLimits.Pl1,cpuLimits.Pl2,cpuLimits.Pl4,
            custom&&(r.Tcc&128)!=0,Math.Clamp(Amd?r.Tcc&127:TjMax-(r.Tcc&127),80,95),
            (Performance.Clamp&16)!=0,(r.DbControl&2)!=0,Math.Clamp((int)r.DbWatts,5,Math.Max(5,c.BoostMaxWatts)),
            c.ConfigurableBase?Math.Clamp(c.FloorWatts+r.TgpOffset,c.FloorWatts,c.BaseMaxWatts):c.BaseMaxWatts,Gpu.Offsets,
            custom&&(Performance.Fans.Isolated&128)!=0,cpu,gpu,true,Math.Clamp((Performance.Fans.Ramp&127)*100,100,12700));
    }
}
public sealed record TuningReceipt(TuningSnapshot Before,TuningSnapshot After,TuningProfile Profile);

public sealed partial class HardwareSession
{
    public TuningSnapshot? TuningState{get;private set;}
    private TuningReceipt? lastTuning;
    private static TuningRegisters ReadTuningRegisters(IHardwareTransport ec,bool cpuTuning,bool integratedOnly)
    {
        byte Get(ushort a)=>ec.Read(a)??throw new IOException("调校参数读取不完整。");
        byte Cpu(ushort address)=>cpuTuning?Get(address):(byte)0;
        byte Gpu(ushort address)=>integratedOnly?(byte)0:Get(address);
        return new(Cpu(Vault.CustomPl1),Cpu(Vault.CustomPl2),Cpu(Vault.CustomPl4),Cpu(Vault.CustomTcc),
            Gpu(Vault.GpuDbControl),Gpu(Vault.GpuDbFlag),Gpu(Vault.GpuDbWatts),Gpu(Vault.GpuTgpOffset),
            Cpu(Vault.TurboPl1),Cpu(Vault.TurboPl2),Cpu(Vault.TurboPl4),Cpu(Vault.TurboTcc),
            Cpu(Vault.OfficePl1),Cpu(Vault.OfficePl2),Cpu(Vault.OfficePl4),Cpu(Vault.BalancePl1),Cpu(Vault.BalancePl2),Cpu(Vault.BalancePl4));
    }
    private TuningSnapshot ReadTuning(IHardwareTransport ec,ITuningGpuCore core)
    {
        string cpu=readCpuName();
        if(!useCoreAdmission&&!cpu.Contains("Intel",StringComparison.OrdinalIgnoreCase)&&!cpu.Contains("AMD",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("处理器架构未知，未开放温控写入。");
        // Use the pinned Core CPU architecture and temperature encoding.
        // GPU generation never selects CPU temperature encoding.
        if(CoreDelegated||!SupportsCustomTuning)throw new InvalidOperationException("Core未提供此通道的自定义调校能力。");
        bool amd=useCoreAdmission?Admission.Amd:cpu.Contains("AMD",StringComparison.OrdinalIgnoreCase);
        int tjMax=useCoreAdmission?Admission.TjMax:100;
        bool cpuTuning=!useCoreAdmission||Admission.ClassicCpuTuning;
        bool integratedOnly=useCoreAdmission&&Admission.IntegratedOnly;
        var r=ReadTuningRegisters(ec,cpuTuning,integratedOnly);
        if(cpuTuning&&!TuningSnapshot.HasUsableFactoryPowerLimits(r))
            throw new TuningFactoryLimitsException(r);
        var gpu=integratedOnly?new GpuTuningState(GpuTuningCapabilities.Unknown with{Name="集成显卡"},null):core.ReadGpuTuning();
        return new(ReadPerformance(ec),r,gpu,tjMax,amd){CpuTuning=cpuTuning,
            CpuCeilings=cpuTuning&&UsesProductionClassicCore?AppData.Load().CpuPowerCeilings:null};
    }
    public TuningSnapshot ProbeTuning(ITuningGpuCore core)=>Serialized(()=>
    {
        Guard();using var ec=open();if(!MatchesProject(Read(ec).Project))throw new IOException("EC 项目不匹配。");
        return TuningState=ReadTuning(ec,core);
    });
    private static bool SameTuning(TuningSnapshot a,TuningSnapshot b)=>SamePerformance(a.Performance,b.Performance)&&a.Registers==b.Registers&&a.Gpu==b.Gpu&&a.TjMax==b.TjMax&&a.Amd==b.Amd&&a.CpuTuning==b.CpuTuning&&a.CpuCeilings==b.CpuCeilings;
    public static int EncodeTemperature(bool amd,int tjMax,bool enabled,int target,int factory)
    {
        int baseline=factory&127;
        return enabled?128|(amd?target:tjMax-target):amd?(baseline is >=60 and <=105?baseline:95):(baseline is >0 and <=30?baseline:Math.Max(5,tjMax-95));
    }
    private bool RecognizedTuningProtocol(FanProtocolState fans)
    {
        if(UsesProductionClassicCore)return CoreMachine.Library.RecognizesClassicFanProtocol(fans,false);
        if(fans.Custom==64)return Enumerable.Range(1,3).Any(mode=>SameTables(fans,FactoryForMachine(fans,mode)));
        if(fans.Custom!=65||fans.Latch!=4||(fans.ApControl&129)!=129||fans.CpuDuty[0]!=0||fans.GpuDutyAndControl[0]!=0)return false;
        if((fans.Isolated&128)==0)return SameTables(fans,FactoryForMachine(fans,3));
        int[] up=[0,..Enumerable.Range(0,15).Select(i=>30+5*i)];int[] down=[0,..up.Skip(1).Select(t=>t-(t<=55?8:5))];
        if(!fans.CpuUp.SequenceEqual(up)||!fans.GpuUp.SequenceEqual(up)||!fans.CpuDown.SequenceEqual(down)||!fans.GpuDown.SequenceEqual(down))return false;
        return new[]{fans.CpuDuty,fans.GpuDutyAndControl}.All(a=>a.Length==16&&a.All(x=>x is >=0 and <=200)&&a.Zip(a.Skip(1)).All(pair=>pair.First<=pair.Second));
    }
    public TuningProfile TuningDraft(TuningSnapshot snapshot)=>lastTuning is {} last&&SameTuning(last.After,snapshot)?last.Profile.Copy():snapshot.CreateDraft();
    public static void ValidateTuning(TuningSnapshot state,TuningProfile p)=>ValidateTuning(state,p,false);
    public static void ValidateTuningDraft(TuningSnapshot state,TuningProfile p)=>ValidateTuning(state,p,true);

    public static TuningProfile PrepareTuningForApply(TuningSnapshot state,TuningProfile draft)
    {
        ValidateTuningDraft(state,draft);
        return state.Gpu.Capabilities.Supported&&!draft.GpuSuperPerformance&&draft.Offsets!=null
            ?draft with{Offsets=new ClockOffsets(0,0)}
            :draft;
    }

    private static void ValidateTuning(TuningSnapshot state,TuningProfile p,bool draft)
    {
        if(state.CpuTuning&&(p.Pl1<15||p.Pl1>state.Pl1Max||p.Pl2<p.Pl1||p.Pl2>state.Pl2Max||p.Pl4<p.Pl2+TuningSnapshot.Pl4Headroom||p.Pl4>state.Pl4Max))
            throw new ArgumentException("功耗须满足 PL1 ≤ PL2、PL4 ≥ PL2 + 20W，并处于机型调校范围。");
        if(state.CpuTuning&&(p.TemperatureC is <80 or >95)||p.FanStepMs is <100 or >12700||p.FanStepMs%100!=0)throw new ArgumentException("温度墙或风扇响应参数无效。");
        var c=state.Gpu.Capabilities;
        if(c.Supported)
        {
            if(p.GpuBaseWatts<c.FloorWatts||p.GpuBaseWatts>c.BaseMaxWatts||!c.ConfigurableBase&&p.GpuBaseWatts!=c.BaseMaxWatts||
               p.GpuBoostWatts<5||p.GpuBoostWatts>c.BoostMaxWatts)throw new ArgumentException("显卡功耗超出硬件能力。");
            if(p.Offsets!=state.Gpu.Offsets&&(p.Offsets==null||state.Gpu.Offsets==null||p.Offsets.CoreMhz<0||p.Offsets.CoreMhz>c.CoreOffsetMax||p.Offsets.MemoryMhz<0||p.Offsets.MemoryMhz>c.MemoryOffsetMax))
                throw new ArgumentException("显卡频率能力未确认，或频率偏移超出范围。");
            if(!draft&&!p.GpuSuperPerformance&&p.Offsets is {} offsets&&(offsets.CoreMhz!=0||offsets.MemoryMhz!=0))throw new ArgumentException("启用显卡超频需同时启用超级性能；关闭时频率偏移应为0。");
        }
        else if(p.Offsets!=state.Gpu.Offsets)throw new ArgumentException("未识别显卡调校能力，禁止修改显卡频率。");
        ValidateFanCurve(p.CpuCurve);
        ValidateFanCurve(p.GpuCurve);
    }

    private static void ValidateFanCurve(FanPoint[]? curve)
    {
        if(curve is not {Length:8}||curve.Any(x=>x is not {Celsius:>=30 and <=100,Percent:>=0 and <=100})||
           curve.Zip(curve.Skip(1)).Any(x=>x.First.Celsius>=x.Second.Celsius||x.First.Percent>x.Second.Percent)||curve[^1].Celsius>95||curve[^1].Percent!=100)
            throw new ArgumentException("每条曲线需8个递增温度点、非递减转速，并在95°C以前达到100%。");
    }
    public static FanProtocolState BuildTuningFans(FanProtocolState current,TuningProfile p,FanProtocolState? factory=null)
    {
        var baseFans=factory??Factory(current,3);
        if(p.CustomFans)
        {
            int[] up=[0,..Enumerable.Range(0,15).Select(i=>30+5*i)];
            int[] down=[0,..up.Skip(1).Select(t=>t-(t<=55?8:5))];
            int[] Duty(FanPoint[] curve)=>up.Select((t,index)=>
            {
                if(index==0)return 0;
                if(t<=curve[0].Celsius)return curve[0].Percent*2;
                if(t>=curve[^1].Celsius)return curve[^1].Percent*2;
                int i=Array.FindIndex(curve,x=>x.Celsius>=t);var a=curve[i-1];var b=curve[i];
                return (int)Math.Round(2*(a.Percent+(b.Percent-a.Percent)*(t-a.Celsius)/(double)(b.Celsius-a.Celsius)),MidpointRounding.AwayFromZero);
            }).ToArray();
            baseFans=baseFans with{CpuUp=up.ToArray(),CpuDown=down.ToArray(),CpuDuty=Duty(p.CpuCurve),GpuUp=up.ToArray(),GpuDown=down.ToArray(),GpuDutyAndControl=Duty(p.GpuCurve)};
        }
        return baseFans with{Custom=(byte)(current.Custom|1),Isolated=(byte)((current.Isolated&~128)|(p.CustomFans?128:0)),Ramp=(byte)(128|(p.SmoothFans?p.FanStepMs/100:1))};
    }
    public TuningReceipt ApplyTuning(ITuningGpuCore core,TuningSnapshot baseline,TuningProfile requested)=>Serialized(()=>
    {
        var p=requested.Copy();
        if(!Active)throw new InvalidOperationException("请先连接设备。");
        Guard();using var ec=open();var root=Read(ec);var before=ReadTuning(ec,core);
        if(!MatchesProject(root.Project)||!SameTuning(before,baseline))throw new InvalidOperationException("硬件状态已变化，请重新读取后再应用。");
        var sourceFans=before.Performance.Fans;
        // A matched classic factory mode can supply OEM rows. All six rows will be replaced under forced cooling.
        bool replaceOemTable=UsesProductionClassicCore&&Admission.Ready&&Admission.Performance&&Admission.CustomTuning
            &&expected is {} captured&&root.Project==captured.Project&&root.Platform==captured.Platform
            &&sourceFans.Custom==64&&(sourceFans.Isolated&128)==0;
        if(!replaceOemTable&&!RecognizedTuningProtocol(sourceFans))
        {
            DiagnosticLog.Record("tuning.fan_protocol.rejected",new{root.Project,root.Platform,before.Performance.Mode,fans=sourceFans});
            throw new InvalidOperationException("当前风扇协议未适配。");
        }
        ValidateTuning(before,p);
        void CheckPower(){Guard();var power=ec.Read(Vault.PowerStatus);if(!power.HasValue||!AllowBatteryPerformance&&(acOnline()!=true||(power.Value&Vault.TypeCMask)!=0&&(power.Value&Vault.DcMask)==0))throw new IOException("极客调校需要原装适配器交流供电。");}
        CheckPower();
        var fans=BuildTuningFans(sourceFans,p,FactoryForMachine(sourceFans,3));
        if(UsesProductionClassicCore&&!RecognizedTuningProtocol(fans))
            throw new InvalidOperationException("目标不是允许的自定义风扇表协议。");
        var caps=before.Gpu.Capabilities;
        byte clamp=(byte)((before.Performance.Clamp&~Vault.ModeClampMask)|(caps.Supported?(p.GpuSuperPerformance?16:0):(before.Performance.Clamp&16)));
        var target=new PerformanceSnapshot(fans,clamp,(byte)((before.Performance.Trigger&~Vault.SilentMask)|Vault.RgbEnableMask));
        audit(new{at=DateTimeOffset.Now,operation="custom-tuning-intent",before,profile=p});
        try
        {
            List<ClassicControlStep> steps=[];
            void Flush()=>FlushClassicControls(root.Project,steps);
            void Write(ushort address,int value,int ignore=0)
            {
                byte desired=checked((byte)value);
                if(UsesProductionClassicCore){steps.Add(new(address,desired,ignore));return;}
                Guard();
                if(!ec.Write(address,desired)||ec.Read(address) is not byte actual||(actual&~ignore)!=(desired&~ignore))
                    throw new IOException("调校写入回读不一致。");
            }
            Write(Vault.FanClamp,before.Performance.Clamp|Vault.FanBoostMask);
            // Yield an already active custom mode before staging power values.
            // This avoids transient PL1/PL2 inversions while replacing its draft.
            Write(Vault.CustomMode,before.Performance.Fans.Custom&~1);
            Write(Vault.FanLatch,4);Write(Vault.FanLatch,0);Write(Vault.ApControl,fans.ApControl);
            var tables=new[]{(Vault.CpuUpBase,fans.CpuUp),(Vault.CpuDownBase,fans.CpuDown),(Vault.CpuDutyBase,fans.CpuDuty),
                (Vault.GpuUpBase,fans.GpuUp),(Vault.GpuDownBase,fans.GpuDown),(Vault.GpuDutyBase,fans.GpuDutyAndControl)};
            Flush();
            if(UsesProductionClassicCore)WriteClassicFanTables(root.Project,tables,factory:false);
            else foreach(var (start,values) in tables)for(int i=0;i<16;i++)Write((ushort)(start+i),values[i]);
            CheckPower();
            // Custom mode is asserted only after the complete power/fan draft is staged.
            int tcc=0;
            if(before.CpuTuning)
            {
                Write(Vault.CustomPl1,p.Pl1);Write(Vault.CustomPl2,p.Pl2);Write(Vault.CustomPl4,p.Pl4);
                tcc=EncodeTemperature(before.Amd,before.TjMax,p.TemperatureEnabled,p.TemperatureC,before.Registers.TurboTcc);
                Write(Vault.CustomTcc,tcc);
            }
            int boost=p.GpuSuperPerformance?caps.BoostMaxWatts:p.GpuBoostEnabled?p.GpuBoostWatts:0;
            int dbControl=(before.Registers.DbControl&~7)|(p.GpuSuperPerformance||p.GpuBoostEnabled?7:5);
            int offset=caps.ConfigurableBase?(p.GpuSuperPerformance?caps.BaseMaxWatts:p.GpuBaseWatts)-caps.FloorWatts:before.Registers.TgpOffset;
            if(caps.Supported)
            {
                Write(Vault.GpuDbControl,dbControl);Write(Vault.GpuDbFlag,255);
                if(caps.ConfigurableBase)Write(Vault.GpuTgpOffset,offset);
                Write(Vault.GpuDbWatts,boost);
            }
            Write(Vault.FanIsolated,fans.Isolated);Write(Vault.FanRamp,fans.Ramp);Write(Vault.ModeIndex,fans.ModeIndex);
            Write(Vault.SilentFlag,fans.Silent,1);Write(Vault.Trigger,target.Trigger,Vault.RgbEnableMask|Vault.SilentMask);
            Write(Vault.CustomMode,fans.Custom);Write(Vault.FanLatch,fans.Latch);
            Write(Vault.FanClamp,clamp|Vault.FanBoostMask);
            Flush();
            if(caps.Supported&&p.Offsets!=before.Gpu.Offsets){CheckPower();core.SetClockOffsets(before.Gpu.Offsets!,p.Offsets!);}
            CheckPower();Write(Vault.FanClamp,clamp);Flush();
            var after=ReadTuning(ec,core);var r=after.Registers;
            if(!SamePerformance(after.Performance,target)||before.CpuTuning&&(r.Pl1!=p.Pl1||r.Pl2!=p.Pl2||r.Pl4!=p.Pl4||r.Tcc!=tcc)||
               caps.Supported&&(r.DbControl!=dbControl||r.DbFlag!=255||r.DbWatts!=boost||r.TgpOffset!=offset||after.Gpu.Offsets!=p.Offsets))throw new IOException("最终调校状态与草稿不一致。");
            expected=Read(ec);PerformanceState=after.Performance;TuningState=after;SynchronizePerformanceIndicator(after.Performance.Mode);
            var receipt=new TuningReceipt(before,after,p);audit(new{at=DateTimeOffset.Now,operation="custom-tuning-confirmed",receipt});lastTuning=receipt;lastPerformance=null;return receipt;
        }
        catch(Exception error)
        {
            faulted=true;bool coolingConfirmed=false;
            try{if(blocked()==null&&ec.Read(Vault.FanClamp) is byte value)coolingConfirmed=ec.Write(Vault.FanClamp,(byte)(value|Vault.FanBoostMask))&&ec.Read(Vault.FanClamp)==(byte)(value|Vault.FanBoostMask);}catch{}
            try{audit(new{at=DateTimeOffset.Now,operation="custom-tuning-stopped",coolingConfirmed,error=error.Message});}catch{}
            throw new IOException("调校已停止，可能部分生效；保留强冷并重新读取状态。"+error.Message,error);
        }
    });
}
