using System.Runtime.InteropServices;

namespace OpenRevo.Core;

internal sealed partial class WindowsGpuIdentity
{
    internal (int? CoreMhz, int? MemoryMhz, int Status) ReadIntegratedClocks(
        WindowsGpuAdapter adapter, ref uint? graphicsNode, ref bool inspectedNodes)
    {
        var open = new OpenAdapter { Luid = new AdapterLuid { Low = adapter.LuidLow, High = adapter.LuidHigh } };
        int status = D3DKMTOpenAdapterFromLuid(ref open);
        if (status != 0) return (null, null, status);

        // Windows SDK 26100 x64 layout from d3dkmthk.h and d3dkmdt.h.
        const uint nodeMetadata = 25, physicalAdapterCount = 30, nodePerfData = 61, adapterPerfData = 62;
        const int nodeMetadataSize = 80, nodePerfDataSize = 56, adapterPerfDataSize = 64;
        nint buffer = 0;
        try
        {
            buffer = Marshal.AllocHGlobal(nodeMetadataSize);
            int Query(uint type, int size, uint index)
            {
                Marshal.Copy(zeroClockBuffer, 0, buffer, size);
                Marshal.WriteInt32(buffer, (int)index);
                var query = new AdapterQuery { Handle = open.Handle, Type = type, Data = buffer, Size = (uint)size };
                return D3DKMTQueryAdapterInfo(ref query);
            }

            status = Query(physicalAdapterCount, sizeof(uint), 0);
            if (status != 0 || Marshal.ReadInt32(buffer) != 1)
                return (null, null, status != 0 ? status : unchecked((int)0xC00000BB)); // STATUS_NOT_SUPPORTED

            if (!inspectedNodes)
            {
                for (uint node = 0; node < 32; node++)
                {
                    // PhysicalAdapterIndex is zero because this is a single physical adapter.
                    if (Query(nodeMetadata, nodeMetadataSize, node) != 0) break;
                    if (Marshal.ReadInt32(buffer, 4) != 1) continue; // DXGK_ENGINE_TYPE_3D
                    graphicsNode = node;
                    break;
                }
                inspectedNodes = true;
            }

            // Both performance structures store the current clock at byte offset 8.
            int coreStatus = graphicsNode is uint ordinal ? Query(nodePerfData, nodePerfDataSize, ordinal) : 0;
            int? core = graphicsNode.HasValue && coreStatus == 0 ? ClockMhz(Marshal.ReadInt64(buffer, 8)) : null;
            int memoryStatus = Query(adapterPerfData, adapterPerfDataSize, 0);
            int? memory = memoryStatus == 0 ? ClockMhz(Marshal.ReadInt64(buffer, 8)) : null;
            return (core, memory, coreStatus != 0 ? coreStatus : memoryStatus);
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            D3DKMTCloseAdapter(in open.Handle);
        }
    }

    private static readonly byte[] zeroClockBuffer = new byte[80];
    private static int? ClockMhz(long hz) => hz is >= 1_000_000 and <= 20_000_000_000 ? (int)(hz / 1_000_000) : null;
}
