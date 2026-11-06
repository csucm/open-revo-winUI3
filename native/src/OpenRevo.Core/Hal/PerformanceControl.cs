namespace OpenRevo.Core.Hal;

public sealed record PerformanceSnapshot(FanProtocolState Fans, byte Clamp, byte Trigger)
{
    public int Mode => (Fans.Custom&1)!=0?4:(Clamp & Vault.ModeClampMask) == 160 ? 1 : (Fans.ModeIndex & Vault.ModeIndexMask) == 16 ? 3 : 2;
}
public sealed record PerformanceReceipt(PerformanceSnapshot Before, PerformanceSnapshot After);

public sealed partial class HardwareSession
{
    private PerformanceReceipt? lastPerformance;
    public PerformanceSnapshot? PerformanceState { get; private set; }
    private static PerformanceSnapshot ReadPerformance(IHardwareTransport ec) => new(ReadFanProtocol(ec),
        ec.Read(Vault.FanClamp) ?? throw new IOException("档位读取失败"), ec.Read(Vault.Trigger) ?? throw new IOException("档位触发状态读取失败"));
    public PerformanceSnapshot ProbePerformance() => Serialized(() =>
    {
        Guard(); using var ec = open(); return PerformanceState = ReadPerformance(ec);
    });
    private static FanProtocolState Factory(FanProtocolState current, int mode)
    {
        int[] up = mode == 1 ? [0,55,57,59,61,63,65,67,69,255,255,255,255,255,255,255]
            : mode == 2 ? [0,55,58,61,65,69,72,75,78,81,255,255,255,255,255,255]
            : [0,55,58,61,65,69,72,75,78,81,84,255,255,255,255,255];
        int[] down = mode == 1 ? [48,58,60,62,64,66,68,70,255,255,255,255,255,255,255,255]
            : mode == 2 ? [48,58,61,65,69,73,76,79,82,255,255,255,255,255,255,255]
            : [48,58,61,65,69,73,76,79,82,85,255,255,255,255,255,255];
        int[] duty = mode == 1 ? [0,60,60,60,60,70,70,90,110,110,110,110,110,110,110,110]
            : mode == 2 ? [0,60,70,80,100,110,120,130,150,160,160,160,160,160,160,160]
            : [0,60,70,80,100,110,120,130,150,160,180,180,180,180,180,180];
        int[] gpuUp = mode == 1 ? [0,55,55,57,57,59,62,64,66,255,255,255,255,255,255,255] : up.ToArray();
        int[] gpuDown = mode == 1 ? [48,56,58,58,60,63,65,67,255,255,255,255,255,255,255,255] : down.ToArray();
        var gpu = duty.ToArray(); gpu[15] = Math.Max(160, gpu[15]);
        return current with { CpuUp = up, CpuDown = down, CpuDuty = duty, GpuUp = gpuUp, GpuDown = gpuDown, GpuDutyAndControl = gpu,
            Latch = 4, ApControl = (byte)(current.ApControl | 129), ModeIndex = (byte)((current.ModeIndex & ~Vault.ModeIndexMask) | (mode == 3 ? 16 : 0)),
            Silent = (byte)(mode == 1 ? 1 : 0), Custom = (byte)(current.Custom & ~1), Isolated = (byte)(current.Isolated & ~128), Ramp = 131 };
    }
    private FanProtocolState FactoryForMachine(FanProtocolState current,int mode)
    {
        var template=Factory(current,mode);if(!useCoreAdmission)return template;
        var table=CoreMachine.Library.Read(21,(uint)mode);
        int[] Row(string key){var row=table.GetProperty(key).EnumerateArray().Select(v=>v.GetInt32()).ToArray();if(row.Length!=16||row.Any(v=>v is <0 or >255))throw new IOException("Core风扇表不完整。");return row;}
        var gpu=Row("gpu_duty");gpu[15]=Math.Max(160,gpu[15]);
        return template with{CpuUp=Row("cpu_upt"),CpuDown=Row("cpu_downt"),CpuDuty=Row("cpu_duty"),GpuUp=Row("gpu_upt"),GpuDown=Row("gpu_downt"),GpuDutyAndControl=gpu,
            Custom=64}; // Core set_custom_mode_ec(false): use the complete factory value, not an OEM source bit.
    }
    private static bool SameTables(FanProtocolState a, FanProtocolState b) => a.CpuUp.SequenceEqual(b.CpuUp) && a.CpuDown.SequenceEqual(b.CpuDown)
        && a.CpuDuty.SequenceEqual(b.CpuDuty) && a.GpuUp.SequenceEqual(b.GpuUp) && a.GpuDown.SequenceEqual(b.GpuDown) && a.GpuDutyAndControl.SequenceEqual(b.GpuDutyAndControl);
    private static bool SamePerformance(PerformanceSnapshot a, PerformanceSnapshot b) => SameTables(a.Fans,b.Fans) && a.Fans.ModeIndex == b.Fans.ModeIndex
        && a.Fans.Custom == b.Fans.Custom && a.Fans.Isolated == b.Fans.Isolated && a.Fans.Ramp == b.Fans.Ramp
        && a.Fans.Latch == b.Fans.Latch && a.Fans.ApControl == b.Fans.ApControl
        && a.Clamp == b.Clamp && (a.Trigger & ~(Vault.RgbEnableMask|Vault.SilentMask)) == (b.Trigger & ~(Vault.RgbEnableMask|Vault.SilentMask));
    public PerformanceReceipt SetPerformanceMode(int mode, bool originalDcConfirmed = false)
    {
        if(!SupportsPerformance)throw new InvalidOperationException("Core未提供性能档位控制。");
        if (mode is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(mode));
        return ChangePerformance(current => FactoryPerformance(current,mode),mode==3,originalDcConfirmed,factoryReplacement:true);
    }
    private PerformanceSnapshot FactoryPerformance(PerformanceSnapshot current,int mode)=>new(FactoryForMachine(current.Fans,mode),
            (byte)((current.Clamp & ~Vault.ModeClampMask) | (mode == 1 ? 160 : mode == 3 ? 16 : 0)),
            (byte)((current.Trigger & ~Vault.SilentMask) | Vault.RgbEnableMask | (mode == 1 ? Vault.SilentMask : 0)));
    // Keep the existing source admission for temporary office mode. Do not newly replace
    // OEM tables here: the subsequent exact restore still requires a recognized target.
    public PerformanceReceipt SetTemporaryOfficeMode()=>ChangePerformance(current=>FactoryPerformance(current,1),false,false);
    public PerformanceReceipt SetFactoryPerformance(ITuningGpuCore core,int mode,bool originalDcConfirmed=false)=>Serialized(()=>
    {
        if(!SupportsPerformance)throw new InvalidOperationException("Core未提供性能档位控制。");
        if(mode is <1 or >3)throw new ArgumentOutOfRangeException(nameof(mode));
        if(!Active)throw new InvalidOperationException("请先连接设备。");Guard();
        // Reading GPU capabilities is harmless. All EC/power/target checks must precede any GPU reset.
        var gpu=core.ReadGpuTuning();
        Action? resetClocks=null;
        if(gpu.Offsets is {} offsets&&(offsets.CoreMhz!=0||offsets.MemoryMhz!=0))
            resetClocks=()=>
            {
                audit(new{at=DateTimeOffset.Now,operation="factory-clock-reset-intent",offsets});
                Guard();core.SetClockOffsets(offsets,new(0,0));
            };
        var result=ChangePerformance(current=>FactoryPerformance(current,mode),mode==3,originalDcConfirmed,
            factoryReplacement:true,preflight:before=>
        {
            if(before.Mode==4&&gpu.Capabilities.Supported&&gpu.Offsets==null)throw new InvalidOperationException("无法确认显卡超频状态，请先重新读取。");
        },beforeWrite:resetClocks);
        if(result.Before.Mode==4)lastPerformance=null;
        TuningState=null;return result;
    });
    public object? PerformanceDiagnostics {get;private set;}
    private PerformanceReceipt ChangePerformance(Func<PerformanceSnapshot,PerformanceSnapshot> target, bool turbo, bool dcConfirmed, PerformanceSnapshot? required = null,
        bool factoryReplacement=false,Action<PerformanceSnapshot>? preflight=null,Action? beforeWrite=null)
        => Serialized(() =>
    {
        if (!Active) throw new InvalidOperationException("请先启用本次硬件接管。");
        Guard(); using var ec = open(); var rootState = Read(ec); var before = ReadPerformance(ec);
        PerformanceDiagnostics=new{phase="preflight",factoryReplacement,rootState,before};
        // A matched classic Core channel defines the write protocol. OEM source table contents
        // need not equal our target tables when replacing all six rows. Tuning and undo retain
        // the strict source classifier; isolated fixtures do not gain production admission.
        bool replaceOemTable=factoryReplacement&&UsesProductionClassicCore&&Admission.Ready&&Admission.Performance;
        if (!MatchesProject(rootState.Project) || expected==null || rootState.Project!=expected.Project || rootState.Platform!=expected.Platform)
            throw new InvalidOperationException("当前设备或平台状态已变化，未修改性能档位。请重新读取并接管。");
        if (!replaceOemTable&&!RecognizedTuningProtocol(before.Fans))
            throw new InvalidOperationException("当前不是已验证的工厂风扇表协议，未修改性能档位。");
        if (required != null && !SamePerformance(before,required)) throw new InvalidOperationException("性能或风扇状态已被外部修改，未执行撤销。");
        void CheckPower()
        {
            var power=ec.Read(Vault.PowerStatus);
            if (turbo && (!power.HasValue || !AllowBatteryPerformance&&(!dcConfirmed || acOnline() != true || (power.Value & Vault.TypeCMask) != 0 && (power.Value & Vault.DcMask) == 0)))
                throw new AcPowerRequiredException("狂暴模式需要确认原装DC适配器，并检测到Windows交流供电；可在设置页明确确认后解除离电限制。");
        }
        CheckPower(); var desired = target(before);
        bool validTarget=UsesProductionClassicCore
            ? CoreMachine.Library.RecognizesClassicFanProtocol(desired.Fans,true)
            : Enumerable.Range(1,3).Any(mode=>SameTables(desired.Fans,FactoryForMachine(desired.Fans,mode)))
                && desired.Fans.Latch==4 && (desired.Fans.ApControl&129)==129 && desired.Fans.Custom==64;
        if(!validTarget)
            throw new InvalidOperationException("目标不是允许的工厂档位协议。");
        preflight?.Invoke(before);
        Guard();CheckPower();
        audit(new { at=DateTimeOffset.Now,operation="performance-intent",before,desired });
        bool attemptedWrite=false;
        try
        {
            if(beforeWrite!=null){attemptedWrite=true;beforeWrite();}
            Guard();CheckPower();
            if(beforeWrite!=null&&!SamePerformance(ReadPerformance(ec),before))throw new IOException("显卡重置后性能或风扇状态已变化，未继续写入档位。");
            List<ClassicControlStep> steps=[];
            void Flush(){if(steps.Count==0)return;attemptedWrite=true;FlushClassicControls(rootState.Project,steps);}
            void Write(ushort a, int value)
            {
                int ignore=a==Vault.Trigger?Vault.RgbEnableMask|Vault.SilentMask:a==Vault.SilentFlag?1:0;
                if(UsesProductionClassicCore){steps.Add(new(a,checked((byte)value),ignore));return;}
                Guard();
                attemptedWrite=true;
                if (!ec.Write(a,checked((byte)value))) throw new IOException("档位写入失败。");
                var actual=ec.Read(a);
                if (!actual.HasValue || (actual.Value & ~ignore)!=(value & ~ignore)) throw new IOException("档位回读失败。");
            }
            // Keep physical cooling clamped until the complete table is committed.
            Write(Vault.FanClamp,before.Clamp | Vault.FanBoostMask);
            Write(Vault.FanLatch,4); Write(Vault.FanLatch,0); Write(Vault.ApControl,desired.Fans.ApControl);
            var tables=new[] {(Vault.CpuUpBase,desired.Fans.CpuUp),(Vault.CpuDownBase,desired.Fans.CpuDown),(Vault.CpuDutyBase,desired.Fans.CpuDuty),
                (Vault.GpuUpBase,desired.Fans.GpuUp),(Vault.GpuDownBase,desired.Fans.GpuDown),(Vault.GpuDutyBase,desired.Fans.GpuDutyAndControl)};
            Flush();
            if(UsesProductionClassicCore)WriteClassicFanTables(rootState.Project,tables);
            else foreach(var (start,values) in tables) for(int i=0;i<16;i++) Write((ushort)(start+i),values[i]);
            CheckPower();
            Write(Vault.CustomMode,desired.Fans.Custom); Write(Vault.FanIsolated,desired.Fans.Isolated); Write(Vault.FanRamp,desired.Fans.Ramp);
            Write(Vault.ModeIndex,desired.Fans.ModeIndex); Write(Vault.SilentFlag,desired.Fans.Silent); Write(Vault.Trigger,desired.Trigger);
            Write(Vault.FanLatch,desired.Fans.Latch); Write(Vault.FanClamp,desired.Clamp);
            Flush();
            var after=ReadPerformance(ec);
            if(!SamePerformance(after,desired)) throw new IOException("最终档位或风扇表不一致。");
            expected=Read(ec); PerformanceState=after;SynchronizePerformanceIndicator(after.Mode); var result=new PerformanceReceipt(before,after);
            audit(new {at=DateTimeOffset.Now,operation="performance-confirmed",result});
            PerformanceDiagnostics=new{phase="confirmed",factoryReplacement,rootState,result};
            lastPerformance=result; return result;
        }
        catch(Exception error)
        {
            if(!attemptedWrite)
            {
                PerformanceDiagnostics=new{phase="preflight-rejected",factoryReplacement,rootState,before,desired,error=error.Message};
                throw;
            }
            faulted=true;
            PerformanceDiagnostics=new{phase="failed-stop",factoryReplacement,rootState,before,desired,error=error.Message};
            bool coolingConfirmed=false;
            try
            {
                if(blocked()==null && ec.Read(Vault.FanClamp) is byte value)
                { byte cooling=(byte)(value|Vault.FanBoostMask); coolingConfirmed=ec.Write(Vault.FanClamp,cooling) && ec.Read(Vault.FanClamp)==cooling; }
            }
            catch { }
            try { audit(new {at=DateTimeOffset.Now,operation="performance-stopped",coolingConfirmed,error=error.Message}); } catch { }
            throw new IOException("档位操作已停止；可能部分生效。请保留强冷并重新读取诊断。"+error.Message,error);
        }
    });
    private sealed record ClassicControlStep(ushort address,byte value,int ignore);
    private void FlushClassicControls(byte project,List<ClassicControlStep> steps)
    {
        if(steps.Count==0)return;
        Guard();
        CoreMachine.Library.ExecuteFeature(13,new{kind="fan_controls",project,steps});
        steps.Clear();
    }

    private void WriteClassicFanTables(byte project,(ushort Start,int[] Values)[] tables)
    {
        Guard();
        CoreMachine.Library.ExecuteFeature(13,new{kind="fan_tables",project,values=tables.SelectMany(table=>table.Values).ToArray()});
    }

    public PerformanceReceipt UndoPerformance() => Serialized(() =>
    {
        var last=lastPerformance ?? throw new InvalidOperationException("没有本次会话可撤销的性能更改。");
        var result=ChangePerformance(_=>last.Before,last.Before.Mode==3,true,last.After); lastPerformance=null; return result;
    });
    public PerformanceReceipt RestoreAutomaticOffice(PerformanceReceipt receipt)=>Serialized(()=>
    {
        // Restore only our exact temporary office state, preserving later user changes.
        var result=ChangePerformance(_=>receipt.Before,receipt.Before.Mode==3,true,receipt.After);
        lastPerformance=null;return result;
    });
}
