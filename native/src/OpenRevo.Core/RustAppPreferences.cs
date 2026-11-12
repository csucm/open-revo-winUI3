using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenRevo.Core;

internal static class RustAppPreferences
{
    private const string Bridge = "openrevo_native_bridge.dll";
    private const int SnapshotCapacity = 16384;
    private const int InvalidArgument = unchecked((int)0x80070057);

    internal static AppPreferences Load(string directory, bool forUpdate = false)
    {
        var bytes = new byte[SnapshotCapacity];
        int size = LoadNative(directory, (nuint)directory.Length, forUpdate ? 1u : 0u,
            bytes, (nuint)bytes.Length);
        return Decode(bytes, size, "读取");
    }

    internal static void Save(string directory, AppPreferences preferences)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(preferences);
        int result = SaveNative(directory, (nuint)directory.Length, bytes, (nuint)bytes.Length);
        if (result == InvalidArgument) throw new ArgumentException("应用设置无效或超出文件大小限制。");
        if (result < 0) throw new IOException($"无法保存应用设置 (0x{result:X8})。");
    }

    internal static AppPreferences Edit<T>(string directory, T edit)
    {
        byte[] input = JsonSerializer.SerializeToUtf8Bytes(edit);
        var output = new byte[SnapshotCapacity];
        int size = EditNative(directory, (nuint)directory.Length, input, (nuint)input.Length,
            output, (nuint)output.Length);
        if (size == InvalidArgument) throw new ArgumentException("应用设置无效或超出文件大小限制。");
        return Decode(output, size, "保存");
    }

    private static AppPreferences Decode(byte[] bytes, int size, string action)
    {
        if (size <= 0 || size > bytes.Length) throw new IOException($"无法{action}应用设置 (0x{size:X8})。");
        return JsonSerializer.Deserialize<AppPreferences>(bytes.AsSpan(0, size))
            ?? throw new IOException("原生应用设置快照无效。");
    }

    [DllImport(Bridge, EntryPoint = "or_native_app_preferences_load", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int LoadNative([MarshalAs(UnmanagedType.LPWStr)] string directory, nuint length,
        uint strict, [Out] byte[] output, nuint capacity);

    [DllImport(Bridge, EntryPoint = "or_native_app_preferences_save", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int SaveNative([MarshalAs(UnmanagedType.LPWStr)] string directory, nuint length,
        byte[] input, nuint bytes);

    [DllImport(Bridge, EntryPoint = "or_native_app_preferences_edit", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int EditNative([MarshalAs(UnmanagedType.LPWStr)] string directory, nuint length,
        byte[] input, nuint bytes, [Out] byte[] output, nuint capacity);
}
