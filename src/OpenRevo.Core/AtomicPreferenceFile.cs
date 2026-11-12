namespace OpenRevo.Core;

// A bounded retry for short-lived readers/scanners that deny replacement.
// Never delete the destination first: a failed save must keep the old file.
internal static class AtomicPreferenceFile
{
    internal static void Write(string path, string content)
    {
        string temporary = path + "-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); return; }
                catch (Exception error) when (attempt < 5 && Retryable(error))
                {
                    Thread.Sleep(20 << attempt); // Total delay at most 620 ms.
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Cleanup must not mask the original replacement failure.
                DiagnosticLog.Failure("preferences.temporary_cleanup", error, throttle: true);
            }
        }
    }

    private static bool Retryable(Exception error) =>
        error is IOException or UnauthorizedAccessException &&
        (error.HResult & 0xffff) is 5 or 32 or 33;
}
