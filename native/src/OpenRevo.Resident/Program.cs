using System.Diagnostics;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The short-lived read-only sensor path precedes all app initialization.
        // It acquires no startup lease, control session, stores or event workers.
        if (args is ["--sampling-worker"]) return ResidentSamplingWorker.RunAsync().GetAwaiter().GetResult();
        bool installed = false;
        bool open = false;
#if RESIDENT_INSTALLED_HOST
        installed = args is ["--installed-open" or "--installed-logon"];
        open = installed && args[0] == "--installed-open";
#endif
        if (!installed && (args.Length == 0 || args[0] != "--isolated-candidate")) return 2;
        bool nativePanel = installed || args.Length > 1 && args[1] == "--native-panel";
        string[] candidateArguments = installed ? [] : args[(nativePanel ? 2 : 1)..];
        bool memoryProfile = candidateArguments is ["--memory-profile"];
        if (!memoryProfile && (candidateArguments.Length > 1 || candidateArguments.Any(arg => arg is not ("--debug" or "--log-errors") &&
            !(arg.StartsWith("--debug=", StringComparison.Ordinal) && PanelMaintenanceRequest.ValidFilter(arg[8..]))))) return 2;
        using var current = Process.GetCurrentProcess();
        if (current.SessionId == 0) return 2;
        if (!GpuModeControl.IsAdministrator()) return 3;
        bool diagnosticsReady = false;
        try
        {
            string? installation = null;
            if (installed)
            {
                installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
                StartupRegistration.ValidateResidentInstallation(installation);
                if (!File.Exists(Path.Combine(installation, "panel-native", "OpenRevo.ResidentNativePanel.exe")))
                    throw new IOException("安装缺少配对的 Open-Revo 面板。");
            }
            var entry = ResidentStartupLease.Enter(installed, requestWake: !installed || open, out var startupLease);
            using var lease = startupLease;
            if (entry == ResidentStartupLease.Entry.AlreadyRunning) return 0;
            if (entry == ResidentStartupLease.Entry.Blocked) return 4;
            if (installation != null) UserDataStore.RecoverInstalledPortableReset(installation);
            Directory.CreateDirectory(UserDataStore.ValidateDirectory(Path.Combine(AppData.DiagnosticsPath, "runtime")));
            DiagnosticLog.StartProcess(installed ? "resident-installed" : "resident-candidate", sharedTempFallback: false);
            diagnosticsReady = true;
            if (ExistingUiConflict() is { } reason)
            {
                DiagnosticLog.Record("resident.start.blocked", new { reason });
                return 4;
            }
            UserDataStore.EnsureMarker();
            SystemPreferences.UseNativeStore();
            AppData.UseNativeStore();
            var runtime = new ApplicationHardwareRuntime(ExistingUiConflict,
                useNativeModeKeyEvents: true, useNativePowerRequests: true);
            runtime.Telemetry.UseResidentSamplingSource(new ResidentSamplingClient());
            using var tray = new ResidentTray(runtime, nativePanel,
                memoryProfile ? null : candidateArguments.SingleOrDefault(), installation, memoryProfile);
            return tray.Run(open);
        }
        catch (Exception error)
        {
            if (diagnosticsReady) DiagnosticLog.Failure("resident.start.failed", error);
            return 1;
        }
    }

    private static string? ExistingUiConflict()
    {
        // Also cover a normal UI in another Windows session.
        Process[] instances = Process.GetProcessesByName("OpenRevo.Native");
        try { return instances.Length == 0 ? null : "请先正常退出其他硬件控制程序：OpenRevo.Native 已运行。"; }
        finally { foreach (var process in instances) process.Dispose(); }
    }
}
