using System.Globalization;
namespace OpenRevo.Core;

public sealed record TrayTelemetrySample(DateTimeOffset At, int? CpuTemperature, double? CpuUsage,
    int? GpuTemperature, double? GpuUsage, bool GpuReadAllowed)
{
    public string ToTooltip()
    {
        static string Temp(int? n) => n is > 0 and < 125 ? n.Value.ToString(CultureInfo.InvariantCulture) + "°C" : "—";
        static string Usage(double? n) => n is >= 0 and <= 100 && double.IsFinite(n.Value) ? n.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
        return $"Open-Revo\nCPU {Temp(CpuTemperature)} · 占用 {Usage(CpuUsage)}\nGPU {Temp(GpuReadAllowed?GpuTemperature:null)} · 占用 {Usage(GpuReadAllowed?GpuUsage:null)}" + (GpuReadAllowed ? "" : "（节能限制/未读取）");
    }
}

// On-demand only: no timer, worker thread or UI framework. Caller owns resource lifetime.
public sealed class TrayTelemetryCache(TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private TrayTelemetrySample? sample;
    private Task<TrayTelemetrySample>? inFlight;
    public Task<TrayTelemetrySample> RequestAsync(Func<Task<TrayTelemetrySample>> read)
    {
        lock (gate)
        {
            if (inFlight is { IsCompleted: false }) return inFlight;
            if (sample is { } cached && clock.GetUtcNow() - cached.At is { TotalSeconds: >= 0 and < 5 }) return Task.FromResult(cached);
            return inFlight = ReadAsync(read);
        }
    }
    private async Task<TrayTelemetrySample> ReadAsync(Func<Task<TrayTelemetrySample>> read)
    {
        // Yield before calling user code so synchronous completion cannot race assignment.
        await Task.Yield();
        TrayTelemetrySample next;
        try { next = await read().ConfigureAwait(false); }
        catch { next = new(clock.GetUtcNow(), null, null, null, null, false); }
        lock (gate) sample = next;
        return next;
    }
}


