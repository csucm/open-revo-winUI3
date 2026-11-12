using System.Text;
using System.Text.Json;

namespace OpenRevo.Core;

public sealed record ExtensionVersion(int Major, int Minor);
public sealed record ExtensionCapabilities(ExtensionVersion Api, string[] StateTopics, string[] Actions,
    int MaximumConfigBytes, bool HardwareActions, bool WebCompatibility);
public sealed record ExtensionState(DateTimeOffset SampledAt, double? CpuPercent, double? UsedMemoryGiB,
    double? TotalMemoryGiB, int? BatteryPercent, bool? OnAc);
public sealed record ExtensionActionResult(bool Success, string Code);

// Trusted host bindings only. This API does not load or execute third-party code.
// All methods/callbacks run on the owning UI dispatcher; a future IPC host must marshal to it.
public sealed class NativeExtensionHost : IDisposable
{
    public const int MaximumConfigBytes = 16384;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly string configDirectory;
    private readonly Action<string> navigate;
    private readonly Dictionary<string, NativeExtensionClient> clients = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private ExtensionState? latest;
    public NativeExtensionHost(string configDirectory, Action<string> navigate)
    {
        this.configDirectory = Path.GetFullPath(configDirectory);
        this.navigate = navigate;
    }
    internal void Verify()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Extension API requires its owning dispatcher.");
    }
    public NativeExtensionClient Register(string id, int apiMajor)
    {
        Verify();
        if (apiMajor != 1) throw new NotSupportedException("Unsupported extension API major.");
        if (id.Length is < 1 or > 64 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid extension ID.", nameof(id));
        if (clients.ContainsKey(id)) throw new InvalidOperationException("Extension already registered.");
        var client = new NativeExtensionClient(this, Path.Combine(configDirectory, id + ".json"));
        clients.Add(id, client); return client;
    }
    public void Publish(ExtensionState state)
    {
        Verify(); latest = state;
        foreach (var client in clients.Values.ToArray()) client.Deliver(state);
    }
    internal ExtensionState? Latest => latest;
    internal void Navigate(string page) => navigate(page);
    public void Dispose()
    {
        if (disposed) return;
        Verify(); foreach (var client in clients.Values) client.Dispose(); clients.Clear(); latest = null; disposed = true;
    }
}

public sealed class NativeExtensionClient : IDisposable
{
    private readonly NativeExtensionHost host;
    private readonly string configPath;
    private readonly List<Subscription> subscriptions = [];
    private bool enabled = true, disposed;
    internal NativeExtensionClient(NativeExtensionHost host, string configPath) { this.host = host; this.configPath = configPath; }
    private void Verify() { host.Verify(); ObjectDisposedException.ThrowIf(disposed, this); }
    public ExtensionCapabilities QueryCapabilities()
    {
        Verify(); return new(new(1, 0), ["system.summary"], ["ui.open.overview", "ui.open.display", "ui.open.power", "ui.open.devices", "ui.open.settings"], NativeExtensionHost.MaximumConfigBytes, false, false);
    }
    public bool Enabled { get { Verify(); return enabled; } }
    public void SetEnabled(bool value)
    {
        Verify(); enabled = value;
        if (!enabled) foreach (var subscription in subscriptions.ToArray()) subscription.Dispose();
    }
    public IDisposable Subscribe(string topic, Action<ExtensionState> observer)
    {
        Verify(); ArgumentNullException.ThrowIfNull(observer);
        if (!enabled) throw new InvalidOperationException("Extension disabled.");
        if (topic != "system.summary") throw new NotSupportedException("Unknown state topic.");
        var subscription = new Subscription(this, observer); subscriptions.Add(subscription);
        if (host.Latest is { } state) subscription.Deliver(state);
        return subscription;
    }
    internal void Deliver(ExtensionState state)
    {
        if (!enabled || disposed) return;
        foreach (var subscription in subscriptions.ToArray()) subscription.Deliver(state);
    }
    public ExtensionActionResult Execute(string action)
    {
        Verify();
        if (!enabled) return new(false, "disabled");
        if (!QueryCapabilities().Actions.Contains(action, StringComparer.Ordinal)) return new(false, "unsupported_action");
        try { host.Navigate(action[8..]); return new(true, "ok"); }
        catch { return new(false, "host_action_failed"); }
    }
    public JsonElement? LoadConfig()
    {
        Verify(); if (!File.Exists(configPath)) return null;
        using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > NativeExtensionHost.MaximumConfigBytes) throw new InvalidDataException("Extension config too large.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("data").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unsupported extension config.");
        return root.GetProperty("data").Clone();
    }
    public void SaveConfig(JsonElement data)
    {
        Verify();
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("Config must be an object.");
        // Validate depth and payload before creating any file.
        string json = "{\"version\":1,\"data\":" + data.GetRawText() + "}";
        if (Encoding.UTF8.GetByteCount(json) > NativeExtensionHost.MaximumConfigBytes) throw new InvalidDataException("Extension config too large.");
        using var validated = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        AtomicPreferenceFile.Write(configPath, json);
    }
    public void Dispose()
    {
        if (disposed) return;
        host.Verify(); foreach (var subscription in subscriptions.ToArray()) subscription.Dispose(); disposed = true;
    }
    private sealed class Subscription(NativeExtensionClient client, Action<ExtensionState> observer) : IDisposable
    {
        private bool disposed;
        public void Deliver(ExtensionState state)
        {
            if (disposed || !client.enabled || client.disposed) return;
            try { observer(state); }
            catch { Dispose(); } // One failing observer cannot break UI sampling or other clients.
        }
        public void Dispose()
        {
            if (disposed) return;
            client.host.Verify(); disposed = true; client.subscriptions.Remove(this);
        }
    }
}

