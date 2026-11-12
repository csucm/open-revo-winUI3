using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenRevo.PluginHost;

namespace OpenRevo.Native;

// UI-thread only. Weak bindings let unloaded pages and closed popups be collected.
internal static class NativePluginThemePalette
{
    private sealed class Binding(Action refresh) { internal readonly Action Refresh = refresh; }
    private sealed class Surface(string name) { internal string Name = name; }
    private static readonly ConditionalWeakTable<DependencyObject, Binding> bindings = new();
    private static readonly ConditionalWeakTable<DependencyObject, Surface> surfaces = new();
    private static readonly List<WeakReference<FrameworkElement>> roots = [];
    private static PluginThemePalette current = PluginThemePalette.Empty;

    internal static void Bind(DependencyObject element, Action refresh)
    { bindings.Remove(element); bindings.Add(element, new(refresh)); }
    internal static void RegisterRoot(FrameworkElement element, string surface)
    {
        if (surfaces.TryGetValue(element, out var existing))
        {
            if (existing.Name != surface) { existing.Name = surface; Refresh(); }
            return;
        }
        surfaces.Add(element, new(surface));
        roots.RemoveAll(r => !r.TryGetTarget(out _)); roots.Add(new(element));
    }
    internal static string Resolve(FrameworkElement element, string token)
    {
        if (current.IsEmpty) return PluginThemePalette.Default(token, Paint.Dark(element));
        return current.Resolve(FindSurface(element), Paint.Dark(element), token);
    }
    private static string FindSurface(FrameworkElement element)
    {
        string surface = "main";
        for (DependencyObject? item = element; item != null; item = VisualTreeHelper.GetParent(item))
            if (surfaces.TryGetValue(item, out var found)) { surface = found.Name; break; }
        return surface;
    }
    internal static string Hover(FrameworkElement element, string nativeColor)
        => current.HasSurface(FindSurface(element), Paint.Dark(element)) ? Resolve(element, "subtle") : nativeColor;
    internal static void Apply(PluginThemePalette palette)
    {
        var previous = current; current = palette;
        try { Refresh(); }
        catch { current = previous; Refresh(); throw; }
    }
    private static void Refresh()
    {
        roots.RemoveAll(r => !r.TryGetTarget(out _));
        var visited = new HashSet<DependencyObject>();
        void Visit(DependencyObject item)
        {
            if (!visited.Add(item)) return;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) Visit(VisualTreeHelper.GetChild(item, i));
            // Parent controls repaint last so primary-button labels retain their contrasting color.
            if (bindings.TryGetValue(item, out var binding)) binding.Refresh();
        }
        foreach (var reference in roots.ToArray())
            if (reference.TryGetTarget(out var root) && root.IsLoaded) Visit(root);
    }
}
