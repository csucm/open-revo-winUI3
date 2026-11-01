#include "Bridge.h"
#include <algorithm>
#include <stdexcept>
#include <vector>
namespace openrevo {
std::string readQuery(QueryFunction query, ReadQuery kind, std::uint32_t mode) {
    if (!query) throw std::runtime_error("Missing query export");
    if (kind != ReadQuery::Provenance && kind != ReadQuery::FactoryFanTable)
        throw std::runtime_error("Query outside prototype read-only allowlist");
    if ((kind == ReadQuery::Provenance && mode != 0) ||
        (kind == ReadQuery::FactoryFanTable && (mode < 1 || mode > 3)))
        throw std::runtime_error("Invalid read-only query mode");
    auto id = static_cast<std::uint32_t>(kind);
    auto size = query(id, mode, nullptr, 0);
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (size < 2 || size > 1024 * 1024) throw std::runtime_error("Invalid ABI response size/status");
        std::vector<unsigned char> buffer(static_cast<size_t>(size), 0xFF);
        auto actual = query(id, mode, buffer.data(), buffer.size());
        if (actual > size) { size = actual; continue; }
        if (actual < 2 || buffer[static_cast<size_t>(actual) - 1] != 0)
            throw std::runtime_error("Unterminated ABI response");
        auto end = buffer.begin() + actual - 1;
        if (std::find(buffer.begin(), end, 0) != end) throw std::runtime_error("Embedded NUL in ABI response");
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<char*>(buffer.data()), actual - 1, nullptr, 0))
            throw std::runtime_error("Invalid UTF-8 in ABI response");
        return { reinterpret_cast<char*>(buffer.data()), static_cast<size_t>(actual - 1) };
    }
    throw std::runtime_error("ABI response kept growing");
}
ReadOnlyBridge::ReadOnlyBridge(std::filesystem::path const& path) {
    if (!path.is_absolute() || path.filename() != L"openrevo_native_bridge.dll")
        throw std::runtime_error("Expected absolute path to restricted bridge DLL");
    module_ = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module_) throw std::runtime_error("Restricted bridge DLL could not be loaded");
    try {
        using Abi = std::uint32_t (__cdecl *)();
        auto abi = reinterpret_cast<Abi>(GetProcAddress(module_, "or_native_abi_version"));
        query_ = reinterpret_cast<QueryFunction>(GetProcAddress(module_, "or_native_query"));
        if (!abi || !query_ || abi() != 1) throw std::runtime_error("Unsupported restricted bridge ABI");
    } catch (...) { FreeLibrary(module_); module_ = nullptr; throw; }
}
ReadOnlyBridge::~ReadOnlyBridge() { if (module_) FreeLibrary(module_); }
std::string ReadOnlyBridge::provenance() const { return readQuery(query_, ReadQuery::Provenance); }
}
