using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
if(args.Length==2&&args[0]=="--native-extension-checks"){NativeExtensionChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--preference-persistence-checks"){PreferencePersistenceChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--update-checks"){await UpdateChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--update-source-check")
{
    using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(3)};
    var updater=new ReleaseUpdater(http);
    var release=await updater.CheckAsync(new Version(0,0,0))??throw new IOException("No native release found.");
    var download=await updater.DownloadAsync(release,Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!,"download"));
    File.WriteAllText(args[1],JsonSerializer.Serialize(new{release,download.Path,installerLaunches=0},new JsonSerializerOptions{WriteIndented=true}));return;
}
if(args.Length==3&&args[0]=="--update-catalog-check")
{
    var release=ReleaseUpdater.ParseReleases(File.ReadAllText(args[1]),new Version(0,0,0))??throw new IOException("No native release found.");
    using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(3)};
    var download=await new ReleaseUpdater(http).DownloadAsync(release,Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!,"download"));
    File.WriteAllText(args[2],JsonSerializer.Serialize(new{release,download.Path,installerLaunches=0,catalog="GitHub authenticated read; no token in product"},new JsonSerializerOptions{WriteIndented=true}));return;
}
if(args.Length==2&&args[0]=="--osd-frame-checks"){OsdFrameChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--responsive-layout-checks"){ResponsiveLayoutChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--editor-fixes-checks"){EditorFixChecks.Run(args[1]);return;}
if(args.Length==3&&args[0]=="--read-color-engine")
{
    using var colorCore=new UpstreamCoreLibrary(Path.GetFullPath(args[1]));
    var reads=new System.Collections.Concurrent.ConcurrentBag<object>();
    for(int cycle=0;cycle<3;cycle++){
    int round=cycle;var callers=Enumerable.Range(0,8).Select(index=>new Thread(()=>
    {
        try{var state=colorCore.Read(22);reads.Add(new{round,index,callerThread=Environment.CurrentManagedThreadId,state,success=state.TryGetProperty("matrix",out var m)&&m.GetArrayLength()==25});}
        catch(Exception error){reads.Add(new{index,callerThread=Environment.CurrentManagedThreadId,success=false,error=error.ToString()});}
    })).ToArray();
    foreach(var thread in callers)thread.Start();foreach(var thread in callers)thread.Join();
    colorCore.StopDesktopServices();}
    bool passed=reads.Count==24&&reads.All(r=>JsonSerializer.SerializeToElement(r).GetProperty("success").GetBoolean());
    File.WriteAllText(args[2],JsonSerializer.Serialize(new{allPassed=passed,readOnly=true,colorWrites=0,ownerRestartCycles=3,reads},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=passed?0:1;return;
}
if(args.Length==2&&args[0]=="--diagnostics-checks"){DiagnosticsChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--diagnostic-log-child"){new DiagnosticJournal(args[1]).Write("startup.child.failed","error",new{stage="readback"},new IOException("task mismatch",new System.ComponentModel.Win32Exception(5)));return;}
if(args.Length==2&&args[0]=="--read-display-diagnostics")
{
    object result;try{result=new{success=true,display=DisplayControl.Read(),topology=DisplayTopology.LastSnapshot};}catch(Exception error){result=new{success=false,error=error.ToString(),topology=DisplayTopology.LastSnapshot};}
    object brightness;try{brightness=new{success=true,value=ScreenBrightness.Read()};}catch(Exception e){brightness=new{success=false,type=e.GetType().Name,error=e.Message,hresult=$"0x{e.HResult:X8}"};}
    File.WriteAllText(args[1],JsonSerializer.Serialize(new{mode=result,brightness},new JsonSerializerOptions{WriteIndented=true}));return;
}

if(args.Length==2&&args[0]=="--core-admission-checks"){CoreAdmissionChecks.Run(args[1]);return;}
if(args.Length==1&&args[0]=="--read-core-admission"){Console.WriteLine(JsonSerializer.Serialize(CoreMachine.Refresh(),new JsonSerializerOptions{WriteIndented=true}));return;}

if(args.Length==2&&args[0]=="--overview-power-checks")
{
    OverviewPowerChecks.Run(args[1]);return;
}

if(args.Length==1&&args[0]=="--read-overview-power")
{
    var session=HardwareSession.ForThisMachine();
    Console.WriteLine(JsonSerializer.Serialize(session.ProbeOverviewPowerTargets(),new JsonSerializerOptions{WriteIndented=true}));return;
}

if(args.Length==2&&args[0]=="--settings-core-checks")
{
    SettingsCoreChecks.Run(args[1]);return;
}

if(args.Length==4&&args[0]=="--tuning-preset-checks")
{
    TuningPresetChecks.Run(args[1],args[2],args[3]);return;
}

if(args.Length==2&&args[0]=="--read-desktop-services")
{
    using var core=new UpstreamCoreLibrary(Path.GetFullPath(args[1]));
    Console.WriteLine(JsonSerializer.Serialize(new{at=DateTimeOffset.Now,policies=DesktopPower.ReadPolicies(),power=core.Read(13),lighting=core.Read(15),color=core.Read(16),icc=core.Read(14),hardwareSettersInvoked=false},new JsonSerializerOptions{WriteIndented=true}));return;
}

if(args.Length==2&&args[0]=="--read-overview-core")
{
    using var core=new UpstreamCoreLibrary(Path.GetFullPath(args[1]));
    var samples=new List<object>();
    for(int i=0;i<3;i++){samples.Add(new{at=DateTimeOffset.Now,metrics=core.ReadOverviewTelemetry(true)});if(i<2)await Task.Delay(2100);}
    var switches=core.ReadOverviewSwitches();
    var notification=core.SendOsd(3,1);
    bool queueCleared=core.Read(12).ValueKind==JsonValueKind.Null;
    Console.WriteLine(JsonSerializer.Serialize(new{samples,switches,notification,queueCleared,hardwareSettersInvoked=false},new JsonSerializerOptions{WriteIndented=true}));
    return;
}

if(args.Length==1&&args[0] is "--live-win-lock-roundtrip" or "--live-win-lock-rapid")
{
    bool admin=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    if(admin)throw new IOException("Win lock test must run without administrator elevation.");
    var session=HardwareSession.ForThisMachine();session.Takeover();var before=session.ProbeSwitches();
    var timings=new List<object>();
    try
    {
        foreach(bool target in Enumerable.Range(0,args[0]=="--live-win-lock-rapid"?8:2).Select(i=>i%2==0?!before.WinKeyLocked:before.WinKeyLocked))
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();var result=session.SetWinKeyLocked(target);
            timings.Add(new{target,confirmed=result.WinKeyLocked,elapsedMs=watch.ElapsedMilliseconds});
        }
    }
    catch(Exception error)
    {
        timings.Add(new{failure=error.Message});
        // After a failed five-second acknowledgement, do not replay it. Wait,
        // reread and restore only the baseline if it is still different.
        Thread.Sleep(2500);session.Takeover();
        if(session.ProbeSwitches().WinKeyLocked!=before.WinKeyLocked)session.SetWinKeyLocked(before.WinKeyLocked);
        Environment.ExitCode=1;
    }
    finally
    {
        var after=session.ProbeSwitches();
        Console.WriteLine(JsonSerializer.Serialize(new{administratorToken=admin,before,after,restored=after.WinKeyLocked==before.WinKeyLocked,timings},new JsonSerializerOptions{WriteIndented=true}));
    }
    return;
}

if(args.Length==2&&args[0]=="--read-custom-tuning")
{
    using var core=new UpstreamCoreLibrary(Path.GetFullPath(args[1]));
    var session=HardwareSession.ForThisMachine();
    Console.WriteLine(JsonSerializer.Serialize(session.ProbeTuning(core),new JsonSerializerOptions{WriteIndented=true}));return;
}

if(args.Length==2 && args[0] is "--gpu-rpc-read" or "--gpu-rpc-noop")
{
    var reply=await GpuModeControl.Request(args[0]=="--gpu-rpc-read"?0:4,0,Path.GetFullPath(args[1]));
    Console.WriteLine(JsonSerializer.Serialize(reply,new JsonSerializerOptions{WriteIndented=true}));
    if(!reply.Success||reply.State?.Known!=true||reply.Changed)Environment.ExitCode=1;
    return;
}

if(args.Length==2 && args[0]=="--check-camera-access-guard")
{
    using var core=new UpstreamCoreLibrary(Path.GetFullPath(args[1]));
    var before=core.Read(8);bool rejected=false;
    try{core.AllowCameraAccess();}catch(IOException){rejected=true;}
    var after=core.Read(8);
    if(!rejected || before.ToString()!=after.ToString())throw new IOException("Non-admin camera access mutation was not rejected without side effects.");
    Console.WriteLine(JsonSerializer.Serialize(new{rejected,before,after}));return;
}

if(args.Length==2 && args[0] is "--live-core-camera-power-roundtrip" or "--core-camera-off")
{
    using var core=new UpstreamCoreLibrary(Path.GetFullPath(args[1]));
    var session=HardwareSession.ForThisMachine();session.Takeover();
    var before=session.ProbeSwitches();var pnp=CameraControl.Read();
    bool admin=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    if(admin)throw new IOException("This check must run without administrator elevation.");
    if(args[0]=="--core-camera-off")
    {
        Console.WriteLine(JsonSerializer.Serialize(new{AdministratorToken=admin,before,after=session.SetCameraPoweredViaCore(core,false),pnp=CameraControl.Read(),accessBlocked=core.IsCameraAccessBlocked()}));return;
    }
    if(pnp?.Enabled!=true || core.IsCameraAccessBlocked())throw new IOException("One-time Windows camera setup is not complete.");
    var log=new List<object>{new{phase="baseline",AdministratorToken=admin,switches=before,pnp,accessBlocked=core.IsCameraAccessBlocked()}};
    try
    {
        foreach(bool enabled in new[]{true,false,true,false})
        {
            var actual=session.SetCameraPoweredViaCore(core,enabled);
            Thread.Sleep(200);
            log.Add(new{phase="core-power",enabled,actual,core=core.Read(8),pnp=CameraControl.Read(),accessBlocked=core.IsCameraAccessBlocked()});
        }
    }
    finally
    {
        if(session.Active)session.SetCameraPoweredViaCore(core,before.CameraPowered);
        log.Add(new{phase="final",switches=session.ProbeSwitches(),pnp=CameraControl.Read(),accessBlocked=core.IsCameraAccessBlocked()});
        Console.WriteLine(JsonSerializer.Serialize(log,new JsonSerializerOptions{WriteIndented=true}));
    }
    return;
}

if(args.Length==2&&args[0]=="--live-camera-roundtrip")
{
    var session=HardwareSession.ForThisMachine();session.Takeover();
    var baseline=CameraControl.Read()??throw new IOException("Camera unavailable.");
    var power=session.ProbeSwitches();
    if(!baseline.Enabled.HasValue)throw new IOException("Camera state unknown.");
    var log=new List<object>{new{phase="baseline",camera=baseline,switches=power}};
    void Save()=>File.WriteAllText(Path.GetFullPath(args[1]),JsonSerializer.Serialize(log,new JsonSerializerOptions{WriteIndented=true}));
    Save();
    try
    {
        int status=CameraControl.RunPrivileged(!baseline.Enabled.Value);
        log.Add(new{phase="toggled",status,camera=CameraControl.Read(),switches=session.ProbeSwitches()});Save();
        if(status!=0)throw new IOException("Camera toggle failed.");
    }
    finally
    {
        int status=CameraControl.RunPrivileged(baseline.Enabled.Value);
        if(status==0 && session.ProbeSwitches().CameraPowered!=power.CameraPowered)session.SetCameraPowered(power.CameraPowered);
        log.Add(new{phase="restored",status,camera=CameraControl.Read(),switches=session.ProbeSwitches()});Save();
        if(status!=0 || CameraControl.Read()?.Enabled!=baseline.Enabled || session.ProbeSwitches().CameraPowered!=power.CameraPowered)
            throw new IOException("Camera restoration not confirmed.");
    }
    return;
}

if(args.Contains("--read-quick-tools")||args.Contains("--live-quick-roundtrip"))
{
    string dll=Path.GetFullPath(args[^1]);using var core=new UpstreamCoreLibrary(dll);
    var session=HardwareSession.ForThisMachine();var control=session.Takeover();var switches=session.ProbeSwitches();
    bool? mic=core.ReadMicrophone();var camera=CameraControl.Read();
    var log=new List<object>{new{phase="baseline",control,switches,mic,camera}};
    if(args.Contains("--live-quick-roundtrip"))
    {
        if(mic is not bool baseline)throw new IOException("Default microphone unavailable.");
        bool micChanged=false,winChanged=false;
        try
        {
            core.SetMicrophone(!baseline);micChanged=true;log.Add(new{phase="mic-toggled",actual=core.ReadMicrophone()});
            if(core.ReadMicrophone()!=!baseline)throw new IOException("Mic readback mismatch.");
            core.SetMicrophone(baseline);micChanged=false;
            session.SetWinKeyLocked(!switches.WinKeyLocked);winChanged=true;log.Add(new{phase="win-toggled",actual=session.ProbeSwitches()});
            session.SetWinKeyLocked(switches.WinKeyLocked);winChanged=false;
            using(var power=new SessionPower()){power.SetKeepAwake(true);power.ReleaseDisplay();log.Add(new{phase="power-system-only",power.Enabled,power.DisplayHeld});power.SetKeepAwake(false);}
        }
        finally
        {
            if(micChanged && core.ReadMicrophone()==!baseline)core.SetMicrophone(baseline);
            if(winChanged && session.ProbeSwitches().WinKeyLocked==!switches.WinKeyLocked)
            {if(!session.Active)session.Takeover();session.SetWinKeyLocked(switches.WinKeyLocked);}
            log.Add(new{phase="final",switches=session.ProbeSwitches(),mic=core.ReadMicrophone(),camera=CameraControl.Read(),control=session.State});
            Console.WriteLine(JsonSerializer.Serialize(log,new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    else Console.WriteLine(JsonSerializer.Serialize(log,new JsonSerializerOptions{WriteIndented=true}));
    return;
}

if (args.Contains("--restore-lighting-test"))
{
    int flag=Array.IndexOf(args,"--restore-lighting-test");
    var saved=JsonSerializer.Deserialize<KeyboardLighting>(File.ReadAllText(args[flag+1]))!;
    var expected=JsonSerializer.Deserialize<KeyboardLighting>(File.ReadAllText(args[flag+2]))!;
    var controller=HardwareSession.ForThisMachine();controller.Takeover();
    Console.WriteLine(JsonSerializer.Serialize(controller.RestoreKeyboardLightingSnapshot(saved,expected)));return;
}
if (args.Contains("--read-temperatures"))
{
    using var sensors = new TemperatureSensors(); sensors.Read(false); Thread.Sleep(1000);
    Console.WriteLine(JsonSerializer.Serialize(sensors.Read(true))); return;
}
if (args.Contains("--probe-gpu-lighting"))
{
    using var core = new UpstreamCoreLibrary(Path.GetFullPath(args[Array.IndexOf(args,"--probe-gpu-lighting")+1]));
    Console.WriteLine(JsonSerializer.Serialize(new { gpu=core.Read(4),lighting=core.Read(5) },new JsonSerializerOptions{WriteIndented=true}));return;
}
if (args.Contains("--probe-upstream-core"))
{
    int flag = Array.IndexOf(args, "--probe-upstream-core");
    using var core = new UpstreamCoreLibrary(Path.GetFullPath(args[flag + 1]));
    var metadata = core.Read(1);
    var tables = Enumerable.Range(1, 3).Select(mode => core.Read(2, (uint)mode)).ToArray();
    if (!tables.Select(t => t.GetProperty("max_duty").GetInt32()).SequenceEqual(new[] { 110, 160, 180 }))
        throw new InvalidOperationException("Unexpected upstream fan tables.");
    bool rejected = false;
    try { core.Read(2, 99); } catch (InvalidOperationException) { rejected = true; }
    if (!rejected) throw new InvalidOperationException("Invalid mode accepted.");
    for (int i = 0; i < 100; i++) core.Read(2, 2);
    Console.WriteLine(JsonSerializer.Serialize(new { metadata, tables, power = core.Read(3),
        invalidModeRejected = rejected, repeatedQueries = 100, hardwareWrites = false }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if(args.Contains("--recover-performance-balance"))
{
    var controller=HardwareSession.ForThisMachine(); controller.Takeover();
    var recovered=controller.SetPerformanceMode(2); controller.SetFanBoost(false);
    Console.WriteLine(JsonSerializer.Serialize(new { recovered, final=controller.ProbePerformance() },new JsonSerializerOptions{WriteIndented=true})); return;
}

if(args.Contains("--live-performance-roundtrip"))
{
    var controller=HardwareSession.ForThisMachine(); var baseline=controller.Takeover()??throw new IOException("This live check requires classic EC state.");
    var log=new List<object> { new {phase="baseline",state=controller.ProbePerformance()} }; bool changed=false;
    try
    {
        controller.SetFanBoost(true);
        foreach(int mode in new[]{1,2,3})
        {
            var receipt=controller.SetPerformanceMode(mode,true); changed=true; log.Add(new {phase="applied",mode,receipt});
            Thread.Sleep(1000); log.Add(new {phase="delayed",mode,state=controller.ProbePerformance()});
            log.Add(new {phase="restored",receipt=controller.UndoPerformance()}); changed=false;
        }
    }
    finally
    {
        try { if(changed && controller.Active) log.Add(controller.UndoPerformance()); if(controller.Active) controller.SetFanBoost(baseline.FanBoost); }
        finally { Console.WriteLine(JsonSerializer.Serialize(log,new JsonSerializerOptions{WriteIndented=true})); }
    }
    return;
}

if (args.Contains("--read-display-devices")) { Console.WriteLine(JsonSerializer.Serialize(DisplayControl.EnumerateDiagnosticDevices(), new JsonSerializerOptions { WriteIndented = true })); return; }

if (args.Contains("--read-fan-protocol"))
{
    Console.WriteLine(JsonSerializer.Serialize(HardwareSession.ForThisMachine().ProbeFanProtocol(), new JsonSerializerOptions { WriteIndented = true })); return;
}

if (args.Contains("--live-display-roundtrip"))
{
    var baseline = DisplayControl.Read();
    if (!baseline.Active) throw new InvalidOperationException("内置屏未激活，不在UU远程会话中执行显示切换测试。");
    int originalBrightness = ScreenBrightness.Read();
    int temporaryRate = baseline.Rates.First(rate => rate != baseline.Hertz);
    var displayResults = new List<object> { new { phase = "baseline", display = baseline, brightness = originalBrightness } };
    try
    {
        ScreenBrightness.Set(75); displayResults.Add(new { phase = "brightness-75", actual = ScreenBrightness.Read() });
        DisplayControl.SetRefresh(baseline.Hertz, temporaryRate); Thread.Sleep(1500);
        displayResults.Add(new { phase = "temporary-refresh", display = DisplayControl.Read() });
    }
    finally
    {
        var current = DisplayControl.Read();
        if (current.Hertz == temporaryRate) DisplayControl.SetRefresh(temporaryRate, baseline.Hertz);
        ScreenBrightness.Set(originalBrightness);
        displayResults.Add(new { phase = "restored", display = DisplayControl.Read(), brightness = ScreenBrightness.Read() });
        Console.WriteLine(JsonSerializer.Serialize(displayResults, new JsonSerializerOptions { WriteIndented = true }));
    }
    return;
}

if (args.Contains("--read-lighting"))
{
    var controller = HardwareSession.ForThisMachine(); Console.WriteLine(JsonSerializer.Serialize(controller.ProbeLighting())); return;
}

if (args.Contains("--read-display"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { display = DisplayControl.Read(), brightness = ScreenBrightness.Read() }, new JsonSerializerOptions { WriteIndented = true })); return;
}
if (args.Contains("--live-lighting-roundtrip"))
{
    var controller = HardwareSession.ForThisMachine(); controller.Takeover();
    var before = controller.ProbeLighting(); bool changed = false;
    var records = new List<object> { new { phase = "baseline", before } };
    try { records.Add(controller.SetKeyboardLighting(0, 255, 220, 2, false)); changed = true; Thread.Sleep(1500); records.Add(new { phase = "readback", state = controller.ProbeLighting() }); }
    finally
    {
        try { if (changed) records.Add(controller.UndoKeyboardLighting()); }
        catch (Exception error) { records.Add(new { restoreError = error.Message }); Environment.ExitCode = 1; }
        finally { Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true })); }
    }
    return;
}

if (args.Contains("--read-control"))
{
    var session = HardwareSession.ForThisMachine();
    Console.WriteLine(JsonSerializer.Serialize(new { control = session.Takeover(), telemetry = EcReader.Read(DeviceIdentity.Read()) }, new JsonSerializerOptions { WriteIndented = true }));
    session.Release(); return;
}

if (args.Contains("--live-control-roundtrip"))
{
    var session = HardwareSession.ForThisMachine();
    var baseline = session.Takeover()??throw new IOException("This live check requires classic EC state.");
    var records = new List<object> { new { phase = "baseline", state = baseline, fans = EcReader.Read(DeviceIdentity.Read()) } };
    bool batteryChanged = false, fanChanged = false;
    try
    {
        records.Add(session.SetFanBoost(true)); fanChanged = !baseline.FanBoost;
        Thread.Sleep(4000);
        records.Add(new { phase = "fan-boost-after-4s", state = session.State, fans = EcReader.Read(DeviceIdentity.Read()) });
        records.Add(session.SetFanBoost(baseline.FanBoost)); fanChanged = false;
        records.Add(session.SetBatteryPreset(baseline.Upper == 80 ? 60 : 80)); batteryChanged = true;
        Thread.Sleep(4000);
        // Takeover is intentionally not called here: undo must reject external changes.
        records.Add(session.UndoBattery()); batteryChanged = false;
        records.Add(new { phase = "restored", state = session.State, fans = EcReader.Read(DeviceIdentity.Read()) });
    }
    finally
    {
        try { if (batteryChanged) records.Add(session.UndoBattery()); }
        finally
        {
            if (fanChanged) records.Add(session.SetFanBoost(baseline.FanBoost));
            session.Release();
            Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    return;
}

var results = new List<object>();
void Check(string name, Action test)
{
    try { test(); results.Add(new { name, passed = true }); }
    catch (Exception e) { results.Add(new { name, passed = false, error = e.Message }); Environment.ExitCode = 1; }
}
void Assert(bool condition, string detail) { if (!condition) throw new Exception(detail); }
var device = new DeviceIdentity("MECHREVO", "JiguangX Series GM6IQ0C", "GM6IQ0C", "", []);
Check("GPU undo clears restart requirement and survives app reload",()=>
{
    var state=new GpuBootState(100,3,3);
    Assert(!state.Pending&&state.Observe(2).Pending,"Initial/changed mode mismatch");
    state=JsonSerializer.Deserialize<GpuBootState>(JsonSerializer.Serialize(state.Observe(2)))!;
    Assert(state.Pending&&!state.Observe(3).Pending&&state.InitialMode==3,"Undo or reload lost boot mode");
    Assert(!state.IsCurrentBoot(110)&&state.IsCurrentBoot(103),"Old boot accepted");
    Assert(new GpuBootState(100,0,3,true).Pending,"Unknown legacy baseline pretended safe");
});
Check("GPU migration requires a complete continuous verified audit chain",()=>
{
    string Intent(int before,int target)=>JsonSerializer.Serialize(new{at=DateTimeOffset.FromUnixTimeSeconds(200),operation="gpu-next-boot-intent",before=new{Mode=before},target});
    string Confirm(int before,int after)=>JsonSerializer.Serialize(new{at=DateTimeOffset.FromUnixTimeSeconds(201),operation="gpu-next-boot-confirmed",receipt=new{Before=new{Mode=before},After=new{Mode=after},Changed=true}});
    string[] chain=[Intent(3,2),Confirm(3,2),Intent(2,3),Confirm(2,3)];
    Assert(GpuBootStateStore.RecoverAudit(chain,3,100)==3,"Roundtrip baseline not recovered");
    Assert(GpuBootStateStore.RecoverAudit(chain[..3],2,100)==0,"Unconfirmed intent accepted");
    Assert(GpuBootStateStore.RecoverAudit(chain,2,100)==0,"External firmware change ignored");
    Assert(GpuBootStateStore.RecoverAudit(chain,3,300)==0,"Previous boot audit accepted");
    Assert(GpuBootStateStore.RecoverAudit([Intent(3,2),Confirm(1,2)],2,100)==0,"Broken chain accepted");
});
Check("Hot switch probes distinguish unsupported from unknown and restrict their meaning",()=>
{
    Assert(GpuRuntimeCapabilities.Decode(0x55)==true&&GpuRuntimeCapabilities.Decode(0xAA)==false&&GpuRuntimeCapabilities.Decode(null)==null&&GpuRuntimeCapabilities.Decode(0)==null,"Firmware replies misinterpreted");
    Assert(new GpuRuntimeCapabilities(true,false,85,170,null).Description(3).Contains("仍需重启"),"ACPI support incorrectly advertised as live MUX support");
});
Check("Elevated startup uses a current-user logon task with an exact executable",()=>
{
    string path=@"C:\Review & Test\OpenRevo.Native.exe",sid="S-1-5-21-123";
    string xml=StartupRegistration.BuildTaskXml(path,sid);
    Assert(StartupRegistration.MatchesTask(xml,path,sid),"Task did not roundtrip XML escaping");
    Assert(!StartupRegistration.MatchesTask(xml,@"C:\Other\OpenRevo.Native.exe",sid)&&!StartupRegistration.MatchesTask(xml,path,sid+"-1"),"Other installation/user accepted");
    Assert(!StartupRegistration.MatchesTask(xml.Replace("HighestAvailable","LeastPrivilege"),path,sid),"Non-elevated task accepted");
});
Check("Installer startup migration preserves only the exact prior enabled choice",()=>
{
    string path=@"C:\Users\Owner\Programs\OpenRevoNative\OpenRevo.Native.exe";
    Assert(StartupRegistration.MatchesLegacyCommand($"\"{path}\" --startup",path),"Own legacy startup not recognized");
    Assert(!StartupRegistration.MatchesLegacyCommand(null,path)&&!StartupRegistration.MatchesLegacyCommand("",path),"Disabled startup opted in");
    Assert(!StartupRegistration.MatchesLegacyCommand("\"C:\\Review\\OpenRevo.Native.exe\" --startup",path),"Other preview migrated");
    Assert(!StartupRegistration.MatchesLegacyCommand($"\"{path}\" --startup --extra",path),"Unrecognized arguments accepted");
});
var ecTestAdmission=CoreCompatibility.Unknown("") with{Supported=true,Ready=true,ClassicEc=true,Driver="Tongfang-ACPI",Project=12};
Check("Unknown Core platform performs zero driver opens", () =>
{
    int opens = 0; var result = EcReader.Read(device with { Board = "UNKNOWN" }, () => { opens++; throw new Exception(); },CoreCompatibility.Unknown("通道不可用"));
    Assert(opens == 0 && result.CpuFan == null, "Model gate failed");
});
Check("Unavailable Core driver performs zero driver opens", () =>
{
    int opens = 0; EcReader.Read(device with { Manufacturer = "OTHER" }, () => { opens++; throw new Exception(); },CoreCompatibility.Unknown("通道不可用"));
    Assert(opens == 0, "Vendor gate failed");
});
Check("Failed identity stops all remaining reads", () =>
{
    var transport = new FakeEc(new()); var result = EcReader.Read(device, () => transport,ecTestAdmission);
    Assert(transport.Reads == 1 && transport.Disposed && result.CpuFan == null, "Did not fail closed");
});
Check("Failed fan read remains unknown; never substituted with zero", () =>
{
    var transport = new FakeEc(new() { [Vault.ProjectId] = 12, [Vault.BatteryUpper] = 80, [Vault.FanClamp] = 0 });
    var result = EcReader.Read(device, () => transport,ecTestAdmission);
    Assert(result.CpuFan == null && result.GpuFan == null && result.BatteryLimit == 80 && result.FanBoost == false, "Missing readings invented");
});
Check("Valid zero RPM distinguished from read failure", () =>
{
    var transport = new FakeEc(new() { [Vault.ProjectId] = 12, [Vault.CpuFanHigh] = 0, [Vault.CpuFanLow] = 0 });
    Assert(EcReader.Read(device, () => transport,ecTestAdmission).CpuFan == 0, "Zero RPM lost");
});
Check("Implausible readings rejected", () =>
{
    var transport = new FakeEc(new() { [Vault.ProjectId] = 12, [Vault.CpuFanHigh] = 255, [Vault.CpuFanLow] = 255, [Vault.BatteryUpper] = 255 });
    var result = EcReader.Read(device, () => transport,ecTestAdmission);
    Assert(result.CpuFan == null && result.BatteryLimit == null, "Invalid values accepted");
});
Check("Torn fan counter rejected", () =>
{
    var transport = new FakeEc(new() { [Vault.ProjectId] = 12, [Vault.CpuFanHigh] = 10, [Vault.CpuFanLow] = 255 }) { Tear = true };
    Assert(EcReader.Read(device, () => transport,ecTestAdmission).CpuFan == null, "Torn reading accepted");
});
Check("Driver access denied yields unavailable result", () =>
{
    var result = EcReader.Read(device, () => throw new System.ComponentModel.Win32Exception(5),ecTestAdmission);
    Assert(result.CpuFan == null && result.Status.Contains("5"), "Driver error hidden");
});
Check("Power apply and undo require successful readback", () =>
{
    var backend = new FakePower(); var session = new PowerChangeSession(backend);
    session.Apply(backend.A, backend.B); Assert(backend.Current == backend.B && session.CanUndo, "Apply failed");
    session.Undo(); Assert(backend.Current == backend.A && !session.CanUndo && backend.Writes == 2, "Undo failed");
});
Check("Stale confirmation cannot change power plan", () =>
{
    var backend = new FakePower(); var session = new PowerChangeSession(backend); backend.Current = backend.B;
    try { session.Apply(backend.A, backend.B); throw new Exception("Accepted stale state"); } catch (InvalidOperationException) { }
    Assert(backend.Writes == 0, "Stale operation wrote");
});
Check("Unknown power plan rejected before writing", () =>
{
    var backend = new FakePower(); var session = new PowerChangeSession(backend);
    try { session.Apply(backend.A, Guid.NewGuid()); throw new Exception("Accepted unknown plan"); } catch (InvalidOperationException) { }
    Assert(backend.Writes == 0, "Unknown plan wrote");
});
Check("Undo does not overwrite an external change", () =>
{
    var backend = new FakePower(); var session = new PowerChangeSession(backend); session.Apply(backend.A, backend.B); backend.Current = backend.A;
    try { session.Undo(); throw new Exception("Overwrote external state"); } catch (InvalidOperationException) { }
    Assert(backend.Writes == 1, "Undo touched external state");
});
Check("Readback mismatch is reported as failure", () =>
{
    var backend = new FakePower { IgnoreWrites = true }; var session = new PowerChangeSession(backend);
    try { session.Apply(backend.A, backend.B); throw new Exception("Reported false success"); } catch (InvalidOperationException) { }
});
Check("Windows read-only integration: identity, power plans, telemetry", () =>
{
    var identity = DeviceIdentity.Read(); var plans = new WindowsPowerPlans();
    Assert(!string.IsNullOrWhiteSpace(identity.Product), "No identity");
    Assert(plans.List().Any(p => p.Id == plans.Active()), "Active plan not enumerated");
    var telemetry = new Telemetry(); var first = telemetry.Read(); Thread.Sleep(150); var next = telemetry.Read();
    Assert(first.CpuPercent == null && next.CpuPercent is >= 0 and <= 100, "Invalid CPU sample");
    Assert(next.TotalMemoryGiB > 0 && next.WorkingSetMiB > 0, "Invalid memory sample");
});
Check("Preferences reject corrupt, oversized and invalid input", () =>
{
    string folder = Path.Combine(Environment.CurrentDirectory, "work", "checks", "OpenRevoNative-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    string file = Path.Combine(folder, "preferences.json");
    try
    {
        foreach (var content in new[] { "{", "{\"Theme\":99,\"SampleSeconds\":1}", new string('x', 5000) })
        {
            File.WriteAllText(file, content);
            Assert(AppData.Load(folder) == new AppPreferences(), "Unsafe preferences accepted");
        }
        AppData.Save(new(2, 10), folder);
        Assert(AppData.Load(folder) == new AppPreferences(2, 10), "Preferences not persisted");
        Assert(Directory.GetFiles(folder).Length == 1, "Temporary file leaked");
    }
    finally { if (File.Exists(file)) File.Delete(file); Directory.Delete(folder); }
});
Check("Hardware takeover is read-only and conflict blocks all driver opens", () =>
{
    int opens = 0;
    var blocked = new HardwareSession(() => { opens++; return new FakeControl(); }, () => "conflict", _ => { });
    try { blocked.Takeover(); throw new Exception("Conflict ignored"); } catch (InvalidOperationException) { }
    Assert(opens == 0, "Conflict opened driver");
    var fake = new FakeControl(); var session = new HardwareSession(() => fake, () => null, _ => { });
    session.Takeover(); Assert(fake.Writes == 0 && session.Active, "Takeover wrote to hardware");
});
Check("Fan boost preserves all other bits and rejects stale hardware state", () =>
{
    var fake = new FakeControl(); fake.Values[Vault.FanClamp] = 0xA0;
    var session = new HardwareSession(() => fake, () => null, _ => { }); session.Takeover();
    session.SetFanBoost(true); Assert(fake.Values[Vault.FanClamp] == 0xE0, "Unrelated clamp bits destroyed");
    session.SetFanBoost(false); Assert(fake.Values[Vault.FanClamp] == 0xA0, "Failed to restore boost bit");
    fake.Values[Vault.FanClamp] = 0x10; int writes = fake.Writes;
    try { session.SetFanBoost(true); throw new Exception("External change overwritten"); } catch (InvalidOperationException) { }
    Assert(fake.Writes == writes, "Stale state wrote");
});
Check("Battery preset preserves touchpad and unrelated bits; undo restores exact thresholds", () =>
{
    var fake = new FakeControl(); var session = new HardwareSession(() => fake, () => null, _ => { });
    var baseline = session.Takeover()??throw new IOException("This live check requires classic EC state."); session.SetBatteryPreset(80);
    Assert(fake.Values[Vault.BatteryUpper] == 80 && fake.Values[Vault.BatteryLower] == 75 && fake.Values[Vault.BatteryMode] == 25, "Preset corrupted hardware bits");
    session.UndoBattery(); Assert(session.State == baseline, "Undo did not restore exact snapshot");
    Assert(fake.WrittenAddresses.All(a => a == Vault.BatteryUpper || a == Vault.BatteryLower || a == Vault.BatteryMode), "Battery path touched platform or fan registers");
});
Check("Invalid battery values and unknown EC project never write", () =>
{
    var fake = new FakeControl(); var session = new HardwareSession(() => fake, () => null, _ => { }); session.Takeover();
    try { session.SetBatteryPreset(150); throw new Exception("Invalid value accepted"); } catch (ArgumentOutOfRangeException) { }
    fake.Values[Vault.ProjectId] = 24;
    try { session.SetFanBoost(true); throw new Exception("Wrong project accepted"); } catch (InvalidOperationException) { }
    Assert(fake.Writes == 0, "Invalid operation wrote");
});
Check("Partial battery write failure stops remaining writes and requires new takeover", () =>
{
    var fake = new FakeControl { FailAt = 2 }; var session = new HardwareSession(() => fake, () => null, _ => { }); session.Takeover();
    try { session.SetBatteryPreset(80); throw new Exception("Write error ignored"); } catch (IOException) { }
    Assert(fake.Writes == 2 && !session.Active, "Did not stop on partial failure");
    try { session.SetFanBoost(true); throw new Exception("Faulted session wrote"); } catch (InvalidOperationException) { }
    Assert(fake.Writes == 2, "Faulted session continued");
});
Check("Failed readback and unavailable audit log never report success", () =>
{
    var fake = new FakeControl { IgnoreWrite = true }; var session = new HardwareSession(() => fake, () => null, _ => { }); session.Takeover();
    try { session.SetFanBoost(true); throw new Exception("Readback mismatch ignored"); } catch (IOException) { }
    Assert(!session.Active, "Readback mismatch left session enabled");
    fake = new FakeControl(); int logs = 0; session = new HardwareSession(() => fake, () => null, _ => { if (++logs > 1) throw new IOException("disk full"); }); session.Takeover();
    try { session.SetFanBoost(true); throw new Exception("Missing audit accepted"); } catch (IOException) { }
    Assert(fake.Writes == 0, "Wrote without intent log");
});
Check("Battery support is not inferred from corrupt thresholds or unknown mode", () =>
{
    var fake = new FakeControl(); fake.Values[Vault.Support2] = 0; fake.Values[Vault.BatteryMode] = 0;
    var session = new HardwareSession(() => fake, () => null, _ => { }); session.Takeover();
    try { session.SetBatteryPreset(80); throw new Exception("Unknown contract accepted"); } catch (InvalidOperationException) { }
    Assert(fake.Writes == 0, "Unknown battery capability wrote");
});
Check("Reducing charging limit never creates inverted threshold pair", () =>
{
    var fake = new FakeControl { RejectInvertedThresholds = true };
    fake.Values[Vault.BatteryUpper] = 100; fake.Values[Vault.BatteryLower] = 95; fake.Values[Vault.BatteryMode] = 9;
    var session = new HardwareSession(() => fake, () => null, _ => { }); session.Takeover(); session.SetBatteryPreset(60);
    Assert(fake.Values[Vault.BatteryLower] == 55 && fake.Values[Vault.BatteryUpper] == 60, "Lowering thresholds failed");
});
Check("Conflict appearing during operation stops remaining hardware writes", () =>
{
    var fake = new FakeControl(); var session = new HardwareSession(() => fake, () => fake.Writes > 0 ? "new OEM process" : null, _ => { }); session.Takeover();
    try { session.SetBatteryPreset(80); throw new Exception("Conflict ignored"); } catch (IOException) { }
    Assert(fake.Writes == 1 && !session.Active, "Did not stop mid-operation conflict");
});
Check("Undo refuses externally changed charging state", () =>
{
    var fake = new FakeControl(); var session = new HardwareSession(() => fake, () => null, _ => { }); session.Takeover(); session.SetBatteryPreset(80);
    fake.Values[Vault.BatteryUpper] = 90; int writes = fake.Writes;
    try { session.UndoBattery(); throw new Exception("Undo overwrote external state"); } catch (InvalidOperationException) { }
    Assert(fake.Writes == writes, "Undo wrote despite external change");
});
Check("Single-zone RGB preserves unrelated trigger/backlight bits and restores exact state", () =>
{
    var fake = new FakeControl(); var controller = new HardwareSession(() => fake, () => null, _ => { }); controller.Takeover();
    var before = controller.ProbeLighting(); controller.SetKeyboardLighting(0, 255, 220, 2, false);
    var state = controller.ProbeLighting();
    Assert(state.Red == 0 && state.Green == 50 && state.Blue == 43, "RGB scaling mismatch");
    Assert((state.Trigger & 0x5F) == (before.Trigger & 0x5F) && (state.Backlight & 13) == (before.Backlight & 13), "Unrelated bits changed");
    controller.UndoKeyboardLighting(); Assert(controller.ProbeLighting() == before, "Lighting restore failed");
});
Check("Unsupported lighting brightness and fan-isolation conflict cannot write", () =>
{
    var fake = new FakeControl(); var controller = new HardwareSession(() => fake, () => null, _ => { }); controller.Takeover();
    try { controller.SetKeyboardLighting(0, 255, 0, 8, false); throw new Exception("Invalid brightness accepted"); } catch (ArgumentOutOfRangeException) { }
    fake.Values[Vault.FanIsolated] = 1;
    try { controller.SetKeyboardLighting(0, 255, 0, 2, false); throw new Exception("Fan conflict ignored"); } catch (InvalidOperationException) { }
    Assert(fake.Writes == 0, "Invalid lighting wrote");
});
Check("Lighting undo tolerates only the two firmware self-clearing command bits", () =>
{
    var fake = new FakeControl { ClearLightingCommands = true }; fake.Values[Vault.Trigger] = 128; fake.Values[Vault.Backlight] = 128;
    var controller = new HardwareSession(() => fake, () => null, _ => { }); controller.Takeover();
    var before = controller.ProbeLighting(); controller.SetKeyboardLighting(0, 255, 220, 2, false); controller.UndoKeyboardLighting();
    Assert(controller.ProbeLighting() == before, "Self-clearing command broke restore");
});
Check("Internal panel technology excludes external and remote displays", () =>
{
    Assert(DisplayTopology.IsInternalTechnology(11),"eDP rejected");
    Assert(DisplayTopology.IsVirtual("GameViewer Virtual Display Adapter"),"Virtual adapter accepted");
    Assert(!DisplayTopology.IsInternalTechnology(5),"HDMI accepted as internal");
});
Check("Performance rejects invalid modes, battery and unconfirmed turbo before writes",()=>
{
    var fake=new FakeControl(); fake.SeedPerformance(); var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>false); c.Takeover();
    try {c.SetPerformanceMode(4,true);throw new Exception("Invalid mode accepted");} catch(ArgumentOutOfRangeException){}
    try {c.SetPerformanceMode(3,true);throw new Exception("Battery turbo accepted");} catch(InvalidOperationException){}
    Assert(fake.Writes==0,"Power gate wrote hardware");
});
Check("Factory modes preserve unrelated mode, lighting and battery bits and undo full tables",()=>
{
    var fake=new FakeControl{ClearLightingCommands=true};fake.SeedPerformance();fake.Values[Vault.Trigger]=144;fake.Values[Vault.ModeIndex]=161;
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true);c.Takeover();var before=c.ProbePerformance();
    foreach(int mode in new[]{1,2,3})
    {
        var receipt=c.SetPerformanceMode(mode,true);
        Assert(receipt.After.Mode==mode,"Mode readback incorrect");
        Assert((receipt.After.Fans.ModeIndex & 225)==161 && (receipt.After.Trigger & 144)==144,"Unrelated bits destroyed");
        c.UndoPerformance();var after=c.ProbePerformance();
        Assert(after.Fans.CpuDuty.SequenceEqual(before.Fans.CpuDuty) && after.Clamp==before.Clamp,"Undo did not restore fan table/clamp");
    }
    Assert(fake.Values[Vault.BatteryUpper]==60 && fake.Values[Vault.BatteryMode]==41,"Performance changed battery");
});
Check("Partial performance failure stops tables and attempts strong cooling",()=>
{
    var fake=new FakeControl();fake.SeedPerformance();var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true);c.Takeover();fake.FailAt=10;
    try{c.SetPerformanceMode(2);throw new Exception("Write failure ignored");}catch(IOException){}
    Assert(!c.Active && fake.WrittenAddresses.Last()==Vault.FanClamp && (fake.Values[Vault.FanClamp]&Vault.FanBoostMask)!=0,"Failure did not preserve cooling");
});
Check("Unrecognized fan tables cannot be overwritten by performance presets",()=>
{
    var fake=new FakeControl();fake.SeedPerformance();fake.Values[Vault.CpuDutyBase]=9;
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true);c.Takeover();
    try{c.SetPerformanceMode(1);throw new Exception("Unknown table overwritten");}catch(InvalidOperationException){}
    Assert(fake.Writes==0,"Unknown fan contract wrote");
});
Check("Automatic refresh restores chosen AC rate and never repeats settled writes",()=>
{
    var policy=new AutomaticRefresh();int current=165,writes=0;
    DisplayState Read()=>new(2560,1600,current,[60,165],"verified-internal");
    void Change(int before,int target){Assert(before==current,"Stale mode");current=target;writes++;}
    policy.Evaluate(true,true,Read,Change);policy.Evaluate(true,false,Read,Change);
    policy.Evaluate(true,false,Read,Change);Assert(current==60 && writes==1,"Low mode not settled");
    policy.Evaluate(true,true,Read,Change);Assert(current==165 && writes==2,"AC mode not restored");
});
Check("Automatic refresh skips inactive remote panel and handles disable restoration",()=>
{
    var policy=new AutomaticRefresh();bool active=false;int current=165,writes=0;
    DisplayState Read()=>new(2560,1600,current,[60,165],active?"verified-internal":"",Active:active);
    void Change(int before,int target){current=target;writes++;}
    policy.Evaluate(true,false,Read,Change);Assert(writes==0 && policy.Pending,"Changed inactive panel");
    active=true;policy.Evaluate(true,false,Read,Change);Assert(current==60,"Pending transition missed");
    policy.Evaluate(false,false,Read,Change);Assert(current==165 && writes==2,"Disable did not restore");
    policy.Evaluate(true,null,Read,Change);Assert(writes==2,"Unknown AC state wrote");
    policy.Evaluate(true,false,Read,Change);Assert(current==60,"Battery mode did not re-enter");
    active=false;policy.InvalidateTopology();policy.Evaluate(false,false,Read,Change);
    Assert(current==60 && policy.Pending,"Inactive disable was not deferred");
    active=true;policy.Evaluate(false,false,Read,Change);
    Assert(current==165 && !policy.Pending,"Deferred disable did not restore after reconnect");
});
Check("Keyboard capability distinguishes hardware and never guesses unknown as RGB",()=>
{
    Assert(KeyboardCapability.Detect(false,false,true).Kind=="SingleZone","Single-zone detection failed");
    Assert(KeyboardCapability.Detect(true,true,true).Kind=="PerKey","Physical priority wrong");
    Assert(KeyboardCapability.Detect(false,true,false).HasZones,"Four-zone missing");
    Assert(!KeyboardCapability.Detect(false,false,false,"White").HasColor,"White presented as RGB");
    Assert(KeyboardCapability.Detect(false,false,false).Kind=="Unknown","Unknown fabricated");
});
Check("Win-key trigger preserves other bits, requires readback and avoids duplicate writes",()=>
{
    var transport=new FakeControl{ConsumeWinLock=true};transport.Values[Vault.SwitchStatus]=128;transport.Values[Vault.CameraCutoff]=160;
    var session=new HardwareSession(()=>transport,()=>null,_=>{});session.Takeover();
    byte other=transport.Values[Vault.Trigger];
    Assert(session.SetWinKeyLocked(true).WinKeyLocked,"Lock not confirmed");
    Assert(transport.Values[Vault.Trigger]==other && transport.Values[Vault.SwitchStatus]==129,"Unrelated bits changed");
    int writes=transport.Writes;session.SetWinKeyLocked(true);Assert(transport.Writes==writes,"Duplicate trigger replayed");
    Assert(!session.SetWinKeyLocked(false).WinKeyLocked,"Unlock not confirmed");
});
Check("Win-key pending command or lost acknowledgement cannot report success",()=>
{
    var transport=new FakeControl();transport.Values[Vault.SwitchStatus]=0;transport.Values[Vault.CameraCutoff]=0;
    var session=new HardwareSession(()=>transport,()=>null,_=>{});session.Takeover();
    transport.Values[Vault.Trigger]|=Vault.WinLockMask;
    try{session.SetWinKeyLocked(true);throw new Exception("Pending command accepted");}catch(IOException){}
    Assert(transport.Writes==0,"Pending command rewritten");transport.Values[Vault.Trigger]&=254;
    try{session.SetWinKeyLocked(true);throw new Exception("Missing acknowledgement accepted");}catch(IOException){}
    Assert(!session.Active && transport.Writes==1,"Failed command did not stop control");
});
Check("Camera power changes only its bit and requires exact readback",()=>
{
    var transport=new FakeControl();transport.Values[Vault.SwitchStatus]=0;transport.Values[Vault.CameraCutoff]=165;
    var session=new HardwareSession(()=>transport,()=>null,_=>{});session.Takeover();
    Assert(!session.SetCameraPowered(false).CameraPowered && transport.Values[Vault.CameraCutoff]==173,"Cutoff changed unrelated bits");
    Assert(session.SetCameraPowered(true).CameraPowered && transport.Values[Vault.CameraCutoff]==165,"Power did not restore");
    transport.IgnoreWrite=true;
    try{session.SetCameraPowered(false);throw new Exception("Ignored write accepted");}catch(IOException){}
    Assert(!session.Active,"Failed camera readback left control active");
});
Check("Keep-awake request is idempotent, screen-off releases display only, exit releases all",()=>
{
    var request=new FakeSessionPower();using(var session=new SessionPower(request))
    {
        session.SetKeepAwake(true);session.SetKeepAwake(true);Assert(request.Active.SetEquals(new uint[]{0,1})&&request.Calls==2,"Duplicate power requests");
        session.ReleaseDisplay();Assert(session.Enabled&&!session.DisplayHeld&&request.Active.SetEquals(new uint[]{1}),"Screen-off cleared system request");
        session.SetKeepAwake(true);Assert(request.Active.Count==2,"Display request not resumed");
        session.SetKeepAwake(false);session.HoldSystemOnly();Assert(request.Active.SetEquals(new uint[]{1})&&session.Enabled&&!session.DisplayHeld,"System-only hold lit the display");
    }
    Assert(request.Disposed&&request.Active.Count==0,"Exit leaked power request");
});
Check("Keep-awake rolls back partial enable failure",()=>
{
    var request=new FakeSessionPower{FailDisplay=true};using var session=new SessionPower(request);
    try{session.SetKeepAwake(true);throw new Exception("Failed request accepted");}catch(IOException){}
    Assert(!session.Enabled&&!session.DisplayHeld&&request.Active.Count==0,"Failed enable left system awake");
});
Check("Core camera adapter validates exact bit preservation, deduplicates and stops failed readback",()=>
{
    var transport=new FakeControl();transport.Values[Vault.SwitchStatus]=0;transport.Values[Vault.CameraCutoff]=165;
    var core=new FakeCameraPowerCore(transport);var session=new HardwareSession(()=>transport,()=>null,_=>{});session.Takeover();
    Assert(!session.SetCameraPoweredViaCore(core,false).CameraPowered && transport.Values[Vault.CameraCutoff]==173,"Core adapter clobbered another bit");
    session.SetCameraPoweredViaCore(core,false);Assert(core.Calls==1,"Duplicate core mutation");
    Assert(session.SetCameraPoweredViaCore(core,true).CameraPowered && transport.Values[Vault.CameraCutoff]==165,"Core adapter failed to restore");
    core.Ignore=true;
    try{session.SetCameraPoweredViaCore(core,false);throw new Exception("Failed core acknowledgement accepted");}catch(IOException){}
    Assert(!session.Active,"Failed core mutation did not stop hardware control");
});
Check("Camera privacy recovery skips device commands for an already-started camera",()=>
{
    Assert(!CameraControl.NeedsDeviceTransition(true,true),"Privacy-only repair re-enabled an active device");
    Assert(CameraControl.NeedsDeviceTransition(false,true),"Explicitly disabled device was not repaired");
    Assert(!CameraControl.NeedsDeviceTransition(null,true),"Unknown device state caused a guessed command");
});
Check("GPU modes reject stale, unsupported and unreadable state without staging",()=>
{
    var transport=new FakeControl();var core=new FakeGpuModeCore();var session=new HardwareSession(()=>transport,()=>null,_=>{});session.Takeover();
    foreach(var(target,expected)in new[]{(1,2),(3,3),(9,0)})
    {
        try{session.ChangeGpuMode(core,target,expected);throw new Exception("Unsafe GPU mode accepted");}catch(InvalidOperationException){}catch(ArgumentOutOfRangeException){}
    }
    core.State=core.State with{HardwareRead=false};
    try{session.ChangeGpuMode(core,3);throw new Exception("Unknown GPU state accepted");}catch(InvalidOperationException){}
    Assert(core.Stages==0&&transport.Writes==0,"Rejected mode changed hardware");
});
Check("GPU same-mode deduplicates, supported mode stages and requires final readback",()=>
{
    var transport=new FakeControl();var core=new FakeGpuModeCore();var session=new HardwareSession(()=>transport,()=>null,_=>{});session.Takeover();
    Assert(!session.ChangeGpuMode(core,2,2).Changed&&core.Stages==0,"Same-mode firmware was rewritten");
    Assert(session.ChangeGpuMode(core,3,2).After.Mode==3&&core.Stages==1,"Supported mode not confirmed");
    core.Ignore=true;
    try{session.ChangeGpuMode(core,2,3);throw new Exception("Wrong GPU readback accepted");}catch(IOException){}
    Assert(!session.Active,"Failed readback left writes enabled");
});
Check("GPU failed audit or controller conflict prevents native staging",()=>
{
    var core=new FakeGpuModeCore();var transport=new FakeControl();bool conflict=false,denyAudit=false;
    var session=new HardwareSession(()=>transport,()=>conflict?"conflict":null,_=>{if(denyAudit)throw new IOException("audit failed");});session.Takeover();
    denyAudit=true;try{session.ChangeGpuMode(core,3);throw new Exception("Failed audit accepted");}catch(IOException){}
    denyAudit=false;conflict=true;try{session.ChangeGpuMode(core,3);throw new Exception("Conflict accepted");}catch(InvalidOperationException){}
    Assert(core.Stages==0,"Audit or conflict allowed firmware staging");
});
Check("Tuning draft and stale baseline never mutate hardware",()=>
{
    var fake=new FakeControl();fake.SeedTuning();var gpu=new FakeTuningGpu();
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true,()=>"Intel i7-14650HX");c.Takeover();
    var state=c.ProbeTuning(gpu);var draft=state.CreateDraft() with{CustomFans=true};
    HardwareSession.ValidateTuning(state,draft);Assert(fake.Writes==0&&gpu.Writes==0,"Read/edit wrote hardware");
    fake.Values[Vault.CustomPl1]++;
    try{c.ApplyTuning(gpu,state,draft);throw new Exception("Stale baseline accepted");}catch(InvalidOperationException){}
    Assert(fake.Writes==0&&gpu.Writes==0,"Stale apply mutated hardware");
});
Check("Tuning rejects unsafe power, unknown clocks and descending curves before writes",()=>
{
    var fake=new FakeControl();fake.SeedTuning();var gpu=new FakeTuningGpu();
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true,()=>"Intel i7-14650HX");c.Takeover();var s=c.ProbeTuning(gpu);var p=s.CreateDraft();
    var descending=p.Copy();descending.CpuCurve[3]=new(55,20);
    foreach(var bad in new[]{p with{Pl1=p.Pl2+1},p with{Pl4=p.Pl2+19},p with{TemperatureC=105},p with{Offsets=new(251,0)},descending})
    {try{c.ApplyTuning(gpu,s,bad);throw new Exception("Unsafe profile accepted");}catch(ArgumentException){}}
    Assert(fake.Writes==0&&gpu.Writes==0,"Validation wrote hardware");
});
Check("Tuning curve encoding preserves zero standby, monotonic duty and thermal hysteresis",()=>
{
    var fake=new FakeControl();fake.SeedTuning();var gpu=new FakeTuningGpu();
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true,()=>"Intel i7-14650HX");c.Takeover();var s=c.ProbeTuning(gpu);var p=s.CreateDraft() with{CustomFans=true};
    var fans=HardwareSession.BuildTuningFans(s.Performance.Fans,p);
    Assert(fans.CpuDuty[0]==0&&fans.GpuDutyAndControl[0]==0&&fans.CpuDuty[15]==200&&fans.GpuDutyAndControl[15]==200,"Standby or high-temperature safety lost");
    Assert(fans.CpuDuty.Zip(fans.CpuDuty.Skip(1)).All(pair=>pair.First<=pair.Second),"Descending duty");
    Assert(Enumerable.Range(1,15).All(i=>fans.CpuDown[i]<fans.CpuUp[i]),"Thermal hysteresis inverted");
});
Check("Tuning applies and reads back CPU GPU and fan state, then exits to factory",()=>
{
    var fake=new FakeControl();fake.SeedTuning();fake.Values[Vault.ModeIndex]=161;fake.Values[Vault.Trigger]=144;fake.Values[Vault.FanIsolated]=2;
    var gpu=new FakeTuningGpu();var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true,()=>"Intel i7-14650HX");c.Takeover();var s=c.ProbeTuning(gpu);
    var p=s.CreateDraft() with{CustomFans=true,TemperatureEnabled=true,TemperatureC=90,GpuSuperPerformance=true,Offsets=new(100,200)};
    var receipt=c.ApplyTuning(gpu,s,p);
    Assert(receipt.After.Performance.Mode==4&&receipt.After.Registers.Tcc==138&&gpu.State.Offsets==new ClockOffsets(100,200),"Custom state not confirmed");
    Assert((fake.Values[Vault.ModeIndex]&225)==161&&(fake.Values[Vault.Trigger]&144)==144&&(fake.Values[Vault.FanIsolated]&127)==2,"Unrelated bits overwritten");
    Assert(fake.Values[Vault.BatteryUpper]==60&&fake.Values[Vault.BatteryMode]==41,"Tuning changed charging");
    var result=c.SetFactoryPerformance(gpu,2);Assert(result.After.Mode==2&&gpu.State.Offsets==new ClockOffsets(0,0),"Factory exit left custom mode or overclock");
});
Check("Unknown GPU capabilities preserve all GPU registers",()=>
{
    var fake=new FakeControl();fake.SeedTuning();var gpu=new FakeTuningGpu{State=new(GpuTuningCapabilities.Unknown,null)};
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true,()=>"Intel i7-14650HX");c.Takeover();var s=c.ProbeTuning(gpu);
    c.ApplyTuning(gpu,s,s.CreateDraft());
    Assert(!fake.WrittenAddresses.Any(a=>a==Vault.GpuDbControl||a==Vault.GpuDbFlag||a==Vault.GpuDbWatts||a==Vault.GpuTgpOffset)&&gpu.Writes==0,"Unknown GPU was modified");
});
Check("Tuning readback failure stops, clamps cooling and blocks further writes",()=>
{
    var fake=new FakeControl();fake.SeedTuning();var gpu=new FakeTuningGpu{Ignore=true};
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true,()=>"Intel i7-14650HX");c.Takeover();var s=c.ProbeTuning(gpu);
    try{c.ApplyTuning(gpu,s,s.CreateDraft() with{GpuSuperPerformance=true,Offsets=new(100,200)});throw new Exception("Clock failure accepted");}catch(IOException){}
    Assert(!c.Active&&(fake.Values[Vault.FanClamp]&Vault.FanBoostMask)!=0,"Failure released cooling or control lock");
    int writes=fake.Writes;try{c.ApplyTuning(gpu,s,s.CreateDraft());throw new Exception("Faulted session wrote");}catch(InvalidOperationException){}
    Assert(fake.Writes==writes,"Faulted session continued writing");
});
Check("Tuning power loss, audit failure, conflict and unknown CPU prevent writes",()=>
{
    foreach(string cause in new[]{"power","audit","conflict","cpu"})
    {
        var fake=new FakeControl();fake.SeedTuning();var gpu=new FakeTuningGpu();bool deny=false;
        var c=new HardwareSession(()=>fake,()=>deny&&cause=="conflict"?"conflict":null,_=>{if(deny&&cause=="audit")throw new IOException("disk full");},()=>cause!="power",()=>cause=="cpu"?"unknown":"Intel i7-14650HX");c.Takeover();
        try{var s=c.ProbeTuning(gpu);deny=true;c.ApplyTuning(gpu,s,s.CreateDraft());throw new Exception("Blocked tuning accepted: "+cause);}catch(Exception e)when(e is IOException or InvalidOperationException){}
        Assert(fake.Writes==0&&gpu.Writes==0,"Blocked tuning wrote: "+cause);
    }
});
Check("GPU power envelopes distinguish configurable and fixed base with unknown rejection",()=>
{
    var variable=GpuTuningCapabilities.FromLimits("NVIDIA GeForce RTX 4070 Laptop GPU",60,140);
    Assert(variable.ConfigurableBase&&variable.FloorWatts==60&&variable.BaseMaxWatts==115&&variable.BoostMaxWatts==25,"Configurable envelope wrong");
    var fixedBase=GpuTuningCapabilities.FromLimits("NVIDIA GeForce RTX 4080 Laptop GPU",150,175);
    Assert(fixedBase.Supported&&!fixedBase.ConfigurableBase&&fixedBase.BaseMaxWatts==150,"Fixed base interpreted as configurable");
    Assert(!GpuTuningCapabilities.FromLimits("NVIDIA GTX 1060",60,120).Supported&&!GpuTuningCapabilities.FromLimits("NVIDIA RTX 4070",0,140).Supported,"Unknown capabilities guessed");
});
Check("Tuning power loss mid-table prevents custom activation and preserves cooling",()=>
{
    var fake=new FakeControl();fake.SeedTuning();var gpu=new FakeTuningGpu();
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>fake.Writes<90,()=>"Intel i7-14650HX");c.Takeover();var s=c.ProbeTuning(gpu);
    try{c.ApplyTuning(gpu,s,s.CreateDraft() with{CustomFans=true});throw new Exception("Lost power was ignored");}catch(IOException){}
    Assert(!c.Active&&fake.Values[Vault.CustomMode]==64&&(fake.Values[Vault.FanClamp]&Vault.FanBoostMask)!=0&&gpu.Writes==0,"Power loss activated custom mode or lost cooling");
});
Check("Fixed-base GPU tuning preserves cTGP register and uses physical clock bounds",()=>
{
    var fake=new FakeControl();fake.SeedTuning();fake.Values[Vault.GpuTgpOffset]=17;
    var gpu=new FakeTuningGpu{State=new(new("NVIDIA RTX 4070",true,false,115,115,25,100,500),new(0,0))};
    var c=new HardwareSession(()=>fake,()=>null,_=>{},()=>true,()=>"Intel i7-14650HX");c.Takeover();var s=c.ProbeTuning(gpu);var p=s.CreateDraft();
    try{c.ApplyTuning(gpu,s,p with{GpuSuperPerformance=true,Offsets=new(101,0)});throw new Exception("Driver limit exceeded");}catch(ArgumentException){}
    Assert(fake.Writes==0,"Invalid clock target touched EC");
    c.ApplyTuning(gpu,s,p with{GpuSuperPerformance=true,Offsets=new(100,500)});
    Assert(fake.Values[Vault.GpuTgpOffset]==17&&!fake.WrittenAddresses.Contains(Vault.GpuTgpOffset),"Fixed-base card cTGP register overwritten");
});
Check("Dedicated VRAM bytes map to GiB and percent, not physical RAM or transfer clock",()=>
{
    var gpu=new OverviewGpu(42,450,601,36,8589934592,3221225472,13.2);
    Assert(gpu.TotalGiB==8&&gpu.UsedGiB==3&&gpu.MemoryPercent==37.5&&gpu.MemoryMhz==601,"VRAM unit/domain mismatch");
    Assert(!(gpu with{MemoryTotalBytes=0}).HasMemory&&!(gpu with{MemoryUsedBytes=17179869184}).HasMemory,"Invalid VRAM accepted");
});
Check("Fn Core switch preserves all other firmware bits and control state",()=>
{
    var ec=new FakeControl();ec.Values[Vault.FnLock]=0x81;var core=new FakeOverviewCore(ec);
    var session=new HardwareSession(()=>ec,()=>null,_=>{});session.Takeover();int battery=ec.Values[Vault.BatteryMode];
    session.SetOverviewSwitchViaCore(core,OverviewSwitch.FnLock,false,true);
    Assert(ec.Values[Vault.FnLock]==0x91&&ec.Values[Vault.BatteryMode]==battery&&core.Calls==1,"Fn write clobbered unrelated bits");
    session.SetOverviewSwitchViaCore(core,OverviewSwitch.FnLock,true,false);Assert(ec.Values[Vault.FnLock]==0x81,"Fn roundtrip failed");
});
Check("Touchpad Core switch preserves battery protection and next battery preset preserves touchpad",()=>
{
    var ec=new FakeControl();var core=new FakeOverviewCore(ec);var session=new HardwareSession(()=>ec,()=>null,_=>{});session.Takeover();
    session.SetOverviewSwitchViaCore(core,OverviewSwitch.Touchpad,true,false);
    Assert(ec.Values[Vault.BatteryMode]==33&&ec.Values[Vault.BatteryUpper]==60&&ec.Values[Vault.BatteryLower]==55,"Touchpad changed battery limits");
    session.SetBatteryPreset(80);Assert((ec.Values[Vault.BatteryMode]&Vault.TouchpadMask)==0&&ec.Values[Vault.BatteryUpper]==80,"Battery preset changed touchpad");
    session.SetOverviewSwitchViaCore(core,OverviewSwitch.Touchpad,false,true);session.UndoBattery();Assert(ec.Values[Vault.BatteryUpper]==60&&(ec.Values[Vault.BatteryMode]&Vault.TouchpadMask)!=0,"Battery undo overwrote touchpad state");
});
Check("Stale extra-switch state and environmental guard prevent a Core write",()=>
{
    var ec=new FakeControl();ec.Values[Vault.FnLock]=1;var core=new FakeOverviewCore(ec);var session=new HardwareSession(()=>ec,()=>null,_=>{});session.Takeover();
    bool failed=false;try{session.SetOverviewSwitchViaCore(core,OverviewSwitch.FnLock,true,false);}catch(IOException){failed=true;}
    Assert(failed&&core.Calls==0,"Stale Fn state was written");
});
Check("Extra-switch unrelated register changes fail closed",()=>
{
    var ec=new FakeControl();ec.Values[Vault.FnLock]=1;var core=new FakeOverviewCore(ec){CorruptBattery=true};var session=new HardwareSession(()=>ec,()=>null,_=>{});session.Takeover();
    bool failed=false;try{session.SetOverviewSwitchViaCore(core,OverviewSwitch.FnLock,false,true);}catch(IOException){failed=true;}
    Assert(failed&&!session.Active,"Unrelated EC change did not stop further writes");
});
Check("Sleep policies distinguish missing, mixed AC/DC and DC-only support",()=>
{
    var p=DesktopPower.Policies[0];
    Assert(!new PowerPolicyValue(p,null,0).Supported&&new PowerPolicyValue(p,null,0).Enabled==null,"Missing policy became a false off state");
    Assert(new PowerPolicyValue(p,1,0).Supported&&new PowerPolicyValue(p,1,0).Enabled==null,"Mixed AC/DC became confirmed off");
    var dc=DesktopPower.Policies.Single(p=>p.DcOnly);Assert(new PowerPolicyValue(dc,null,1800).Enabled==true,"DC-only timeout required AC support");
});
Check("New power-option Core path preserves shared USB and recovery register bits",()=>
{
    var ec=new FakeControl();ec.Values[Vault.Trigger]=0xA5;ec.Values[Vault.AcRecovery]=0xE3;
    var core=new FakeDesktopPowerCore(ec);var s=new HardwareSession(()=>ec,()=>null,_=>{});s.Takeover();
    s.SetDesktopPowerOption(core,"usb",true);s.SetDesktopPowerOption(core,"recovery",true);
    Assert(ec.Values[Vault.Trigger]==0xB5&&ec.Values[Vault.AcRecovery]==0xEB&&ec.Values[Vault.BatteryMode]==41,"Shared bits changed");
    s.SetDesktopPowerOption(core,"usb",false);s.SetDesktopPowerOption(core,"recovery",false);
    Assert(ec.Values[Vault.Trigger]==0xA5&&ec.Values[Vault.AcRecovery]==0xE3,"Own bits did not restore");
});
Check("Power-option mismatch stops the session and desktop service guards reject conflicts",()=>
{
    var ec=new FakeControl();ec.Values[Vault.AcRecovery]=0;var core=new FakeDesktopPowerCore(ec){Ignore=true};
    var s=new HardwareSession(()=>ec,()=>null,_=>{});s.Takeover();
    try{s.SetDesktopPowerOption(core,"recovery",true);throw new Exception("False success");}catch(IOException){}
    Assert(!s.Active,"Mismatch did not block later writes");bool called=false;
    var blocked=new HardwareSession(()=>ec,()=>"other controller",_=>{});
    try{blocked.ApplyDesktopService("sleep",()=>called=true);throw new Exception("Conflict accepted");}catch(InvalidOperationException){}
    Assert(!called,"Conflict reached system mutation callback");
});
Check("Sleep auto-office restores its exact prior mode and rejects later manual changes",()=>
{
    var ec=new FakeControl{ClearLightingCommands=true};ec.SeedPerformance();var s=new HardwareSession(()=>ec,()=>null,_=>{},()=>true);s.Takeover();
    s.SetPerformanceMode(2);var temporary=s.SetPerformanceMode(1);s.RestoreAutomaticOffice(temporary);
    Assert(s.ProbePerformance().Mode==2&&ec.Values[Vault.BatteryMode]==41,"Wake did not restore previous mode");
    temporary=s.SetPerformanceMode(1);s.SetPerformanceMode(3,true);int writes=ec.Writes;
    try{s.RestoreAutomaticOffice(temporary);throw new Exception("Manual change overwritten");}catch(InvalidOperationException){}
    Assert(ec.Writes==writes&&s.ProbePerformance().Mode==3,"Wake overwrote later manual mode");
});
Console.WriteLine(JsonSerializer.Serialize(new { allPassed = Environment.ExitCode == 0, tests = results }, new JsonSerializerOptions { WriteIndented = true }));

