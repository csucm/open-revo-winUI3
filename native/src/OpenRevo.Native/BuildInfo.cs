using System.Reflection;
using OpenRevo.Core;
namespace OpenRevo.Native;
internal static class BuildInfo
{
    private static string? Metadata(string name)=>typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value=>value.Key==name)?.Value;
    internal static Version PublicReleaseVersion {get;}=Version.TryParse(Metadata("PublicReleaseVersion"),out var version)?version:throw new InvalidOperationException("缺少有效的公开发布版本。");
    internal static string VersionLabel=>"v"+PublicReleaseVersion.ToString(3);
    internal static string UpdateDescription=>$"{VersionLabel} · 社区版发布源";
    internal static string InformationalVersion=>typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??typeof(App).Assembly.GetName().Version?.ToString(3)??"未知";
    internal static object Snapshot=>new{informationalVersion=InformationalVersion,buildChannel=Metadata("BuildChannel"),publicReleaseVersion=PublicReleaseVersion.ToString(3),nativeModule=typeof(App).Module.ModuleVersionId,coreModule=typeof(DiagnosticLog).Module.ModuleVersionId,upstreamBase="72e98685b2c0538fa0a58409a6bcce9e7b6336f7",selectedCoreTarget="33c41aa9639a7814b7476b7a7485ec84497a9494",corePatch="REQ-CORE-SYNC-01 (selective backport; base Core 0.9.5)",executable=DiagnosticJournal.Scrub(Environment.ProcessPath??"")};
}
