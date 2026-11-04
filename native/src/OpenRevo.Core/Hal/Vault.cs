// Generated from upstream ec_registry.json at b7d2b526e60cd04965eadab21e606bdb1e1aa56e.
// All hardware addresses and protocol masks remain isolated in this vault.
namespace OpenRevo.Core.Hal;
public static class Vault
{
    public const ushort ProjectId = 1856;
    public const ushort CpuFanHigh = 1124;
    public const ushort CpuFanLow = 1125;
    public const ushort GpuFanHigh = 1132;
    public const ushort GpuFanLow = 1131;
    public const ushort BatteryUpper = 1977;
    public const ushort FanClamp = 1873;
    public const byte FanBoostMask = 64;
    public const ushort BatteryLower = 2000;
    public const ushort BatteryMode = 1958;
    public const ushort PlatformId = 1987;
    public const ushort Support2 = 1894;
    public const ushort Support1 = 1893;
    public const byte FanSupportMask = 128;
    public const byte VerifiedProject = 16;
    public const ushort PowerStatus = 1996;
    public const byte BatterySupportMask = 64;
    public const byte BatteryModeMask = 49;
    public const byte BatteryLongLife = 1, BatteryBalance = 17, BatteryStation = 33;
    public const ushort RgbRed = 1897, RgbGreen = 1898, RgbBlue = 1899;
    public const ushort Backlight = 1932, Trigger = 1895, FanIsolated = 1989;
    public const byte SingleZoneSupportMask = 4, RgbEnableMask = 32, RainbowMask = 128;
    public const byte BacklightCommandMask = 16;
    public const ushort CpuUpBase = 3840, CpuDownBase = 3856, CpuDutyBase = 3872;
    public const ushort GpuUpBase = 3888, GpuDownBase = 3904, GpuDutyBase = 3920;
    public const ushort FanLatch = 1990, ApControl = 1857, ModeIndex = 1963, SilentFlag = 1115, CustomMode = 1798;
    public const ushort FanRamp = 1927;
    public const byte ModeClampMask = 176, ModeIndexMask = 30, SilentMask = 8, DcMask = 1, TypeCMask = 62;
    public const ushort SwitchStatus = 1896, CameraCutoff = 1831;
    public const byte WinLockMask = 1, CameraCutoffMask = 8;
    public const ushort FnLock=1870;
    public const byte FnLockMask=16,TouchpadMask=8;
    // Pinned Core ec_registry.json: AC recovery and power-off USB charge.
    public const ushort AcRecovery=1830;
    public const byte AcRecoveryMask=8,UsbChargeMask=16;
    // Custom tuning protocol, pinned Core 4ab36ff / config/ec_registry.json.
    public const ushort CustomPl1=1923,CustomPl2=1924,CustomPl4=1925,CustomTcc=1926;
    public const ushort TurboPl1=1959,TurboPl2=1960,TurboPl4=1961,TurboTcc=2010;
    public const ushort GpuDbControl=1859,GpuTgpOffset=1860,GpuDbFlag=1861,GpuDbWatts=1862;
    public const ushort OfficePl1=1844,OfficePl2=1845,OfficePl4=1846,BalancePl1=1840,BalancePl2=1841,BalancePl4=1842;
}
