#include "Osd.h"
#include "OsdFramePolicy.h"
#include "Shell.h"
#include "SmokeTrace.h"
#include <dwmapi.h>
#include <winrt/Microsoft.UI.Composition.h>
#include <winrt/Windows.UI.Composition.h>
using namespace winrt;
using namespace Microsoft::UI::Xaml;
using namespace Microsoft::UI::Xaml::Controls;
using namespace Microsoft::UI::Xaml::Media;
namespace openrevo {
std::shared_ptr<Osd> Osd::create() { auto result=std::shared_ptr<Osd>(new Osd);result->initialize();return result; }
void Osd::initialize() {
    smokeTrace("osd.window");window_=Window();smokeTrace("osd.window.created");window_.Title(L"Open-Revo C++ 屏幕提示");window_.as<IWindowNative>()->get_WindowHandle(&hwnd_);smokeTrace("osd.hwnd");
    compositor_=Windows::UI::Composition::Compositor();
    smokeTrace("osd.compositor");
    window_.as<Microsoft::UI::Composition::ICompositionSupportsSystemBackdrop>().SystemBackdrop(compositor_.CreateColorBrush(Windows::UI::Color{0,0,0,0}));
    smokeTrace("osd.backdrop");
    root_=Grid();root_.Background(SolidColorBrush(Windows::UI::Color{0,0,0,0}));
    root_.RequestedTheme(ElementTheme::Dark);root_.Language(L"zh-CN");
    Border frame;frame.Padding({14,6,28,6});frame.Margin({12,12,12,12});frame.CornerRadius({4,4,4,4});
    frame.Background(SolidColorBrush(Windows::UI::Color{166,15,23,42}));
    StackPanel content;content.Orientation(Orientation::Horizontal);content.Spacing(12);content.VerticalAlignment(VerticalAlignment::Center);
    Image icon;icon.Width(24);icon.Height(24);
    icon.Source(Imaging::SvgImageSource(Windows::Foundation::Uri((appDirectory()/L"Assets"/L"Osd"/L"caps-38BDF8.svg").wstring())));
    content.Children().Append(icon);label_=TextBlock();label_.Text(L"Caps 已锁定");label_.FontSize(24);label_.FontWeight(Windows::UI::Text::FontWeights::SemiBold());label_.Foreground(SolidColorBrush(Windows::UI::Color{255,255,255,255}));
    content.Children().Append(label_);frame.Child(content);root_.Children().Append(frame);window_.Content(root_);
    smokeTrace("osd.content");auto presenter=Microsoft::UI::Windowing::OverlappedPresenter::Create();presenter.SetBorderAndTitleBar(false,false);presenter.IsAlwaysOnTop(true);presenter.IsResizable(false);presenter.IsMinimizable(false);presenter.IsMaximizable(false);
    window_.AppWindow().SetPresenter(presenter);window_.AppWindow().IsShownInSwitchers(false);
    smokeTrace("osd.presenter");check_bool(SetWindowSubclass(hwnd_,windowProc,0x4F5344,reinterpret_cast<DWORD_PTR>(this)));applyFrame();prepareAlpha();smokeTrace("osd.frame");
    timer_=window_.DispatcherQueue().CreateTimer();timer_.Interval(std::chrono::milliseconds(1600));timer_.IsRepeating(false);
    auto weak=weak_from_this();tickToken_=timer_.Tick([weak](auto&&,auto&&){if(auto self=weak.lock())self->hide();});smokeTrace("osd.timer");
}
void Osd::showCaps(bool locked) {
    if(closed_)return;timer_.Stop();label_.Text(locked?L"Caps 已锁定":L"Caps 已解锁");
    auto monitor=MonitorFromWindow(GetForegroundWindow(),MONITOR_DEFAULTTOPRIMARY);MONITORINFO info{sizeof(info)};if(!GetMonitorInfoW(monitor,&info))return;
    auto dpi=GetDpiForWindow(hwnd_);double scale=dpi?dpi/96.0:1;int width=static_cast<int>(340*scale),height=static_cast<int>(76*scale);
    auto const& work=info.rcWork;
    window_.AppWindow().MoveAndResize({work.left+(work.right-work.left-width)/2,work.top+(work.bottom-work.top)*4/5,width,height});
    applyFrame();window_.AppWindow().Show(false);applyFrame();timer_.Start();
}
void Osd::hide(){if(closed_)return;if(timer_)timer_.Stop();if(window_)window_.AppWindow().Hide();}
void Osd::prepareAlpha(){MARGINS margins{};DwmExtendFrameIntoClientArea(hwnd_,&margins);auto region=CreateRectRgn(0,0,0,0);DWM_BLURBEHIND blur{DWM_BB_ENABLE|DWM_BB_BLURREGION,TRUE,region,FALSE};DwmEnableBlurBehindWindow(hwnd_,&blur);DeleteObject(region);}
void Osd::applyFrame(){
    SetWindowLongPtrW(hwnd_,GWL_STYLE,OsdFramePolicy::style(static_cast<std::uint32_t>(GetWindowLongPtrW(hwnd_,GWL_STYLE))));
    SetWindowLongPtrW(hwnd_,GWL_EXSTYLE,OsdFramePolicy::extended(static_cast<std::uint32_t>(GetWindowLongPtrW(hwnd_,GWL_EXSTYLE))));
    int border=-2,round=1;DwmSetWindowAttribute(hwnd_,34,&border,sizeof(border));DwmSetWindowAttribute(hwnd_,33,&round,sizeof(round));
    SetWindowPos(hwnd_,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_FRAMECHANGED);
}
LRESULT CALLBACK Osd::windowProc(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp,UINT_PTR,DWORD_PTR data){
    auto self=reinterpret_cast<Osd*>(data);
    if(msg==WM_STYLECHANGING && lp && (static_cast<int>(wp)==GWL_STYLE||static_cast<int>(wp)==GWL_EXSTYLE)){
        auto result=DefSubclassProc(hwnd,msg,wp,lp);auto change=reinterpret_cast<STYLESTRUCT*>(lp);
        change->styleNew=static_cast<int>(wp)==GWL_STYLE?OsdFramePolicy::style(change->styleNew):OsdFramePolicy::extended(change->styleNew);return result;
    }
    if(msg==WM_NCCALCSIZE)return 0;if(msg==WM_NCHITTEST)return HTTRANSPARENT;if(msg==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
    if(msg==WM_ERASEBKGND){RECT rect{};GetClientRect(hwnd,&rect);FillRect(reinterpret_cast<HDC>(wp),&rect,static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));return 1;}
    if(msg==WM_DWMCOMPOSITIONCHANGED && self)self->prepareAlpha();
    return DefSubclassProc(hwnd,msg,wp,lp);
}
bool Osd::verifyHidden(){
    bool passed=!IsWindowVisible(hwnd_);
    for(int i=0;i<8;++i){
        SetWindowLongPtrW(hwnd_,GWL_STYLE,GetWindowLongPtrW(hwnd_,GWL_STYLE)|OsdFramePolicy::frameBits);
        SetWindowLongPtrW(hwnd_,GWL_EXSTYLE,GetWindowLongPtrW(hwnd_,GWL_EXSTYLE)|OsdFramePolicy::edgeBits);
        SetWindowPos(hwnd_,nullptr,0,0,320,80,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
        RECT client{},outer{};GetClientRect(hwnd_,&client);GetWindowRect(hwnd_,&outer);
        passed=passed && !(GetWindowLongPtrW(hwnd_,GWL_STYLE)&OsdFramePolicy::frameBits) && !(GetWindowLongPtrW(hwnd_,GWL_EXSTYLE)&OsdFramePolicy::edgeBits) && client.right==outer.right-outer.left && client.bottom==outer.bottom-outer.top;
    }
    return passed && !IsWindowVisible(hwnd_);
}
void Osd::close(){if(closed_)return;hide();closed_=true;if(timer_ && tickToken_.value){timer_.Tick(tickToken_);tickToken_={};}if(hwnd_)RemoveWindowSubclass(hwnd_,windowProc,0x4F5344);if(window_){if(compositor_)window_.as<Microsoft::UI::Composition::ICompositionSupportsSystemBackdrop>().SystemBackdrop(nullptr);window_.Close();}if(compositor_)compositor_.Close();compositor_=nullptr;}
Osd::~Osd(){try{close();}catch(...){OutputDebugStringW(L"OpenRevo.Cpp: OSD cleanup failed\n");}}
}
