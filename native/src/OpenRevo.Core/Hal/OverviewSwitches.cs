namespace OpenRevo.Core.Hal;
public sealed partial class HardwareSession
{
    public OverviewSwitches SetOverviewSwitchViaCore(IOverviewSwitchCore core,OverviewSwitch key,bool beforeEnabled,bool target)=>Serialized(()=>
    {
        if(key is not(OverviewSwitch.Touchpad or OverviewSwitch.FnLock))throw new ArgumentOutOfRangeException(nameof(key));
        if(!Active)throw new InvalidOperationException("请先启用硬件接管。");
        Guard();using var ec=open();var before=Read(ec);
        if(!MatchesProject(before.Project))throw new IOException("设备不匹配。");
        ushort address=key==OverviewSwitch.Touchpad?Vault.BatteryMode:Vault.FnLock;
        byte mask=key==OverviewSwitch.Touchpad?Vault.TouchpadMask:Vault.FnLockMask;
        byte raw=ec.Read(address)??throw new IOException("硬件状态未能读取。");
        if(key==OverviewSwitch.FnLock&&((raw&mask)!=0)!=beforeEnabled)throw new IOException("Fn状态已经变化，请重读。");
        if(core.ReadOverviewSwitches().Get(key)!=beforeEnabled)throw new IOException("设备状态已经变化，请重读。");
        byte desired=target?(byte)(raw|mask):(byte)(raw&~mask);
        audit(new{at=DateTimeOffset.Now,operation="overview-switch-intent",key,beforeEnabled,target});
        try
        {
            Guard();core.SetOverviewSwitch(key,beforeEnabled,target);
            var confirmed=core.ReadOverviewSwitches();var after=Read(ec);
            var expectedControl=key==OverviewSwitch.Touchpad?before with{BatteryMode=desired}:before;
            if(confirmed.Get(key)!=target||ec.Read(address)!=desired||after!=expectedControl)throw new IOException("硬件回读不一致，后续写入已停止。");
            expected=after;
            audit(new{at=DateTimeOffset.Now,operation="overview-switch-confirmed",key,target});return confirmed;
        }
        catch{faulted=true;throw;}
    });
}
