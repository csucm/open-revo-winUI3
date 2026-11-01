using OpenRevo.Core.Hal;
using System.Text.Json;
internal static class EditorFixChecks
{
    public static void Run(string path)
    {
        var checks=new List<object>();
        void Check(string name,Func<bool> run){try{checks.Add(new{name,passed=run()});}catch(Exception e){checks.Add(new{name,passed=false,error=e.Message});}}
        var original=TuningProfile.DefaultCurve();
        Check("Raising a node pushes later duties up",()=>{var next=FanCurveEditing.WithDuty(original,2,90);return next.Skip(2).Take(5).All(p=>p.Percent>=90)&&next[^1].Percent==100;});
        Check("Lowering a node pulls earlier duties down",()=>FanCurveEditing.WithDuty(original,4,10).Take(5).All(p=>p.Percent<=10));
        Check("Temperature anchors stay unchanged",()=>FanCurveEditing.WithDuty(original,1,60).Select(p=>p.Celsius).SequenceEqual(original.Select(p=>p.Celsius)));
        Check("Editing cannot mutate the original draft",()=>{var saved=original.ToArray();FanCurveEditing.WithDuty(original,3,90);return original.SequenceEqual(saved);});
        Check("Final safety point stays at 100 percent",()=>FanCurveEditing.WithDuty(original,7,0)[7].Percent==100);
        Check("Large positive input clamps to 100 percent",()=>FanCurveEditing.WithDuty(original,1,int.MaxValue).Skip(1).All(p=>p.Percent==100));
        Check("Large negative input clamps to zero",()=>FanCurveEditing.WithDuty(original,5,int.MinValue).Take(6).All(p=>p.Percent==0));
        Check("Repeated dragging preserves monotonicity",()=>{var next=original;for(int n=0;n<300;n++)next=FanCurveEditing.WithDuty(next,n%7,(n*37)%101);return next.Zip(next.Skip(1)).All(pair=>pair.First.Percent<=pair.Second.Percent)&&next[^1].Percent==100;});
        Check("Wrong node count is rejected",()=>{try{FanCurveEditing.WithDuty(original[..7],1,80);return false;}catch(ArgumentException){return true;}});
        Check("Invalid node index is rejected",()=>{try{FanCurveEditing.WithDuty(original,8,80);return false;}catch(ArgumentOutOfRangeException){return true;}});
        bool passed=checks.All(c=>JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean());
        File.WriteAllText(path,JsonSerializer.Serialize(new{allPassed=passed,count=checks.Count,hardwareWrites=0,checks},new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=passed?0:1;
    }
}
