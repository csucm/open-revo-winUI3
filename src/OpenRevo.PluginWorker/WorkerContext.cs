using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginWorker;

internal sealed class WorkerContext : IPluginContext, IAsyncDisposable
{
    private readonly PluginChannel channel;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly SemaphoreSlim viewGate = new(1, 1);
    private long lastView;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<PluginEvent> events = Channel.CreateBounded<PluginEvent>(16);
    private readonly Task receive;
    private readonly object sync = new();
    private TaskCompletionSource<JsonElement>? pending;
    private long nextId, pendingId;
    private string? pendingMethod;
    public CancellationToken CancellationToken => lifetime.Token;
    public Task Completion => receive;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal WorkerContext(PluginChannel channel) { this.channel = channel; receive = ReceiveAsync(); }

    public Task<PluginCapabilities> QueryCapabilitiesAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginCapabilities>(PluginMethods.CapabilitiesQuery, new { }, cancellationToken);
    public Task<PluginCapabilityCatalog> QueryCatalogAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginCapabilityCatalog>(PluginMethods.CatalogQuery, new { }, cancellationToken);
    public Task<PluginThemeSchema> QueryThemeSchemaAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginThemeSchema>(PluginMethods.ThemeSchemaQuery, new { }, cancellationToken);
    public Task<PluginResult> SubscribeSummaryAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginResult>(PluginMethods.StateSubscribe, new PluginTopic("system.summary"), cancellationToken);
    public Task<PluginResult> SubscribePowerAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginResult>(PluginMethods.StateSubscribe, new PluginTopic("system.power"), cancellationToken);
    public Task<PluginResult> UnsubscribeSummaryAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginResult>(PluginMethods.StateUnsubscribe, new PluginTopic("system.summary"), cancellationToken);
    public Task<PluginResult> UnsubscribePowerAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginResult>(PluginMethods.StateUnsubscribe, new PluginTopic("system.power"), cancellationToken);
    public Task<PluginResult> LoadConfigAsync(CancellationToken cancellationToken = default)
        => CallAsync<PluginResult>(PluginMethods.ConfigLoad, new { }, cancellationToken);
    public Task<PluginResult> SaveConfigAsync<T>(T config, CancellationToken cancellationToken = default)
        => CallAsync<PluginResult>(PluginMethods.ConfigSave, new PluginConfig(JsonSerializer.SerializeToElement(config)), cancellationToken);
    public Task<PluginResult> ExecuteAsync(string action, JsonElement arguments, CancellationToken cancellationToken = default)
        => CallAsync<PluginResult>(PluginMethods.ActionExecute, new PluginAction(action, arguments), cancellationToken);
    public async Task<PluginResult> SetViewAsync(PluginView view, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await viewGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (lastView != 0)
            {
                TimeSpan wait = TimeSpan.FromMilliseconds(260) - Stopwatch.GetElapsedTime(lastView);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, linked.Token).ConfigureAwait(false);
            }
            var result = await CallAsync<PluginResult>("ui.view.set", view, linked.Token).ConfigureAwait(false);
            lastView = Stopwatch.GetTimestamp(); return result;
        }
        finally { viewGate.Release(); }
    }
    public async IAsyncEnumerable<PluginEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await foreach (var item in events.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false)) yield return item;
    }
    private async Task<T> CallAsync<T>(string method, object payload, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        await requestGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            long id = Interlocked.Increment(ref nextId);
            lock (sync) { pending = source; pendingId = id; pendingMethod = method; }
            await channel.SendAsync(PluginMessageKind.Request, id, method, payload, linked.Token).ConfigureAwait(false);
            JsonElement response = await source.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return response.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing host response.");
        }
        catch { lifetime.Cancel(); await channel.DisposeAsync().ConfigureAwait(false); throw; }
        finally { lock (sync) pending = null; requestGate.Release(); }
    }
    private async Task ReceiveAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                if (message.Kind == PluginMessageKind.Event)
                {
                    if (!events.Writer.TryWrite(new(message.Method, message.Payload)))
                        throw new InvalidDataException("Plugin did not consume host events.");
                }
                else if (message.Kind == PluginMessageKind.Response)
                {
                    lock (sync)
                    {
                        if (message.RequestId != pendingId || message.Method != pendingMethod || pending == null ||
                            !pending.TrySetResult(message.Payload)) throw new InvalidDataException("Unexpected host response.");
                    }
                }
                else throw new InvalidDataException("Invalid host message direction.");
            }
        }
        catch (Exception error)
        {
            lock (sync) pending?.TrySetException(error);
        }
        finally { lifetime.Cancel(); events.Writer.TryComplete(); }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); await channel.DisposeAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
    }
}
