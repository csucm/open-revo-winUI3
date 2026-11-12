using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.Resident;

internal static class ResidentPanelLaunch
{
    private const string Bridge = "openrevo_native_bridge.dll";

    internal static SafeAccessTokenHandle OpenDesktopToken()
    {
        int result = OpenToken(out var token);
        if (result == 0 && !token.IsInvalid) return token;
        token.Dispose();
        Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
        throw new IOException("无法取得 Windows 桌面的普通权限令牌。");
    }

    internal static ResidentPanelChild Start(SafeAccessTokenHandle token, string executable, string folder, string pipe)
    {
        int result = StartNative(token, executable, folder, pipe, out var handle, out int pid);
        try
        {
            Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
            if (pid <= 0 || handle.IsInvalid) throw new IOException("面板启动未返回有效的进程句柄。");
            return new ResidentPanelChild(handle, pid);
        }
        catch
        {
            if (!handle.IsInvalid) ResidentPanelChild.AbortLaunch(handle, pid);
            handle.Dispose();
            throw;
        }
    }

    [DllImport(Bridge, EntryPoint = "or_native_resident_desktop_token", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int OpenToken(out SafeAccessTokenHandle token);

    [DllImport(Bridge, EntryPoint = "or_native_resident_panel_start", ExactSpelling = true, CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int StartNative(SafeAccessTokenHandle token, string executable, string folder, string pipe,
        out SafeWaitHandle process, out int pid);
}

internal sealed class ResidentPanelChild : IDisposable
{
    private sealed class ProcessWait : WaitHandle
    {
        internal ProcessWait(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private readonly Process? candidate;
    private readonly ProcessWait? installed;
    private readonly RegisteredWaitHandle? registration;

    internal ResidentPanelChild(Process candidate)
    {
        this.candidate = candidate;
        Id = candidate.Id;
        Exit = candidate.WaitForExitAsync();
    }

    internal ResidentPanelChild(SafeWaitHandle handle, int pid)
    {
        Id = pid;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        installed = new ProcessWait(handle);
        registration = ThreadPool.RegisterWaitForSingleObject(installed,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited, Timeout.Infinite, executeOnlyOnce: true);
        Exit = exited.Task;
    }

    internal int Id { get; }
    internal Task Exit { get; }

    internal int ExitCode
    {
        get
        {
            if (candidate != null) return candidate.ExitCode;
            if (!GetExitCodeProcess(installed!.SafeWaitHandle, out int code)) throw LastError();
            return code;
        }
    }

    internal void Terminate()
    {
        if (candidate != null)
        {
            if (TerminateCandidate(candidate.SafeHandle, 1)) return;
            var error = LastError();
            if (!candidate.HasExited) throw error;
        }
        else if (!TerminateInstalled(installed!.SafeWaitHandle, 1))
        {
            var error = LastError();
            if (!installed.WaitOne(0)) throw error;
        }
    }

    internal static void AbortLaunch(SafeWaitHandle handle, int pid)
    {
        if (!TerminateInstalled(handle, 1))
            OpenRevo.Core.DiagnosticLog.Failure("resident.panel.launch_abort_failed", LastError(), new { processId = pid });
    }

    public void Dispose()
    {
        registration?.Unregister(null);
        installed?.Dispose();
        candidate?.Dispose();
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", ExactSpelling = true, SetLastError = true)]
    private static extern bool TerminateCandidate(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", ExactSpelling = true, SetLastError = true)]
    private static extern bool TerminateInstalled(SafeWaitHandle process, uint exitCode);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out int exitCode);
}
