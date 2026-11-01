using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
internal static class SettingsCoreChecks
{
    internal static void Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);var results=new List<object>();bool passed=true;
        void Check(string name,bool ok){results.Add(new{name,passed=ok});passed&=ok;}
        bool Rejected(Action action){try{action();return false;}catch(Exception e)when(e is ArgumentException or IOException or InvalidOperationException){return true;}}
        try
        {
            string root=Path.Combine(folder,"files-"+Guid.NewGuid().ToString("N"));UserDataStore.EnsureMarker(root);
            AppData.Save(new(2,2),root);AppData.Update(p=>p with{SampleSeconds=10},root);
            Check("Background preferences preserve chosen dark theme",AppData.Load(root).Theme==2&&AppData.Load(root).SampleSeconds==10);
            var options=new SystemPreferences(WakeMode:"full",MiniAutoHide:false,UnlockBatteryLimit:true,OsdTheme:"pill",OsdScale:140,OsdPosition:"bottom-center");options.Save(root);
            Check("System and OSD preferences survive reload",SystemPreferences.Load(root)==options);
            Check("Battery performance unlock defaults off",!new SystemPreferences().UnlockBatteryLimit);
            string target=root+"-migrated",pointer=Path.Combine(folder,Guid.NewGuid().ToString("N")+".location.json");
            UserDataStore.Migrate(root,target,pointer);
            Check("Migration preserves original and copies data",File.ReadAllText(Path.Combine(root,"preferences.json"))==File.ReadAllText(Path.Combine(target,"preferences.json")));
            Check("Migration commits pointer only after copy",JsonSerializer.Deserialize<string>(File.ReadAllText(pointer))==Path.GetFullPath(target));
            Check("Nonempty destination rejected without overwrite",Rejected(()=>UserDataStore.Migrate(root,target,pointer)));
            Check("Nested migration rejected",Rejected(()=>UserDataStore.Migrate(root,Path.Combine(root,"nested"),pointer)));
            Check("Drive root rejected",Rejected(()=>UserDataStore.ValidateDirectory(Path.GetPathRoot(root)!)));
            string backup=UserDataStore.Reset(target);
            Check("Reset retains a complete backup and a fresh marked directory",File.Exists(Path.Combine(backup,"preferences.json"))&&!File.Exists(Path.Combine(target,"preferences.json"))&&File.Exists(Path.Combine(target,"openrevo-native-data.json")));
            string unowned=root+"-unowned";Directory.CreateDirectory(unowned);
            Check("Unowned directory reset rejected",Rejected(()=>UserDataStore.Reset(unowned)));
            var ec=new FakeControl();ec.SeedPerformance();var session=new HardwareSession(()=>ec,()=>null,_=>{},()=>false);session.Takeover();
            Check("Default DC guard blocks battery turbo without writes",Rejected(()=>session.SetPerformanceMode(3,true))&&ec.Writes==0);
            session.SetBatteryPerformancePermission(true);session.SetPerformanceMode(3);
            Check("Explicit unlock permits battery turbo through the normal transaction",session.ProbePerformance().Mode==3&&ec.Writes>0);
            session.SetBatteryPerformancePermission(false);session.SetPerformanceMode(2);int writes=ec.Writes;
            Check("Disabling unlock restores original DC gate",Rejected(()=>session.SetPerformanceMode(3,true))&&ec.Writes==writes);
            var tuningEc=new FakeControl();tuningEc.SeedTuning();var gpu=new FakeTuningGpu();var tuning=new HardwareSession(()=>tuningEc,()=>null,_=>{},()=>false,()=>"Intel i7-14650HX");tuning.Takeover();var baseline=tuning.ProbeTuning(gpu);
            Check("Custom tuning is DC protected by default",Rejected(()=>tuning.ApplyTuning(gpu,baseline,baseline.CreateDraft()))&&tuningEc.Writes==0);
            tuning.SetBatteryPerformancePermission(true);tuning.ApplyTuning(gpu,baseline,baseline.CreateDraft());
            Check("Explicit unlock permits custom transaction without relaxing numeric limits",tuning.ProbePerformance().Mode==4);
            var after=tuning.ProbeTuning(gpu);var invalid=after.CreateDraft();var curve=invalid.CpuCurve.ToArray();curve[^1]=curve[^1] with{Percent=90};writes=tuningEc.Writes;
            Check("Battery unlock retains 100 percent fan endpoint protection",Rejected(()=>tuning.ApplyTuning(gpu,after,invalid with{CpuCurve=curve}))&&tuningEc.Writes==writes);
        }
        catch(Exception error){passed=false;results.Add(new{error=error.ToString(),passed=false});}
        File.WriteAllText(Path.Combine(folder,"settings-core-checks.json"),JsonSerializer.Serialize(new{allPassed=passed,realHardwareSettersInvoked=false,tests=results},new JsonSerializerOptions{WriteIndented=true}));
        if(!passed)throw new Exception("Settings core checks failed.");
    }
}
