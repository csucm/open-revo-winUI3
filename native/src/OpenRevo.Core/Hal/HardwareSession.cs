using System.Text.Json;
using Microsoft.Win32;

namespace OpenRevo.Core.Hal;

public sealed record ControlState(byte Project, byte FanClamp, byte Upper, byte Lower, byte BatteryMode, byte Platform, byte Support1, byte Support2, byte PowerStatus)
{
    public bool FanBoost => (FanClamp & Vault.FanBoostMask) != 0;
    public bool BatteryLimitReached { get; init; }
    public byte RawUpper => (byte)(Upper | (BatteryLimitReached ? Vault.BatteryReachedMask : 0));
    // Verified Project 16 and the diagnosed Jiaolong Project 26 can retain preset
    // thresholds after a selector reset. Keep the exact pair and project guard.
    public bool BatteryControlAvailable => (Support2 & Vault.BatterySupportMask) != 0 ||
        Project is >0 and <255 && Lower == Upper - 5 &&
        ((Upper, BatteryMode & Vault.BatteryModeMask) is (60, Vault.BatteryStation) or (80, Vault.BatteryBalance) or (100, Vault.BatteryLongLife) ||
         (Project is Vault.VerifiedProject or Vault.JiaolongBatteryProject)&&(Upper is 60 or 80)&&(BatteryMode&Vault.BatteryModeMask)==Vault.BatteryLongLife);

    internal bool MatchesBatteryMode(byte actual)
    {
        if (actual == BatteryMode) return true;
        // Project 26 firmware clears the selector after reaching the retained
        // threshold (diagnostic: 60/55, 0x29 -> 0x09). Keep all other bits exact.
        if (Project != Vault.JiaolongBatteryProject || Lower != Upper - 5 ||
            Upper is not (60 or 80) || ((actual ^ BatteryMode) & ~Vault.BatteryModeMask) != 0) return false;
        int preset = Upper == 60 ? Vault.BatteryStation : Vault.BatteryBalance;
        int expected = BatteryMode & Vault.BatteryModeMask, observed = actual & Vault.BatteryModeMask;
        return (expected == preset || expected == Vault.BatteryLongLife) &&
            (observed == preset || observed == Vault.BatteryLongLife);
    }
    internal bool MatchesBattery(ControlState other) => Project == other.Project &&
        Upper == other.Upper && Lower == other.Lower && MatchesBatteryMode(other.BatteryMode);
}
public sealed class AcPowerRequiredException(string message) : InvalidOperationException(message) { }
 
public sealed record HardwareReceipt(string Operation, ControlState Before, ControlState After, DateTimeOffset At);
public sealed record ModeCommandReceipt(int Before, int After, PerformanceReceipt? Classic = null);

