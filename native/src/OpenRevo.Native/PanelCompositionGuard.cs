using System.Runtime.InteropServices;

namespace OpenRevo.Native;

// Suppress system transitions; cloak only non-animated layout preparation.
internal sealed class PanelCompositionGuard : IDisposable
{
    private readonly nint window;
    private readonly int previousTransitions, previousAppCloak;
    private readonly bool transitionsChanged, cloakChanged;
    private bool disposed;
    internal bool Cloaked { get; }
    internal PanelCompositionGuard(nint window,bool cloak=true)
    {
        this.window = window;
        if (GetAttribute(window, 3, out previousTransitions, 4) == 0)
        {
            int disabled = 1;
            transitionsChanged = SetAttribute(window, 3, ref disabled, 4) == 0;
        }
        if (GetAttribute(window, 14, out int cloaked, 4) == 0)
            previousAppCloak = (cloaked & 1) != 0 ? 1 : 0;
        int hide = 1;
        Cloaked = cloak && SetAttribute(window, 13, ref hide, 4) == 0;
        cloakChanged = Cloaked && previousAppCloak == 0;
        if (Cloaked) Flush();
    }
    internal void Reveal(){if(cloakChanged&&!disposed){int visible=0;SetAttribute(window,13,ref visible,4);}}
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (cloakChanged) { int visible = 0; SetAttribute(window, 13, ref visible, 4); }
        if (transitionsChanged) { int previous = previousTransitions; SetAttribute(window, 3, ref previous, 4); }
    }
    [DllImport("dwmapi.dll", EntryPoint="DwmSetWindowAttribute")]
    private static extern int SetAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll", EntryPoint="DwmGetWindowAttribute")]
    private static extern int GetAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint="DwmFlush")]
    internal static extern int Flush();
}

