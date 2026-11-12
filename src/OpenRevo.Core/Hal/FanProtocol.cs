namespace OpenRevo.Core.Hal;

public sealed record FanProtocolState(int[] CpuUp, int[] CpuDown, int[] CpuDuty, int[] GpuUp, int[] GpuDown, int[] GpuDutyAndControl,
    byte Latch, byte ApControl, byte ModeIndex, byte Silent, byte Custom, byte Isolated, byte Ramp);
#if GM7TG0M_DIAGNOSTICS
internal sealed record OemFanSnapshotReceipt(byte Project, byte Platform, string Driver, PerformanceSnapshot Performance);
#endif

public sealed partial class HardwareSession
{
#if GM7TG0M_DIAGNOSTICS
    internal OemFanSnapshotReceipt CaptureGm7tg0mFanSnapshot() => Serialized<OemFanSnapshotReceipt>(() =>
    {
        Guard();
        if (!CanReadGm7tg0mFactoryMode)
            throw new NotSupportedException("此诊断仅适用于已识别的 GM7TG0M（N.1.07MRO06）。");

        const byte project = Gm7tg0mFactoryMode.Project, platform = Gm7tg0mFactoryMode.Platform;
        using var ec = open();
        void CheckIdentity()
        {
            if (ec.Read(Vault.ProjectId) != project || ec.Read(Vault.PlatformId) != platform)
                throw new IOException("EC 项目或平台身份与预期不符。");
        }

        CheckIdentity();
        var first = ReadPerformance(ec);
        CheckIdentity();
        var second = ReadPerformance(ec);
        CheckIdentity();
        Guard();
        if (!SamePerformance(first, second) || first.Trigger != second.Trigger || first.Fans.Silent != second.Fans.Silent ||
            first.Mode != second.Mode)
            throw new IOException("两次读取的风扇表或控制状态发生变化，请等原厂档位稳定后重试。");

        return new(project, platform, Admission.Driver, second);
    });
#endif

    public FanProtocolState ProbeFanProtocol() => Serialized(() =>
    {
        Guard(); using var ec = open();
        return ReadFanProtocol(ec);
    });
    private static FanProtocolState ReadFanProtocol(IHardwareTransport ec)
    {
        byte Get(ushort address) => ec.Read(address) ?? throw new IOException("风扇协议读取失败。");
        int[] Range(ushort address) => Enumerable.Range(0, 16).Select(i => (int)Get((ushort)(address + i))).ToArray();
        return new FanProtocolState(Range(Vault.CpuUpBase), Range(Vault.CpuDownBase), Range(Vault.CpuDutyBase), Range(Vault.GpuUpBase), Range(Vault.GpuDownBase),
            Range(Vault.GpuDutyBase), Get(Vault.FanLatch), Get(Vault.ApControl), Get(Vault.ModeIndex), Get(Vault.SilentFlag), Get(Vault.CustomMode), Get(Vault.FanIsolated), Get(Vault.FanRamp));
    }
}
