using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.ResidentProtocol;

public static class PanelPipe
{
    public static bool IsServer(PipeStream stream, int processId, int sessionId) =>
        GetNamedPipeServerProcessId(stream.SafePipeHandle, out uint actualProcess) && actualProcess == processId &&
        GetNamedPipeServerSessionId(stream.SafePipeHandle, out uint actualSession) && actualSession == sessionId;

    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);
}