sealed class FakeOverviewCore(FakeControl ec):IOverviewSwitchCore
{
    public int Calls;public bool CorruptBattery;
    public OverviewSwitches ReadOverviewSwitches()=>new(true,true,(ec.Values[Vault.BatteryMode]&Vault.TouchpadMask)!=0,(ec.Values.GetValueOrDefault(Vault.FnLock)&Vault.FnLockMask)!=0);
    public void SetOverviewSwitch(OverviewSwitch key,bool expected,bool target)
    {
        Calls++;ushort address=key==OverviewSwitch.Touchpad?Vault.BatteryMode:Vault.FnLock;byte mask=key==OverviewSwitch.Touchpad?Vault.TouchpadMask:Vault.FnLockMask;
        ec.Values[address]=target?(byte)(ec.Values[address]|mask):(byte)(ec.Values[address]&~mask);if(CorruptBattery)ec.Values[Vault.BatteryUpper]++;
    }
}
sealed class FakeTuningGpu:ITuningGpuCore
{
    public GpuTuningState State=new(new("NVIDIA GeForce RTX 4070 Laptop GPU",true,true,60,115,25,250,1000),new(0,0));
    public int Writes;public bool Ignore;
    public GpuTuningState ReadGpuTuning()=>State;
    public void SetClockOffsets(ClockOffsets expected,ClockOffsets target){if(State.Offsets!=expected)throw new IOException("Stale offsets");Writes++;if(!Ignore)State=State with{Offsets=target};}
}