// Injectable test transport. Production construction stays internal; no raw UI/IPC gateway.
public interface IHardwareTransport : IEcTransport { bool Write(ushort address, byte value); }
public sealed partial class HardwareSession
{
    private readonly Func<IHardwareTransport> open;
    private readonly Func<string?> blocked;
    private readonly Action<object> audit;
    private readonly Func<bool?> acOnline;
    private readonly Func<string> readCpuName;
    private ControlState? expected;
    private HardwareReceipt? lastBattery;
    private bool faulted;
    private bool useCoreAdmission,delegatedActive;
    private readonly CoreCompatibility? fixtureAdmission;
    private CoreCompatibility Admission=>fixtureAdmission??CoreMachine.Current;
    private bool UsesProductionClassicCore=>useCoreAdmission&&fixtureAdmission==null&&!CoreDelegated;
    public bool CoreDelegated=>useCoreAdmission&&!Admission.ClassicEc;
    public bool SupportsPerformance=>!useCoreAdmission||Admission.Performance;
    public bool SupportsBattery=>!useCoreAdmission||Admission.Battery;
    public bool SupportsFanBoost=>!useCoreAdmission||Admission.FanBoost;
    public bool SupportsCustomTuning=>!useCoreAdmission||Admission.CustomTuning||DeclaredCpuTuning!=null;
    public bool SupportsTouchpad=>!CoreDelegated||Admission.TouchpadControl;
    public int? CurrentMode=>CoreDelegated?Admission.Mode:PerformanceState?.Mode;
    private bool MatchesProject(byte project)=>useCoreAdmission?Admission.Project==project&&project is >0 and <255:project==Vault.VerifiedProject;
    private static readonly object gate = new();
    private int batteryPerformancePermission;
    public bool AllowBatteryPerformance=>Volatile.Read(ref batteryPerformancePermission)!=0;
    public void SetBatteryPerformancePermission(bool enabled)=>Volatile.Write(ref batteryPerformancePermission,enabled?1:0);
    public bool? ProbeOriginalAdapter()=>Serialized(()=>
    {
        Guard();bool? ac=acOnline();if(ac!=true)return ac;
        using var ec=open();byte? power=ec.Read(Vault.PowerStatus);return power.HasValue?(bool?)((power.Value&Vault.TypeCMask)==0||(power.Value&Vault.DcMask)!=0):null;
    });
    public bool Active => (expected != null||delegatedActive) && !faulted;
    public ControlState? State => expected;
    public ModeCommandReceipt ApplyModeCommand(ITuningGpuCore? gpu,int mode,bool originalDcConfirmed=false)
    {
        if(!CoreDelegated)
        {
            var classic=gpu!=null?SetFactoryPerformance(gpu,mode,originalDcConfirmed):SetPerformanceMode(mode,originalDcConfirmed);
            return new(classic.Before.Mode,classic.After.Mode,classic);
        }
        return TryApplyDelegatedMode(mode)!;
    }
    internal ModeCommandReceipt? TryApplyDelegatedMode(int mode, Predicate<int>? canReplace = null)
    {
        return Serialized<ModeCommandReceipt?>(()=>
        {
            Guard();if(!Active||!CoreDelegated||!SupportsPerformance)throw new InvalidOperationException("Core驱动未提供性能档位控制。");
            if(mode is <1 or >3)throw new ArgumentOutOfRangeException(nameof(mode));
            if(mode==3&&!AllowBatteryPerformance&&acOnline()!=true)throw new AcPowerRequiredException("狂暴模式需要交流供电。请接上电源后再切换。");
            int before=CoreMachine.Refresh().Mode??throw new IOException("Core未提供当前档位。");
            if(canReplace?.Invoke(before)==false)return null;
            audit(new{at=DateTimeOffset.Now,operation="core-mode-intent",before,target=mode});
            CoreMachine.Library.SetDeclarativeMode(before,mode);var after=CoreMachine.Refresh();
            if(after.Mode!=mode){faulted=true;throw new IOException("Core档位回读不一致。");}
            audit(new{operation="core-mode-confirmed",before,after=after.Mode});
            return new ModeCommandReceipt(before,mode);
        });
    }
    public HardwareSession(Func<IHardwareTransport> open, Func<string?> blocked, Action<object> audit, Func<bool?>? acOnline = null,Func<string>? cpuName=null,CoreCompatibility? fixtureAdmission=null)
        { (this.open, this.blocked, this.audit, this.acOnline,readCpuName) = (open, blocked, audit, acOnline ?? (() => new Telemetry().Read().OnAc),cpuName??(()=>DeviceIdentity.Read().Processor));this.fixtureAdmission=fixtureAdmission;useCoreAdmission=fixtureAdmission!=null; }
    public static HardwareSession ForThisMachine() => ForThisMachine(null);
    internal static HardwareSession ForThisMachine(Func<string?>? runtimeBlock) => new(() => new CoreEcTransport(),
        () => runtimeBlock?.Invoke() ?? CheckEnvironment(), record =>
    {
        Directory.CreateDirectory(AppData.DirectoryPath);
        File.AppendAllText(Path.Combine(AppData.DirectoryPath, "hardware-audit.jsonl"), JsonSerializer.Serialize(record) + Environment.NewLine);
    }){useCoreAdmission=true};
    public static string? CheckMachineIdentity()
    {
        var admission=CoreMachine.Current;
        return admission.Ready?null:admission.Reason;
    }
    public static string? CheckEnvironment() => CheckMachineIdentity() ?? HardwareProcessConflicts.Check();
    internal string? CheckAvailability() => blocked();
    private T Serialized<T>(Func<T> action)
    {
        lock (gate)
        {
            using var mutex = new Mutex(false, @"Local\OpenRevoNative.HardwareOperation");
            bool owned;
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("另一硬件操作尚未完成。");
            try { return action(); } finally { mutex.ReleaseMutex(); }
        }
    }
    private void Guard()
    {
        string? reason = blocked();
        if (reason != null) throw new InvalidOperationException(reason);
    }
    private static ControlState Read(IHardwareTransport ec)
    {
        byte Get(ushort address) => ec.Read(address) ?? throw new IOException("硬件状态读取失败，未继续操作。");
        byte project = Get(Vault.ProjectId);
        if (project is 0 or 255) throw new IOException("EC 项目身份无效。");
        byte clamp = Get(Vault.FanClamp), rawUpper = Get(Vault.BatteryUpper);
        var state = new ControlState(project, clamp, (byte)(rawUpper & Vault.BatteryPercentMask), Get(Vault.BatteryLower), Get(Vault.BatteryMode), Get(Vault.PlatformId), Get(Vault.Support1), Get(Vault.Support2), Get(Vault.PowerStatus))
            { BatteryLimitReached = (rawUpper & Vault.BatteryReachedMask) != 0 };
        if (state.Upper is < 40 or > 100 || state.Lower > state.Upper || state.Platform is 4 or 5)
        {
            // Preserve the rejected read, rather than reporting the last accepted snapshot.
            DiagnosticLog.Event("hardware.control-state.rejected","warning",new{state,
                invalidUpper=state.Upper is <40 or >100,invalidLower=state.Lower>state.Upper,
                platformOverride=state.Platform is 4 or 5});
            throw new IOException("当前电池阈值或平台覆盖状态不满足普通控制条件；请用原控制中心恢复标准设置。");
        }
        return state;
    }
    public ControlState? Takeover() => Serialized<ControlState?>(() =>
    {
        if(useCoreAdmission&&fixtureAdmission==null)CoreMachine.Refresh();
        if(useCoreAdmission&&!Admission.Ready)throw new InvalidOperationException(Admission.Reason);
        if(CoreDelegated){Guard();delegatedActive=true;requestedFanBoost=null;faulted=false;audit(new{at=DateTimeOffset.Now,operation="takeover-core-driver",admission=Admission});return null;}
        Guard(); using var ec = open(); var state = Read(ec);
        if (!MatchesProject(state.Project)) throw new InvalidOperationException("EC 项目与Core探测结果不符，未接管。");
        audit(new { at = DateTimeOffset.Now, operation = "takeover-read-only", state });
        expected = state; faulted = false; lastBattery = null; lastPerformance = null; lastLighting = null;lastTuning=null;TuningState=null; return state;
    });
    public void Release() { lock (gate) { expected = null;delegatedActive=false; requestedFanBoost=null; lastBattery = null; lastPerformance = null; lastLighting = null;lastTuning=null;TuningState=null; faulted = false; } }
    private HardwareReceipt Apply(string operation, bool battery, Func<ControlState, ControlState> target)
        => Serialized(() =>
        {
            if(battery&&!SupportsBattery||operation=="fan-boost"&&!SupportsFanBoost)throw new InvalidOperationException("Core未提供此硬件功能。");
            if (!Active || expected == null) throw new InvalidOperationException("请先启用本次会话的硬件接管。发生错误后需重新读取并接管。");
            Guard(); using var ec = open(); var before = Read(ec);
            if (!MatchesProject(before.Project)) throw new InvalidOperationException("EC 项目与已验证的本机适配不符。");
            if (before.Project != expected.Project || before.Platform != expected.Platform ||
                (battery ? !expected.MatchesBattery(before) : before.FanClamp != expected.FanClamp))
                throw new InvalidOperationException("确认期间硬件状态已变化；请重新读取并接管后再操作。");
            var desired = target(before);
            audit(new { at = DateTimeOffset.Now, operation, phase = "intent", before, desired });
            bool attemptedWrite = false;
            void Write(ushort address, byte value)
            {
                Guard();
                attemptedWrite = true;
                if (!ec.Write(address, value)) throw new IOException("驱动报告写入失败，硬件状态可能部分改变；后续写入已停止。");
                byte? actual = ec.Read(address);
                bool matches = actual.HasValue && (address == Vault.BatteryUpper ?
                    (actual.Value & Vault.BatteryPercentMask) == value : address == Vault.BatteryMode && battery ?
                    desired.MatchesBatteryMode(actual.Value) : actual.Value == value);
                if (!matches) throw new IOException("硬件写入回读不一致，后续写入已停止。需要重新读取当前状态。");
            }
            try
            {
                if (battery)
                {
                    if (!before.BatteryControlAvailable) throw new InvalidOperationException("EC 未声明支持充电限制，且当前状态不符合本机普通充电档位协议。");
                    if(UsesProductionClassicCore)
                    {
                        Guard();attemptedWrite=true;
                        CoreMachine.Library.ExecuteFeature(13,new{kind="battery",project=before.Project,oldUpper=before.RawUpper,
                            oldLower=before.Lower,oldMode=before.BatteryMode,upper=desired.Upper,lower=desired.Lower,mode=desired.BatteryMode});
                    }
                    else
                    {
                    if (desired.Upper < before.Lower) { Write(Vault.BatteryLower, desired.Lower); Write(Vault.BatteryUpper, desired.Upper); }
                    else { Write(Vault.BatteryUpper, desired.Upper); Write(Vault.BatteryLower, desired.Lower); }
                    Write(Vault.BatteryMode, desired.BatteryMode);
                    }
                }
                else
                {
                    if ((before.Support1 & Vault.FanSupportMask) == 0) throw new InvalidOperationException("EC 未声明支持强冷。");
                    if(UsesProductionClassicCore)
                    {
                        Guard();attemptedWrite=true;
                        CoreMachine.Library.ExecuteFeature(13,new{kind="boost",project=before.Project,before=before.FanClamp,after=desired.FanClamp});
                    }
                    else Write(Vault.FanClamp, desired.FanClamp);
                }
                var after = Read(ec);
                if (battery ? !desired.MatchesBattery(after) : after.FanClamp != desired.FanClamp)
                    throw new IOException("最终硬件状态未保持目标值；请检查其他控制程序或固件行为。");
                var receipt = new HardwareReceipt(operation, before, after, DateTimeOffset.Now);
                expected = after; audit(receipt);
                if (battery) lastBattery = receipt;
                return receipt;
            }
            catch (Exception e)
            {
                faulted = true;
                try { audit(new { at = DateTimeOffset.Now, operation, phase = "failed-stop", attemptedWrite, error = e.Message }); }
                catch (Exception) { /* Retain the original device/audit failure. */ }
                if (attemptedWrite) throw new IOException("操作已停止，硬件可能已部分改变，请重新读取当前状态。" + e.Message, e);
                throw;
            }
        });
    public HardwareReceipt SetFanBoost(bool enabled) => Apply("fan-boost", false, current => current with
        { FanClamp = enabled ? (byte)(current.FanClamp | Vault.FanBoostMask) : (byte)(current.FanClamp & ~Vault.FanBoostMask) });
    public HardwareReceipt SetBatteryPreset(int upper)
    {
        if (upper is not (60 or 80 or 100)) throw new ArgumentOutOfRangeException(nameof(upper), "仅支持 60 / 80 / 100 三档。");
        return Apply("battery-preset", true, current => current with
        {
            Upper = (byte)upper, Lower = (byte)(upper - 5), BatteryLimitReached = false,
            BatteryMode = (byte)((current.BatteryMode & ~Vault.BatteryModeMask) | (upper == 60 ? Vault.BatteryStation : upper == 80 ? Vault.BatteryBalance : Vault.BatteryLongLife))
        });
    }
    public HardwareReceipt UndoBattery() => Serialized(() =>
    {
        var receipt = lastBattery ?? throw new InvalidOperationException("本次会话没有可撤销的充电限制更改。");
        var result = Apply("undo-battery-preset", true, current => current with { Upper = receipt.Before.Upper, Lower = receipt.Before.Lower, BatteryLimitReached = false, BatteryMode = (byte)((current.BatteryMode & ~Vault.BatteryModeMask) | (receipt.Before.BatteryMode & Vault.BatteryModeMask)) });
        lastBattery = null; return result;
    });
}

