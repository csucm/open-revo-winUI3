// Short-lived installed file/update tools. Never constructs a hardware runtime or sensor reader.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true };
    private sealed record Request(string Operation, JsonElement Payload);
    private static readonly SemaphoreSlim writes = new(1,1);
    private static Stream? output;
    [STAThread]
    private static int Main(string[] args) => Run(args).GetAwaiter().GetResult();
    private static async Task<int> Run(string[] args)
    {
        try
        {
            if(args is not ["--native-tools",var id]||!int.TryParse(id,out int parentId))return 2;
            using var parent=RequireParent(parentId);
            using var input=Console.OpenStandardInput(); using var stdout=Console.OpenStandardOutput(); output=stdout;
            var request=await Read(input);
            string installation=Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            StartupRegistration.ValidateResidentInstallation(installation);
            // Validate/resolve exactly the current desktop user's existing data ownership.
            using var desktop=OpenDesktop();
            T Desktop<T>(Func<T> work)=>WindowsIdentity.RunImpersonated(desktop,work);
            if(request.Operation=="context")
            {
                UserDataStore.RecoverInstalledPortableReset(installation);
                string source=Desktop(()=>{UserDataStore.EnsureMarker();return AppData.DirectoryPath;});
                await Write(new { Done=true,Context=new { Installed=true,DataDirectory=source,DiagnosticsDirectory=Path.Combine(source,"diagnostics"),MigrationRoot=Path.GetDirectoryName(AppData.DefaultDirectoryPath)!,DebugAvailable=false,LogLevel=SystemPreferences.Load(source).LogLevel,LogFilter=SystemPreferences.Load(source).LogFilter } });
                return 0;
            }
            string directory=Desktop(()=>AppData.DirectoryPath);
            if(request.Operation=="maintenance")
            {
                var r=request.Payload.GetProperty("Request").Deserialize<PanelMaintenanceRequest>(Json)??throw new IOException("缺少维护请求。");
                if(!r.Valid)throw new IOException("维护参数无效。");
                var prefs=SystemPreferences.Load(directory);
                PanelMaintenanceSnapshot Settings()=>new(directory,Path.Combine(directory,"diagnostics"),Path.GetDirectoryName(AppData.DefaultDirectoryPath)!,false,prefs.LogLevel,prefs.LogFilter,true);
                PanelMaintenanceReply reply;
                switch(r.Action)
                {
                    case PanelMaintenanceAction.Read:reply=new(Settings());break;
                    case PanelMaintenanceAction.OpenData:
                    case PanelMaintenanceAction.OpenDiagnostics:
                    case PanelMaintenanceAction.PrepareMigration:
                        string folder=Desktop(()=>{string p=UserDataStore.ValidateDirectory(r.Action==PanelMaintenanceAction.OpenData?directory:r.Action==PanelMaintenanceAction.OpenDiagnostics?Path.Combine(directory,"diagnostics"):Settings().MigrationRoot);Directory.CreateDirectory(p);return p;});reply=new(Settings(),folder);break;
                    case PanelMaintenanceAction.ReadReport:
                        // Native owner supplies its own state. The tool only collects/scrubs existing logs.
                        var report=JsonSerializer.SerializeToNode(new { diagnosticsSchema=5,exportedAt=DateTimeOffset.UtcNow,host="OpenRevo.NativeCore",protocol=PanelProtocol.Revision,state=request.Payload.GetProperty("Snapshot"),logs=DiagnosticLog.Collect(Path.Combine(directory,"diagnostics"),includeSharedTemp:false) })!;
                        Scrub(report);string content=report.ToJsonString(new(){WriteIndented=true});if(Encoding.UTF8.GetByteCount(content)>6*1024*1024)throw new IOException("诊断报告超过边界。");reply=new(Report:content);break;
                    case PanelMaintenanceAction.Migrate:
                        string target=Desktop(()=>UserDataStore.ValidateInstalledDataLocation(r.Destination!,installation));
                        string next=Desktop(()=>UserDataStore.MigrateInstalled(directory,target,installation));reply=new(Directory:next,ExitRequired:true);break;
                    case PanelMaintenanceAction.Reset:
                        string backup=directory.Equals(Path.Combine(installation,"data"),StringComparison.OrdinalIgnoreCase)?UserDataStore.ResetInstalledPortable(installation):Desktop(()=>UserDataStore.Reset(UserDataStore.ValidateInstalledDataLocation(directory,installation)));
                        reply=new(Directory:backup,ExitRequired:true);break;
                    default:throw new IOException("当前会话未启用额外调试日志选项。");
                }
                await Write(new { Done=true,Outcome=r.Action is PanelMaintenanceAction.Migrate or PanelMaintenanceAction.Reset?9:1,Maintenance=reply });return 0;
            }
            if(request.Operation=="startup")
            {
                var r=request.Payload.Deserialize<PanelApplicationRequest>(Json)??throw new IOException("缺少启动请求。");if(!r.Valid||r.Action is not (PanelApplicationAction.ReadStartup or PanelApplicationAction.SetStartup))throw new IOException("无效启动操作。");
                bool enabled=r.Action==PanelApplicationAction.ReadStartup?StartupRegistration.ReadResidentLogon(installation):StartupRegistration.SetResidentLogon(installation,r.Enabled!.Value);
                await Write(new { Done=true,Outcome=r.Action==PanelApplicationAction.ReadStartup?1:2,Application=new PanelApplicationReply(StartupAvailable:true,StartupEnabled:enabled) });return 0;
            }
            if(request.Operation=="update-directory")
            {await Write(new { Done=true,Outcome=1,Application=new PanelApplicationReply(Directory:StartupRegistration.PrepareResidentUpdateCache(installation)) });return 0;}
            var current=ReleaseUpdater.InstalledUpdateVersion(new Version("0.7.0"));
            if(request.Operation=="update-check")
            {
                using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};
                var release=await new ReleaseUpdater(http).CheckAsync(current);
                await Write(new { Done=true,Release=release,Update=new PanelUpdateSnapshot("0.7.0",true,release==null?PanelUpdatePhase.Current:PanelUpdatePhase.Available,release?.Version.ToString(3),release?.Size) });return 0;
            }
            if(request.Operation=="update-download")
            {
                var release=request.Payload.Deserialize<NativeRelease>(Json)??throw new IOException("缺少更新目标。");
                if(release.Version<=current)throw new IOException("更新版本无效。");
                string cache=StartupRegistration.PrepareResidentUpdateCache(installation);
                using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(20)};
                var progress=new Progress(release);
                var download=await new ReleaseUpdater(http).DownloadAsync(release,cache,progress);
                await Write(new { Done=true,Download=download,Update=new PanelUpdateSnapshot("0.7.0",true,PanelUpdatePhase.Verified,release.Version.ToString(3),release.Size,100) });return 0;
            }
            if(request.Operation=="update-install")
            {
                var download=request.Payload.Deserialize<DownloadedUpdate>(Json)??throw new IOException("缺少更新文件。");
                StartupRegistration.ValidateResidentUpdatePayload(installation,download.Path);
                var release=download.Release;
                if(release.Version<=current||release.Size<=0||release.Size>ReleaseUpdater.MaximumInstallerSize||release.Sha256.Length!=64||!release.Sha256.All(Uri.IsHexDigit))throw new IOException("更新身份无效。");
                using(var file=new FileStream(download.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    if(file.Length!=release.Size||!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新文件校验不一致。");
                string script=ReleaseUpdater.BuildInstallScript(download,installation,parent.Id,parent.StartTime.ToUniversalTime().Ticks);
                var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell/v1.0/powershell.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
                foreach(string arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})info.ArgumentList.Add(arg);
                using var helper=Process.Start(info)??throw new IOException("无法启动更新安装助手。");
                await Write(new { Done=true,Outcome=3 });return 0;
            }
            throw new IOException("未知辅助操作。");
        }
        catch(Exception error)
        {
            try{await Write(new { Done=true,Outcome=4,Error=DiagnosticJournal.Scrub(error.Message,1024) });}catch{}
            return 1;
        }
    }
    private sealed class Progress(NativeRelease release):IProgress<double>
    {
        private int latest=-1;
        public void Report(double value)
        {
            int percent=Math.Clamp((int)(value*100),0,99);if(percent<=latest)return;latest=percent;
            Write(new { Update=new PanelUpdateSnapshot("0.7.0",true,PanelUpdatePhase.Downloading,release.Version.ToString(3),release.Size,percent) }).GetAwaiter().GetResult();
        }
    }
    private static async Task<Request> Read(Stream input)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));byte[] header=new byte[4];await input.ReadExactlyAsync(header,deadline.Token);int size=BinaryPrimitives.ReadInt32LittleEndian(header);if(size is <2 or >1024*1024)throw new IOException("辅助请求超过边界。");byte[] bytes=new byte[size];await input.ReadExactlyAsync(bytes,deadline.Token);return JsonSerializer.Deserialize<Request>(bytes,Json)??throw new IOException("辅助请求为空。");
    }
    private static async Task Write<T>(T reply)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(reply);if(bytes.Length>7*1024*1024)throw new IOException("辅助结果超过边界。");await writes.WaitAsync();try{await output!.WriteAsync(BitConverter.GetBytes(bytes.Length));await output.WriteAsync(bytes);await output.FlushAsync();}finally{writes.Release();}
    }
    private static Process RequireParent(int id)
    {
        if(GetFileType(GetStdHandle(-10))!=3||GetFileType(GetStdHandle(-11))!=3)throw new IOException("辅助程序需要私有继承管道。");
        var basic=new BasicInfo();if(NtQueryInformationProcess(Process.GetCurrentProcess().Handle,0,ref basic,Marshal.SizeOf<BasicInfo>(),out _)!=0||basic.Parent.ToInt64()!=id)throw new IOException("辅助父进程不匹配。");
        var parent=Process.GetProcessById(id);using var current=Process.GetCurrentProcess();string expected=Path.Combine(AppContext.BaseDirectory,"RevoCore.exe");
        if(parent.SessionId==0||parent.SessionId!=current.SessionId||!string.Equals(parent.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase)){parent.Dispose();throw new IOException("辅助程序仅接受同会话配对原生Core。");}
        using var identity=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)){parent.Dispose();throw new IOException("辅助操作缺少安装授权。");}return parent;
    }
    private static SafeAccessTokenHandle OpenDesktop(){int code=OpenToken(out var token);if(code==0&&!token.IsInvalid)return token;token.Dispose();Marshal.ThrowExceptionForHR(code,new IntPtr(-1));throw new IOException("无法取得普通桌面令牌。");}
    private static void Scrub(System.Text.Json.Nodes.JsonNode? node)
    {
        if(node is System.Text.Json.Nodes.JsonObject obj)foreach(var key in obj.Select(p=>p.Key).ToArray()){if(obj[key] is System.Text.Json.Nodes.JsonValue v&&v.TryGetValue<string>(out var text))obj[key]=DiagnosticJournal.Scrub(text,8192);else Scrub(obj[key]);}
        else if(node is System.Text.Json.Nodes.JsonArray array)for(int i=0;i<array.Count;i++){if(array[i] is System.Text.Json.Nodes.JsonValue v&&v.TryGetValue<string>(out var text))array[i]=DiagnosticJournal.Scrub(text,8192);else Scrub(array[i]);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicInfo{public nint Reserved,Peb,Reserved2,Reserved3,Pid,Parent;}
    [DllImport("ntdll.dll")]private static extern int NtQueryInformationProcess(nint process,int type,ref BasicInfo info,int size,out int returned);
    [DllImport("kernel32.dll")]private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll")]private static extern uint GetFileType(nint handle);
    [DllImport("openrevo_native_bridge.dll",EntryPoint="or_native_resident_desktop_token",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]private static extern int OpenToken(out SafeAccessTokenHandle token);
}
