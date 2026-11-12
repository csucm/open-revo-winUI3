using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

// Owned by the host UI's serialized plugin workflow. Worker events are marshalled there before use.
public sealed class PluginFolderCatalog(string dataDirectory)
{
    private readonly Dictionary<string, PluginPackage> packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginView> views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);
    private readonly HashSet<string> disabledThisSession = new(StringComparer.Ordinal);
    private PluginDiscoveryEntry[] discoveryErrors = [];
    private PluginPlatformStore? store;
    private PluginPlatformStore Store => store ??= new(dataDirectory);

    public bool DiscoveryStarted { get; private set; }
    public PluginPackage[] Packages => packages.Values.ToArray();
    public PluginDiscoveryEntry[] DiscoveryErrors => discoveryErrors.ToArray();
    public PluginRecord? FindRecord(string id) => Store.FindPlugin(id);
    public string? Error(string id) => errors.GetValueOrDefault(id);
    public void SetError(string id, string message) => errors[id] = message;
    public void ClearError(string id) => errors.Remove(id);
    public bool DisabledThisSession(string id) => disabledThisSession.Contains(id);
    public void DisableInSession(string id) => disabledThisSession.Add(id);
    public PluginView? View(string id) => views.GetValueOrDefault(id);
    public void RememberView(string id, PluginView view) => views[id] = view;
    public void MarkEnabled(string id)
    {
        errors.Remove(id);
        disabledThisSession.Remove(id);
    }

    public void SaveChoice(PluginPackage package, bool enabled, string? fault = null)
    {
        var document = Store.Snapshot;
        string id = package.Manifest.Id;
        var old = document.Plugins.FirstOrDefault(p => p.Id == id);
        var record = old == null ? new PluginRecord(id, package.Digest, enabled, fault, [], [], []) :
            old with { Digest = package.Digest, Enabled = enabled, Fault = fault, ActiveRun = null };
        if (old == record) return;
        Store.Commit(document with { Plugins = document.Plugins.Where(p => p.Id != id).Append(record).ToArray() });
    }

    // Call after stopping sessions whose package disappeared or changed digest.
    public void AcceptDiscovery(PluginDiscoveryResult result)
    {
        if (result.RootError != null) throw new IOException(result.RootError);
        var previous = packages.ToDictionary(p => p.Key, p => p.Value.Digest, StringComparer.Ordinal);
        packages.Clear();
        foreach (var package in result.Entries.Select(e => e.Package).OfType<PluginPackage>())
        {
            string id = package.Manifest.Id;
            packages[id] = package;
            if (previous.GetValueOrDefault(id) != package.Digest)
            {
                views.Remove(id);
                errors.Remove(id);
            }
        }
        foreach (string id in views.Keys.Except(packages.Keys).ToArray()) views.Remove(id);
        discoveryErrors = result.Entries.Where(e => e.Package == null).ToArray();
        DiscoveryStarted = true;
    }
}
