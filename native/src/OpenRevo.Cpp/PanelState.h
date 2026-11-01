#pragma once
namespace openrevo {
// The shell and native checks use exactly the same transition policy.
struct PanelState {
    bool mini = true;
    bool hidden = false;
    bool exiting = false;
    bool minimize(bool trayAvailable) noexcept {
        if (exiting) return false;
        if (!mini) { mini = true; hidden = false; return true; }
        return hide(trayAvailable);
    }
    bool hide(bool trayAvailable) noexcept {
        if (!trayAvailable || exiting) return false;
        hidden = true; return true;
    }
    void wake() noexcept { if (!exiting) hidden = false; }
    void expand() noexcept { if (!exiting) { mini = !mini; hidden = false; } }
};
}
