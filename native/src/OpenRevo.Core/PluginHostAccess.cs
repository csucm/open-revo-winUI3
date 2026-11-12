using System.Runtime.CompilerServices;

// Trusted in-process broker only. Never referenced by the untrusted worker or plugin SDK.
[assembly: InternalsVisibleTo("OpenRevo.PluginHost")]
