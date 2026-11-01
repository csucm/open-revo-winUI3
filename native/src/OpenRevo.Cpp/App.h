#pragma once
#include "App.xaml.g.h"
#include "Shell.h"
namespace winrt::OpenRevo::Cpp::implementation {
struct App : AppT<App> {
    App();
    void OnLaunched(Microsoft::UI::Xaml::LaunchActivatedEventArgs const&);
private:
    std::shared_ptr<openrevo::Shell> shell_;
};
}
