#include "pch.h"
#include "Shell.h"
#include "Bridge.h"
#include "SmokeTrace.h"
#include <tlhelp32.h>
using namespace winrt;
using namespace Microsoft::UI::Xaml;
using namespace Microsoft::UI::Xaml::Controls;
using namespace Microsoft::UI::Xaml::Media;
using namespace Windows::Foundation;
namespace openrevo {
namespace {
constexpr UINT trayMessage = WM_APP + 17;
constexpr UINT_PTR subclassId = 0x435050;
UINT wakeMessage() { static UINT message = RegisterWindowMessageW(L"OpenRevo.Cpp.Prototype.Wake.v1"); return message; }
TextBlock text(hstring const& value, double size = 12) {
    TextBlock block; block.Text(value); block.FontSize(size); block.VerticalAlignment(VerticalAlignment::Center); return block;
}
Button disabled(hstring const& value, double height = 32) {
    Button button; button.Content(box_value(value)); button.FontSize(12); button.MinWidth(0); button.Padding({ 2, 2, 2, 2 });
    button.Height(height); button.HorizontalAlignment(HorizontalAlignment::Stretch); button.IsEnabled(false);
    Automation::AutomationProperties::SetName(button, value);
    ToolTipService::SetToolTip(button, box_value(L"迁移原型：保护层尚未迁完，暂不可用")); return button;
}
Grid columns(std::vector<UIElement> const& items, double gap = 6) {
    Grid grid; grid.ColumnSpacing(gap);
    for (int i = 0; i < static_cast<int>(items.size()); ++i) {
        ColumnDefinition column; column.Width({ 1, GridUnitType::Star }); grid.ColumnDefinitions().Append(column);
        Grid::SetColumn(items[i].as<FrameworkElement>(), i); grid.Children().Append(items[i]);
    }
    return grid;
}
Grid choices(std::initializer_list<hstring> labels, double height = 32) {
    std::vector<UIElement> buttons;
    for (auto const& label : labels) buttons.push_back(disabled(label, height));
    return columns(buttons);
}
Border group(hstring const& heading, UIElement const& content, hstring const& detail = L"") {
    auto card = Markup::XamlReader::Load(L"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DesignCardFill}' BorderBrush='{ThemeResource DesignStroke}' BorderThickness='1' CornerRadius='12'/>").as<Border>();
    card.Padding({ 7,7,7,7 });
    StackPanel stack; stack.Spacing(4);
    Grid row; ColumnDefinition left; left.Width({ 1,GridUnitType::Star }); row.ColumnDefinitions().Append(left);
    ColumnDefinition right; right.Width(GridLengthHelper::Auto()); row.ColumnDefinitions().Append(right);
    auto title = text(heading); title.FontWeight(Windows::UI::Text::FontWeights::SemiBold()); row.Children().Append(title);
    if (!detail.empty()) { auto info = text(detail); Grid::SetColumn(info, 1); row.Children().Append(info); }
    stack.Children().Append(row); stack.Children().Append(content); card.Child(stack); return card;
}
Grid brightness(double maximum, bool presets) {
    Grid result;
    RowDefinition heading; heading.Height(GridLengthHelper::Auto()); result.RowDefinitions().Append(heading);
    RowDefinition track; track.Height({ 28,GridUnitType::Pixel }); result.RowDefinitions().Append(track);
    if (presets) result.Children().Append(choices({ L"暗光 25%",L"室内 60%",L"户外 100%" },24));
    Slider slider; slider.Minimum(maximum == 100 ? 10 : 0); slider.Maximum(maximum); slider.Value(slider.Minimum());
    slider.Height(28); slider.MinHeight(28); slider.IsEnabled(false); slider.HorizontalAlignment(HorizontalAlignment::Stretch);
    slider.Resources().Insert(box_value(L"SliderHorizontalHeight"), box_value(28.0));
    slider.Resources().Insert(box_value(L"SliderPreContentMargin"), box_value(12.0));
    slider.Resources().Insert(box_value(L"SliderPostContentMargin"), box_value(12.0));
    Automation::AutomationProperties::SetName(slider, presets ? L"屏幕亮度，状态待读取" : L"键盘背光，状态待读取");
    Grid::SetRow(slider, 1); result.Children().Append(slider); return result;
}
}
std::filesystem::path appDirectory() {
    std::wstring name(32768, L'\0'); auto size = GetModuleFileNameW(nullptr, name.data(), static_cast<DWORD>(name.size()));
    if (!size || size >= name.size()) throw std::runtime_error("Cannot resolve executable directory");
    name.resize(size); return std::filesystem::path(name).parent_path();
}
std::shared_ptr<Shell> Shell::create(bool hiddenCheck) {
    auto shell = std::shared_ptr<Shell>(new Shell); shell->initialize(hiddenCheck); return shell;
}
void Shell::initialize(bool hiddenCheck) {
    smokeTrace("shell.window");window_ = Window(); window_.Title(L"Open-Revo · C++ 原型");
    window_.as<IWindowNative>()->get_WindowHandle(&hwnd_);
    appWindow_=window_.AppWindow();
    window_.AppWindow().SetIcon((appDirectory() / L"App.ico").wstring());
    root_ = Markup::XamlReader::Load(L"<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource ApplicationPageBackgroundThemeBrush}'/>").as<Grid>();
    root_.Language(L"zh-CN"); window_.Content(root_);
    RowDefinition captionRow; captionRow.Height({ 32,GridUnitType::Pixel }); root_.RowDefinitions().Append(captionRow);
    RowDefinition bodyRow; bodyRow.Height({ 1,GridUnitType::Star }); root_.RowDefinitions().Append(bodyRow);
    Grid caption; caption.Margin({ 12,0,138,0 }); ColumnDefinition dragColumn; dragColumn.Width({ 1,GridUnitType::Star }); caption.ColumnDefinitions().Append(dragColumn);
    ColumnDefinition actionsColumn; actionsColumn.Width(GridLengthHelper::Auto()); caption.ColumnDefinitions().Append(actionsColumn);
    auto brand = text(L"Open-Revo",14); caption.Children().Append(brand);
    StackPanel actions; actions.Orientation(Orientation::Horizontal);
    Button expand; expand.Content(box_value(L"↗")); expand.FontSize(14); expand.Width(36); expand.Height(32); expand.MinWidth(0); expand.Padding({0,0,0,0});
    Automation::AutomationProperties::SetName(expand,L"展开或收起面板");
    auto weak = weak_from_this(); expand.Click([weak](auto&&,auto&&) { if(auto self=weak.lock()) self->toggle(); }); actions.Children().Append(expand);
    Button theme; theme.Content(box_value(L"◐")); theme.FontSize(14); theme.Width(40); theme.Height(32); theme.MinWidth(0); theme.Padding({0,0,0,0});
    Automation::AutomationProperties::SetName(theme,L"切换浅色或深色主题");
    theme.Click([weak](auto&&,auto&&) { if(auto self=weak.lock()) self->root_.RequestedTheme(self->root_.ActualTheme()==ElementTheme::Dark?ElementTheme::Light:ElementTheme::Dark); }); actions.Children().Append(theme);
    Grid::SetColumn(actions,1); caption.Children().Append(actions); root_.Children().Append(caption);
    window_.ExtendsContentIntoTitleBar(true); window_.SetTitleBar(brand);
    page_ = StackPanel(); page_.Spacing(6); page_.Padding({ 12,4,12,10 });
    ScrollViewer scroll; scroll.HorizontalScrollBarVisibility(ScrollBarVisibility::Disabled); scroll.VerticalScrollBarVisibility(ScrollBarVisibility::Auto); scroll.Content(page_); Grid::SetRow(scroll,1); root_.Children().Append(scroll);
    smokeTrace("shell.render");render(); resize();smokeTrace("shell.rendered");
    closed_ = window_.Closed(auto_revoke,[weak](auto&&,auto&&) { if(auto self=weak.lock()) { self->dispose(); Application::Current().Exit(); } });
    smokeTrace("shell.closed.registered");
    closingToken_ = appWindow_.Closing([weak](auto&&,Microsoft::UI::Windowing::AppWindowClosingEventArgs const& args) { if(auto self=weak.lock(); self && !self->state_.exiting) { args.Cancel(true); self->hide(); } });
    smokeTrace("shell.closing.registered");
    if (!hiddenCheck) {
        taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
        check_bool(SetWindowSubclass(hwnd_,windowProc,subclassId,reinterpret_cast<DWORD_PTR>(this)));
        icon_ = reinterpret_cast<HICON>(LoadImageW(nullptr,(appDirectory()/L"App.ico").c_str(),IMAGE_ICON,0,0,LR_LOADFROMFILE|LR_DEFAULTSIZE));
        addTray(); readCore();
        auto queue=window_.DispatcherQueue();
        try{caps_=std::make_unique<CapsWatcher>([weak,queue](bool locked){queue.TryEnqueue([weak,locked]{if(auto self=weak.lock();self && !self->disposed_){if(!self->osd_)self->osd_=Osd::create();self->osd_->showCaps(locked);}});});}
        catch(std::exception const& error){ToolTipService::SetToolTip(coreStatus_,box_value(to_hstring(error.what())));}
    }
}
void Shell::render() {
    page_.Children().Clear();
    page_.Children().Append(group(L"性能模式：待读取",choices({L"办公",L"均衡",L"狂暴",L"自定义"},44),L"CPU：—"));
    page_.Children().Append(group(L"显卡模式：待读取",choices({L"核显模式",L"混合输出",L"独显直连"},44),L"GPU：—"));
    page_.Children().Append(group(L"屏幕刷新率：— Hz",choices({L"低刷联动",L"待读取"}),L"内置屏待识别"));
    page_.Children().Append(group(L"☼ 屏幕亮度：—%",brightness(100,true)));
    StackPanel lighting; lighting.Spacing(2); lighting.Children().Append(choices({L"状态待读取",L"背光颜色",L"更多"})); lighting.Children().Append(brightness(4,false));
    page_.Children().Append(group(L"键盘背光：待读取",lighting));
    auto battery = choices({L"工作站",L"平衡",L"长效"});
    ToolTipService::SetToolTip(battery,box_value(L"60/55、80/75、100/95；限充不主动放电。原型尚不可用。"));
    page_.Children().Append(group(L"电池健康",battery,L"上限 —"));
    page_.Children().Append(choices({L"一键\n强冷",L"摄像头",L"麦克风",L"Win 键",L"保持\n唤醒",L"关闭\n屏幕"},50));
    coreStatus_ = text(L"原生迁移原型 · 未接管"); page_.Children().Append(coreStatus_);
}
void Shell::resize() {
    auto dpi = GetDpiForWindow(hwnd_); double scale = dpi ? dpi/96.0 : 1;
    window_.AppWindow().Resize({static_cast<int>((state_.mini?400:1180)*scale),static_cast<int>((state_.mini?640:850)*scale)});
}
void Shell::show() { window_.Activate(); }
void Shell::hide() { if(state_.hide(tray_ || addTray())) window_.AppWindow().Hide(); }
void Shell::wake() { if(state_.exiting)return; state_.wake(); ShowWindow(hwnd_,SW_RESTORE); window_.Activate(); SetForegroundWindow(hwnd_); }
void Shell::toggle() { state_.expand(); resize(); wake(); }
bool Shell::addTray() {
    if (!icon_ || disposed_) return false;
    NOTIFYICONDATAW data{sizeof(data)}; data.hWnd=hwnd_; data.uID=1; data.uFlags=NIF_MESSAGE|NIF_ICON|NIF_TIP; data.uCallbackMessage=trayMessage; data.hIcon=icon_;
    wcscpy_s(data.szTip,L"Open-Revo · C++ 隔离原型"); tray_=Shell_NotifyIconW(NIM_ADD,&data)!=FALSE;
    if(tray_) { data.uVersion=NOTIFYICON_VERSION_4; Shell_NotifyIconW(NIM_SETVERSION,&data); } return tray_;
}
void Shell::trayMenu() {
    auto menu=CreatePopupMenu(); if(!menu)return;
    AppendMenuW(menu,MF_STRING,1,L"打开控制面板"); AppendMenuW(menu,MF_STRING,2,L"打开迷你面板"); AppendMenuW(menu,MF_STRING,3,L"打开完整面板"); AppendMenuW(menu,MF_SEPARATOR,0,nullptr); AppendMenuW(menu,MF_STRING,9,L"退出 C++ 原型");
    POINT p{}; GetCursorPos(&p); SetForegroundWindow(hwnd_);
    auto command=TrackPopupMenu(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON,p.x,p.y,0,hwnd_,nullptr); DestroyMenu(menu);
    if(command==9)exit(); else if(command>=1 && command<=3) { if(command!=1)state_.mini=command==2; resize(); wake(); }
}
void Shell::exit() { if(state_.exiting)return; state_.exiting=true; dispose(); window_.Close(); }
void Shell::dispose() {
    if(disposed_)return; disposed_=true;
    if(appWindow_ && closingToken_.value) { appWindow_.Closing(closingToken_);closingToken_={}; }
    caps_.reset();osd_.reset();
    // Join before the local bridge in the worker can unload. No UI-thread waits in the worker.
    readWorker_.request_stop(); if(readWorker_.joinable())readWorker_.join();
    if(hwnd_)RemoveWindowSubclass(hwnd_,windowProc,subclassId);
    if(tray_) { NOTIFYICONDATAW data{sizeof(data)}; data.hWnd=hwnd_; data.uID=1; Shell_NotifyIconW(NIM_DELETE,&data); tray_=false; }
    if(icon_) { DestroyIcon(icon_); icon_=nullptr; }
}
Shell::~Shell() { dispose(); closed_.revoke(); if(window_ && !state_.exiting) { state_.exiting=true; window_.Close(); } }
void Shell::readCore() {
    auto weak=weak_from_this(); auto queue=window_.DispatcherQueue(); auto dll=appDirectory()/L"openrevo_native_bridge.dll";
    readWorker_=std::jthread([weak,queue,dll](std::stop_token stop) {
        hstring status;
        try {
            ReadOnlyBridge bridge(dll); auto payload=bridge.provenance();
            auto json=Windows::Data::Json::JsonObject::Parse(to_hstring(payload));
            if(json.GetNamedNumber(L"abi")!=1 || json.GetNamedBoolean(L"hardware_writes") || json.GetNamedBoolean(L"background_workers")) throw std::runtime_error("Unexpected provenance contract");
            status=L"Core "+json.GetNamedString(L"upstream_version")+L" · 只读 · 未接管";
        } catch(std::exception const& e) { status=L"Core 不可用："+to_hstring(e.what()); }
        catch(hresult_error const& e) { status=L"Core 响应无效："+e.message(); }
        if(!stop.stop_requested())queue.TryEnqueue([weak,status] { if(auto self=weak.lock();self && !self->disposed_)self->coreStatus_.Text(status); });
    });
}
LRESULT CALLBACK Shell::windowProc(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp,UINT_PTR,DWORD_PTR data) {
    auto self=reinterpret_cast<Shell*>(data);
    if(!self || self->disposed_)return DefSubclassProc(hwnd,msg,wp,lp);
    try {
        if(msg==wakeMessage()) { self->wake(); return 0; }
        if(msg==self->taskbarCreated_) { self->tray_=false; if(!self->addTray() && self->state_.hidden)self->wake(); }
        if(msg==trayMessage) { auto action=LOWORD(lp); if(action==WM_LBUTTONUP || action==NIN_SELECT || action==NIN_KEYSELECT)self->wake(); else if(action==WM_CONTEXTMENU || action==WM_RBUTTONUP)self->trayMenu(); return 0; }
        if(msg==WM_SYSCOMMAND && (wp&0xFFF0)==SC_MINIMIZE) { if(self->state_.minimize(self->tray_)) { if(self->state_.hidden)self->window_.AppWindow().Hide(); else self->resize(); } return 0; }
        if(msg==WM_SYSCOMMAND && (wp&0xFFF0)==SC_MAXIMIZE && self->state_.mini) { self->toggle(); return 0; }
        if(msg==WM_DPICHANGED && self->state_.mini) { auto rect=reinterpret_cast<RECT*>(lp); SetWindowPos(hwnd,nullptr,rect->left,rect->top,rect->right-rect->left,rect->bottom-rect->top,SWP_NOZORDER|SWP_NOACTIVATE); return 0; }
    } catch(...) { OutputDebugStringW(L"OpenRevo.Cpp: window message failed\n"); }
    return DefSubclassProc(hwnd,msg,wp,lp);
}
void Shell::verifyHidden(std::filesystem::path const& output) {
    // Never Activate, show a tray icon, install a keyboard hook or initialize hardware here.
    bool resources=Application::Current().Resources().HasKey(box_value(L"CardStyle"));
    root_.Measure({400,640}); root_.Arrange({0,0,400,640});
    auto light=root_.RequestedTheme(); root_.RequestedTheme(ElementTheme::Light); root_.Measure({400,640}); root_.RequestedTheme(ElementTheme::Dark); root_.Measure({400,640}); root_.RequestedTheme(light);
    Windows::Data::Json::JsonObject report; report.Insert(L"passed",Windows::Data::Json::JsonValue::CreateBooleanValue(resources && page_.Children().Size()==8 && !IsWindowVisible(hwnd_)));
    report.Insert(L"windowVisible",Windows::Data::Json::JsonValue::CreateBooleanValue(IsWindowVisible(hwnd_)!=FALSE));
    report.Insert(L"miniGroups",Windows::Data::Json::JsonValue::CreateNumberValue(page_.Children().Size()));
    report.Insert(L"xamlResources",Windows::Data::Json::JsonValue::CreateBooleanValue(resources));
    smokeTrace("smoke.osd.create");auto osd=Osd::create();smokeTrace("smoke.osd.verify");auto osdPassed=osd->verifyHidden();osd.reset();smokeTrace("smoke.osd.closed");
    report.Insert(L"osdHiddenFrame",Windows::Data::Json::JsonValue::CreateBooleanValue(osdPassed));
    if(!osdPassed)report.SetNamedValue(L"passed",Windows::Data::Json::JsonValue::CreateBooleanValue(false));
    Windows::Data::Json::JsonArray modules; auto snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,GetCurrentProcessId());
    if(snapshot!=INVALID_HANDLE_VALUE) { MODULEENTRY32W entry{sizeof(entry)}; if(Module32FirstW(snapshot,&entry)) do { modules.Append(Windows::Data::Json::JsonValue::CreateStringValue(entry.szModule)); }while(Module32NextW(snapshot,&entry)); CloseHandle(snapshot); }
    report.Insert(L"modules",modules); std::ofstream stream(output); stream<<to_string(report.Stringify()); stream.close();
}
}
