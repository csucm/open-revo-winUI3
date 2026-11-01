#pragma once
#include <optional>
#include <cstdint>
namespace openrevo {
struct CapsTransitions {
    bool locked;
    bool pressed = false;
    std::optional<bool> accept(std::uint32_t key, bool down) noexcept {
        if (key != 0x14) return {};
        if (!down) { pressed=false; return {}; }
        if (pressed) return {};
        pressed=true; locked=!locked; return locked;
    }
};
}
