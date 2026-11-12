using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginFixtures;

// Test fixture only; never installed or enabled by the application. No hardware or Internet endpoints.
public sealed class IsolationProbe : IPlugin
{
    public async Task RunAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var config = await context.LoadConfigAsync(cancellationToken);
        var data = config.Data ?? throw new InvalidDataException("Missing isolated test fixture configuration.");
        string canary = data.GetProperty("canary").GetString()!;
        int host = data.GetProperty("host").GetInt32(), port = data.GetProperty("port").GetInt32();
        bool readDenied = false, writeDenied = false, childDenied = false;
        string networkOutcome = "connected";
        try { _ = File.ReadAllText(canary); } catch (UnauthorizedAccessException) { readDenied = true; }
        try { File.WriteAllText(canary, "tampered-by-isolated-fixture"); } catch (UnauthorizedAccessException) { writeDenied = true; }
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(2000);
            try { await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token); }
            catch (SocketException error) { networkOutcome = error.SocketErrorCode.ToString(); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { networkOutcome = "timed_out"; }
        }
        nint process = OpenProcess(0x20, false, (uint)host); // request VM_WRITE right; never write any memory
        int processError = Marshal.GetLastWin32Error();
        bool processDenied = process == 0 && processError == 5;
        if (process != 0) CloseHandle(process);
        try
        {
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "OpenRevo.PluginWorker.exe"))
            { UseShellExecute = false, CreateNoWindow = true, Arguments = "--probe-child" });
            if (child is { HasExited: false }) child.Kill(); // only this fixture's unexpected child, never another process
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 5 or 367 or 1816) { childDenied = true; }
        var denied = await context.SetPerformanceAsync(PluginPerformanceMode.Office, cancellationToken);
        var invalid = await context.ExecuteAsync("battery.limit.set", JsonSerializer.SerializeToElement(new { percent = 77 }), cancellationToken);
        var report = new { readDenied, writeDenied, networkOutcome, processDenied, childDenied,
            hardwareDenied = !denied.Success && denied.Code == "permission_denied",
            invalidBatteryRejected = !invalid.Success && invalid.Code == "unsupported_action" };
        await context.SetViewAsync(new("probe-complete", [new("report", "text", "隔离结果", JsonSerializer.Serialize(report))]), cancellationToken);
        // Deliberately ignore cancellation: host disable must terminate this CPU-bound worker through its Job.
        while (true) Thread.SpinWait(100000);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
