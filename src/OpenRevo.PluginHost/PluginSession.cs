using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenRevo.Core;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

/// <summary>
/// Implemented by trusted application code. Hardware implementations must recheck lease.IsActive
/// at the actual execution boundary and reuse the application's existing queue and guarded command.
/// Do not load plugin types or execute plugin-provided delegates in this interface.
/// </summary>
public interface IPluginHostServices
{
    PluginCapability[] QueryCapabilities();
    PluginCapabilityCatalog QueryCatalog() => new(new(PluginProtocol.Major, PluginProtocol.Minor),
        QueryCapabilities().Select(c => new PluginCapabilityInfo(c.Id, "permission", "", c.Available ? "available" : "unavailable",
            c.UnavailableReason, [])).ToArray());
    PluginThemeSchema QueryThemeSchema() => new(false, 1, "static", [], [], [], "RRGGBB", 0, false);
    // Optional readonly OS snapshot on first subscription; must not acquire or poll hardware.
    PluginPowerState? QueryPowerState() => null;
    Task<PluginResult> ExecuteAsync(PluginLease lease, PluginAction action, CancellationToken cancellationToken);
}

public sealed class PluginLease
{
    private int active;
    private readonly HashSet<string> grants;
    public string PluginId { get; }
    public string PackageDigest { get; }
    public bool IsActive => Volatile.Read(ref active) == 1;
    public bool Allows(string permission) => IsActive && grants.Contains(permission);
    internal PluginLease(PluginPackage package, IEnumerable<string> grants)
    {
        PluginId = package.Manifest.Id; PackageDigest = package.Digest;
        this.grants = new(grants, StringComparer.Ordinal);
        if (this.grants.Any(p => !PluginPermissions.IsKnown(p) || !package.Manifest.Permissions.Contains(p)))
            throw new InvalidDataException("Grants must be known requested permissions.");
    }
    internal bool Granted(string permission) => grants.Contains(permission);
    internal void Activate()
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new OperationCanceledException("Plugin lease was already activated or revoked.");
    }
    internal void Revoke() => Interlocked.Exchange(ref active, -1);
}

public sealed class PluginSession : IAsyncDisposable
{
    private readonly PluginPackage package;
    private readonly IPluginHostServices services;
    private readonly string configPath, stage;
    private readonly PluginSandbox sandbox;
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Outgoing> outgoing = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(16)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private PluginChannel? channel;
    private Task? running;
    private long lastRequest, rateWindow = Stopwatch.GetTimestamp();
    private int rateCount, stopped, started, summarySubscribed, powerSubscribed;
    private readonly object disposalGate = new();
    private Task? disposal, cleanup;
    private CancellationTokenRegistration externalCancellation;
    private long lastAction, lastConfigSave, lastView;
    private PluginView? view;
    private PluginSystemSummary? latest;
    private PluginPowerState? latestPower;
    public bool HasPowerSubscription => Lease.Allows(PluginPermissions.PowerRead) && Volatile.Read(ref powerSubscribed) != 0;
    public bool HasSummarySubscription => Lease.Allows(PluginPermissions.SummaryRead) && Volatile.Read(ref summarySubscribed) != 0;
    public PluginLease Lease { get; }
    public PluginManifest Manifest => package.Manifest with { Permissions = (string[])package.Manifest.Permissions.Clone() };
    public string? FailureCode { get; private set; }
    public bool IsRunning => Lease.IsActive && Volatile.Read(ref started) != 0;
    public Task Completion => running ?? Task.CompletedTask;
    public PluginView? View => CloneView(Volatile.Read(ref view));
    public event Action? Changed;
    public event Action<string?>? Stopped;
    private sealed record Outgoing(string Method, long Id, PluginMessageKind Kind, object Payload);

