namespace OpenRevo.Core.Hal;

public sealed record OverviewPowerTargets(int Mode,int? Pl1Watts,int? TgpWatts,int? BaseTgpWatts,int? MaximumBoostWatts,string Detail,DateTimeOffset At)
{
    public string Label=>Mode==4?"当前设定":"原厂目标";
    public string Text=>$"{Label} PL1: {Pl1Watts?.ToString()??"—"} W | TGP: {TgpWatts?.ToString()??"—"} W";
    // Factory targets use the physical envelope, never the mutable DB slider.
    public static OverviewPowerTargets Calculate(int mode,int? office,int? balance,int? turbo,int? custom,
        GpuTuningCapabilities gpu,int? offset,int? dbControl,int? dbWatts,string detail="")
    {
        int? Valid(int? value)=>value is >0 and <255?value:null;
        int? pl1=mode switch{1=>Valid(office),2=>Valid(balance),3=>Valid(turbo),4=>Valid(custom),_=>null};
        int? basis=gpu.Supported&&gpu.BaseMaxWatts>0?gpu.BaseMaxWatts:null;
        int? boost=gpu.Supported&&gpu.BoostMaxWatts>0?gpu.BoostMaxWatts:null;
        int? target=null;
        if(mode is >=1 and <=3&&basis.HasValue)target=mode==1?basis:boost.HasValue?basis+boost:null;
        if(mode==4&&basis.HasValue)
        {
            int? currentBase=gpu.ConfigurableBase?offset.HasValue&&gpu.FloorWatts+offset.Value<=basis?gpu.FloorWatts+offset.Value:null:basis;
            if(currentBase.HasValue&&dbControl.HasValue)
            {
                if((dbControl.Value&2)==0)target=currentBase;
                else if(dbWatts is >=0&&boost.HasValue&&dbWatts<=boost)target=currentBase+dbWatts;
            }
        }
        return new(mode,pl1,target,basis,boost,detail,DateTimeOffset.Now);
    }
}

public sealed partial class HardwareSession
{
    public OverviewPowerTargets ProbeOverviewPowerTargets()=>Serialized(()=>
    {
        Guard();using var ec=open();
        if(ec.Read(Vault.ProjectId) is not byte project||!MatchesProject(project))throw new IOException("EC项目不匹配，未读取原厂目标。");
        byte? custom=ec.Read(Vault.CustomMode),clamp=ec.Read(Vault.FanClamp),index=ec.Read(Vault.ModeIndex);
        int mode=custom is 64 or 65&&clamp.HasValue&&index.HasValue?(custom.Value&1)!=0?4:(clamp.Value&Vault.ModeClampMask)==160?1:(index.Value&Vault.ModeIndexMask)==16?3:2:0;
        var gpu=GpuTuningCapabilities.Probe();
        var result=OverviewPowerTargets.Calculate(mode,ec.Read(Vault.OfficePl1),ec.Read(Vault.BalancePl1),ec.Read(Vault.TurboPl1),ec.Read(Vault.CustomPl1),gpu,
            ec.Read(Vault.GpuTgpOffset),ec.Read(Vault.GpuDbControl),ec.Read(Vault.GpuDbWatts),
            "PL1：本机EC原厂寄存器；TGP：按固定Core算法从NVML默认/最高功耗边界计算。自定义值来自当前EC，不使用编辑草稿。");
        if(custom!=ec.Read(Vault.CustomMode)||clamp!=ec.Read(Vault.FanClamp)||index!=ec.Read(Vault.ModeIndex))throw new IOException("读取期间性能模式改变，等待重新读取。");
        return result with{Detail=result.Detail+(!result.Pl1Watts.HasValue?" CPU目标尚未读出。":"")+(!result.TgpWatts.HasValue?" GPU功耗边界或当前设定尚未读出。":"")};
    });
}
