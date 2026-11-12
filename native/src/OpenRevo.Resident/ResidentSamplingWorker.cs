using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using OpenRevo.Core;

namespace OpenRevo.Resident;

// Private redirected handles carry only sensor requests. There is no endpoint
// for settings, command execution, EC access, takeover or plugin invocation.
internal static class ResidentSamplingWorker
{
    internal sealed record Request(int Operation, bool SampleGpu, bool? DiscreteDeclared, int Generation, int? CoreCpu);
    internal sealed record Reply(OverviewTelemetry? Overview = null, TemperatureReading? Temperature = null,
        JsonElement? Diagnostics = null);
    private const int MaxFrame = 64 * 1024;

    internal static async Task<int> RunAsync()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var watchdog = new Timer(_ => Environment.Exit(5), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        ReadOnlyTelemetryReader? reader = null;
        int? generation = null;
        try
        {
            while (true)
            {
                // Also ends an orphan if its parent disappeared or stopped sending.
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var request = await ReadAsync<Request>(input, idle.Token).ConfigureAwait(false);
                if (request.Operation is not (1 or 2 or 3 or 4)) return 2;
                // Native driver calls cannot be cancelled halfway. This worker has
                // no hardware transaction or settings, so a hang can end its process.
                watchdog.Change(TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
                reader ??= new(Path.Combine(AppContext.BaseDirectory, "openrevo_native_bridge.dll"));
                if (generation != request.Generation)
                {
                    DisplayTopology.InvalidateGpuIdentity();
                    generation = request.Generation;
                }
                Reply reply = request.Operation switch
                {
                    1 => new(Overview: reader.ReadOverviewTelemetry(request.SampleGpu, request.DiscreteDeclared)),
                    2 => new(Temperature: reader.ReadTemperature(request.SampleGpu, request.CoreCpu)),
                    3 => new(Diagnostics: JsonSerializer.SerializeToElement(reader.GpuTelemetryDiagnostics)),
                    _ => ReleaseGpu(reader)
                };
                await WriteAsync(output, reply, idle.Token).ConfigureAwait(false);
                watchdog.Change(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
            }
        }
        catch (EndOfStreamException) { return 0; }
        catch (OperationCanceledException) { return 0; }
        catch { return 1; }
        finally { reader?.Dispose(); }
    }

    private static Reply ReleaseGpu(ReadOnlyTelemetryReader reader)
    {
        reader.ReleaseGpu();
        return new();
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaxFrame) throw new IOException("Invalid sensor frame size.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new IOException("Invalid sensor response.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length is < 2 or > MaxFrame) throw new IOException("Sensor frame exceeds its bounds.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ResidentSamplingClient : IResidentTelemetrySource
{
    private readonly object gate = new();
    private Process? worker;
    private bool disposed, retiring;
    private long retryAfter;

    public int? ProcessId { get { lock (gate) return worker?.Id; } }
    public object? GpuTelemetryDiagnostics
    {
        get
        {
            lock (gate)
            {
                // Exporting a report must not start an idle sampler.
                if (disposed || retiring || worker?.HasExited != false)
                    return new { samplingState = "idle-released", processId = (int?)null };
                return Send(new(3, false, null, DisplayTopology.GpuGeneration, null)).Diagnostics;
            }
        }
    }

    public OverviewTelemetry ReadOverviewTelemetry(bool sampleGpu, bool? discreteDeclared = null)
    {
        int generation = DisplayTopology.GpuGeneration;
        var reply = Send(new(1, sampleGpu, discreteDeclared, generation, null));
        return (reply.Overview ?? throw new IOException("Sampling overview unavailable.")) with { TopologyGeneration = generation };
    }

    public TemperatureReading ReadTemperature(bool sampleGpu, int? coreCpu) =>
        Send(new(2, sampleGpu, null, DisplayTopology.GpuGeneration, coreCpu)).Temperature
            ?? throw new IOException("Sampling temperature unavailable.");

    public void ReleaseGpu()
    {
        lock (gate)
        {
            if (!disposed && !retiring && worker?.HasExited == false)
            {
                try { Send(new(4, false, null, DisplayTopology.GpuGeneration, null)); }
                catch (Exception error)
                {
                    // Demand changes must not end the resident sampling loop
                    // when the disposable worker has already failed.
                    DiagnosticLog.Failure("resident.telemetry.gpu_release_failed", error, throttle: true);
                }
            }
        }
    }

    private ResidentSamplingWorker.Reply Send(ResidentSamplingWorker.Request request)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Stopwatch.GetTimestamp() < retryAfter) throw new IOException("Sensor worker is waiting before retry.");
            Task<ResidentSamplingWorker.Reply>? exchange = null;
            try
            {
                if (retiring) StopWorker();
                if (worker?.HasExited != false)
                {
                    StopWorker();
                    string directory = UserDataStore.ValidateDirectory(AppContext.BaseDirectory);
                    string executable = Path.Combine(directory, "OpenRevo.Resident.exe");
                    if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Sampling executable is unavailable.");
                    worker = Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                        RedirectStandardInput = true, RedirectStandardOutput = true,
                        ArgumentList = { "--sampling-worker" }
                    }) ?? throw new IOException("Could not start sensor worker.");
                    DiagnosticLog.Record("resident.telemetry.worker_started", new { processId = worker.Id });
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                exchange = ExchangeAsync(worker, request, deadline.Token);
                // Anonymous Windows pipes may have a synchronous underlying handle.
                // Bound the wait even when cancelling that handle cannot interrupt I/O.
                return exchange.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch
            {
                retryAfter = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                if (exchange != null)
                    _ = exchange.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                // Never fall back to loading these drivers in the resident.
                StopWorker();
                throw;
            }
        }
    }

    private static async Task<ResidentSamplingWorker.Reply> ExchangeAsync(Process worker,
        ResidentSamplingWorker.Request request, CancellationToken cancellationToken)
    {
        await ResidentSamplingWorker.WriteAsync(worker.StandardInput.BaseStream, request, cancellationToken).ConfigureAwait(false);
        return await ResidentSamplingWorker.ReadAsync<ResidentSamplingWorker.Reply>(worker.StandardOutput.BaseStream, cancellationToken)
            .ConfigureAwait(false);
    }

    public void ReleaseSamplingResources() { lock (gate) StopWorker(); }

    private void StopWorker()
    {
        if (worker == null) return;
        var child = worker;
        retiring = true;
        child.StandardInput.Close();
        if (!child.WaitForExit(1000))
        {
            child.Kill();
            if (!child.WaitForExit(2000)) throw new IOException("Sampling worker exit was not confirmed.");
        }
        // Retain an unconfirmed child's handle so a retry cannot create a second
        // sampler while the first is still alive.
        worker = null;
        retiring = false;
        try { DiagnosticLog.Record("resident.telemetry.worker_exited", new { processId = child.Id, child.ExitCode }); }
        finally { child.Dispose(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            StopWorker();
        }
    }
}
