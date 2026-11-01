using Microsoft.UI.Xaml;

namespace OpenRevo.Native;

public partial class App : Application
{
    private MainWindow? window;
    private Mutex? instance;
    public App()
    {
        UnhandledException += (_, e) => SaveFailure(e.Exception);
#if DESIGN_REVIEW_HOST
        Environment.SetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR",Path.Combine(AppContext.BaseDirectory,"data"));
#endif
        string mode=Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() switch{"--startup-enable" or "--startup-remove" or "--startup-migrate"=>"startup-maintenance","--startup"=>"startup","--gpu-helper" or "--camera-helper"=>"helper",_=>"interactive"};
        OpenRevo.Core.DiagnosticLog.StartProcess(mode);
        OpenRevo.Core.DiagnosticLog.Record("app.build",new{native=typeof(App).Module.ModuleVersionId,core=typeof(OpenRevo.Core.DiagnosticLog).Module.ModuleVersionId,executable=Environment.ProcessPath});
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{if(e.ExceptionObject is Exception error)OpenRevo.Core.DiagnosticLog.Failure("process.unhandled",error);};
        TaskScheduler.UnobservedTaskException+=(_,e)=>OpenRevo.Core.DiagnosticLog.Failure("task.unobserved",e.Exception);
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>OpenRevo.Core.DiagnosticLog.Record("process.exiting",new{exitCode=Environment.ExitCode});
        InitializeComponent();

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
#if DESIGN_REVIEW_HOST
        string[] command = [Environment.ProcessPath??"OpenRevo.DesignReview.exe","--ui-design-checks"];
#else
        string[] command = Environment.GetCommandLineArgs();
#endif
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
        bool readOnlyReview=command.Length==2&&command[1] is "--ui-checks" or "--ui-layout-checks" or "--ui-window-checks" or "--ui-replica-checks" or "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks";
        instance = new Mutex(true, readOnlyReview?@"Local\OpenRevo.Native.ReadOnlyReview":@"Local\OpenRevo.Native.InstalledPreview", out bool first);
        if (!first) {if(!readOnlyReview&&!command.Contains("--startup"))MainWindow.WakeExistingInstance();Exit(); return; }
        try
        {
            window = new MainWindow(command.Length==2&&command[1] is "--ui-checks" or "--ui-layout-checks" or "--ui-window-checks" or "--ui-replica-checks" or "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks" or "--ui-camera-checks" or "--ui-brightness-checks" or "--ui-win-checks" or "--ui-gpu-checks",command.Length==2&&command[1] is "--review-details" or "--ui-window-checks" or "--ui-replica-checks" or "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks");
            if(IsPanelShapeReview)window.StartPanelShapeReview();
            else {window.Activate();if(command.Contains("--startup"))window.StartHiddenToTray();}
        }
        catch (Exception error) { SaveFailure(error); Exit(); }
    }
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
