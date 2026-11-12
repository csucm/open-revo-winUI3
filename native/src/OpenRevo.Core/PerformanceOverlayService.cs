namespace OpenRevo.Core;

public sealed class PerformanceOverlayReceipt
{
    internal PerformanceOverlayReceipt(int mode) => Mode = mode;
    public int Mode { get; }
}

// Callers retain their existing command queue. This service owns confirmation and retry policy only.
public sealed class PerformanceOverlayService
{
    private readonly object gate = new();
    private int confirmedMode, failedMode, failures;
    private DateTimeOffset retryAfter;

    public bool ShouldSynchronize(int mode, DateTimeOffset now)
    {
        lock (gate) return mode != 0 && mode != confirmedMode && (mode != failedMode || now >= retryAfter);
    }
    public PerformanceOverlayReceipt Apply(UpstreamCoreLibrary core, int mode)
    {
        // Even a failed command may have changed Windows state. Invalidate the old confirmation first.
        lock (gate) confirmedMode = 0;
        DesktopPower.SetOverlay(core, mode);
        return new(mode);
    }
    public void Confirm(PerformanceOverlayReceipt receipt)
    {
        lock (gate)
        {
            confirmedMode = receipt.Mode;
            failedMode = failures = 0;
            retryAfter = default;
        }
    }
    public bool RecordFailure(int mode, DateTimeOffset now)
    {
        lock (gate)
        {
            bool report = mode != failedMode;
            confirmedMode = 0;
            failures = report ? 1 : Math.Min(failures + 1, 3);
            failedMode = mode;
            retryAfter = now.AddSeconds(failures switch { 1 => 3, 2 => 15, _ => 60 });
            return report;
        }
    }
    public void Reset()
    {
        lock (gate)
        {
            confirmedMode = failedMode = failures = 0;
            retryAfter = default;
        }
    }
}
