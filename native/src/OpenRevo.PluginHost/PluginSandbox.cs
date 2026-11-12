using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OpenRevo.PluginHost;

/// <summary>Windows-enforced sandbox. No ordinary-process fallback exists.</summary>
internal sealed class PluginSandbox : IDisposable
{
    private readonly string profile = "OpenRevo.Plugin." + Guid.NewGuid().ToString("N");
    private nint sid;
    private KernelHandle? process, job;
    internal string Sid { get; private set; } = "";
    internal string TemporaryDirectory { get; private set; } = "";
    private bool disposed;

    internal PluginSandbox()
    {
        int result = SandboxNative.CreateAppContainerProfile(profile, "Open-Revo plugin", "Isolated native plugin", 0, 0, out sid);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            Sid = new SecurityIdentifier(sid).Value;
            Marshal.ThrowExceptionForHR(SandboxNative.GetAppContainerFolderPath(Sid, out nint path));
            try { TemporaryDirectory = Marshal.PtrToStringUni(path) ?? throw new IOException("Missing sandbox folder."); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        catch { Dispose(); throw; }
    }

    internal void Start(string worker, string entry, string type, SafePipeHandle endpoint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (process != null) throw new InvalidOperationException("Sandbox already started.");
        nint attributes = 0, caps = 0, child = 0, handles = 0, environment = 0;
        bool initialized = false;
        try
        {
            job = SandboxNative.CreateJobObjectW(0, null);
            if (job.IsInvalid) throw SandboxNative.Error();
            SandboxNative.SetJob(job, 9, new SandboxNative.JobExtendedLimit
            {
                Basic = new() { Flags = 0x2000 | 0x100 | 0x8 | 0x400, ProcessLimit = 1 }, // kill, memory, one process, no error dialog
                ProcessMemory = 256u * 1024 * 1024
            });
            SandboxNative.SetJob(job, 15, new SandboxNative.CpuLimit { Flags = 0x1 | 0x4, Rate = 2000 });
            SandboxNative.SetJob(job, 4, 0xffu); // worker has no desktop/UI privileges
            nuint bytes = 0;
            SandboxNative.InitializeProcThreadAttributeList(0, 3, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            if (!SandboxNative.InitializeProcThreadAttributeList(attributes, 3, 0, ref bytes)) throw SandboxNative.Error();
            initialized = true;
            caps = Marshal.AllocHGlobal(Marshal.SizeOf<SandboxNative.SecurityCapabilities>());
            Marshal.StructureToPtr(new SandboxNative.SecurityCapabilities { Sid = sid }, caps, false);
            if (!SandboxNative.UpdateProcThreadAttribute(attributes, 0, 0x20009, caps,
                (nuint)Marshal.SizeOf<SandboxNative.SecurityCapabilities>(), 0, 0)) throw SandboxNative.Error();
            child = Marshal.AllocHGlobal(sizeof(uint)); Marshal.WriteInt32(child, 1); // child process creation restricted
            if (!SandboxNative.UpdateProcThreadAttribute(attributes, 0, 0x2000e, child, sizeof(uint), 0, 0)) throw SandboxNative.Error();
            if (!SandboxNative.SetHandleInformation(endpoint, 1, 1)) throw SandboxNative.Error();
            nint pipeHandle = endpoint.DangerousGetHandle();
            handles = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(handles, pipeHandle);
            if (!SandboxNative.UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)IntPtr.Size, 0, 0))
                throw SandboxNative.Error();
            // Do not inherit PATH, runtime startup hooks, profiler settings, secrets or arbitrary user environment.
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // Windows uses the profile paths when constructing the AppContainer environment.
            // Filesystem access still comes from the verified token and ACL, never these values.
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', new[]
            {
                "APPDATA=" + roamingAppData, "COMPlus_EnableDiagnostics=0", "DOTNET_EnableDiagnostics=0",
                "LOCALAPPDATA=" + localAppData, "SystemDrive=" + Path.GetPathRoot(systemRoot)!.TrimEnd('\\'),
                "SystemRoot=" + systemRoot, "TEMP=" + TemporaryDirectory, "TMP=" + TemporaryDirectory,
                "USERPROFILE=" + userProfile, "WINDIR=" + systemRoot
            }) + "\0\0");
            var startup = new SandboxNative.StartupInfoEx
            {
                Info = new() { Size = (uint)Marshal.SizeOf<SandboxNative.StartupInfoEx>() }, Attributes = attributes
            };
            var args = new[] { worker, "--pipe-handle", pipeHandle.ToString(System.Globalization.CultureInfo.InvariantCulture), "--entry", entry, "--type", type };
            var command = new StringBuilder(string.Join(' ', args.Select(Quote)));
            // suspended: verify token and apply the Job before executing even the worker's first instruction.
            if (!SandboxNative.CreateProcessW(worker, command, 0, 0, true, 0x80000 | 0x4 | 0x8000000 | 0x400,
                environment, Path.GetDirectoryName(worker)!, ref startup, out var info)) throw SandboxNative.Error();
            process = new KernelHandle(info.Process);
            using var thread = new KernelHandle(info.Thread);
            VerifyToken();
            if (!SandboxNative.AssignProcessToJobObject(job, process)) throw SandboxNative.Error();
            if (SandboxNative.ResumeThread(thread) == uint.MaxValue) throw SandboxNative.Error();
        }
        catch { Dispose(); throw; }
        finally
        {
            if (initialized) SandboxNative.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (caps != 0) Marshal.FreeHGlobal(caps);
            if (child != 0) Marshal.FreeHGlobal(child);
            if (handles != 0) Marshal.FreeHGlobal(handles);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    internal uint? ExitCode() =>
        process is { IsClosed: false } && SandboxNative.GetExitCodeProcess(process, out uint code) && code != 259 ? code : null;

    private void VerifyToken()
    {
        if (process == null || !SandboxNative.OpenProcessToken(process, 0x8, out var token)) throw SandboxNative.Error();
        using (token)
        {
            nint buffer = Marshal.AllocHGlobal(1024);
            try
            {
                if (!SandboxNative.GetTokenInformation(token, 29, buffer, 1024, out _) || Marshal.ReadInt32(buffer) != 1)
                    throw new UnauthorizedAccessException("Plugin process is not an AppContainer.");
                if (!SandboxNative.GetTokenInformation(token, 31, buffer, 1024, out _) ||
                    new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value != Sid)
                    throw new UnauthorizedAccessException("Plugin AppContainer identity mismatch.");
                if (!SandboxNative.GetTokenInformation(token, 30, buffer, 1024, out _) || Marshal.ReadInt32(buffer) != 0)
                    throw new UnauthorizedAccessException("Unexpected OS capabilities in plugin token.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    internal static void ProtectDirectory(string path, string? sandboxSid = null)
    {
        PluginPackage.RejectReparseAncestors(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Missing current user SID.");
        acl.SetOwner(user);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (sandboxSid != null)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sandboxSid), FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char value in argument)
        {
            if (value == '\\') { slashes++; continue; }
            if (value == '"') result.Append('\\', slashes * 2 + 1).Append(value);
            else result.Append('\\', slashes).Append(value);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        bool exited = process == null || process.IsInvalid;
        try
        {
            if (!exited)
            {
                // Also kills a suspended child if startup failed before assignment to the Job.
                SandboxNative.TerminateProcess(process!, 1);
                job?.Dispose();
                uint wait = SandboxNative.WaitForSingleObject(process!, 5000);
                exited = wait == 0;
                if (!exited) throw new IOException("Plugin worker termination could not be confirmed.");
            }
        }
        finally
        {
            process?.Dispose(); job?.Dispose();
            if (exited)
            {
                int result = SandboxNative.DeleteAppContainerProfile(profile);
                if (result < 0) OpenRevo.Core.DiagnosticLog.Record("plugin.profile_cleanup_failed", new { result });
            }
            if (sid != 0) { SandboxNative.FreeSid(sid); sid = 0; }
        }
    }
}
