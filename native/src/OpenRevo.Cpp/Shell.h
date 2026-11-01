#pragma once
#include "pch.h"
#include "PanelState.h"
#include "CapsWatcher.h"
#include "Osd.h"
namespace openrevo {
std::filesystem::path appDirectory();
class Shell final : public std::enable_shared_from_this<Shell> {
public:
    static std::shared_ptr<Shell> create(bool hiddenCheck = false);
    ~Shell();
    void show();
    void verifyHidden(std::filesystem::path const& output);
private:
    void initialize(bool hiddenCheck);
    void render();
    void resize();
    void readCore();
    void hide();
    void wake();
    void toggle();
    void exit();
    bool addTray();
    void trayMenu();
    void dispose();
    static LRESULT CALLBACK windowProc(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
    winrt::Microsoft::UI::Xaml::Window window_{nullptr};
    winrt::Microsoft::UI::Windowing::AppWindow appWindow_{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::Grid root_{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::StackPanel page_{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::TextBlock coreStatus_{nullptr};
    winrt::Microsoft::UI::Xaml::Window::Closed_revoker closed_;
    winrt::event_token closingToken_{};
    PanelState state_;
    HWND hwnd_ = nullptr;
    HICON icon_ = nullptr;
    bool tray_ = false;
    bool disposed_ = false;
    UINT taskbarCreated_ = 0;
    std::jthread readWorker_;
    std::unique_ptr<CapsWatcher> caps_;
    std::shared_ptr<Osd> osd_;
};
}
