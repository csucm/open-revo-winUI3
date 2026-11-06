using System.Runtime.InteropServices;

namespace OpenRevo.Core;

internal enum WindowsGpuKind { Unknown, Integrated, Discrete, Virtual }

internal sealed record WindowsGpuAdapter(
    string Name, string Luid, uint Vendor, uint Device, uint Subsystem, uint Flags,
    ulong DedicatedBytes, ulong DedicatedSystemBytes, ulong SharedBytes, WindowsGpuKind Kind, bool Physical,
    uint? AdapterType, int TypeStatus, bool? DxCoreHardware, bool? DxCoreIntegrated,
    string? DevicePath, int DevicePathStatus, bool ArchitectureConflict, string Evidence)
{
    internal uint LuidLow { get; init; }
    internal int LuidHigh { get; init; }
}

internal sealed partial class WindowsGpuIdentity : IDisposable
{
    private nint factory;
    internal bool IsCurrent => factory != 0 && Method<FactoryCurrent>(factory, 13)(factory);
    internal int EnumerationStatus { get; private set; }

    internal WindowsGpuAdapter[] Enumerate()
    {
        Dispose();
        Guid dxgiId = new("770aae78-f26f-4dba-a829-253c83d1b387");
        EnumerationStatus = CreateDXGIFactory1(in dxgiId, out factory);
        if (EnumerationStatus < 0) return [];

        nint dxcore = 0;
        try
        {
            Guid coreId = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
            try { _ = DXCoreCreateAdapterFactory(in coreId, out dxcore); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }

            var enumerate = Method<EnumerateAdapter>(factory, 12);
            var adapters = new Dictionary<string, WindowsGpuAdapter>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < 32; index++)
            {
                int status = enumerate(factory, index, out nint adapter);
                if (status == unchecked((int)0x887A0002)) return [.. adapters.Values]; // DXGI_ERROR_NOT_FOUND
                if (status < 0 || adapter == 0)
                {
                    EnumerationStatus = status;
                    return [];
                }
                try
                {
                    int descriptionStatus = Method<Describe>(adapter, 10)(adapter, out var desc);
                    if (descriptionStatus < 0)
                    {
                        EnumerationStatus = descriptionStatus;
                        return [];
                    }
                    var candidate = Identify(desc, dxcore);
                    adapters.TryAdd(candidate.Luid, candidate);
                }
                finally { Marshal.Release(adapter); }
            }
            EnumerationStatus = unchecked((int)0x8007007A); // Enumeration exceeded the bound.
            return [];
        }
        finally { if (dxcore != 0) Marshal.Release(dxcore); }
    }

    private static WindowsGpuAdapter Identify(Description desc, nint dxcore)
    {
        string key = DisplayTopology.AdapterKey(desc.Low, desc.High);
        var luid = new AdapterLuid { Low = desc.Low, High = desc.High };
        var (path, pathStatus) = DisplayTopology.AdapterDevicePath(desc.Low, desc.High);
        var (hardware, integrated) = ReadDxCore(dxcore, luid);
        bool software = (desc.Flags & 2) != 0;
        bool remote = (desc.Flags & 1) != 0;
        bool pci = path?.StartsWith(@"\\?\PCI#", StringComparison.OrdinalIgnoreCase) == true
            && path.Contains($"VEN_{desc.Vendor:X4}&DEV_{desc.Device:X4}", StringComparison.OrdinalIgnoreCase);
        // Type inspection is an adapter metadata query. Leave NVIDIA on its
        // existing NVML path and avoid opening a kernel handle for it.
        var (type, typeStatus) = software || remote || desc.Vendor == 0x10de
            ? ((uint?)null, 0) : ReadAdapterType(luid);
        bool virtualDevice = software || remote || hardware == false ||
            type is uint flags && (flags & (4u | 64u | 128u)) != 0 ||
            (!pci && hardware != true && DisplayTopology.IsVirtual(desc.Name)) ||
            path?.StartsWith(@"\\?\ROOT#", StringComparison.OrdinalIgnoreCase) == true ||
            path?.StartsWith(@"\\?\SWD#", StringComparison.OrdinalIgnoreCase) == true;
        bool physical = !virtualDevice && (hardware == true || pci && type is uint t && (t & 1) != 0);
        bool hybridIntegrated = type is uint ti && (ti & 32) != 0;
        bool hybridDiscrete = type is uint td && (td & 16) != 0;
        bool contradictory = hybridIntegrated && (hybridDiscrete || integrated == false) ||
            hybridDiscrete && integrated == true;
        WindowsGpuKind kind = virtualDevice ? WindowsGpuKind.Virtual :
            !physical || contradictory ? WindowsGpuKind.Unknown :
            integrated == true || hybridIntegrated ? WindowsGpuKind.Integrated :
            integrated == false || hybridDiscrete ? WindowsGpuKind.Discrete : WindowsGpuKind.Unknown;
        string evidence = virtualDevice ? "virtual, remote or software evidence" :
            contradictory ? "conflicting architecture evidence" :
            integrated.HasValue && physical ? "DXCore IsIntegrated" :
            physical && (hybridIntegrated || hybridDiscrete) ? "D3DKMT hybrid type" :
            physical ? "physical adapter; architecture unknown" : "unconfirmed physical identity";
        return new(desc.Name, key, desc.Vendor, desc.Device, desc.Subsystem, desc.Flags,
            (ulong)desc.DedicatedVideo, (ulong)desc.DedicatedSystem, (ulong)desc.SharedSystem,
            kind, physical, type, typeStatus, hardware, integrated, path, pathStatus, contradictory, evidence)
        { LuidLow = desc.Low, LuidHigh = desc.High };
    }

    private static (bool? Hardware, bool? Integrated) ReadDxCore(nint factory, AdapterLuid luid)
    {
        if (factory == 0) return (null, null);
        Guid adapterId = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
        if (Method<GetByLuid>(factory, 4)(factory, in luid, in adapterId, out nint adapter) < 0 || adapter == 0)
            return (null, null);
        try
        {
            bool? Read(uint property) =>
                Method<HasProperty>(adapter, 5)(adapter, property) &&
                Method<GetProperty>(adapter, 6)(adapter, property, 1, out byte value) >= 0
                    ? value != 0 : null;
            return (Read(11), Read(12));
        }
        finally { Marshal.Release(adapter); }
    }

    private static (uint? Type, int Status) ReadAdapterType(AdapterLuid luid)
    {
        var open = new OpenAdapter { Luid = luid };
        int status = D3DKMTOpenAdapterFromLuid(ref open);
        if (status != 0) return (null, status);
        nint buffer = 0;
        try
        {
            buffer = Marshal.AllocHGlobal(sizeof(uint));
            var query = new AdapterQuery { Handle = open.Handle, Type = 15, Data = buffer, Size = sizeof(uint) };
            status = D3DKMTQueryAdapterInfo(ref query);
            return (status == 0 ? unchecked((uint)Marshal.ReadInt32(buffer)) : null, status);
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            D3DKMTCloseAdapter(in open.Handle);
        }
    }

    public void Dispose()
    {
        if (factory == 0) return;
        Marshal.Release(factory);
        factory = 0;
    }

    private static T Method<T>(nint pointer, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterLuid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { public AdapterLuid Luid; public uint Handle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterQuery { public uint Handle, Type; public nint Data; public uint Size; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public nuint DedicatedVideo, DedicatedSystem, SharedSystem;
        public uint Low;
        public int High;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumerateAdapter(nint self, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Describe(nint self, out Description description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool FactoryCurrent(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetByLuid(nint self, in AdapterLuid luid, in Guid iid, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool HasProperty(nint self, uint property);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetProperty(nint self, uint property, nuint size, out byte value);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [DllImport("dxcore.dll", ExactSpelling = true)]
    private static extern int DXCoreCreateAdapterFactory(in Guid iid, out nint factory);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter open);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTQueryAdapterInfo(ref AdapterQuery query);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTCloseAdapter(in uint handle);
}
