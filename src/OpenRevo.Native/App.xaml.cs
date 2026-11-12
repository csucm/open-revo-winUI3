using Microsoft.UI.Xaml;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public partial class App : Application
{
    private const string InstalledInstanceName = @"Local\OpenRevo.Native.InstalledPreview";
#if NATIVE_PLUGIN_PREVIEW_HOST
    private Window? pluginPreviewWindow;
#elif RESIDENT_PANEL_HOST
    private MainWindow? window;
#else
    private MainWindow? window;
    private ApplicationHardwareRuntime? hardwareRuntime;
    private Mutex? instance;
    private Mutex? hardwareOwner;
#endif
    public App()
    {
#if RESIDENT_PANEL_HOST
        // The host owns the data directory and persistent diagnostic journal.
        UnhandledException += (_, e) => RecordResidentPanelFailure("resident.panel.failure", e.Exception);
        InitializeComponent();
#else
        if(Environment.GetCommandLineArgs().Any(a=>a is "--approved-ui-review" or "--painted-ui-review"))
            Environment.SetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR",Path.Combine(AppContext.BaseDirectory,"data"));
        UnhandledException += (_, e) => SaveFailure(e.Exception);
#if DESIGN_REVIEW_HOST
        Environment.SetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR",Path.Combine(AppContext.BaseDirectory,"data"));
#endif
        string mode=Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() switch{"--startup-enable" or "--startup-remove" or "--startup-migrate" or "--installed-logon-migrate"=>"startup-maintenance","--startup"=>"startup","--gpu-helper" or "--camera-helper"=>"helper",_=>"interactive"};
        OpenRevo.Core.DiagnosticLog.StartProcess(mode);
        OpenRevo.Core.DiagnosticLog.Record("app.build",new{native=typeof(App).Module.ModuleVersionId,core=typeof(OpenRevo.Core.DiagnosticLog).Module.ModuleVersionId,executable=Environment.ProcessPath});
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{if(e.ExceptionObject is Exception error)OpenRevo.Core.DiagnosticLog.Failure("process.unhandled",error);};
        TaskScheduler.UnobservedTaskException+=(_,e)=>OpenRevo.Core.DiagnosticLog.Failure("task.unobserved",e.Exception);
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>OpenRevo.Core.DiagnosticLog.Record("process.exiting",new{exitCode=Environment.ExitCode});
        InitializeComponent();
#if !DESIGN_REVIEW_HOST
        WindowBrandIcon.SetProcessIdentity();
#endif
#endif
    }
    internal static bool IsAdmissionReview =>
#if ADMISSION_REVIEW_HOST
        true;
#else
        false;
#endif
    internal static bool IsPanelShapeReview =>
#if PANEL_SHAPE_REVIEW_HOST
        true;
#else
        false;
#endif
    internal static bool IsCapsUiReview =>
#if CAPS_UI_REVIEW_HOST
        true;
#else
        false;
#endif
    internal static bool IsOsdPowerReview =>
#if OSD_POWER_REVIEW_HOST
        true;
#else
        false;
#endif
    internal static bool IsDesignReview =>
#if DESIGN_REVIEW_HOST
        true;
#else
        Environment.GetCommandLineArgs().Contains("--ui-design-checks");
#endif
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if NATIVE_PLUGIN_PREVIEW_HOST
        pluginPreviewWindow = new NativePluginPreviewWindow();
        pluginPreviewWindow.Activate();
        return;
#elif RESIDENT_PANEL_HOST
        _ = OpenResidentPanelAsync();
#else
#if DESIGN_REVIEW_HOST
        string[] command = [Environment.ProcessPath??"OpenRevo.DesignReview.exe","--ui-design-checks"];
#else
        string[] command = Environment.GetCommandLineArgs();
#endif
#if GM7TG0M_DIAGNOSTICS
        if (command.Length > 1 && command[1] == "--capture-oem-fan-snapshot")
        {
            Environment.ExitCode = 2;
            try
            {
                if (command.Length == 4)
                {
                    using var diagnosticInstance = new Mutex(true, InstalledInstanceName, out bool firstInstance);
                    using var diagnosticOwner = firstInstance ? HardwareOwnerLease.TryReserve() : null;
                    Environment.ExitCode = OpenRevo.Core.OemFanSnapshotCommand.Run(command[2], command[3], diagnosticOwner != null);
                }
            }
            catch (Exception error) { OpenRevo.Core.DiagnosticLog.Failure("fan.snapshot.oem", error); }
            Exit();
            return;
        }
#endif
        if(command is [_, "--installed-logon-migrate", var previousDirectory])
        {
            try { OpenRevo.Core.StartupRegistration.MigrateInstalledLogon(previousDirectory); Environment.ExitCode = 0; }
            catch (Exception error) { SaveFailure(error); Environment.ExitCode = 11; }
            Exit();
            return;
        }
        if(command.Length==2&&command[1] is "--startup-migrate" or "--startup-enable" or "--startup-remove")
        {
            try
            {
                if(!OpenRevo.Core.Hal.GpuModeControl.IsAdministrator())throw new UnauthorizedAccessException("Startup maintenance requires elevation.");
                if(command[1]=="--startup-migrate")OpenRevo.Core.StartupRegistration.MigrateLegacy();
                else if(command[1]=="--startup-enable")OpenRevo.Core.StartupRegistration.SetEnabled(true);
                else OpenRevo.Core.StartupRegistration.SetEnabled(false);
                Environment.ExitCode=0;
            }
            catch(Exception error){SaveFailure(error);Environment.ExitCode=11;}
            Exit();return;
        }
        if(command.Length==3 && command[1]=="--camera-helper" && command[2]=="prepare")
        {
            try{Environment.ExitCode=OpenRevo.Core.CameraControl.PreparePrivileged();}catch{Environment.ExitCode=11;}
            Exit();return;
        }
        if(command.Length==3 && command[1]=="--camera-helper" && command[2] is "0" or "1")
        {
            Environment.ExitCode=OpenRevo.Core.CameraControl.RunPrivileged(command[2]=="1");Exit();return;
        }
        if(command.Length>1 && command[1]=="--gpu-helper")
        {
            Environment.ExitCode=command.Length==6 && int.TryParse(command[3],out int parent) && int.TryParse(command[4],out int target) && int.TryParse(command[5],out int expected)
                ? OpenRevo.Core.Hal.GpuModeControl.RunPrivileged(command[2],parent,target,expected):10;
            Exit();return;
        }
        // Only the developer's read-only UI review may coexist with the console.
        // Explicit live hardware checks retain the production singleton.
        bool readOnlyReview=command.Length==2&&command[1] is "--ui-checks" or "--ui-layout-checks" or "--ui-window-checks" or "--ui-replica-checks" or "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks" or "--approved-ui-review" or "--painted-ui-review";
        instance = new Mutex(true, readOnlyReview?@"Local\OpenRevo.Native.ReadOnlyReview":InstalledInstanceName, out bool first);
        if (!first) {if(!readOnlyReview&&!command.Contains("--startup"))MainWindow.WakeExistingInstance();Exit(); return; }
        try
        {
            if (!readOnlyReview)
            {
                hardwareOwner = HardwareOwnerLease.TryReserve();
                if (hardwareOwner == null)
                {
                    DiagnosticLog.Record("app.start.blocked", new { reason = "hardware_owner_exists_or_unavailable" });
                    Environment.ExitCode = 4;
                    Exit();
                    return;
                }
            }
            hardwareRuntime = new ApplicationHardwareRuntime();
            window = new MainWindow(hardwareRuntime,command.Length==2&&command[1] is "--ui-checks" or "--ui-layout-checks" or "--ui-window-checks" or "--ui-replica-checks" or "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks" or "--approved-ui-review" or "--painted-ui-review" or "--ui-camera-checks" or "--ui-brightness-checks" or "--ui-win-checks" or "--ui-gpu-checks",command.Length==2&&command[1] is "--review-details" or "--ui-window-checks" or "--ui-replica-checks" or "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks" or "--approved-ui-review" or "--painted-ui-review");
            if(IsPanelShapeReview)window.StartPanelShapeReview();
            else {window.Activate();if(command.Contains("--startup"))window.StartHiddenToTray();}
        }
        catch (Exception error) { SaveFailure(error); Exit(); }
#endif
    }
