using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace OpenRevo.Core;

// Operational diagnostics are independent of optional verbose/debug logging.
// Each process has its own file; a failed disk write never stops a hardware action.
public sealed class DiagnosticJournal
{
    private readonly object gate=new();
    private readonly Queue<JsonElement> recent=new();
    private readonly Dictionary<string,(DateTimeOffset at,int suppressed)> repeated=new();
    private readonly int maxBytes,retainedSessions;
    public string Session{get;}=$"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    public string? DirectoryPath{get;}
    public string? FilePath=>DirectoryPath==null?null:Path.Combine(DirectoryPath,"session-"+Session+".jsonl");
    public string? LastWriteError{get;private set;}
    public DiagnosticJournal(string? directory,int maxBytes=1024*1024,int retainedSessions=8)
    {DirectoryPath=directory;this.maxBytes=Math.Max(1024,maxBytes);this.retainedSessions=Math.Clamp(retainedSessions,1,16);}
    public void Write(string name,string level="info",object? data=null,Exception? error=null,string? operation=null,bool throttle=false)
    {
        try
        {
            lock(gate)
            {
                int suppressed=0;
                if(throttle)
                {
                    string key=name+":"+error?.GetType().Name+":"+error?.HResult;
                    if(repeated.TryGetValue(key,out var prior)&&DateTimeOffset.UtcNow-prior.at<TimeSpan.FromSeconds(30)){repeated[key]=(prior.at,prior.suppressed+1);return;}
                    if(repeated.TryGetValue(key,out prior))suppressed=prior.suppressed;
                    if(repeated.Count>128)repeated.Clear();repeated[key]=(DateTimeOffset.UtcNow,0);
                }
                var entry=JsonSerializer.SerializeToElement(new{schema=1,at=DateTimeOffset.UtcNow,session=Session,pid=Environment.ProcessId,thread=Environment.CurrentManagedThreadId,level,eventId=Limit(name,100),operation,suppressed,data=SafeData(data),exception=ExceptionData(error)});
                if(recent.Count>=256)recent.Dequeue();recent.Enqueue(entry);
                if(FilePath==null)return;
                try
                {
                    Directory.CreateDirectory(DirectoryPath!);
                    byte[] bytes=Encoding.UTF8.GetBytes(entry.GetRawText()+"\n");
                    if(File.Exists(FilePath)&&new FileInfo(FilePath).Length+bytes.Length>maxBytes)
                    {
                        for(int i=2;i>=1;i--){string old=FilePath+"."+i;if(File.Exists(old))File.Move(old,FilePath+"."+(i+1),true);}
                        File.Move(FilePath,FilePath+".1",true);
                    }
                    using(var stream=new FileStream(FilePath,FileMode.Append,FileAccess.Write,FileShare.ReadWrite)){stream.Write(bytes);stream.Flush();}
                    LastWriteError=null;Prune();
                }
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){LastWriteError=$"{e.GetType().Name} 0x{e.HResult:X8}: {Scrub(e.Message)}";}
            }
        }
        catch{ /* Diagnostics must not replace the original error. */ }
    }
    private void Prune()
    {
        var groups=Directory.GetFiles(DirectoryPath!,"session-*.jsonl*").GroupBy(p=>p.Split(".jsonl",StringSplitOptions.None)[0]).OrderByDescending(g=>g.Max(File.GetLastWriteTimeUtc)).ToArray();
        foreach(var group in groups.Skip(retainedSessions))
            if(!group.Key.EndsWith("session-"+Session,StringComparison.Ordinal))
                foreach(string path in group)try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public object Snapshot()
    {
        lock(gate)
        {
            int bytes=0;var events=new List<JsonElement>();foreach(var entry in recent.Reverse()){int size=Encoding.UTF8.GetByteCount(entry.GetRawText());if(bytes+size>256*1024)break;bytes+=size;events.Add(entry);}events.Reverse();
            return new{schema=1,Session,directory=DirectoryPath,file=FilePath,LastWriteError,memoryCapacity=256,eventsTruncated=events.Count<recent.Count,exportLimitBytes=256*1024,events};
        }
    }
    public static string Scrub(string? text,int limit=16000)
    {
        if(string.IsNullOrEmpty(text))return text??"";
        string value=Regex.Replace(text,@"(?i)[a-z]:[\\/]Users[\\/][^\\/\s""<>]+","%USERPROFILE%");
        value=Regex.Replace(value,@"S-1-(?:\d+-){2,}\d+","<user-sid>",RegexOptions.IgnoreCase);
        value=Regex.Replace(value,@"(?i)(password|token|secret|authorization|cookie)([""']?\s*[:=]\s*)([""'])(.*?)\3","$1$2$3<redacted>$3");
        value=Regex.Replace(value,@"(?i)(password|token|secret|authorization|cookie)([""']?\s*[:=]\s*)[^\s,;""'}]+","$1$2<redacted>");
        return Limit(value,limit);
    }
    private static string Limit(string text,int size)=>text.Length<=size?text:text[..size]+"…<truncated>";
    private static object? ExceptionData(Exception? error)
    {
        if(error==null)return null;var chain=new List<object>();
        for(Exception? e=error;e!=null&&chain.Count<8;e=e.InnerException)
            chain.Add(new{type=e.GetType().FullName,message=Scrub(e.Message),hresult=$"0x{e.HResult:X8}",win32=e is System.ComponentModel.Win32Exception w?(int?)w.NativeErrorCode:null,stack=Scrub(e.StackTrace)});
        return chain;
    }
    private static object? SafeData(object? data)
    {
        if(data==null)return null;
        try
        {
            var node=JsonSerializer.SerializeToNode(data,new JsonSerializerOptions{MaxDepth=16});
            JsonNode? Clean(JsonNode? current,int depth)
            {
                if(current==null)return null;if(depth>10)return JsonValue.Create("<depth-limit>");
                if(current is JsonObject obj){foreach(string key in obj.Select(p=>p.Key).ToArray())obj[key]=Regex.IsMatch(key,"password|token|secret|cookie|authorization|credential|^(sid|userId|userName|account)$",RegexOptions.IgnoreCase)?JsonValue.Create("<redacted>"):Clean(obj[key]?.DeepClone(),depth+1);return obj;}
                if(current is JsonArray array){var result=new JsonArray();foreach(var item in array.Take(128))result.Add(Clean(item?.DeepClone(),depth+1));return result;}
                if(current is JsonValue value&&value.TryGetValue<string>(out var s))return JsonValue.Create(Scrub(s));return current;
            }
            var clean=Clean(node,0);if(clean?.ToJsonString().Length>48000)return new{truncated=true,summary=Scrub(clean.ToJsonString())};return clean;
        }
        catch(Exception e){return new{serializationFailed=e.GetType().Name};}
    }
}

