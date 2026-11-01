using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
using System.Text.Json.Nodes;
internal static class TuningPresetChecks
{
    internal static void Run(string bridge,string fixture,string folder)
    {
        var results=new List<object>();
        void Check(string name,bool ok){results.Add(new{name,passed=ok});if(!ok)throw new Exception(name);}
        void Reject(string name,Action action){bool rejected=false;try{action();}catch(Exception error)when(error is ArgumentException or InvalidOperationException or JsonException or IOException){rejected=true;}Check(name,rejected);}
        // Recorded fixture only: no EC reads or setters, and a task-owned store.
        var state=JsonSerializer.Deserialize<TuningSnapshot>(File.ReadAllText(fixture))!;
        using var core=new UpstreamCoreLibrary(Path.GetFullPath(bridge));var originals=core.Read(18);
        var builtins=originals.EnumerateArray().Select(p=>TuningPresetStore.FromOriginal(p,state,true)).ToArray();
        Check("Four Core builtin profiles with canonical IDs",builtins.Select(p=>p.Id).SequenceEqual(TuningPresetStore.BuiltinIds));
        Check("Silent and quiet fan endpoints adapted and documented",builtins.Where(p=>p.Id is "preset_silent_beast" or "preset_quiet").All(p=>p.Profile.CpuCurve[^1].Percent==100&&p.Profile.GpuCurve[^1].Percent==100&&p.Adjustments.Any(n=>n.Contains("末点"))));
        Check("CPU factory values use recorded machine registers",builtins[1].Profile.Pl1==state.Registers.BalancePl1&&builtins[2].Profile.Pl1==state.Registers.OfficePl1&&builtins[3].Profile.Pl2==state.Registers.TurboPl2);
        Check("Fixed GPU power uses the machine limit with a visible note",builtins.All(p=>p.Profile.GpuBaseWatts==state.Gpu.Capabilities.BaseMaxWatts&&p.Adjustments.Any(n=>n.Contains("固定"))));
        var store=new TuningPresetStore(Path.Combine(folder,"store-"+Guid.NewGuid().ToString("N")));
        var custom=store.Duplicate(builtins[0],"复制方案",state);Check("Duplicate owns independent fan arrays",!ReferenceEquals(custom.Profile.CpuCurve,builtins[0].Profile.CpuCurve));
        Check("Custom save/load roundtrip",store.Load(builtins,state).Any(p=>p.Id==custom.Id&&p.Name==custom.Name));
        var exported=TuningPresetStore.Export(custom);var imported=store.Import(exported,state);
        Check("Export/import preserves every native parameter",JsonSerializer.Serialize(imported.Profile)==JsonSerializer.Serialize(custom.Profile));
        var renamed=custom with{Name="重命名"};store.Save(renamed,state);Check("Rename persists",store.Load(builtins,state).Single(p=>p.Id==custom.Id).Name=="重命名");
        store.Delete(renamed);Check("Delete removes selection and retains a trash copy",!store.Load(builtins,state).Any(p=>p.Id==custom.Id)&&Directory.EnumerateFiles(Path.Combine(store.DirectoryPath,"trash")).Count()==1);
        Reject("Builtin delete rejected",()=>store.Delete(builtins[0]));
        Reject("Path traversal ID rejected",()=>store.Save(custom with{Id="../escaped"},state));
        Reject("Overlong names rejected",()=>store.Duplicate(custom,new string('长',13),state));
        Reject("Empty JSON rejected",()=>store.Import("{}",state));
        Reject("Null profile rejected",()=>store.Import("{\"name\":\"坏\",\"native_profile\":null}",state));
        var bad=JsonNode.Parse(exported)!.AsObject();bad["native_profile"]!["CpuCurve"]=null;
        Reject("Null fan array rejected",()=>store.Import(bad.ToJsonString(),state));
        bad=JsonNode.Parse(exported)!.AsObject();bad["native_profile"]!["CpuCurve"]![7]!["Percent"]=90;
        Reject("Unsafe native fan endpoint rejected",()=>store.Import(bad.ToJsonString(),state));
        bad=JsonNode.Parse(exported)!.AsObject();bad["native_profile"]!["Pl1"]=9999;
        Reject("Out of range native power rejected",()=>store.Import(bad.ToJsonString(),state));
        File.WriteAllText(Path.Combine(store.DirectoryPath,"user_"+new string('a',32)+".json"),"{\"Id\":\"user_"+new string('a',32)+"\",\"Name\":null,\"Profile\":null}");
        Check("Malformed local record skipped",store.Load(builtins,state).Length==5);
        var malformed=JsonNode.Parse(originals[0].GetRawText())!.AsObject();malformed["profile"]!["cpu_pl1_watts"]="invalid";
        Reject("Malformed original numeric type rejected",()=>store.Import(malformed.ToJsonString(),state));
        var foreign=store.Import(originals[0].GetRawText(),state);Check("Original JSON import is adapted and retains notes",!foreign.BuiltIn&&foreign.Adjustments.Any(n=>n.Contains("末点")));
        File.WriteAllText(Path.Combine(folder,"preset-checks.json"),JsonSerializer.Serialize(new{allPassed=true,hardwareSettersInvoked=false,source="recorded snapshot + Core readonly builtin query 18",tests=results,presets=builtins},new JsonSerializerOptions{WriteIndented=true}));
    }
}
