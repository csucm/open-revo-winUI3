using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
internal static class OverviewPowerChecks
{
    internal static void Run(string path)
    {
        var checks=new List<object>();bool passed=true;
        void Check(string name,bool ok){checks.Add(new{name,passed=ok});passed&=ok;}
        var caps=GpuTuningCapabilities.FromLimits("NVIDIA GeForce RTX 4070 Laptop GPU",60,140);
        OverviewPowerTargets Make(int mode,GpuTuningCapabilities? gpu=null,int? db=10,int? control=2,int? offset=20)=>OverviewPowerTargets.Calculate(mode,45,75,90,82,gpu??caps,offset,control,db);
        Check("Office factory target uses its EC PL1 and base TGP",Make(1) is{Pl1Watts:45,TgpWatts:115});
        Check("Balance uses its EC PL1 and physical maximum DB",Make(2) is{Pl1Watts:75,TgpWatts:140});
        Check("Turbo uses its EC PL1 and physical maximum DB",Make(3) is{Pl1Watts:90,TgpWatts:140});
        Check("Changing mutable DB slider cannot falsify factory TGP",Make(3,db:5).TgpWatts==140);
        Check("Custom shows current EC PL1 and offset plus enabled DB",Make(4) is{Pl1Watts:82,TgpWatts:90,Label:"当前设定"});
        Check("Custom disabled DB adds zero instead of its stale slider",Make(4,control:0).TgpWatts==80);
        Check("Unknown DB enable flag leaves custom TGP unknown",Make(4,control:null).TgpWatts==null);
        Check("Out of range custom offset is unknown",Make(4,offset:100).TgpWatts==null);
        Check("Out of range custom DB is unknown",Make(4,db:40).TgpWatts==null);
        Check("Missing GPU does not erase a valid CPU PL1",Make(2,GpuTuningCapabilities.Unknown) is{Pl1Watts:75,TgpWatts:null});
        Check("Zero and all-ones CPU bytes are not shown as factory watts",OverviewPowerTargets.Calculate(1,0,75,90,82,caps,20,2,10).Pl1Watts==null&&OverviewPowerTargets.Calculate(1,255,75,90,82,caps,20,2,10).Pl1Watts==null);
        var ec=new FakeControl();ec.SeedTuning();var session=new HardwareSession(()=>ec,()=>null,_=>{});
        var actual=session.ProbeOverviewPowerTargets();
        Check("Independent target read works before opening or probing the editor",actual.Pl1Watts==75&&session.TuningState==null);
        Check("Target probe does not perform a hardware write or acquire control",ec.Writes==0&&!session.Active);
        ec.Values.Remove(Vault.BalancePl1);Check("Partial EC failure remains unknown",session.ProbeOverviewPowerTargets().Pl1Watts==null);
        ec.Values[Vault.ProjectId]=17;bool rejected=false;try{session.ProbeOverviewPowerTargets();}catch(IOException){rejected=true;}
        Check("Unmatched EC identity is rejected without writes",rejected&&ec.Writes==0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path,JsonSerializer.Serialize(new{allPassed=passed,hardwareSettersInvoked=false,checks},new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=passed?0:1;
    }
}
