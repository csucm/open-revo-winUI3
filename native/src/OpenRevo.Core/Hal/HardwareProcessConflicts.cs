using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.Core.Hal;

internal static class HardwareProcessConflicts
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int NoMoreFiles = 18;
    private const int MaxPath = 260;

    public static string? Check()
    {
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
            return SnapshotError(Marshal.GetLastPInvokeError());

        var entry = new ProcessEntry { Size = (uint)Unsafe.SizeOf<ProcessEntry>() };
        if (!Process32FirstW(snapshot, ref entry))
            return SnapshotError(Marshal.GetLastPInvokeError());

        Span<char> lowerName = stackalloc char[MaxPath];
        do
        {
            ReadOnlySpan<ushort> executable = entry.Executable;
            var name = MemoryMarshal.Cast<ushort, char>(executable);
            int end = name.IndexOf('\0');
            if (end >= 0)
                name = name[..end];
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];

            int length = name.ToLowerInvariant(lowerName);
            ReadOnlySpan<char> normalized = lowerName[..length];
            if (normalized is "open-revo" or "openrevo" ||
                normalized.Contains("controlcenter", StringComparison.Ordinal) ||
                normalized.Contains("gamingcenter", StringComparison.Ordinal) ||
                normalized.Contains("oemservice", StringComparison.Ordinal) ||
                normalized.Contains("uniwillservice", StringComparison.Ordinal))
                return $"请先正常退出其他硬件控制程序：{normalized.ToString()}（PID {entry.ProcessId}）。";
        } while (Process32NextW(snapshot, ref entry));

        int error = Marshal.GetLastPInvokeError();
        return error == NoMoreFiles ? null : SnapshotError(error);
    }

    private static string SnapshotError(int error) => $"无法检查其他硬件控制程序（Windows 错误 {error}）。";

    [InlineArray(MaxPath)]
    private struct ExecutableName
    {
        private ushort firstCharacter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, ThreadCount, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        public ExecutableName Executable;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
