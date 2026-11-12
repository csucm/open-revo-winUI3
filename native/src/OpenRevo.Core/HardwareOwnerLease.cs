namespace OpenRevo.Core;

public static class HardwareOwnerLease
{
    private const string Name = @"Global\OpenRevo.RevoCore.HardwareOwner";

    // Keep the returned handle for the lifetime of the app-level hardware owner.
    // Object creation is atomic across sessions. There is no thread-owned lock.
    public static Mutex? TryReserve()
    {
        try
        {
            var lease = new Mutex(false, Name, out bool created);
            if (created) return lease;
            lease.Dispose();
        }
        catch (UnauthorizedAccessException) { }
        catch (WaitHandleCannotBeOpenedException) { }
        return null;
    }
}
