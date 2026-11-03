using System.Text.Json;
using System.Text.RegularExpressions;
using OpenRevo.Core.Hal;
namespace OpenRevo.Core;

public sealed record TuningPreset(string Id,string Name,string Icon,bool BuiltIn,TuningProfile Profile,string[] Adjustments)
{public override string ToString()=>Name;}
public sealed class TuningPresetStore
{
    public static readonly string[] BuiltinIds=["preset_silent_beast","preset_balance","preset_quiet","preset_extreme"];
    private static readonly JsonSerializerOptions json=new(){PropertyNameCaseInsensitive=true,WriteIndented=true};
    public string DirectoryPath{get;}
    public TuningPresetStore(string? directory=null)=>DirectoryPath=Path.GetFullPath(directory??Path.Combine(AppData.DirectoryPath,"profiles"));
    private string FileFor(string id)
    {
        if(!BuiltinIds.Contains(id)&&!Regex.IsMatch(id,@"\Auser_[a-f0-9]{32}\z"))throw new ArgumentException("方案标识无效。");
        return Path.Combine(DirectoryPath,id+".json");
    }
    private static void Shape(TuningProfile? profile)
    {
        if(profile==null||profile.CpuCurve is not{Length:8}||profile.GpuCurve is not{Length:8}||profile.CpuCurve.Any(p=>p==null)||profile.GpuCurve.Any(p=>p==null))throw new ArgumentException("每条风扇曲线需要8个有效节点。");
    }
    public static string ValidName(string? name)
    {
        name=name?.Trim()??"";if(name.Length is <1 or >12||name.Any(char.IsControl))throw new ArgumentException("方案名需为1–12个字符。");return name;
    }
    public void Save(TuningPreset preset,TuningSnapshot state)
    {
        Shape(preset.Profile);HardwareSession.ValidateTuning(state,preset.Profile);ValidName(preset.Name);
        string file=FileFor(preset.Id);Directory.CreateDirectory(DirectoryPath);string temporary=Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+".tmp");
        File.WriteAllText(temporary,JsonSerializer.Serialize(preset,json));File.Move(temporary,file,true);
    }
    public TuningPreset[] Load(TuningPreset[] builtins,TuningSnapshot state)
    {
        var result=builtins.ToDictionary(p=>p.Id);if(!Directory.Exists(DirectoryPath))return builtins;
        foreach(string file in Directory.EnumerateFiles(DirectoryPath,"*.json").Order().Take(100))
        {
            try
            {
                if(new FileInfo(file).Length>1024*1024)continue;
                var p=JsonSerializer.Deserialize<TuningPreset>(File.ReadAllText(file),json);if(p==null)continue;
                if(Path.GetFileName(file)!=Path.GetFileName(FileFor(p.Id)))continue;
                Shape(p.Profile);HardwareSession.ValidateTuning(state,p.Profile);ValidName(p.Name);
                bool builtin=BuiltinIds.Contains(p.Id);if(builtin)p=p with{Name=builtins.Single(b=>b.Id==p.Id).Name,Icon=builtins.Single(b=>b.Id==p.Id).Icon,BuiltIn=true};else p=p with{BuiltIn=false};
                result[p.Id]=p with{Profile=p.Profile.Copy(),Adjustments=p.Adjustments??[]};
            }
            catch(Exception error) when(error is IOException or JsonException or ArgumentException or UnauthorizedAccessException){}
        }
        return result.Values.ToArray();
    }
    public TuningPreset LoadForApply(string id,TuningPreset[] builtins,TuningSnapshot state)
    {
        string file=FileFor(id);TuningPreset preset;
        if(File.Exists(file))
        {
            if(new FileInfo(file).Length>1024*1024)throw new IOException("方案文件过大。");
            preset=JsonSerializer.Deserialize<TuningPreset>(File.ReadAllText(file),json)??throw new IOException("方案内容为空。");
            if(preset.Id!=id)throw new IOException("方案标识不一致。");
        }
        else preset=builtins.FirstOrDefault(p=>p.Id==id)??throw new IOException("已保存方案不存在。");
        Shape(preset.Profile);HardwareSession.ValidateTuning(state,preset.Profile);ValidName(preset.Name);
        return preset with{Profile=preset.Profile.Copy()};
    }
    public TuningPreset Duplicate(TuningPreset source,string name,TuningSnapshot state)
    {var p=source with{Id="user_"+Guid.NewGuid().ToString("N"),Name=ValidName(name),BuiltIn=false,Profile=source.Profile.Copy()};Save(p,state);return p;}
    public void Delete(TuningPreset preset)
    {
        if(preset.BuiltIn||BuiltinIds.Contains(preset.Id))throw new InvalidOperationException("内置方案不可删除。");
        string file=FileFor(preset.Id);if(!File.Exists(file))return;
        string trash=Path.Combine(DirectoryPath,"trash");Directory.CreateDirectory(trash);File.Move(file,Path.Combine(trash,preset.Id+"-"+Guid.NewGuid().ToString("N")+".json"));
    }
    public static TuningPreset FromOriginal(JsonElement source,TuningSnapshot state,bool builtin)
    {
        if(source.ValueKind!=JsonValueKind.Object)throw new ArgumentException("方案必须为JSON对象。");
        var data=source.TryGetProperty("profile",out var body)?body:source;
        if(data.ValueKind!=JsonValueKind.Object)throw new ArgumentException("方案参数必须为JSON对象。");
        foreach(string field in new[]{"cpu_pl1_watts","cpu_pl2_watts","cpu_pl4_watts","cpu_fan_curve","gpu_fan_curve"})if(!data.TryGetProperty(field,out _))throw new ArgumentException("方案缺少参数："+field);
        int Int(string key,int fallback)
        {if(!data.TryGetProperty(key,out var value))return fallback;if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt32(out int n))throw new ArgumentException("方案参数必须为整数："+key);return n;}
        bool Bool(string key,bool fallback)
        {if(!data.TryGetProperty(key,out var value))return fallback;if(value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("方案开关类型无效："+key);return value.GetBoolean();}
        FanPoint[] Curve(string key)=>data.TryGetProperty(key,out var array)&&array.ValueKind==JsonValueKind.Array?array.EnumerateArray().Select(p=>new FanPoint(p.GetProperty("temp").GetInt32(),p.GetProperty("duty").GetInt32())).ToArray():TuningProfile.DefaultCurve();
        string id=builtin?source.GetProperty("id").GetString()!:"user_"+Guid.NewGuid().ToString("N");
        string name=source.TryGetProperty("name",out var title)?title.GetString()??"导入方案":"导入方案";
        string icon=source.TryGetProperty("icon_key",out var key)?key.GetString()??"star":"star";
        var r=state.Registers;var c=state.Gpu.Capabilities;var notes=new List<string>();
        var defaultDraft=state.CreateDraft();
        if(c.Supported&&!c.ConfigurableBase)notes.Add("本机GPU基础功耗固定为"+c.BaseMaxWatts+"W");
        int pl1=Int("cpu_pl1_watts",defaultDraft.Pl1),pl2=Int("cpu_pl2_watts",defaultDraft.Pl2),pl4=Int("cpu_pl4_watts",defaultDraft.Pl4);
        if(builtin)
        {
            (pl1,pl2,pl4)=id switch{
                "preset_balance"=>(r.BalancePl1,r.BalancePl2,r.BalancePl4),"preset_quiet"=>(r.OfficePl1,r.OfficePl2,r.OfficePl4),"preset_extreme"=>(r.TurboPl1,r.TurboPl2,r.TurboPl4),
                _=>(r.TurboPl1>r.BalancePl1?r.BalancePl1+(int)Math.Round((r.TurboPl1-r.BalancePl1)*.5):(int)Math.Round(r.TurboPl1*.85),r.TurboPl1,Math.Min((int)Math.Round(r.TurboPl1*1.25),r.TurboPl4))};
            notes.Add("CPU参数按本机原厂功耗重新计算");
        }
        int Limit(int value,int min,int max,string label){int n=Math.Clamp(value,min,max);if(n!=value)notes.Add($"{label}：{value} → {n}");return n;}
        pl2=Limit(pl2,15,Math.Min(state.Pl2Max,state.Pl4Max-20),"PL2");pl1=Limit(pl1,15,Math.Min(state.Pl1Max,pl2),"PL1");pl4=Limit(pl4,pl2+20,state.Pl4Max,"PL4");
        FanPoint[] Adapt(FanPoint[] curve,string label)
        {
            if(curve.Length!=8)throw new ArgumentException("原版方案每条曲线必须有8个节点。");
            if(curve[^1].Percent!=100||curve[^1].Celsius>95){notes.Add(label+"末点调整为95°C前100%");curve[^1]=new(Math.Min(95,curve[^1].Celsius),100);}return curve;
        }
        int baseWatts=c.Supported?c.ConfigurableBase?Limit(Int("gpu_ctgp_watts",c.BaseMaxWatts),c.FloorWatts,c.BaseMaxWatts,"GPU基础功耗"):c.BaseMaxWatts:defaultDraft.GpuBaseWatts;
        if(builtin&&c.Supported)baseWatts=c.ConfigurableBase&&id=="preset_quiet"?Math.Clamp((int)Math.Round(c.BaseMaxWatts*.75),c.FloorWatts,c.BaseMaxWatts):c.BaseMaxWatts;
        int db=c.Supported?Limit(builtin?(id=="preset_quiet"?Math.Min(10,c.BoostMaxWatts):c.BoostMaxWatts):Int("gpu_db_watts",c.BoostMaxWatts),5,c.BoostMaxWatts,"Dynamic Boost"):defaultDraft.GpuBoostWatts;
        bool super=Bool("gpu_super_perf",true);
        var offsets=c.Supported&&state.Gpu.Offsets!=null?new ClockOffsets(Limit(Int("gpu_core_offset_mhz",0),0,c.CoreOffsetMax,"GPU核心频率"),Limit(Int("gpu_mem_offset_mhz",0),0,c.MemoryOffsetMax,"显存频率")):state.Gpu.Offsets;
        if(!super&&offsets!=null&&(offsets.CoreMhz!=0||offsets.MemoryMhz!=0)){notes.Add("超级性能关闭，频率偏移归零");offsets=new(0,0);}
        if(Bool("bios_oc_enabled",false))notes.Add("BIOS解锁未接入，本次不修改BIOS开关");
        var profile=new TuningProfile(pl1,pl2,pl4,Bool("cpu_temp_target_enabled",true),Limit(Int("cpu_temp_target",95),80,95,"温度墙"),super,Bool("gpu_db_enabled",true),db,baseWatts,offsets,Bool("fan_curve_enabled",false),Adapt(Curve("cpu_fan_curve"),"CPU风扇"),Adapt(Curve("gpu_fan_curve"),"GPU风扇"),Bool("fan_ramp_rate",true),Limit((int)Math.Round(Int("fan_ramp_speed_ms",300)/100d)*100,100,12700,"风扇响应"));
        Shape(profile);HardwareSession.ValidateTuning(state,profile);return new(id,ValidName(name),icon,builtin,profile,notes.ToArray());
    }
    public TuningPreset Import(string content,TuningSnapshot state)
    {
        if(content.Length>1024*1024)throw new ArgumentException("方案文件不能超过1MiB。");
        using var doc=JsonDocument.Parse(content);var root=doc.RootElement;
        TuningPreset p;
        if(root.TryGetProperty("native_profile",out var native))
        {
            var profile=native.Deserialize<TuningProfile>(json)??throw new ArgumentException("方案参数为空。");Shape(profile);HardwareSession.ValidateTuning(state,profile);
            p=new("user_"+Guid.NewGuid().ToString("N"),ValidName(root.GetProperty("name").GetString()??"导入方案"),"star",false,profile,[]);
        }
        else if(root.TryGetProperty("Pl1",out _))
        {
            var profile=root.Deserialize<TuningProfile>(json)!;Shape(profile);HardwareSession.ValidateTuning(state,profile);p=new("user_"+Guid.NewGuid().ToString("N"),"本机草稿","star",false,profile,[]);
        }
        else p=FromOriginal(root,state,false);
        Save(p,state);return p;
    }
    public static string Export(TuningPreset p)=>JsonSerializer.Serialize(new{name=p.Name,id=p.Id,icon_key=p.Icon,is_builtin=false,native_profile=p.Profile,profile=new{
        cpu_pl1_watts=p.Profile.Pl1,cpu_pl2_watts=p.Profile.Pl2,cpu_pl4_watts=p.Profile.Pl4,cpu_temp_target_enabled=p.Profile.TemperatureEnabled,cpu_temp_target=p.Profile.TemperatureC,bios_oc_enabled=false,
        gpu_super_perf=p.Profile.GpuSuperPerformance,gpu_ctgp_enabled=true,gpu_ctgp_watts=p.Profile.GpuBaseWatts,gpu_db_enabled=p.Profile.GpuBoostEnabled,gpu_db_watts=p.Profile.GpuBoostWatts,gpu_core_offset_mhz=p.Profile.Offsets?.CoreMhz??0,gpu_mem_offset_mhz=p.Profile.Offsets?.MemoryMhz??0,
        fan_curve_enabled=p.Profile.CustomFans,fan_ramp_rate=p.Profile.SmoothFans,fan_ramp_speed_ms=p.Profile.FanStepMs,fan_isolated_output=true,fan_curve_linked=false,cpu_fan_curve=p.Profile.CpuCurve.Select(x=>new{temp=x.Celsius,duty=x.Percent}),gpu_fan_curve=p.Profile.GpuCurve.Select(x=>new{temp=x.Celsius,duty=x.Percent})}},json);
}
