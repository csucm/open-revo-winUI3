using System.Reflection;
using OpenRevo.Core;
namespace OpenRevo.Native;
internal static class BuildInfo
{
    internal static string InformationalVersion=>typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??typeof(App).Assembly.GetName().Version?.ToString(3)??"未知";
    internal static string DisplayVersion=>InformationalVersion+" · "+typeof(App).Module.ModuleVersionId.ToString("N")[..8];
    internal static object Snapshot=>new{informationalVersion=InformationalVersion,nativeModule=typeof(App).Module.ModuleVersionId,coreModule=typeof(DiagnosticLog).Module.ModuleVersionId,upstreamBase="72e98685b2c0538fa0a58409a6bcce9e7b6336f7",selectedCoreTarget="33c41aa9639a7814b7476b7a7485ec84497a9494",corePatch="REQ-CORE-SYNC-01 (selective backport; base Core 0.9.5)",executable=DiagnosticJournal.Scrub(Environment.ProcessPath??"")};
}
