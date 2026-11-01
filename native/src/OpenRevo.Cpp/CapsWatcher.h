#pragma once
#include <windows.h>
#include <functional>
#include <thread>
#include <atomic>
#include <future>
#include "CapsTransitions.h"
namespace openrevo {
// The hook never runs XAML, disk I/O, waits or hardware calls.
class CapsWatcher final {
    std::jthread thread_;
    std::atomic<DWORD> threadId_{0};
    std::function<void(bool)> changed_;
    CapsTransitions transitions_{false};
    HHOOK hook_=nullptr;
    inline static thread_local CapsWatcher* current_=nullptr;
    static LRESULT CALLBACK callback(int code,WPARAM message,LPARAM data) noexcept {
        auto self=current_;
        if(self && code>=0 && data) {
            auto key=reinterpret_cast<KBDLLHOOKSTRUCT const*>(data);
            bool down=message==WM_KEYDOWN || message==WM_SYSKEYDOWN;
            bool up=message==WM_KEYUP || message==WM_SYSKEYUP;
            if(down||up)if(auto value=self->transitions_.accept(key->vkCode,down)) {
                try{self->changed_(*value);}catch(...){}
            }
        }
        return CallNextHookEx(nullptr,code,message,data);
    }
public:
    explicit CapsWatcher(std::function<void(bool)> changed):changed_(std::move(changed)) {
        std::promise<DWORD> initialized; auto ready=initialized.get_future();
        thread_=std::jthread([this,signal=std::move(initialized)](std::stop_token stop) mutable {
            MSG message{}; PeekMessageW(&message,nullptr,0,0,PM_NOREMOVE);
            threadId_.store(GetCurrentThreadId());current_=this;
            transitions_.locked=(GetKeyState(VK_CAPITAL)&1)!=0;
            hook_=SetWindowsHookExW(WH_KEYBOARD_LL,callback,GetModuleHandleW(nullptr),0);
            signal.set_value(hook_?ERROR_SUCCESS:GetLastError());
            if(hook_)while(!stop.stop_requested() && GetMessageW(&message,nullptr,0,0)>0) { TranslateMessage(&message);DispatchMessageW(&message); }
            if(hook_)UnhookWindowsHookEx(hook_);hook_=nullptr;current_=nullptr;
        });
        if(ready.get()!=ERROR_SUCCESS) { stop();throw std::runtime_error("Caps keyboard hook unavailable"); }
    }
    void stop() noexcept { thread_.request_stop();auto id=threadId_.load();if(id)PostThreadMessageW(id,WM_QUIT,0,0);if(thread_.joinable())thread_.join(); }
    ~CapsWatcher(){stop();}
    CapsWatcher(CapsWatcher const&)=delete;
    CapsWatcher& operator=(CapsWatcher const&)=delete;
};
}
