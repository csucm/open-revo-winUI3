using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        int failureStage = 10;
        try
        {
            // Refuse direct CLI execution before loading any third-party assembly.
            using var identity = WindowsIdentity.GetCurrent();
            nint data = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(identity.AccessToken, 29, data, sizeof(int), out _) || Marshal.ReadInt32(data) != 1) return 2;
            }
            finally { Marshal.FreeHGlobal(data); }
            if (args.Length != 6 || args[0] != "--pipe-handle" || args[2] != "--entry" || args[4] != "--type" ||
                !nint.TryParse(args[1], out nint endpoint) || endpoint <= 0) return 2;
            string entry = Path.GetFullPath(args[3]);
            string pluginRoot = Path.Combine(AppContext.BaseDirectory, "plugin") + Path.DirectorySeparatorChar;
            if (!string.Equals(Path.GetDirectoryName(entry) + Path.DirectorySeparatorChar, pluginRoot, StringComparison.OrdinalIgnoreCase)) return 2;
            failureStage = 11;
            using var handle = new SafePipeHandle(endpoint, ownsHandle: true);
            using var pipe = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
            await using var channel = new PluginChannel(pipe);
            failureStage = 12;
            await channel.SendAsync(PluginMessageKind.Event, 0, "worker.ready", new { }).ConfigureAwait(false);
            failureStage = 1;
            await using var context = new WorkerContext(channel);
            var load = new PluginLoadContext(entry);
            var assembly = load.LoadFromAssemblyPath(entry);
            var type = assembly.GetType(args[5], throwOnError: true)!;
            if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract) return 2;
            var plugin = (IPlugin?)Activator.CreateInstance(type) ?? throw new InvalidDataException("Missing plugin entry point.");
            Task pluginTask = plugin.RunAsync(context, context.CancellationToken);
            await Task.WhenAny(pluginTask, context.Completion).ConfigureAwait(false);
            if (pluginTask.IsCompleted) await pluginTask.ConfigureAwait(false);
            return 0;
        }
        catch { return failureStage; } // No file logging or exception strings crossing the sandbox boundary.
    }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint data, int bytes, out int needed);
}

internal sealed class PluginLoadContext(string entry) : AssemblyLoadContext("OpenRevo.Plugin", isCollectible: false)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == typeof(IPlugin).Assembly.GetName().Name) return typeof(IPlugin).Assembly;
        if (assemblyName.Name == null || assemblyName.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidDataException("Invalid dependency name.");
        // Framework assemblies resolve through the shared runtime, not plugin replacements.
        if (assemblyName.Name.StartsWith("System.", StringComparison.Ordinal) || assemblyName.Name is "System" or "netstandard" or "mscorlib") return null;
        string file = Path.Combine(Path.GetDirectoryName(entry)!, assemblyName.Name + ".dll");
        return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
    }
}
