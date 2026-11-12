namespace OpenRevo.Core;

public sealed class SessionPowerRuntime : IDisposable
{
    private readonly object gate = new();
    private readonly RustSessionPower? native;
    private SessionPower? keepAwake, screenOff;
    private uint screenOffInput;
    private bool screenOffPending, disposed;

    public SessionPowerRuntime() { }
    internal SessionPowerRuntime(bool useNativePowerRequests) =>
        native = useNativePowerRequests ? new RustSessionPower() : null;

    public bool KeepAwakeEnabled
    {
        get
        {
            if (native != null) return native.KeepAwakeEnabled;
            lock (gate) return keepAwake?.Enabled == true;
        }
    }

    public bool ToggleKeepAwake()
    {
        if (native != null) return native.ToggleKeepAwake();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            keepAwake ??= new();
            keepAwake.SetKeepAwake(!keepAwake.Enabled);
            return keepAwake.Enabled;
        }
    }

    public Task<bool> TurnOffAsync(nint owner) => TurnOffAsync(() => SessionPower.TurnOffScreen(owner));

    public Task<bool> TurnOffAsync(Action sendScreenOff) =>
        native?.TurnOffAsync(sendScreenOff) ?? TurnOffLegacyAsync(sendScreenOff);

    private async Task<bool> TurnOffLegacyAsync(Action sendScreenOff)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (screenOffPending) return false;
            screenOffPending = true;
        }
        try
        {
            lock (gate)
            {
                if (disposed) return false;
                keepAwake?.ReleaseDisplay();
                screenOff ??= new();
                screenOff.HoldSystemOnly();
            }
            // The originating Windows adapter provides its own HWND and thread.
            await Task.Delay(350);
            lock (gate)
            {
                if (disposed) return false;
                screenOffInput = SessionPower.ReadLastInputTick();
                sendScreenOff();
                return true;
            }
        }
        catch
        {
            lock (gate)
            {
                if (!disposed)
                {
                    try { RestoreDisplay(); }
                    catch (Exception error) { DiagnosticLog.Failure("screen-off.restore.failed", error); }
                }
            }
            throw;
        }
        finally { lock (gate) screenOffPending = false; }
    }

    public void RestoreOnInput()
    {
        if (native != null)
        {
            native.RestoreOnInput();
            return;
        }
        lock (gate)
        {
            if (disposed || screenOff == null || screenOffPending || SessionPower.ReadLastInputTick() == screenOffInput)
                return;
            RestoreDisplay();
        }
    }

    private void RestoreDisplay()
    {
        try { screenOff?.Dispose(); }
        finally
        {
            screenOff = null;
            if (keepAwake?.Enabled == true) keepAwake.SetKeepAwake(true);
        }
    }

    public void Dispose()
    {
        if (native != null)
        {
            native.Dispose();
            return;
        }
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { keepAwake?.Dispose(); }
            finally
            {
                try { screenOff?.Dispose(); }
                finally { keepAwake = screenOff = null; }
            }
        }
    }
}
