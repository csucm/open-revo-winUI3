namespace OpenRevo.Core.Hal;

public sealed record DeviceSwitchState(bool WinKeyLocked, bool CameraPowered);
public sealed partial class HardwareSession
{
    private static long nextWinCommandAt;
    private const int WinCommandIntervalMs=2000;
    private static DeviceSwitchState ReadSwitches(IHardwareTransport ec) => new(
        ((ec.Read(Vault.SwitchStatus) ?? throw new IOException("Win 键状态读取失败。")) & Vault.WinLockMask) != 0,
        ((ec.Read(Vault.CameraCutoff) ?? throw new IOException("相机供电状态读取失败。")) & Vault.CameraCutoffMask) == 0);
    public DeviceSwitchState ProbeSwitches() => Serialized(() =>
    {
        Guard(); using var ec=open();
        if(!MatchesProject(Read(ec).Project))throw new IOException("设备不匹配。");
        return ReadSwitches(ec);
    });
    public DeviceSwitchState SetWinKeyLocked(bool locked) => Serialized(() =>
    {
        if(!Active)throw new InvalidOperationException("请先连接硬件。");
        Guard(); using var ec=open();
        if(!MatchesProject(Read(ec).Project))throw new IOException("设备不匹配。");
        var before=ReadSwitches(ec);
        if(before.WinKeyLocked==locked)return before;
        // Debounce the NEXT command, instead of delaying confirmation of the
        // command just sent. Shared by sessions in this process under gate.
        long remaining=nextWinCommandAt-Environment.TickCount64;
        if(remaining>0)
        {
            Thread.Sleep((int)Math.Min(remaining,WinCommandIntervalMs));Guard();
            before=ReadSwitches(ec);if(before.WinKeyLocked==locked)return before;
        }
        byte trigger=ec.Read(Vault.Trigger)??throw new IOException("键盘状态读取失败。");
        // Do not replay a command already in flight. Firmware consumes bit 0.
        if((trigger & Vault.WinLockMask)!=0)throw new IOException("键盘正在处理上一条指令，请稍后重试。");
        audit(new{at=DateTimeOffset.Now,operation="win-key-intent",before,locked});
        try
        {
            Guard();
            nextWinCommandAt=Environment.TickCount64+WinCommandIntervalMs;
            if(!ec.Write(Vault.Trigger,(byte)(trigger|Vault.WinLockMask)))throw new IOException("键盘指令发送失败。");
            // This EC may take over a second to acknowledge consecutive commands.
            // Wait for a stable status and consumed command; never resend on timeout.
            var wait=System.Diagnostics.Stopwatch.StartNew();long matchedAt=-1;
            while(wait.ElapsedMilliseconds<5000)
            {
                var after=ReadSwitches(ec);
                byte pending=ec.Read(Vault.Trigger)??throw new IOException("键盘指令状态读取失败。");
                if(after.WinKeyLocked==locked && (pending&Vault.WinLockMask)==0)
                {
                    if(matchedAt<0)matchedAt=wait.ElapsedMilliseconds;
                    if(wait.ElapsedMilliseconds-matchedAt>=150){audit(new{at=DateTimeOffset.Now,operation="win-key-confirmed",after});return after;}
                }
                else matchedAt=-1;
                Thread.Sleep(50);
            }
            throw new IOException("Win 键状态未确认改变。");
        }
        catch { faulted=true;throw; }
    });
    public DeviceSwitchState SetCameraPowered(bool enabled) => Serialized(() =>
    {
        if(!Active)throw new InvalidOperationException("请先连接硬件。");
        Guard();using var ec=open();
        if(!MatchesProject(Read(ec).Project))throw new IOException("设备不匹配。");
        byte before=ec.Read(Vault.CameraCutoff)??throw new IOException("相机供电读取失败。");
        byte target=enabled?(byte)(before&~Vault.CameraCutoffMask):(byte)(before|Vault.CameraCutoffMask);
        if(target==before)return ReadSwitches(ec);
        audit(new{at=DateTimeOffset.Now,operation="camera-power-intent",before,target});
        try
        {
            Guard();if(!ec.Write(Vault.CameraCutoff,target))throw new IOException("相机供电设置失败。");
            if(ec.Read(Vault.CameraCutoff)!=target)throw new IOException("相机供电回读不一致。");
            var state=ReadSwitches(ec);audit(new{at=DateTimeOffset.Now,operation="camera-power-confirmed",state});return state;
        }
        catch { faulted=true;throw; }
    });
    public DeviceSwitchState SetCameraPoweredViaCore(ICameraPowerCore core,bool enabled) => Serialized(() =>
    {
        if(!Active)throw new InvalidOperationException("请先连接硬件。");
        Guard();using var ec=open();
        if(!MatchesProject(Read(ec).Project))throw new IOException("设备不匹配。");
        byte before=ec.Read(Vault.CameraCutoff)??throw new IOException("相机供电读取失败。");
        byte target=enabled?(byte)(before&~Vault.CameraCutoffMask):(byte)(before|Vault.CameraCutoffMask);
        if(target==before)return ReadSwitches(ec);
        audit(new{at=DateTimeOffset.Now,operation="core-camera-power-intent",before,target});
        try
        {
            Guard();core.SetCameraPowered(enabled);
            if(ec.Read(Vault.CameraCutoff)!=target)throw new IOException("相机供电回读不一致。");
            var state=ReadSwitches(ec);audit(new{at=DateTimeOffset.Now,operation="core-camera-power-confirmed",state});return state;
        }
        catch{faulted=true;throw;}
    });
}
