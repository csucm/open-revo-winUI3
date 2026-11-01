#pragma once
#include "pch.h"
#include <winrt/Windows.UI.Composition.h>
namespace openrevo {
class Osd final : public std::enable_shared_from_this<Osd> {
public:
    static std::shared_ptr<Osd> create();
    ~Osd();
    void showCaps(bool locked);
    void hide();
    bool verifyHidden();
private:
    void initialize();
    void applyFrame();
    void prepareAlpha();
    void close();
    static LRESULT CALLBACK windowProc(HWND,UINT,WPARAM,LPARAM,UINT_PTR,DWORD_PTR);
    winrt::Microsoft::UI::Xaml::Window window_{nullptr};
    winrt::Windows::UI::Composition::Compositor compositor_{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::Grid root_{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::TextBlock label_{nullptr};
    winrt::Microsoft::UI::Dispatching::DispatcherQueueTimer timer_{nullptr};
    winrt::event_token tickToken_{};
    HWND hwnd_=nullptr;
    bool closed_=false;
};
}
