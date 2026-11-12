using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;

// Explicitly invoked, isolated Windows acceptance harness. No HardwareSession or real hardware backend.
if (args.Length != 4 || args[0] != "--run-isolation-checks")
{
    Console.WriteLine("Explicit opt-in required: --run-isolation-checks <worker-dir> <probe-dir> <new-evidence-dir>");
    return 2;
}
string workerDirectory = Path.GetFullPath(args[1]), probeDirectory = Path.GetFullPath(args[2]), evidence = Path.GetFullPath(args[3]);
if (Directory.Exists(evidence) || File.Exists(evidence)) throw new IOException("Use a new evidence directory; existing files are never overwritten.");
for (var parent = new DirectoryInfo(evidence); parent != null; parent = parent.Parent)
    if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Evidence paths cannot traverse reparse points.");
Directory.CreateDirectory(evidence);
Environment.SetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR", Path.Combine(evidence, "host-data"));
var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
new DirectoryInfo(evidence).SetAccessControl(acl);
var checks = new Dictionary<string, bool>(); string? failure = null, networkOutcome = null;
string root = Path.Combine(evidence, "runtime"), canary = Path.Combine(evidence, "forbidden-canary.txt");
File.WriteAllText(canary, "original-canary");
var service = new NoHardwareServices();
var listener = new TcpListener(IPAddress.Loopback, 0);
try
{
    listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using (var control = new TcpClient())
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await control.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        checks["hostLoopbackControlConnected"] = true;
    }
    var package = PluginPackage.Read(probeDirectory);
    if (package.Manifest.Id != "framework-isolation-probe" || package.Manifest.EntryType != "OpenRevo.PluginFixtures.IsolationProbe")
        throw new InvalidDataException("Only the framework's dedicated probe fixture is accepted.");
    Directory.CreateDirectory(Path.Combine(root, "config"));
    File.WriteAllText(Path.Combine(root, "config", package.Manifest.Id + ".json"), JsonSerializer.Serialize(new
    { version = 1, data = new { canary, host = Environment.ProcessId, port } }));
    // Test-only consent explicitly excludes both requested hardware permissions.
    var approval = PluginApproval.FromUserSelection(package, [PluginPermissions.View, PluginPermissions.Config]);
    await using var runtime = new PluginRuntime(service, root, workerDirectory);
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var session = await runtime.EnableAsync(package, approval, limit.Token);
    while (session.View?.Title != "probe-complete")
    {
        if (!session.IsRunning) throw new IOException("Probe session stopped before reporting: " + session.FailureCode);
        await Task.Delay(25, limit.Token);
    }
    using (var result = JsonDocument.Parse(session.View!.Controls.Single(c => c.Id == "report").Value!))
        foreach (var item in result.RootElement.EnumerateObject())
        {
            if (item.Name == "networkOutcome") networkOutcome = item.Value.GetString();
            else checks[item.Name] = item.Value.GetBoolean();
        }
    // A local WFP drop can time out. This checks only that the connection was not established
    // within the probe deadline, with a successful host control and no queued worker connection.
    checks["workerLoopbackNotEstablishedWithinDeadline"] = networkOutcome is ("AccessDenied" or "ConnectionRefused" or "NetworkUnreachable" or "HostUnreachable" or "timed_out")
        && !listener.Pending();
    await runtime.DisableAsync(package.Manifest.Id).WaitAsync(TimeSpan.FromSeconds(10));
    checks["cpuBoundWorkerStopped"] = !session.IsRunning && session.Completion.IsCompleted;
    checks["stagingCleaned"] = !Directory.EnumerateFileSystemEntries(Path.Combine(root, "sessions")).Any();
    checks["canaryUnchanged"] = File.ReadAllText(canary) == "original-canary";
    checks["hardwareBackendNotReached"] = service.Executions == 0;
}
catch (Exception error) { failure = error.ToString(); }
finally { listener.Stop(); }
bool passed = failure == null && checks.Count >= 12 && checks.Values.All(v => v);
string reportPath = Path.Combine(evidence, "isolation-results.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(new { allPassed = passed, failure, networkOutcome, checks,
    hardwareBackendCalls = service.Executions, actualHardwareBackendAvailable = false,
    internetEndpointsUsed = false, scope = "Isolated Windows sandbox fixture; not hardware or all-protocol acceptance" }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(reportPath);
return passed ? 0 : 1;

internal sealed class NoHardwareServices : IPluginHostServices
{
    internal int Executions;
    public PluginCapability[] QueryCapabilities() =>
        [new(PluginPermissions.View, true, null), new(PluginPermissions.Config, true, null),
         new(PluginPermissions.Performance, true, null), new(PluginPermissions.BatteryLimit, true, null)];
    public Task<PluginResult> ExecuteAsync(PluginLease lease, PluginAction action, CancellationToken cancellationToken)
    { Interlocked.Increment(ref Executions); return Task.FromResult(new PluginResult(false, "no_hardware_backend")); }
}
