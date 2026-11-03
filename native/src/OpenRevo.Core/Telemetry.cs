using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRevo.Core;

public sealed record SystemSnapshot(double? CpuPercent, double? UsedMemoryGiB, double? TotalMemoryGiB,
    int? BatteryPercent, bool? OnAc, bool? Charging, double WorkingSetMiB, double PrivateMiB, DateTimeOffset At);

public sealed class Telemetry
{
    private ulong lastIdle, lastKernel, lastUser;
    private bool sampled;
    public double? ReadCpuPercent()
    {
        double? cpu = null;
        if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            if (sampled && idle >= lastIdle && kernel >= lastKernel && user >= lastUser)
            {
                ulong total = kernel - lastKernel + user - lastUser;
                if (total > 0) cpu = Math.Clamp(100.0 * (1.0 - (double)(idle - lastIdle) / total), 0, 100);
            }
            (lastIdle, lastKernel, lastUser, sampled) = (idle, kernel, user, true);
        }
        return cpu;
    }
    public SystemSnapshot Read()
    {
        double? cpu = ReadCpuPercent();
        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool memoryOk = GlobalMemoryStatusEx(ref mem), batteryOk = GetSystemPowerStatus(out var power);
        using var process = Process.GetCurrentProcess();
        return new(cpu, memoryOk ? (mem.TotalPhysical - mem.AvailablePhysical) / 1073741824d : null,
            memoryOk ? mem.TotalPhysical / 1073741824d : null,
            batteryOk && power.BatteryPercent <= 100 && (power.BatteryFlags & 128) == 0 ? power.BatteryPercent : null,
            batteryOk && power.Ac <= 1 ? power.Ac == 1 : null,
            batteryOk && power.BatteryFlags != 255 && (power.BatteryFlags & 128) == 0 ? (power.BatteryFlags & 8) != 0 : null,
            process.WorkingSet64 / 1048576d, process.PrivateMemorySize64 / 1048576d, DateTimeOffset.Now);
    }
    public void Reset() => sampled = false;
    public static bool? ReadAc() => GetSystemPowerStatus(out var power) && power.Ac <= 1 ? power.Ac == 1 : null;
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, BatteryFlags, BatteryPercent, SystemFlags; public uint LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}

