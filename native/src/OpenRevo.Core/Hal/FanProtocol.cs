namespace OpenRevo.Core.Hal;

public sealed record FanProtocolState(int[] CpuUp, int[] CpuDown, int[] CpuDuty, int[] GpuUp, int[] GpuDown, int[] GpuDutyAndControl,
    byte Latch, byte ApControl, byte ModeIndex, byte Silent, byte Custom, byte Isolated, byte Ramp);
public sealed partial class HardwareSession
{
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
