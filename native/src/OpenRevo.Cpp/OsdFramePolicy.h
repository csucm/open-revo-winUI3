#pragma once
#include <cstdint>
namespace openrevo {
struct OsdFramePolicy {
    static constexpr std::uint32_t frameBits=0x00CF0000, edgeBits=0x00020301;
    static constexpr std::uint32_t style(std::uint32_t value) noexcept { return (value&~frameBits)|0x80000000; }
    static constexpr std::uint32_t extended(std::uint32_t value) noexcept { return (value&~(edgeBits|0x00040080u))|0x080000A0u; }
};
}