sealed class FakeGpuModeCore : IGpuModeCore
{
    public GpuModeState State=new(2,true,false,true,true);public int Stages;public bool Ignore;
    public GpuModeState ReadGpuMode()=>State;
    public bool StageGpuMode(int expected,int target){Stages++;if(State.Mode!=expected)throw new IOException("Stale GPU mode");if(!Ignore)State=State with{Mode=target};return true;}
}
sealed class FakeCameraPowerCore(FakeControl control) : ICameraPowerCore
{
    public int Calls;public bool Ignore;
    public void SetCameraPowered(bool enabled){Calls++;if(!Ignore){byte before=control.Values[Vault.CameraCutoff];control.Values[Vault.CameraCutoff]=enabled?(byte)(before&~Vault.CameraCutoffMask):(byte)(before|Vault.CameraCutoffMask);}}
}
sealed class FakeSessionPower : ISessionPowerRequest
{
    public HashSet<uint> Active=[];public int Calls;public bool Disposed,FailDisplay;
    public void Set(uint type,bool enabled){Calls++;if(FailDisplay&&type==0&&enabled)throw new IOException("Display request denied");if(enabled)Active.Add(type);else Active.Remove(type);}
    public void Dispose(){Disposed=true;Active.Clear();}
}
sealed class FakeControl : IHardwareTransport
{
    public void SeedTuning()
    {
        SeedPerformance();Values[Vault.CustomPl1]=75;Values[Vault.CustomPl2]=75;Values[Vault.CustomPl4]=125;Values[Vault.CustomTcc]=5;
        Values[Vault.TurboPl1]=90;Values[Vault.TurboPl2]=105;Values[Vault.TurboPl4]=150;Values[Vault.TurboTcc]=5;
        Values[Vault.OfficePl1]=45;Values[Vault.OfficePl2]=45;Values[Vault.OfficePl4]=100;Values[Vault.BalancePl1]=75;Values[Vault.BalancePl2]=75;Values[Vault.BalancePl4]=125;
        Values[Vault.GpuDbControl]=7;Values[Vault.GpuDbFlag]=255;Values[Vault.GpuDbWatts]=25;Values[Vault.GpuTgpOffset]=55;
    }
    public void SeedPerformance()
    {
        int[] up=[0,55,58,61,65,69,72,75,78,81,255,255,255,255,255,255];
        int[] down=[48,58,61,65,69,73,76,79,82,255,255,255,255,255,255,255];
        int[] duty=[0,60,70,80,100,110,120,130,150,160,160,160,160,160,160,160];
        foreach(var (start,table) in new[]{(Vault.CpuUpBase,up),(Vault.CpuDownBase,down),(Vault.CpuDutyBase,duty),(Vault.GpuUpBase,up),(Vault.GpuDownBase,down),(Vault.GpuDutyBase,duty)})
            for(int i=0;i<16;i++)Values[(ushort)(start+i)]=(byte)table[i];
        Values[Vault.FanLatch]=4;Values[Vault.ApControl]=129;Values[Vault.ModeIndex]=0;Values[Vault.SilentFlag]=0;Values[Vault.CustomMode]=64;Values[Vault.FanRamp]=131;
    }
    public readonly Dictionary<ushort, byte> Values = new() { [Vault.ProjectId] = Vault.VerifiedProject, [Vault.FanClamp] = 0,
        [Vault.BatteryUpper] = 60, [Vault.BatteryLower] = 55, [Vault.BatteryMode] = 41, [Vault.PlatformId] = 0,
        [Vault.Support1] = Vault.FanSupportMask, [Vault.Support2] = 148, [Vault.PowerStatus] = 0,
        [Vault.RgbRed] = 20, [Vault.RgbGreen] = 30, [Vault.RgbBlue] = 40, [Vault.Backlight] = 153, [Vault.Trigger] = 48, [Vault.FanIsolated] = 0 };
    public int Writes, FailAt; public bool IgnoreWrite, RejectInvertedThresholds, ClearLightingCommands,ConsumeWinLock;
    public readonly List<ushort> WrittenAddresses = [];
    public byte? Read(ushort address) => Values.TryGetValue(address, out byte value) ? value : null;
    public bool Write(ushort address, byte value)
    {
        Writes++; WrittenAddresses.Add(address); if (Writes == FailAt) return false;
        if (!IgnoreWrite) Values[address] = value;
        if(!IgnoreWrite && ConsumeWinLock && address==Vault.Trigger && (value&Vault.WinLockMask)!=0)
        {Values[Vault.SwitchStatus]^=Vault.WinLockMask;Values[address]&=unchecked((byte)~Vault.WinLockMask);}
        if (ClearLightingCommands && address == Vault.Backlight) Values[address] &= unchecked((byte)~Vault.BacklightCommandMask);
        if (ClearLightingCommands && address == Vault.Trigger) Values[address] &= unchecked((byte)~Vault.RgbEnableMask);
        if (RejectInvertedThresholds && Values[Vault.BatteryLower] > Values[Vault.BatteryUpper]) throw new IOException("Inverted threshold pair");
        return true;
    }
    public void Dispose() { }
}