public static class DiagnosticLog
{
    private static DiagnosticJournal journal=new(null);
    private static readonly AsyncLocal<string?> correlation=new();
    public static DiagnosticJournal Current=>Volatile.Read(ref journal);
    public static void StartProcess(string mode)
    {
        string? directory=null;string? failure=null;
        try{directory=Path.Combine(AppData.DiagnosticsPath,"runtime");Directory.CreateDirectory(directory);}catch(Exception e){failure=e.GetType().Name+": "+e.Message;try{directory=Path.Combine(Path.GetTempPath(),"OpenRevoNative","diagnostics","runtime");Directory.CreateDirectory(directory);}catch{directory=null;}}
        Interlocked.Exchange(ref journal,new(directory));
        bool? administrator=null;try{administrator=Hal.GpuModeControl.IsAdministrator();}catch{}
        Record("process.started",new{mode,version=System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),os=Environment.OSVersion.VersionString,architecture=RuntimeInformation.ProcessArchitecture.ToString(),framework=RuntimeInformation.FrameworkDescription,administrator,logPathResolutionFailure=failure});
    }
    public static void Record(string name,object? data=null)=>Current.Write(name,data:data,operation:correlation.Value);
    public static void Event(string name,string level,object? data=null,bool throttle=false)=>Current.Write(name,level,data,operation:correlation.Value,throttle:throttle);
    public static void Failure(string name,Exception error,object? data=null,bool throttle=false)=>Current.Write(name,"error",data,error,correlation.Value,throttle);
    public static Operation Begin(string name,object? data=null)=>new(name,data);
    public sealed class Operation:IDisposable
    {
        private readonly string name,id=Guid.NewGuid().ToString("N")[..12];private readonly string? parent=correlation.Value;
        private readonly Stopwatch timer=Stopwatch.StartNew();private bool finished;
        internal Operation(string name,object? data){this.name=name;correlation.Value=id;Current.Write(name+".started",data:new{parent,details=data},operation:id);}
        public void Complete(object? data=null,string outcome="success"){if(finished)return;finished=true;Current.Write(name+".finished",data:new{outcome,elapsedMs=timer.ElapsedMilliseconds,details=data},operation:id);}
        public void Fail(Exception error,object? data=null){if(finished)return;finished=true;Current.Write(name+".failed","error",new{elapsedMs=timer.ElapsedMilliseconds,details=data},error,id);}
        public void Dispose(){if(!finished)Complete(outcome:"interrupted");correlation.Value=parent;}
    }
    public static object Collect(string diagnosticsDirectory)
    {
        var files=new List<object>();int budget=1024*1024;
        var names=new List<string>();string runtime=Path.Combine(diagnosticsDirectory,"runtime");
        try{foreach(string dir in new[]{runtime,Current.DirectoryPath,Path.Combine(Path.GetTempPath(),"OpenRevoNative","diagnostics","runtime")}.OfType<string>().Distinct())if(Directory.Exists(dir))names.AddRange(Directory.GetFiles(dir!,"session-*.jsonl*").OrderByDescending(File.GetLastWriteTimeUtc).Take(8));names=names.Distinct().OrderByDescending(File.GetLastWriteTimeUtc).Take(8).ToList();}catch(Exception e){Failure("logs.collect.enumeration",e);}
        names.AddRange(new[]{"startup-failure.txt","native.log","hardware-audit.jsonl"}.Select(n=>Path.Combine(diagnosticsDirectory,n)));
        foreach(string path in names)
        {
            if(budget<=0)break;
            try
            {
                if(!File.Exists(path))continue;using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                int size=(int)Math.Min(stream.Length,Math.Min(128*1024,budget));bool truncated=stream.Length>size;stream.Seek(-size,SeekOrigin.End);byte[] bytes=new byte[size];int count=stream.Read(bytes,0,size);budget-=count;
                string content=Encoding.UTF8.GetString(bytes,0,count);if(truncated&&path.Contains(".jsonl",StringComparison.Ordinal)){int line=content.IndexOf('\n');content=line<0?"":content[(line+1)..];}
                files.Add(new{name=Path.GetFileName(path),bytes=stream.Length,truncated,content=DiagnosticJournal.Scrub(content,128*1024)});
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){files.Add(new{name=Path.GetFileName(path),readError=DiagnosticJournal.Scrub(e.Message),hresult=$"0x{e.HResult:X8}"});}
        }
        return new{schema=1,current=Current.Snapshot(),files,totalReadLimitBytes=1024*1024};
    }
}
