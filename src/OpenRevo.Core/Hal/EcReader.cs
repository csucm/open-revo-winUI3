// GPL-3.0-or-later. Read protocol ported from faintonce/open-revo
// b7d2b526e60cd04965eadab21e606bdb1e1aa56e, driver/wmi_ec.rs.
// Bounded protocol transport; hardware writes are exposed only through HardwareSession.



namespace OpenRevo.Core.Hal;

public sealed record EcSnapshot(int? CpuFan, int? GpuFan, int? BatteryLimit, bool? FanBoost, string Status);
public interface IEcTransport : IDisposable { byte? Read(ushort address); }

public static class EcReader
{
    // Device names are descriptive. Admission comes from the pinned Core driver.
    public static bool IsAllowed(DeviceIdentity device) => CoreMachine.Current.Ready;
    public static EcSnapshot Read(DeviceIdentity device, Func<IEcTransport>? open = null,CoreCompatibility? admission=null)
    {
        var capability=admission??CoreMachine.Current;
        if (!capability.Ready) return new(null, null, null, null, capability.Reason);
        if(open==null&&!capability.ClassicEc)return CoreMachine.ReadSnapshot();
        try
        {
            using var ec = (open ?? (() => new CoreEcTransport()))();
            if (ec.Read(Vault.ProjectId) is not byte id || id is 0 or 255||capability.Project.HasValue&&id!=capability.Project) return new(null, null, null, null, "EC 身份与Core探测不一致，已停止读取");
            var cpu = Rpm(ec, Vault.CpuFanHigh, Vault.CpuFanLow);
            var gpu = Rpm(ec, Vault.GpuFanHigh, Vault.GpuFanLow);
            int? limit = ec.Read(Vault.BatteryUpper) is byte raw ? raw & Vault.BatteryPercentMask : null;
            var boost = ec.Read(Vault.FanClamp);
            return new(cpu, gpu, limit is >= 40 and <= 100 ? limit : null,
                boost is byte b ? (b & Vault.FanBoostMask) != 0 : null,
                cpu.HasValue && gpu.HasValue && limit is >= 40 and <= 100 && boost.HasValue ? "EC 只读通道已连接" : "部分 EC 数据不可用");
        }
        catch (System.ComponentModel.Win32Exception e) { return new(null, null, null, null, $"EC 驱动不可访问（Win32 {e.NativeErrorCode}），未安装或启动驱动"); }
        catch (Exception e) { return new(null, null, null, null, $"EC 读取停止：{e.GetType().Name}"); }
    }
    private static int? Rpm(IEcTransport ec, ushort highAddress, ushort lowAddress)
    {
        var high = ec.Read(highAddress); var low = ec.Read(lowAddress); var highAgain = ec.Read(highAddress);
        if (!high.HasValue || !low.HasValue || high != highAgain) return null;
        int value = (high.Value << 8) | low.Value;
        return value <= 12000 ? value : null;
    }
}