sealed class FakeEc(Dictionary<ushort, byte> values) : IEcTransport
{
    public int Reads; public bool Disposed, Tear; private int highReads;
    public byte? Read(ushort address)
    {
        Reads++;
        if (Tear && address == Vault.CpuFanHigh && ++highReads == 2) return 11;
        return values.TryGetValue(address, out var value) ? value : null;
    }
    public void Dispose() => Disposed = true;
}
sealed class FakeDesktopPowerCore(FakeControl ec):IDesktopPowerCore
{
    public bool Ignore;
    public void SetDesktopPowerOption(string key,byte expected,bool enabled)
    {
        ushort addr=key=="usb"?Vault.Trigger:Vault.AcRecovery;byte mask=key=="usb"?Vault.UsbChargeMask:Vault.AcRecoveryMask;
        if(ec.Values[addr]!=expected)throw new IOException("Stale core request");
        if(!Ignore)ec.Write(addr,enabled?(byte)(expected|mask):(byte)(expected&~mask));
    }
}
sealed class FakePower : IPowerPlans
{
    public readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    public Guid Current; public int Writes; public bool IgnoreWrites;
    public FakePower() => Current = A;
    public Guid Active() => Current;
    public IReadOnlyList<PowerPlan> List() => [new(A, "A"), new(B, "B")];
    public void Set(Guid id) { Writes++; if (!IgnoreWrites) Current = id; }
}

