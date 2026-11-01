#include "pch.h"
#include "App.h"
#include "SmokeTrace.h"
#include <DispatcherQueue.h>
#include <winrt/Windows.System.h>
namespace {
std::filesystem::path smokeOutput;
HANDLE instanceMutex = nullptr;
LONG CALLBACK recordSmokeException(EXCEPTION_POINTERS* info) {
    if(info->ExceptionRecord->ExceptionCode!=EXCEPTION_ACCESS_VIOLATION)return EXCEPTION_CONTINUE_SEARCH;
    std::ofstream out(smokeOutput.wstring()+L".native-exception.txt");
    auto address=reinterpret_cast<std::uintptr_t>(info->ExceptionRecord->ExceptionAddress);
    out<<"fault RVA: "<<std::hex<<(address-reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr)))<<'\n';
    void* frames[32]{};auto count=CaptureStackBackTrace(0,32,frames,nullptr);
    for(USHORT i=0;i<count;++i){HMODULE module=nullptr;if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(frames[i]),&module)){wchar_t name[32768]{};GetModuleFileNameW(module,name,32768);out<<std::filesystem::path(name).filename().string()<<" + "<<std::hex<<(reinterpret_cast<std::uintptr_t>(frames[i])-reinterpret_cast<std::uintptr_t>(module))<<'\n';}}
    return EXCEPTION_CONTINUE_SEARCH;
}
}
namespace winrt::OpenRevo::Cpp::implementation {
App::App() { openrevo::smokeTrace("app.construct"); InitializeComponent();openrevo::smokeTrace("app.resources.loaded"); }
void App::OnLaunched(Microsoft::UI::Xaml::LaunchActivatedEventArgs const&) {
    openrevo::smokeTrace("app.launched");shell_ = openrevo::Shell::create(!smokeOutput.empty());
    if (smokeOutput.empty()) shell_->show();
    else {
        openrevo::smokeTrace("smoke.begin");shell_->verifyHidden(smokeOutput);openrevo::smokeTrace("smoke.end");
        shell_.reset();
        Exit();
    }
}
}
int __stdcall wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int count = 0;
    auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    bool invalid = count != 1 && !(count == 3 && std::wstring_view(args[1]) == L"--smoke-check");
    if (!invalid && count == 3) smokeOutput = args[2];
    if (args) LocalFree(args);
    if (invalid || (!smokeOutput.empty() && !smokeOutput.is_absolute())) return 2;
    if(!smokeOutput.empty()){openrevo::smokeTracePath=smokeOutput.wstring()+L".stages.txt";std::ofstream clear(openrevo::smokeTracePath);AddVectoredExceptionHandler(1,recordSmokeException);}
    openrevo::smokeTrace("main.enter");
    if (smokeOutput.empty()) {
        instanceMutex = CreateMutexW(nullptr, FALSE, L"Local\\OpenRevo.Cpp.Prototype.v1");
        if (!instanceMutex) return 3;
        if (GetLastError() == ERROR_ALREADY_EXISTS) {
            PostMessageW(HWND_BROADCAST, RegisterWindowMessageW(L"OpenRevo.Cpp.Prototype.Wake.v1"), 0, 0);
            CloseHandle(instanceMutex); return 0;
        }
    }
    int result = 0;
    try {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        winrt::Windows::System::DispatcherQueueController compositorQueue{nullptr};
        if(!winrt::Windows::System::DispatcherQueue::GetForCurrentThread()) {
            DispatcherQueueOptions options{sizeof(DispatcherQueueOptions),DQTYPE_THREAD_CURRENT,DQTAT_COM_STA};
            winrt::check_hresult(CreateDispatcherQueueController(options,reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(compositorQueue))));
        }
        openrevo::smokeTrace("main.apartment");
        winrt::Microsoft::UI::Xaml::Application::Start([](auto&&) { winrt::make<winrt::OpenRevo::Cpp::implementation::App>(); });
    } catch (winrt::hresult_error const& error) {
        result = 1;
        if (!smokeOutput.empty()) { std::ofstream out(smokeOutput); out << "{\"passed\":false,\"error\":\"" << std::hex << static_cast<std::uint32_t>(error.code().value) << "\"}"; }
        OutputDebugStringW(error.message().c_str());
    } catch (...) { result = 1; }
    if (instanceMutex) CloseHandle(instanceMutex);
    return result;
}
