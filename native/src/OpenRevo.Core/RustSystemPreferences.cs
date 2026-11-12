using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenRevo.Core;

internal static class RustSystemPreferences
{
    private const string Bridge = "openrevo_native_bridge.dll";
    private const int MaxBytes = 8192;

    internal static SystemPreferences Load(string directory)
    {
        var bytes = new byte[MaxBytes];
        int size = LoadNative(directory, (nuint)directory.Length, bytes, (nuint)bytes.Length);
        return Decode(bytes, size, "读取");
    }

    internal static void Save(string directory, SystemPreferences preferences)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(preferences);
        int result = SaveNative(directory, (nuint)directory.Length, bytes, (nuint)bytes.Length);
        if (result == unchecked((int)0x80070057)) throw new ArgumentException("系统设置无效。");
        if (result < 0) throw new IOException($"无法保存系统设置 (0x{result:X8})。");
    }

    internal static SystemPreferences Edit<T>(string directory, T edit)
    {
        byte[] input = JsonSerializer.SerializeToUtf8Bytes(edit);
        var snapshot = new byte[MaxBytes];
        int size = EditNative(directory, (nuint)directory.Length, input, (nuint)input.Length,
            snapshot, (nuint)snapshot.Length);
        if (size == unchecked((int)0x80070057)) throw new ArgumentException("系统设置无效。");
        return Decode(snapshot, size, "保存");
    }

    private static SystemPreferences Decode(byte[] bytes, int size, string action)
    {
        if (size <= 0 || size > bytes.Length)
            throw new IOException($"无法{action}系统设置 (0x{size:X8})。");
        return JsonSerializer.Deserialize<SystemPreferences>(bytes.AsSpan(0, size))
            ?? throw new IOException("原生系统设置快照无效。");
    }

    [DllImport(Bridge, EntryPoint = "or_native_system_preferences_load", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int LoadNative([MarshalAs(UnmanagedType.LPWStr)] string directory, nuint length,
        [Out] byte[] output, nuint capacity);

    [DllImport(Bridge, EntryPoint = "or_native_system_preferences_save", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int SaveNative([MarshalAs(UnmanagedType.LPWStr)] string directory, nuint length,
        byte[] input, nuint bytes);

    [DllImport(Bridge, EntryPoint = "or_native_system_preferences_edit", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int EditNative([MarshalAs(UnmanagedType.LPWStr)] string directory, nuint length,
        byte[] input, nuint bytes, [Out] byte[] output, nuint capacity);
}
