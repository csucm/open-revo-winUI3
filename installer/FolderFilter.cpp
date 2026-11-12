#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// Scope enumeration filtering to the installer's original browse handler.
// Patch only this process's main-module imports, never files or global settings.
using First = HANDLE (WINAPI*)(LPCWSTR, LPWIN32_FIND_DATAW);
using FirstEx = HANDLE (WINAPI*)(LPCWSTR, FINDEX_INFO_LEVELS, LPVOID, FINDEX_SEARCH_OPS, LPVOID, DWORD);
using Next = BOOL (WINAPI*)(HANDLE, LPWIN32_FIND_DATAW);
static First first;
static FirstEx firstEx;
static Next next;
static volatile LONG ownerThread;
static unsigned depth;
struct Slot { PVOID* address; PVOID original; };
static Slot slots[16];
static unsigned slotCount;

static bool hiddenDirectory(const WIN32_FIND_DATAW* data) noexcept {
    return GetCurrentThreadId() == static_cast<DWORD>(InterlockedCompareExchange(&ownerThread, 0, 0)) &&
        (data->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
        (data->dwFileAttributes & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM));
}
static HANDLE filterFirst(HANDLE handle, LPWIN32_FIND_DATAW data) noexcept {
    if (handle == INVALID_HANDLE_VALUE) return handle;
    while (hiddenDirectory(data)) {
        if (!next(handle, data)) {
            DWORD error = GetLastError();
            FindClose(handle);
            SetLastError(error == ERROR_NO_MORE_FILES ? ERROR_FILE_NOT_FOUND : error);
            return INVALID_HANDLE_VALUE;
        }
    }
    return handle;
}
static HANDLE WINAPI visibleFirst(LPCWSTR path, LPWIN32_FIND_DATAW data) noexcept {
    return filterFirst(first(path, data), data);
}
static HANDLE WINAPI visibleFirstEx(LPCWSTR path, FINDEX_INFO_LEVELS info, LPVOID data,
    FINDEX_SEARCH_OPS search, LPVOID filter, DWORD flags) noexcept {
    HANDLE handle = firstEx(path, info, data, search, filter, flags);
    return filterFirst(handle, static_cast<LPWIN32_FIND_DATAW>(data));
}
static BOOL WINAPI visibleNext(HANDLE handle, LPWIN32_FIND_DATAW data) noexcept {
    BOOL found;
    do { found = next(handle, data); } while (found && hiddenDirectory(data));
    return found;
}
static DWORD exchange(PVOID* address, PVOID value) noexcept {
    DWORD old;
    if (!VirtualProtect(address, sizeof(PVOID), PAGE_READWRITE, &old)) return GetLastError();
    InterlockedExchangePointer(address, value);
    DWORD unused;
    return VirtualProtect(address, sizeof(PVOID), old, &unused) ? ERROR_SUCCESS : GetLastError();
}
static DWORD restore() noexcept {
    DWORD failure = 0;
    while (slotCount) {
        const auto& slot = slots[--slotCount];
        DWORD error = exchange(slot.address, slot.original);
        if (error) failure = error;
    }
    return failure;
}
extern "C" DWORD WINAPI BeginDirectoryFilter() noexcept {
    DWORD thread = GetCurrentThreadId();
    if (static_cast<DWORD>(InterlockedCompareExchange(&ownerThread, 0, 0)) == thread) { ++depth; return 0; }
    if (InterlockedCompareExchange(&ownerThread, -1, 0) != 0) return ERROR_BUSY;
    auto kernel = GetModuleHandleW(L"kernel32.dll");
    first = reinterpret_cast<First>(GetProcAddress(kernel, "FindFirstFileW"));
    firstEx = reinterpret_cast<FirstEx>(GetProcAddress(kernel, "FindFirstFileExW"));
    next = reinterpret_cast<Next>(GetProcAddress(kernel, "FindNextFileW"));
    if (!first || !firstEx || !next) { InterlockedExchange(&ownerThread, 0); return ERROR_PROC_NOT_FOUND; }
    auto image = reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(image);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(image + dos->e_lfanew);
    const auto& table = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    bool hasFirst = false, hasNext = false;
    DWORD failure = ERROR_SUCCESS;
    if (table.VirtualAddress) {
        auto descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(image + table.VirtualAddress);
        for (; descriptor->Name && !failure; ++descriptor) {
            auto thunk = reinterpret_cast<IMAGE_THUNK_DATA*>(image + descriptor->FirstThunk);
            for (; thunk->u1.Function && !failure; ++thunk) {
                PVOID original = reinterpret_cast<PVOID>(thunk->u1.Function), replacement = nullptr;
                if (original == reinterpret_cast<PVOID>(first)) { replacement = reinterpret_cast<PVOID>(visibleFirst); hasFirst = true; }
                if (original == reinterpret_cast<PVOID>(firstEx)) { replacement = reinterpret_cast<PVOID>(visibleFirstEx); hasFirst = true; }
                if (original == reinterpret_cast<PVOID>(next)) { replacement = reinterpret_cast<PVOID>(visibleNext); hasNext = true; }
                if (!replacement) continue;
                if (slotCount == ARRAYSIZE(slots)) { failure = ERROR_INSUFFICIENT_BUFFER; break; }
                PVOID* address = reinterpret_cast<PVOID*>(&thunk->u1.Function);
                slots[slotCount++] = { address, original };
                failure = exchange(address, replacement);
            }
        }
    }
    if (failure || !hasFirst || !hasNext) {
        restore(); InterlockedExchange(&ownerThread, 0);
        return failure ? failure : ERROR_PROC_NOT_FOUND;
    }
    depth = 1;
    InterlockedExchange(&ownerThread, static_cast<LONG>(thread));
    return ERROR_SUCCESS;
}
extern "C" DWORD WINAPI EndDirectoryFilter() noexcept {
    if (static_cast<DWORD>(InterlockedCompareExchange(&ownerThread, 0, 0)) != GetCurrentThreadId()) return ERROR_INVALID_STATE;
    if (--depth) return 0;
    InterlockedExchange(&ownerThread, 0);
    return restore();
}
