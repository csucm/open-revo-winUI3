using OpenRevo.Native;
using System.Text.Json;
internal static class ResponsiveLayoutChecks
{
    internal static void Run(string output)
    {
        var checks=new List<object>();
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        void Check(string name,Action test){try{test();checks.Add(new{name,passed=true});}catch(Exception error){checks.Add(new{name,passed=false,error=error.Message});}}
        Check("Two 312 DIP fields share a 640 DIP group",()=>Assert(DesktopGridGeometry.Columns(640,2,312,16)==2,"Unexpected single column"));
        Check("Fields reflow below their combined minimum width",()=>Assert(DesktopGridGeometry.Columns(639,2,312,16)==1,"Narrow fields squeezed"));
        Check("Partial row preceding wide controls uses full width",()=>
        {
            var rows=DesktopGridGeometry.Rows(720,2,16,[false,true,false,false,false]);
            Assert(rows.Count==4&&rows[0]==new DesktopGridRow(0,1,720)&&rows[1]==new DesktopGridRow(1,1,720),"Empty cell beside wide controls");
            Assert(rows[^1]==new DesktopGridRow(4,1,720),"Trailing cell leaves a gap");
        });
        Check("Consecutive full rows do not swallow neighboring fields",()=>
        {
            var rows=DesktopGridGeometry.Rows(960,2,16,[false,false,true,true,false,false]);
            Assert(rows.Select(r=>r.Count).SequenceEqual([2,1,1,2]),"Order or row span changed");
        });
        Check("Overview retains paired four-to-two reflow",()=>Assert(DesktopGridGeometry.Columns(700,4,220,16,true)==2&&DesktopGridGeometry.Columns(928,4,220,16,true)==4,"Unpaired metric column"));
        Check("Eight-color palettes use complete rows instead of seven plus one",()=>Assert(DesktopGridGeometry.Columns(716,8,88,8,true)==4&&DesktopGridGeometry.Columns(760,8,88,8,true)==8,"Unbalanced palette row"));
        Check("Quick switches retain existing even column steps",()=>
        {
            Assert(DesktopGridGeometry.Columns(700,11,90,8,false,true)==6,"Unexpected quick columns");
            Assert(DesktopGridGeometry.Columns(400,11,90,8,false,true)==4,"Unexpected compact quick columns");
        });
        int sampled=0;
        Check("Mixed content stays ordered and fills each row at all sampled widths",()=>
        {
            foreach(double width in new double[]{0,1,240,320,480,639,640,716,840,1024,1280,1536})
            for(int count=0;count<=24;count++)
            {
                bool[] full=Enumerable.Range(0,count).Select(i=>i%5==2).ToArray();
                int columns=DesktopGridGeometry.Columns(width,2,312,16);
                var rows=DesktopGridGeometry.Rows(width,columns,16,full);int next=0;
                foreach(var row in rows)
                {
                    Assert(row.Start==next&&row.Count>=1&&row.Count<=columns,"Item lost, repeated or reordered");
                    Assert(row.ItemWidth>=0&&double.IsFinite(row.ItemWidth),"Invalid width");
                    Assert(Math.Abs(row.ItemWidth*row.Count+16*(row.Count-1)-width)<.001,"Row leaves a hole or overflows");
                    if(row.Count>1)Assert(!full.Skip(row.Start).Take(row.Count).Any(x=>x),"Wide control squeezed into a half row");
                    next+=row.Count;
                }
                Assert(next==count,"Not all controls are arranged");sampled++;
            }
        });
        Check("Empty groups have no rows",()=>Assert(DesktopGridGeometry.Rows(720,2,16,[]).Count==0,"Empty group reserves space"));
        int failed=checks.Count(c=>!JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean());
        File.WriteAllText(output,JsonSerializer.Serialize(new{allPassed=failed==0,count=checks.Count,failed,geometrySamples=sampled,winUiLaunched=false,hardwareWrites=0,checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Responsive layout checks: {checks.Count-failed}/{checks.Count}; {sampled} mixed-content samples");Environment.ExitCode=failed==0?0:1;
    }
}
