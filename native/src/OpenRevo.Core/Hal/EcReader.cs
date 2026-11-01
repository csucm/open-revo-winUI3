// GPL-3.0-or-later. Read protocol ported from faintonce/open-revo
// b7d2b526e60cd04965eadab21e606bdb1e1aa56e, driver/wmi_ec.rs.
// Bounded protocol transport; hardware writes are exposed only through HardwareSession.
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace OpenRevo.Core.Hal;

public sealed record EcSnapshot(int? CpuFan, int? GpuFan, int? BatteryLimit, bool? FanBoost, string Status);
public interface IEcTransport : IDisposable { byte? Read(ushort address); }

internal sealed class AcpiReadTransport : IEcTransport
{
    private readonly SafeFileHandle handle;
    internal AcpiReadTransport()
    {
        handle = CreateFileW(@"\\.\ACPIDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
    }
    public byte? Read(ushort address)
    {
        uint input = address;
        return DeviceIoControl(handle, Vault.ReadIoctl, ref input, sizeof(uint), out uint output, sizeof(uint), out uint count, IntPtr.Zero)
            && count == sizeof(uint) && output <= byte.MaxValue ? (byte)output : null;
    }
    public void Dispose() => handle.Dispose();
    internal bool Write(ushort address, byte value)
    {
        var input = new WriteInput { Address = address, Value = value };
        return DeviceIoControlWrite(handle, Vault.WriteIoctl, ref input, 8, out _, 4, out _, IntPtr.Zero);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WriteInput { public uint Address, Value; }
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlWrite(SafeFileHandle h, uint code, ref WriteInput input, uint size, out uint output, uint outputSize, out uint count, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint input, uint inputSize, out uint output, uint outputSize, out uint count, IntPtr overlapped);
}

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
            var limit = ec.Read(Vault.BatteryUpper);
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
