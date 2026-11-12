#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <map>
#include <string>
#include <vector>

namespace
{
    // The scheduled task is the only elevated entry. Its child receives a
    // small environment, with no per-user runtime hooks, profilers or PATH.
    std::vector<wchar_t> BuildEnvironment()
    {
        std::map<std::wstring, std::wstring> values;
        auto read = [&](const wchar_t* name)
        {
            wchar_t buffer[32768];
            const DWORD size = GetEnvironmentVariableW(name, buffer, ARRAYSIZE(buffer));
            if (size != 0 && size < ARRAYSIZE(buffer)) values.emplace(name, buffer);
        };
        auto folder = [&](const wchar_t* name, REFKNOWNFOLDERID id)
        {
            PWSTR path = nullptr;
            if (SUCCEEDED(SHGetKnownFolderPath(id, KF_FLAG_DONT_VERIFY, nullptr, &path)))
                values.emplace(name, path);
            CoTaskMemFree(path);
        };

        wchar_t windows[MAX_PATH];
        wchar_t system[MAX_PATH];
        if (!GetSystemWindowsDirectoryW(windows, ARRAYSIZE(windows)) ||
            !GetSystemDirectoryW(system, ARRAYSIZE(system))) return {};
        values[L"SYSTEMROOT"] = windows;
        values[L"WINDIR"] = windows;
        values[L"PATH"] = std::wstring(system) + L";" + windows;
        values[L"COMSPEC"] = std::wstring(system) + L"\\cmd.exe";
        folder(L"APPDATA", FOLDERID_RoamingAppData);
        folder(L"LOCALAPPDATA", FOLDERID_LocalAppData);
        folder(L"PROGRAMDATA", FOLDERID_ProgramData);
        folder(L"USERPROFILE", FOLDERID_Profile);
        folder(L"PROGRAMFILES", FOLDERID_ProgramFilesX64);
        folder(L"PROGRAMFILES(X86)", FOLDERID_ProgramFilesX86);
        if (const auto local = values.find(L"LOCALAPPDATA"); local != values.end())
        {
            values[L"TEMP"] = local->second + L"\\Temp";
            values[L"TMP"] = values[L"TEMP"];
        }
        for (const auto name : {L"COMPUTERNAME", L"PROCESSOR_ARCHITECTURE", L"PROCESSOR_IDENTIFIER",
                               L"NUMBER_OF_PROCESSORS", L"USERNAME", L"USERDOMAIN"}) read(name);

        std::vector<wchar_t> block;
        for (const auto& [name, value] : values)
        {
            const auto pair = name + L"=" + value;
            block.insert(block.end(), pair.begin(), pair.end());
            block.push_back(L'\0');
        }
        block.push_back(L'\0');
        return block;
    }

    bool IsAdministrator()
    {
        SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY;
        PSID administrators = nullptr;
        if (!AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS,
                                     0, 0, 0, 0, 0, 0, &administrators)) return false;
        BOOL member = FALSE;
        const BOOL checked = CheckTokenMembership(nullptr, administrators, &member);
        FreeSid(administrators);
        return checked && member;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int count = 0;
    LPWSTR* args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!args) return 1;
    const bool logon = count == 2 && std::wstring(args[1]) == L"--scheduled-logon";
    const bool open = count == 2 && std::wstring(args[1]) == L"--scheduled-open";
    LocalFree(args);
    if (!open && !logon) return 1;

    if (IsAdministrator() && SetDllDirectoryW(L""))
    {
        wchar_t module[32768];
        const DWORD size = GetModuleFileNameW(nullptr, module, ARRAYSIZE(module));
        if (size != 0 && size < ARRAYSIZE(module))
        {
            const auto directory = std::wstring(module, size).substr(0, std::wstring(module, size).find_last_of(L'\\'));
#ifdef REVOCORE_RESIDENT_BOOTSTRAP
#ifdef REVOCORE_NATIVE_HOST
            const auto executable = directory + L"\\RevoCore.exe";
#else
            const auto executable = directory + L"\\OpenRevo.Resident.exe";
#endif
            auto command = L"\"" + executable + L"\"" + (logon ? L" --installed-logon" : L" --installed-open");
#else
            const auto executable = directory + L"\\OpenRevo.Native.exe";
            auto command = L"\"" + executable + L"\"" + (logon ? L" --startup" : L"");
#endif
            auto environment = BuildEnvironment();
            STARTUPINFOW startup{};
            startup.cb = sizeof(startup);
            PROCESS_INFORMATION process{};
            if (!environment.empty() && CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr,
                FALSE, CREATE_UNICODE_ENVIRONMENT, environment.data(), directory.c_str(), &startup, &process))
            {
                CloseHandle(process.hThread);
                CloseHandle(process.hProcess);
                return 0;
            }
        }
    }
    if (open)
        MessageBoxW(nullptr, L"无法通过已登记的管理员任务启动控制台。请运行安装程序修复。", L"Open-Revo", MB_ICONERROR);
    return 1;
}
