using OpenRevo.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
internal static class DiagnosticsChecks
{
    public static void Run(string output)
    {
        string root=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"check-data-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        var checks=new List<object>();void Assert(bool ok,string reason){if(!ok)throw new Exception(reason);}
        void Check(string name,Action run){try{run();checks.Add(new{name,passed=true});}catch(Exception e){checks.Add(new{name,passed=false,error=e.ToString()});}}
        JsonElement Snapshot(DiagnosticJournal journal)=>JsonSerializer.SerializeToElement(journal.Snapshot());
        Check("Process resources provide separate native and managed counters",()=>
        {
            var r=ProcessResources.Read();
            Assert(r.WorkingSetBytes>0&&r.PrivateBytes>0&&r.ManagedBytes>0&&r.PeakWorkingSetBytes>=r.WorkingSetBytes,"Missing process memory counters");
            Assert(r.UptimeMs>=0&&r.Handles>0&&r.Threads>0&&r.Gen2Collections>=0,"Missing lifetime or resource counters");
            var j=new DiagnosticJournal(null);j.Write("process.resources",data:new{resources=r});
            Assert(Snapshot(j).GetProperty("events")[0].GetProperty("data").GetProperty("resources").GetProperty("PrivateBytes").GetInt64()==r.PrivateBytes,"Resource diagnostics lost byte units");
        });
        Check("Structured diagnostics preserve exception chain and native code",()=>
        {
            var j=new DiagnosticJournal(Path.Combine(root,"exceptions"));j.Write("startup.failed","error",new{stage="readback"},new IOException("outer",new Win32Exception(5)));
            var e=Snapshot(j).GetProperty("events")[0];Assert(e.GetProperty("exception").GetArrayLength()==2&&e.GetProperty("exception")[1].GetProperty("win32").GetInt32()==5,"Error chain or Win32 code lost");
            Assert(JsonDocument.Parse(File.ReadAllText(j.FilePath!)).RootElement.GetProperty("pid").GetInt32()==Environment.ProcessId,"Invalid JSONL");
        });
        Check("Sensitive fields and user paths are scrubbed",()=>
        {
            var j=new DiagnosticJournal(null);j.Write("redaction",data:new{token="do-not-store-token-123",Password="do-not-store-password",file=@"C:\Users\Someone\file.json"});string text=Snapshot(j).GetRawText();
            Assert(!text.Contains("do-not-store")&&!text.Contains("Someone")&&text.Contains("USERPROFILE"),"Sensitive value escaped redaction");
        });
        Check("Legacy quoted token text is redacted",()=>Assert(!DiagnosticJournal.Scrub("{\"token\":\"unit secret with spaces\"}").Contains("unit secret"),"Quoted credential leaked"));
        Check("Collected logs keep the newest failure in large files",()=>
        {
            string dir=Path.Combine(root,"tail");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"startup-failure.txt"),new string('x',180000)+" newest-failure-marker");
            Assert(JsonSerializer.Serialize(DiagnosticLog.Collect(dir)).Contains("newest-failure-marker"),"Latest error cut from export");
        });
        Check("Disk failure leaves recoverable memory diagnostics",()=>
        {
            string blocked=Path.Combine(root,"blocked");File.WriteAllText(blocked,"not a directory");var j=new DiagnosticJournal(blocked);j.Write("disk.failure");var state=Snapshot(j);
            Assert(state.GetProperty("events").GetArrayLength()==1&&state.GetProperty("LastWriteError").ValueKind==JsonValueKind.String,"Disk error lost original event");
        });
        Check("Rotating files and memory ring remain bounded",()=>
        {
            string dir=Path.Combine(root,"rotate");var j=new DiagnosticJournal(dir,1024);for(int i=0;i<300;i++)j.Write("rotation",data:new{i,text=new string('x',100)});
            Assert(Directory.GetFiles(dir).Length<=4&&Directory.GetFiles(dir).All(f=>new FileInfo(f).Length<=1024)&&Snapshot(j).GetProperty("events").GetArrayLength()==256,"Retention overflow");
        });
        Check("Old process sessions are pruned without deleting active session",()=>
        {
            string dir=Path.Combine(root,"retention");var a=new DiagnosticJournal(dir,1024,2);a.Write("a");File.SetLastWriteTimeUtc(a.FilePath!,DateTime.UtcNow.AddHours(-2));
            var b=new DiagnosticJournal(dir,1024,2);b.Write("b");File.SetLastWriteTimeUtc(b.FilePath!,DateTime.UtcNow.AddHours(-1));var c=new DiagnosticJournal(dir,1024,2);c.Write("c");
            Assert(!File.Exists(a.FilePath)&&File.Exists(b.FilePath)&&File.Exists(c.FilePath),"Wrong session removed");
        });
        Check("Repeated polling failures do not flood logs",()=>{var j=new DiagnosticJournal(null);for(int i=0;i<100;i++)j.Write("poll.failed","error",error:new IOException("offline"),throttle:true);Assert(Snapshot(j).GetProperty("events").GetArrayLength()==1,"Repeated failure flood");});
        Check("Bad diagnostic object cannot break logging",()=>{var cycle=new Dictionary<string,object>();cycle["self"]=cycle;var j=new DiagnosticJournal(null);j.Write("cycle",data:cycle);Assert(Snapshot(j).GetProperty("events").GetArrayLength()==1,"Serialization error escaped");});
        Check("Async hardware steps retain their operation correlation",()=>
        {
            using(var span=DiagnosticLog.Begin("unit.operation")){Task.Run(()=>DiagnosticLog.Record("unit.child")).GetAwaiter().GetResult();span.Complete();}
            var entries=Snapshot(DiagnosticLog.Current).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("eventId").GetString()?.StartsWith("unit.")==true).ToArray();
            Assert(entries.Length==3&&entries.Select(e=>e.GetProperty("operation").GetString()).Distinct().Count()==1,"Async correlation lost");
        });
        Check("Export collects a separate process startup failure",()=>
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(DiagnosticsChecks).Assembly.Location);
            start.ArgumentList.Add("--diagnostic-log-child");start.ArgumentList.Add(Path.Combine(root,"runtime"));using var child=Process.Start(start)!;
            if(!child.WaitForExit(15000)){child.Kill();throw new Exception("Diagnostic child timeout");}
            var report=JsonSerializer.SerializeToElement(DiagnosticLog.Collect(root));string text=report.GetRawText();Assert(child.ExitCode==0&&text.Contains("startup.child.failed")&&text.Contains("readback"),"Cross-process log missing");
        });
        const string path=@"C:\Apps\Open-Revo\OpenRevo.Native.exe",sid="S-1-5-21-111-222-333-1001";
        string xml=StartupRegistration.BuildTaskXml(path,sid);XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        Check("Task baseline matches",()=>Assert(StartupRegistration.MatchesTask(xml,path,sid),"Baseline rejected"));
        Check("Scheduler omitted default Enabled is accepted",()=>{var x=XElement.Parse(xml);x.Descendants(ns+"Enabled").Remove();Assert(StartupRegistration.MatchesTask(x.ToString(),path,sid),"Default true rejected");});
        Check("Task booleans accept XML value 1",()=>{var x=XElement.Parse(xml);foreach(var e in x.Descendants(ns+"Enabled"))e.Value="1";Assert(StartupRegistration.MatchesTask(x.ToString(),path,sid),"Boolean 1 rejected");});
        Check("Task paths use Windows case normalization",()=>Assert(StartupRegistration.MatchesTask(xml,path.ToLowerInvariant(),sid),"Case-only path rejected"));
        Check("Scheduler account name resolves to current SID",()=>{var x=XElement.Parse(xml);foreach(var e in x.Descendants(ns+"UserId"))e.Value=@"DESKTOP\LocalUser";Assert(StartupRegistration.InspectTask(x.ToString(),path,sid,_=>sid).Matches,"Account normalization rejected");});
        Check("Disabled own task is repairable but not enabled",()=>{var x=XElement.Parse(xml);x.Element(ns+"Settings")!.Element(ns+"Enabled")!.Value="false";var result=StartupRegistration.InspectTask(x.ToString(),path,sid);Assert(result.Owned&&!result.Matches&&!result.Enabled&&result.Differences.Contains("settings.enabled"),"Disabled ownership or difference lost");});
        Check("Other install path is never owned",()=>Assert(!StartupRegistration.InspectTask(xml,@"C:\Other\OpenRevo.Native.exe",sid).Owned,"Other install overwritten"));
        Check("Additional executable action is never owned",()=>{var x=XElement.Parse(xml);x.Element(ns+"Actions")!.Add(new XElement(ns+"Exec",new XElement(ns+"Command","other.exe")));Assert(!StartupRegistration.InspectTask(x.ToString(),path,sid).Owned,"Unknown action accepted");});
        Check("Wrong task principal remains rejected",()=>{var x=XElement.Parse(xml);foreach(var e in x.Descendants(ns+"UserId"))e.Value="another-user";Assert(!StartupRegistration.InspectTask(x.ToString(),path,sid,_=>"S-1-5-18").Owned,"Other principal accepted");});
        Check("Readback reports exact privilege difference",()=>Assert(StartupRegistration.InspectTask(xml.Replace("HighestAvailable","LeastPrivilege"),path,sid).Differences.Contains("principal.runLevel"),"Missing field difference"));
        InternalDisplayCandidate Panel(string id="AUO1234",int technology=11,string name="Internal panel",string adapter="NVIDIA",bool shared=false)=>new("display1",id,name,adapter,technology,true,true,shared);
        Check("Different laptop panel ID is supported by topology",()=>Assert(DisplayTopology.Select([Panel()]).Panel=="AUO1234","Local EDID gate remains"));
        Check("LVDS embedded panel is supported",()=>Assert(DisplayTopology.Select([Panel(technology:6)]).Technology==6,"LVDS rejected"));
        Check("Windows INTERNAL output is supported",()=>Assert(DisplayTopology.Select([Panel(technology:int.MinValue)]).Technology==int.MinValue,"Internal rejected"));
        void Reject(params InternalDisplayCandidate[] values){try{DisplayTopology.Select(values);throw new Exception("Unsafe display accepted");}catch(IOException){}}
        Check("External HDMI is not an internal panel",()=>Reject(Panel(technology:5)));
        Check("Virtual adapter cannot masquerade as eDP",()=>Reject(Panel(adapter:"GameViewer Virtual Display Adapter")));
        Check("Indirect remote display is rejected",()=>Reject(Panel(technology:17)));
        Check("Multiple internal targets are rejected",()=>Reject(Panel(),Panel("BOE9999") with{DeviceName="display2"}));
        Check("Clone source ambiguity blocks control",()=>Reject(Panel(shared:true)));
        Check("Unavailable panel is rejected",()=>Reject(Panel() with{Available=false}));
        Check("WMI and CCD panel IDs normalize consistently",()=>Assert(DisplayTopology.PanelCode(@"\\?\DISPLAY#BOE1234#instance")==DisplayTopology.PanelCode(@"DISPLAY\BOE1234\instance_0"),"Identity mismatch"));
        var failed=checks.Count(c=>!JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean());
        File.WriteAllText(output,JsonSerializer.Serialize(new{allPassed=failed==0,count=checks.Count,failed,hardwareWrites=0,startupTasksChanged=false,desktopAutomation=false,checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Diagnostics checks: {checks.Count-failed}/{checks.Count}");Environment.ExitCode=failed==0?0:1;
    }
}
