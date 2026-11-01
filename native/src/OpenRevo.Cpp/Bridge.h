#pragma once
#include <windows.h>
#include <cstdint>
#include <string>
#include <filesystem>
namespace openrevo {
using QueryFunction = std::int32_t (__cdecl *)(std::uint32_t, std::uint32_t, unsigned char*, std::size_t);
enum class ReadQuery : std::uint32_t { Provenance = 1, FactoryFanTable = 2 };
// The prototype has no mutation entry point or Engine initialization path.
std::string readQuery(QueryFunction query, ReadQuery kind, std::uint32_t mode = 0);
class ReadOnlyBridge final {
    HMODULE module_ = nullptr;
    QueryFunction query_ = nullptr;
public:
    explicit ReadOnlyBridge(std::filesystem::path const& absolutePath);
    ~ReadOnlyBridge();
    ReadOnlyBridge(ReadOnlyBridge const&) = delete;
    ReadOnlyBridge& operator=(ReadOnlyBridge const&) = delete;
    std::string provenance() const;
};
}