    private PluginSession(PluginPackage package, IEnumerable<string> grants, IPluginHostServices services, string dataRoot)
    {
        this.package = package; this.services = services; Lease = new(package, grants);
        if (package.Manifest.Background && !Lease.Granted(PluginPermissions.Background))
            throw new UnauthorizedAccessException("Background execution has not been granted.");
        string root = Path.GetFullPath(dataRoot);
        PluginPackage.RejectReparseAncestors(root);
        Directory.CreateDirectory(root); PluginSandbox.ProtectDirectory(root);
        string configs = Path.Combine(root, "config");
        Directory.CreateDirectory(configs); PluginSandbox.ProtectDirectory(configs);
        configPath = Path.Combine(configs, package.Manifest.Id + ".json");
        string sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions); PluginSandbox.ProtectDirectory(sessions);
        stage = Path.Combine(sessions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); PluginSandbox.ProtectDirectory(stage);
        try
        {
            sandbox = new();
            string user = WindowsIdentity.GetCurrent().User!.Value;
            string name = "OpenRevo.Plugin." + Guid.NewGuid().ToString("N");
            pipeName = name;
            pipe = SandboxNative.CreateLocalPipe(name, user);
        }
        catch { sandbox?.Dispose(); CleanupStage(); throw; }
    }

    /// <summary>
    /// Consent must be bound to the exact package.Digest. Created only by PluginRuntime.
    /// workerDirectory contains only the application's trusted, framework-dependent worker build.
    /// </summary>
    internal static async Task<PluginSession> StartAsync(PluginPackage package, IEnumerable<string> grantedPermissions,
        IPluginHostServices services, string dataRoot, string workerDirectory, CancellationToken cancellationToken,
        CancellationToken revocationToken)
    {
        string[] grants = grantedPermissions.ToArray();
        var session = await Task.Run(() => new PluginSession(package, grants, services, dataRoot), cancellationToken).ConfigureAwait(false);
        session.externalCancellation = revocationToken.Register(() => session.Stop(null));
        try
        {
            await Task.Run(() => session.Stage(workerDirectory), cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using (var endpoint = SandboxNative.ConnectLocalEndpoint(session.pipeName))
            {
                await session.pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
                if (!SandboxNative.GetNamedPipeClientProcessId(session.pipe.SafePipeHandle, out uint peer) || peer != Environment.ProcessId)
                    throw new UnauthorizedAccessException("Plugin endpoint was not connected by the host.");
                await Task.Run(() => session.sandbox.Start(Path.Combine(session.stage, "OpenRevo.PluginWorker.exe"),
                    Path.Combine(session.stage, "plugin", package.Manifest.EntryAssembly), package.Manifest.EntryType, endpoint),
                    cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            session.channel = new(session.pipe);
            var hello = await session.channel.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (hello.Kind != PluginMessageKind.Event || hello.Method != "worker.ready")
                throw new InvalidDataException("Plugin worker did not complete startup.");
            return session;
        }
        catch (Exception error) when (session.sandbox.ExitCode() is uint code)
        { await session.DisposeAsync().ConfigureAwait(false); throw new IOException($"Plugin worker exited during startup (0x{code:X8}).", error); }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private string pipeName = "";

    internal void Activate()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        Lease.Activate(); Volatile.Write(ref started, 1);
        running = Task.Run(RunAsync);
    }

    private void Stage(string workerDirectory)
    {
        string trusted = Path.GetFullPath(workerDirectory); PluginPackage.RejectReparseAncestors(trusted);
        string[] workerFiles = ["OpenRevo.PluginWorker.exe", "OpenRevo.PluginWorker.dll", "OpenRevo.PluginWorker.deps.json",
            "OpenRevo.PluginWorker.runtimeconfig.json", "OpenRevo.PluginSdk.dll"];
        foreach (string name in workerFiles)
        {
            string source = Path.Combine(trusted, name);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid worker file.");
            File.Copy(source, Path.Combine(stage, name), false);
        }
        string code = Path.Combine(stage, "plugin"); Directory.CreateDirectory(code);
        PluginPackage.RejectReparseAncestors(package.DirectoryPath);
        foreach (var (name, hash) in package.Files)
        {
            string source = Path.Combine(package.DirectoryPath, name);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Plugin file became a reparse point.");
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("Plugin file grew after inspection.");
            string destination = Path.Combine(code, name);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            using var check = File.OpenRead(destination);
            if (Convert.ToHexString(SHA256.HashData(check)) != hash) throw new InvalidDataException("Plugin changed; renewed consent is required.");
        }
        // Protected parent; only this instance's SID gains read/execute access. No write grant.
        PluginSandbox.ProtectDirectory(stage, sandbox.Sid);
    }

    public void PublishSummary(PluginSystemSummary summary)
    {
        Volatile.Write(ref latest, summary);
        if (Lease.Allows(PluginPermissions.SummaryRead) && Volatile.Read(ref summarySubscribed) != 0)
            Queue(new(PluginMethods.StateSummary, 0, PluginMessageKind.Event, summary));
    }
    public void PublishPower(PluginPowerState power, bool force = false)
    {
        PluginPowerState? before;
        do
        {
            before = Volatile.Read(ref latestPower);
            if (before != null && power.ObservedAt < before.ObservedAt)
            {
                if (force && HasPowerSubscription) Queue(new(PluginMethods.StatePower, 0, PluginMessageKind.Event, before));
                return;
            }
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref latestPower, power, before), before));
        if (HasPowerSubscription && (force || before == null || before.OnAc != power.OnAc || before.BatteryPercent != power.BatteryPercent))
            Queue(new(PluginMethods.StatePower, 0, PluginMessageKind.Event, power));
    }
    public bool PostUiEvent(PluginUiEvent input)
    {
        var document = Volatile.Read(ref view);
        var control = document?.Controls.FirstOrDefault(c => c.Id == input.ControlId);
        if (!Lease.Allows(PluginPermissions.View) || control is not { Enabled: true } || control.Kind == "text") return false;
        if (control.Kind == "button" && input.Value != null || control.Kind == "toggle" && input.Value is not ("true" or "false") ||
            control.Kind == "choice" && !control.Choices!.Any(c => c.Value == input.Value)) return false;
        return Queue(new("ui.event", 0, PluginMessageKind.Event, input));
    }
    public ValueTask PageClosedAsync() => package.Manifest.Background && Lease.Allows(PluginPermissions.Background)
        ? ValueTask.CompletedTask : DisposeAsync();

    private bool Queue(Outgoing message)
    {
        if (!Lease.IsActive) return false;
        if (outgoing.Writer.TryWrite(message)) return true;
        Stop("outbound_queue_full"); return false;
    }

    private async Task RunAsync()
    {
        Task sending = SendAsync();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await channel!.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                if (message.Kind != PluginMessageKind.Request || message.RequestId <= lastRequest)
                    throw new InvalidDataException("Invalid direction or replayed request.");
                lastRequest = message.RequestId;
                if (Stopwatch.GetElapsedTime(rateWindow) >= TimeSpan.FromSeconds(1)) { rateWindow = Stopwatch.GetTimestamp(); rateCount = 0; }
                if (++rateCount > 30) throw new InvalidDataException("Plugin request rate exceeded.");
                object response;
                try { response = await DispatchAsync(message).ConfigureAwait(false); }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
                { response = new PluginResult(false, "invalid_request"); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { response = new PluginResult(false, "storage_failed"); }
                if (!Queue(new(message.Method, message.RequestId, PluginMessageKind.Response, response))) break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLog.Record("plugin.session_failed", new { Lease.PluginId, type = error.GetType().Name });
            Stop("session_failed");
        }
        finally
        {
            Stop(FailureCode);
            try { await sending.ConfigureAwait(false); } catch (Exception) { /* sender records only bounded failure code */ }
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                FailureCode ??= "cleanup_failed";
                DiagnosticLog.Record("plugin.cleanup_failed", new { Lease.PluginId, type = error.GetType().Name });
                throw;
            }
            finally { Notify(); }
        }
    }

    private async Task SendAsync()
    {
        try
        {
            await foreach (var item in outgoing.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await channel!.SendAsync(item.Kind, item.Id, item.Method, item.Payload, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { Stop("send_failed"); }
    }

    private async Task<object> DispatchAsync(PluginMessage message)
    {
        if (!Lease.IsActive) return new PluginResult(false, "disabled");
        T Decode<T>() => message.Payload.Deserialize<T>(PluginPackage.Json) ?? throw new InvalidDataException("Missing payload.");
        switch (message.Method)
        {
            case PluginMethods.CatalogQuery: return services.QueryCatalog();
            case PluginMethods.ThemeSchemaQuery: return services.QueryThemeSchema();
            case PluginMethods.CapabilitiesQuery:
                var hostCapabilities = services.QueryCapabilities();
                return new PluginCapabilities(new(PluginProtocol.Major, PluginProtocol.Minor),
                    package.Manifest.Permissions.Select(p =>
                    {
                        bool available = Lease.Allows(p) && hostCapabilities.Any(c => c.Id == p && c.Available);
                        return new PluginCapability(p, available, available ? null : "not_granted_or_unavailable");
                    }).ToArray());
            case PluginMethods.StateSubscribe:
                string topic = Decode<PluginTopic>().Topic;
                if (topic == "system.power")
                {
                    if (!Lease.Allows(PluginPermissions.PowerRead)) return Denied();
                    if (Interlocked.Exchange(ref powerSubscribed, 1) == 0)
                    {
                        var power = services.QueryPowerState() ?? Volatile.Read(ref latestPower);
                        if (power != null)
                        {
                            PublishPower(power, force: true);
                        }
                    }
                    return Ok();
                }
                if (topic != "system.summary") return new PluginResult(false, "unknown_topic");
                if (!Lease.Allows(PluginPermissions.SummaryRead)) return Denied();
                Volatile.Write(ref summarySubscribed, 1);
                if (Volatile.Read(ref latest) is { } summary) PublishSummary(summary);
                return Ok();
            case PluginMethods.StateUnsubscribe:
                string removed = Decode<PluginTopic>().Topic;
                if (removed == "system.power") { Volatile.Write(ref powerSubscribed, 0); return Ok(); }
                if (removed != "system.summary") return new PluginResult(false, "unknown_topic");
                Volatile.Write(ref summarySubscribed, 0); return Ok();
            case PluginMethods.ConfigLoad:
                if (!Lease.Allows(PluginPermissions.Config)) return Denied();
                if (!File.Exists(configPath)) return new PluginResult(true, "not_found");
                return new PluginResult(true, "ok", ReadConfig());
            case PluginMethods.ConfigSave:
                if (!Lease.Allows(PluginPermissions.Config)) return Denied();
                if (lastConfigSave != 0 && Stopwatch.GetElapsedTime(lastConfigSave) < TimeSpan.FromMilliseconds(500)) return new PluginResult(false, "rate_limited");
                JsonElement data = Decode<PluginConfig>().Data;
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Config must be an object.");
                string content = "{\"version\":1,\"data\":" + data.GetRawText() + "}";
                if (Encoding.UTF8.GetByteCount(content) > 16384) throw new InvalidDataException("Config too large.");
                // Preserve corrupt/unknown host envelopes. A plugin cannot silently overwrite them.
                if (File.Exists(configPath)) ReadConfig();
                if (!Lease.IsActive) return Denied();
                AtomicPreferenceFile.Write(configPath, content); lastConfigSave = Stopwatch.GetTimestamp(); return Ok();
            case "ui.view.set":
                if (!Lease.Allows(PluginPermissions.View)) return Denied();
                if (lastView != 0 && Stopwatch.GetElapsedTime(lastView) < TimeSpan.FromMilliseconds(250)) return new PluginResult(false, "rate_limited");
                var document = Decode<PluginView>(); PluginViewPolicy.Validate(document);
                Volatile.Write(ref view, document); lastView = Stopwatch.GetTimestamp(); Notify(); return Ok();
            case PluginMethods.ActionExecute:
                var action = Decode<PluginAction>();
                if (!ValidateAction(action, out string permission)) return new PluginResult(false, "unsupported_action");
                if (!Lease.Allows(permission)) return Denied();
                if (!services.QueryCapabilities().Any(c => c.Id == permission && c.Available)) return new PluginResult(false, "unavailable");
                if (lastAction != 0 && Stopwatch.GetElapsedTime(lastAction) < TimeSpan.FromMilliseconds(500)) return new PluginResult(false, "rate_limited");
                lastAction = Stopwatch.GetTimestamp();
                // No unbounded task fanout: the next request is not dispatched until this completes.
                // Revocation interrupts this wait; an already-started hardware transaction remains owned
                // by the trusted host and finishes through its normal safety path.
                return await services.ExecuteAsync(Lease, action, lifetime.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token).ConfigureAwait(false);
            default: return new PluginResult(false, "unsupported_method");
        }
    }

    private static bool ValidateAction(PluginAction action, out string permission)
    {
        permission = "";
        if (action.Arguments.ValueKind != JsonValueKind.Object) return false;
        var values = action.Arguments.EnumerateObject().ToArray();
        if (values.Length != 1) return false;
        switch (action.Action)
        {
            case "performance.set":
                permission = PluginPermissions.Performance;
                return values[0].Name == "mode" && values[0].Value.ValueKind == JsonValueKind.Number &&
                    values[0].Value.TryGetInt32(out int mode) && mode is >= 1 and <= 3;
            case "fanBoost.set":
                permission = PluginPermissions.FanBoost;
                return values[0].Name == "enabled" && values[0].Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case "battery.limit.set":
                permission = PluginPermissions.BatteryLimit;
                return values[0].Name == "percent" && values[0].Value.ValueKind == JsonValueKind.Number &&
                    values[0].Value.TryGetInt32(out int percent) && percent is 60 or 80 or 100;
            case "ui.navigate":
                permission = PluginPermissions.Navigate;
                return values[0].Name == "page" && values[0].Value.ValueKind == JsonValueKind.String &&
                    values[0].Value.GetString() is "overview" or "display" or "power" or "devices" or "settings";
            default: return false;
        }
    }
    private static PluginResult Ok() => new(true, "ok");
    private static PluginResult Denied() => new(false, "permission_denied");
    private JsonElement ReadConfig()
    {
        PluginPackage.RejectReparseAncestors(Path.GetDirectoryName(configPath)!);
        if ((File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid config path.");
        using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16384) throw new InvalidDataException("Config too large.");
        var stored = JsonSerializer.Deserialize<StoredConfig>(stream, PluginPackage.Json)
            ?? throw new InvalidDataException("Missing config envelope.");
        if (stored.Version != 1 || stored.Data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unsupported config envelope.");
        return stored.Data;
    }
    private sealed record StoredConfig(int Version, JsonElement Data);
    private static PluginView? CloneView(PluginView? source) => source == null ? null : source with
    { Controls = source.Controls.Select(c => c with { Choices = c.Choices?.ToArray() }).ToArray() };
    private void Notify()
    {
        // Trusted observers must dispatch to their UI thread. One failing observer is isolated.
        if (Changed is { } changed) foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch (Exception error) { DiagnosticLog.Record("plugin.observer_failed", new { type = error.GetType().Name }); }
    }
    private void Stop(string? reason)
    {
        Lease.Revoke();
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        FailureCode = reason;
        lifetime.Cancel(); outgoing.Writer.TryComplete(); pipe.Dispose();
        if (Stopped is { } stoppedObservers) foreach (Action<string?> observer in stoppedObservers.GetInvocationList())
            try { observer(reason); } catch (Exception error) { DiagnosticLog.Record("plugin.stop_observer_failed", new { type = error.GetType().Name }); }
    }
    public ValueTask DisposeAsync()
    {
        Stop(null);
        lock (disposalGate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        if (running != null) await running.ConfigureAwait(false);
        await CleanupAsync().ConfigureAwait(false);
        Changed = null;
        Stopped = null;
    }
    private Task CleanupAsync()
    {
        lock (disposalGate) return cleanup ??= Task.Run(() =>
        {
            try { sandbox.Dispose(); }
            finally { externalCancellation.Dispose(); CleanupStage(); }
        });
    }

    private void CleanupStage()
    {
        // Never recurse through an arbitrary path or a reparse point. This instance owns a GUID
        // directory under sessions; the sandbox has only RX access. Delete individual known levels.
        try
        {
            string full = Path.GetFullPath(stage);
            if (Path.GetFileName(Path.GetDirectoryName(full)) != "sessions" ||
                !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new InvalidDataException("Invalid staging cleanup path.");
            PluginPackage.RejectReparseAncestors(full);
            string code = Path.Combine(full, "plugin");
            if (Directory.Exists(code))
            {
                PluginPackage.RejectReparseAncestors(code);
                foreach (string file in Directory.EnumerateFileSystemEntries(code))
                {
                    if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new InvalidDataException("Unexpected staging entry.");
                    File.Delete(file);
                }
                Directory.Delete(code, false);
            }
            foreach (string file in Directory.EnumerateFileSystemEntries(full))
            {
                if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new InvalidDataException("Unexpected staging entry.");
                File.Delete(file);
            }
            Directory.Delete(full, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { DiagnosticLog.Record("plugin.stage_cleanup_failed", new { Lease.PluginId, type = error.GetType().Name }); }
    }
}