#if RESIDENT_PANEL_HOST
    private async Task OpenResidentPanelAsync()
    {
        PanelClient? client = null;
        try
        {
            if (Environment.GetCommandLineArgs() is not [_, "--resident-panel", var pipe, var parent] ||
                !int.TryParse(parent, out int parentId) || parentId <= 0)
                throw new ArgumentException("候选原版面板只能从托盘后台打开。");
#if NATIVE_RESIDENT_CORE
            client = await PanelClient.ConnectAsync(pipe, parentId, nativeCore: true);
#else
            client = await PanelClient.ConnectAsync(pipe, parentId);
#endif
            window = new MainWindow(client);
            window.Activate();
        }
        catch (Exception error)
        {
            RecordResidentPanelFailure("resident.panel.start_failed", error);
            client?.Dispose();
            Environment.ExitCode = 1;
            Exit();
        }
    }

    private static void RecordResidentPanelFailure(string eventName, Exception error)
    {
        DiagnosticLog.Failure(eventName, error);
        try { Console.Error.WriteLine(DiagnosticJournal.Scrub(error.ToString()).ReplaceLineEndings(" | ")); }
        catch (IOException) { }
    }
#endif
    private static void SaveFailure(Exception error)
    {
        OpenRevo.Core.DiagnosticLog.Failure("app.failure",error);
        try
        {
            string directory = OpenRevo.Core.AppData.DiagnosticsPath;
            Directory.CreateDirectory(directory);
            string file=Path.Combine(directory,"startup-failure.txt");
            if(File.Exists(file)&&new FileInfo(file).Length>512*1024)File.Move(file,file+".previous",true);
            File.AppendAllText(file,OpenRevo.Core.DiagnosticJournal.Scrub($"{DateTimeOffset.Now:O}\n{error}\nSession: {OpenRevo.Core.DiagnosticLog.Current.Session}\n")+Environment.NewLine);
        }
        catch { /* Preserve the original failure if this folder is read-only. */ }
    }
}

