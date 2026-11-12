using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;
using System.Text.Json;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace OpenRevo.Native;

public sealed partial class MainWindow : Window
{
    private WindowBrandIcon? windowBrandIcon;
    private readonly Grid windowSurface=new();
    private readonly Grid root = (Grid)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DesignInsetFill}'/>");
    private readonly TextBlock captionLabel = Ui.Text("Open-Revo", 14, true);
    private readonly TextBlock miniLabel = Ui.Text("MINI", 8, true, muted:true);
    private Grid? captionGrid;
    private Button? panelToggle;
    private readonly Dictionary<int, Button> performanceButtons = [];
    private readonly ApplicationHardwareRuntime? localRuntime;
    private ApplicationHardwareRuntime hardwareRuntime => localRuntime ??
        throw new InvalidOperationException("当前面板的设备事务必须由常驻后台处理。");
    private HardwareSession hardware => hardwareRuntime.Hardware;
    private HardwareOperationQueue operationQueue => hardwareRuntime.Operations;
    private PerformanceCommandService performance => hardwareRuntime.Performance;
    private DeviceCommandService devices => hardwareRuntime.Devices;
    private readonly PaintNavigation navigation = new();
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel page = new() { Spacing = 18, Padding = new Thickness(32, 24, 32, 32), MaxWidth = 1120, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, TextBlock> live = new();
    private Telemetry telemetry => hardwareRuntime.Telemetry.Foreground;
    private readonly DispatcherQueueTimer timer;
    private DeviceIdentity identity = new("未知", "正在读取设备…", "未知", "未知", []);
    private SystemSnapshot? latest;
    private EcSnapshot ec = new(null, null, null, null, "EC 读取已关闭");
    private Task<EcSnapshot>? ecTask;
    private DateTimeOffset ecStarted, lastEc;
    private bool ecEnabled, closed, mini, initialized, dialogOpen, pollingActive;
    private string currentPage = "overview";
    private bool syncingNavigation;
    private readonly bool uiReviewMode;
    private RectInt32 normalBounds;
    private int interval = 2;
    private int selectedTheme;
    private SystemPreferences systemPreferences;
    private readonly NativeActionLog actions = [];

#if RESIDENT_PANEL_HOST
    internal MainWindow(PanelClient client) : this(null, client, false, false) { }
#else
    internal MainWindow(ApplicationHardwareRuntime hardwareRuntime,bool uiChecks=false,bool reviewDetails=false)
        : this(hardwareRuntime, null, uiChecks, reviewDetails) { }
#endif

    private MainWindow(ApplicationHardwareRuntime? local, PanelClient? client, bool uiChecks, bool reviewDetails)
    {
        localRuntime = local;
        residentClient = client;
        uiReviewMode=uiChecks;
        if (local != null)
        {
            writeBrightness=local.Display.SetBrightnessAsync;
            local.PerformanceInput.PhysicalModeCompleted+=PresentPhysicalModeResult;
            systemPreferences = SystemPreferences.Load();
            var preferences = AppData.Load();
            selectedTheme=preferences.Theme;
            interval=preferences.SampleSeconds;
            lowRefreshOnBattery=preferences.LowRefreshOnBattery;
            osdEnabled=preferences.OsdEnabled;
            hardware.SetBatteryPerformancePermission(systemPreferences.UnlockBatteryLimit);
            overviewSaved=[..OverviewLayout.Load()];
        }
        else
        {
            writeBrightness=SetResidentBrightnessAsync;
            systemPreferences = new();
            InitializeResidentState(client!);
        }
#if NATIVE_RESIDENT_CORE
        Title = "Open Revo";
#else
        Title = "Open-Revo";
#endif
        SystemBackdrop = null;
        MotionDesign.Configure(systemPreferences.MotionSpeed);
        root.RequestedTheme = (ElementTheme)selectedTheme;
        // The target layout can be smaller than the moving HWND. Keep a full
        // client-area backdrop behind it throughout the shape transition.
        void PaintWindowSurface(){root.Background=Paint.Brush(Paint.Bg(root));windowSurface.Background=root.Background;}
        NativePluginThemePalette.RegisterRoot(root,"main");
        NativePluginThemePalette.Bind(root,PaintWindowSurface);
        PaintWindowSurface();
        root.ActualThemeChanged+=(_,_)=>PaintWindowSurface();
        Content = windowSurface;
        windowSurface.Children.Add(root);
        ExtendsContentIntoTitleBar = true;
        root.RowDefinitions.Add(new() { Height = new GridLength(32) });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var caption = captionGrid = new Grid { Margin = new Thickness(12, 0, 138, 0), ColumnSpacing = 0, VerticalAlignment=VerticalAlignment.Top };
        caption.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var drag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        captionDragArea = drag;
        captionLabel.VerticalAlignment = VerticalAlignment.Center;
        drag.Children.Add(new Image { Width = 16, Height = 16, VerticalAlignment=VerticalAlignment.Center, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "App.ico"))) });
        var brand=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,VerticalAlignment=VerticalAlignment.Center};
        miniLabel.VerticalAlignment=VerticalAlignment.Center;miniLabel.RenderTransform=new TranslateTransform{Y=3};miniLabel.Visibility=Visibility.Collapsed;
        brand.Children.Add(captionLabel);brand.Children.Add(miniLabel);
        drag.Children.Add(brand); caption.Children.Add(drag);
        themeToggle = Ui.Button("", async (_, _) => { await ChangeApplicationTheme(root.ActualTheme==ElementTheme.Dark?1:2); });
        themeToggle.Width = 40; themeToggle.Height = 32;
        themeToggle.MinWidth = 0; themeToggle.MinHeight = 0;
        themeToggle.Padding = new Thickness(0); themeToggle.BorderThickness = new Thickness(0);
        themeToggle.CornerRadius = new CornerRadius(0);
        themeToggle.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        themeToggle.VerticalAlignment = VerticalAlignment.Top;
        themeToggle.AllowFocusOnInteraction = false;
        UpdateThemeToggle();
        root.ActualThemeChanged += (_, _) => { if(!closed)UpdateThemeToggle(); };
        var captionActions = new StackPanel { Orientation = Orientation.Horizontal };
        captionActionArea = captionActions;
        panelToggle = Ui.Button("", (_, _) => ToggleMini());
        panelToggle.Width = 36; panelToggle.Height = 32; panelToggle.MinWidth = 0; panelToggle.MinHeight = 0;
        panelToggle.Padding = new Thickness(0); panelToggle.BorderThickness = new Thickness(0);
        panelToggle.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        panelToggle.AllowFocusOnInteraction = false;
        captionActions.Children.Add(panelToggle); captionActions.Children.Add(themeToggle);
        Grid.SetColumn(captionActions, 1); caption.Children.Add(captionActions); root.Children.Add(caption);
        SetTitleBar(drag);
        drag.SizeChanged += (_, _) => UpdateMiniCaptionRegions();
        captionActions.SizeChanged += (_, _) => UpdateMiniCaptionRegions();
        AttachCaptionModeCommand();
        ResizeLogical(1180, 850);
        var icon = Path.Combine(AppContext.BaseDirectory, "App.ico");
        if (File.Exists(icon)) { AppWindow.SetIcon(icon); windowBrandIcon = new(captionWindow, icon); }
        navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        navigation.OpenPaneLength = 174;navigation.CompactPaneLength=54;navigation.IsPaneToggleButtonVisible=false;
        navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        navigation.IsSettingsVisible = false;
        navigation.PaneHeader = null;
        AddNavigation("状态总览", "overview", Symbol.Home);
        AddNavigation("显卡与屏幕", "display", Symbol.Video);
        AddNavigation("电源与电池", "power", Symbol.Manage);
        AddNavigation("灯光与设备", "devices", Symbol.Keyboard);
        AddNavigation("插件兼容", "plugins", Symbol.Library);
        AddNavigation("设置与诊断", "settings", Symbol.Setting);
        var footer = Ui.Text("原生版 0.9.84", 12, muted: true);
        footer.Margin = new Thickness(16, 8, 8, 16); navigation.PaneFooter = null;
        scroll.Content = page; navigation.Content = scroll;
        scroll.SizeChanged += (_,_) => QueueDesktopViewport();
        navigation.SelectionChanged += (_, e) => { if (!syncingNavigation && e.SelectedItem is PaintNavItem item && item.Tag is string id) PaintNavigate(id); };
        Grid.SetRow(navigation, 1); root.Children.Add(navigation);
        navigation.Build();
        if (residentClient == null) navigation.SelectedItem = navigation.MenuItems[0];
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(interval);
        timer.Tick += (_, _) => Tick();
        AppWindow.Changed += (_, e) => {if((e.DidSizeChange||e.DidPresenterChange)&&!panelShapeChanging){AlignCaption();QueueDesktopViewport();}if(!uiReviewMode&&AppWindow.Presenter is OverlappedPresenter{State:OverlappedPresenterState.Minimized})QueuePanelMinimize();UpdatePolling();};
        if (residentClient == null) InitializeTray();
        else AttachResidentPanel();
        AppWindow.Closing += (sender,args)=>
        {
            if (residentClient != null)
            {
                args.Cancel = !residentAllowClose;
                if (args.Cancel) HideToTray();
                return;
            }
            if(sessionEnding){operationQueue.StopAccepting();desktopRuntimeClosed=true;return;}
            if((!uiReviewMode||trayReviewMode)&&!exitRequested){args.Cancel=true;HideToTray();return;}
            if(hardwareRuntimeReleased)return;
            if(hardwareBusy||quickBusy||requestedWinLocked.HasValue||takeoverChanging||tuningApplying||displayBusy||brightnessApplying||displayTopologyReading||gpuChanging||desktopServiceBusy||sleepOfficeBusy||batteryGuardBusy){args.Cancel=true;return;}
            args.Cancel=true;
            _ = CloseDesktopRuntime();
        };
        Activated += (_,_)=>{windowBrandIcon?.Apply();if(!panelShapeChanging){AlignCaption();ResetCaptionHover();}};
        Closed += (_,_)=>{if(localRuntime!=null){localRuntime.PerformanceInput.PhysicalModeCompleted-=PresentPhysicalModeResult;localRuntime.PerformanceInput.SynchronizePhysicalMode(false);DisposeDisplayTopologyRead();StopNativePluginsOnClose();}ReleaseOverviewDrag();ReleaseMiniPreview();HidePanelHandoff();panelCompositionGuard?.Dispose();extensionHost?.Dispose();windowBrandIcon?.Dispose();};
        Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            if (residentClient != null) { residentSensors?.Dispose(); residentClient.Dispose(); Application.Current.Exit(); return; }
            operationQueue.StopAccepting();DisposeTray(); DetachDesktopPowerEvents();powerTimer?.Stop();capsLockObserver?.Dispose(); nativeOsd?.Dispose();lightingDebounce?.Cancel(); _ = hardwareRuntime.ReleaseAsync();
        };
        root.Loaded += async (_, _) =>
        {
            if (initialized) return; initialized = true;
            if (residentClient != null) { InitializeResidentPresentation(); return; }
            if(Environment.GetCommandLineArgs().Contains("--painted-ui-review")){await RunPaintedUiReview();Close();return;}
            if(Environment.GetCommandLineArgs().Contains("--approved-ui-review")){await RunApprovedUiReview();Close();return;}
            if(App.IsPanelShapeReview){await RunPanelShapeReview();return;}
            AlignCaption();
            root.XamlRoot.Changed+=(_,_)=>{if(Math.Abs(root.XamlRoot.RasterizationScale-captionScale)>0.001)AlignCaption();};
            try { identity = await Task.Run(DeviceIdentity.Read); }
            catch (Exception ex) { actions.Add("设备读取失败：" + ex.Message); }
            if (closed) return;
            DiagnosticLog.Record("device.identity",new{identity,bios=DeviceIdentity.ReadBiosVersion()});
            await ReadDisplay();
            var compatibility=await Task.Run(CoreMachine.Refresh);
            ecEnabled = compatibility.Ready;
            if(!compatibility.Ready){hardwareMessage=compatibility.Reason;actions.Add("硬件接管未启动："+compatibility.Reason);}
            if (ecEnabled)
            {
                await AcquireAtStartup();
            }
            await LoadPanelHardware();await InitializeSystemSettingsRuntime();
            if (closed) return;
            Render();
            if(uiChecks){if(App.IsAdmissionReview)await RunAdmissionReview();else if(App.IsCapsUiReview)await RunCapsUiReview();else if(App.IsOsdPowerReview)await RunOsdPowerReview();else if(App.IsDesignReview)await RunDesignReview();else if(Environment.GetCommandLineArgs().Any(a=>a is "--ui-settings-checks" or "--ui-theme-restore-checks"))await RunSettingsTrayReview();else if(Environment.GetCommandLineArgs().Contains("--ui-replica-checks"))await RunReplicaLayoutReview();else if(Environment.GetCommandLineArgs().Contains("--ui-window-checks"))await RunWindowFixChecks();else await RunUiReviewChecks();Close();return;}
            ObserveHardwareOsd();StartCapsLockMonitor();UpdatePolling(); StartPowerPolicy(); await CheckAutomaticRefresh(true);await InitializeDesktopServicesRuntime();
        };
        bool initialMini = client?.Attached.WakeTarget switch
        {
            PanelWakeTarget.Mini => true,
            PanelWakeTarget.Full or PanelWakeTarget.Settings or PanelWakeTarget.Updates => false,
            _ => client?.Attached.Draft?.Compact ??
                (systemPreferences.WakeMode=="mini"||systemPreferences.WakeMode=="remember"&&systemPreferences.LastMini)&&!reviewDetails
        };
        if (initialMini) ToggleMini(); else UpdatePanelToggle();
    }
    private void AddNavigation(string title,string id,Symbol unused)
    {
        navigation.MenuItems.Add(new PaintNavItem(title,id,id switch{"overview"=>"home","devices"=>"keyboard",_=>id}));

    }
    private bool IsVisibleNow() => !closed && AppWindow.IsVisible && (AppWindow.Presenter is not OverlappedPresenter p || p.State != OverlappedPresenterState.Minimized);
    private void UpdatePolling()
    {
        if (residentClient != null) { PublishResidentDemand(); SyncResidentPluginPage(); return; }
        if(App.IsPanelShapeReview||Environment.GetCommandLineArgs().Any(a=>a is "--approved-ui-review" or "--painted-ui-review"))return;
        if (timer == null || !initialized) return;
        bool visible = IsVisibleNow();
        // Moving/resizing a visible native window must not cause extra telemetry reads.
        if (visible == pollingActive) return;
        pollingActive = visible;
        if (visible) { telemetry.Reset(); Tick(); timer.Start(); }
        else
        {
            timer.Stop();
            telemetry.Reset();
            overviewSampling.SetForegroundDemand(null);
            UpdateGpuTemperatureDemand();
        }
    }
    private void Tick()
    {
        if (residentClient != null) return;
        if (!IsVisibleNow()) return;
        latest = telemetry.Read();
        extensionHost?.Publish(new(DateTimeOffset.Now, latest.CpuPercent, latest.UsedMemoryGiB, latest.TotalMemoryGiB, latest.BatteryPercent, latest.OnAc));
        RefreshNativePluginState(true);
        if (ecTask is { IsCompleted: true })
        {
            if (ecEnabled) ec = ecTask.IsCompletedSuccessfully ? ecTask.Result : new(null, null, null, null, "EC 读取异常，数据不可用");
            ecTask = null;
        }
        if (ecEnabled && !gpuChanging && ecTask == null && DateTimeOffset.Now - lastEc > TimeSpan.FromSeconds(6))
        {
            ecStarted = lastEc = DateTimeOffset.Now;
            ecTask = Task.Run(() => EcReader.Read(identity));
        }
        if (ecEnabled && ecTask is { IsCompleted: false } && DateTimeOffset.Now - ecStarted > TimeSpan.FromSeconds(4))
            ec = new(null, null, null, null, "EC 读取超时；未启动重复请求，可关闭读取");
        UpdateGpuTemperatureDemand();
        UpdateValues();
        SamplePanelTemperatures();
        SampleQuickActions();
        SampleOverviewIntegration();EnforceDeveloperPreviewReadOnly();
    }
    private TextBlock Value(string key, double size = 30)
    {
        var block = Ui.Text("—", size, true); live[key] = block; return block;
    }
    private void Put(string key, string value) { if (live.TryGetValue(key, out var block)) block.Text = value; }
    private void UpdateValues()
    {
        RefreshPaintedState();
        if (latest is not { } s) return;
        Put("cpu", s.CpuPercent is double c ? $"{c:0.0}%" : "采样中…");
        Put("memory", s.UsedMemoryGiB is double used ? $"{used:0.0} GB" : "不可用");
        Put("memoryDetail", s.TotalMemoryGiB is double total ? $"共 {total:0.0} GB 物理内存" : "无法读取内存");
        Put("approvedBatteryAmount",s.BatteryPercent?.ToString()??"—");Put("approvedHealthLimit",hardware.State is {} chargeState?"充电上限 "+chargeState.Upper+"%":"充电上限 —");
        Put("battery", s.BatteryPercent is int b ? $"{b}%" : "不可用");
        Put("batteryDetail", s.OnAc switch { true => s.Charging == true ? "已连接电源 · 正在充电" : "已连接电源", false => "正在使用电池", _ => "供电状态未知" });
        if(desktopCpuBar!=null)desktopCpuBar.Value=Math.Clamp(s.CpuPercent??0,0,100);
        UpdateOriginalBatteryGauge();
        Put("cpuFan", ec.CpuFan is int cf ? $"{cf:N0} RPM" : "不可用");
        Put("gpuFan", ec.GpuFan is int gf ? $"{gf:N0} RPM" : "不可用");
        Put("ecStatus", ec.Status);
        Put("limit", ec.BatteryLimit is int limit ? $"{limit}%" : "不可用");
        Put("boost", ec.FanBoost switch { true => "已开启", false => "已关闭", _ => "不可用" });
        Put("footprint", $"工作集 {s.WorkingSetMiB:0.0} MiB  ·  私有内存 {s.PrivateMiB:0.0} MiB");
        Put("updated", $"{s.At:HH:mm:ss} 更新  ·  每 {interval} 秒采样  ·  最小化后暂停");
        UpdateCompactValues();
        UpdateOverviewValues();
        if(!mini)
        {
            SyncDesktopLighting();
        }
    }
    private void Render()
    {
        captureResidentDeclaredDraft=null;
        NativePluginThemePalette.RegisterRoot(root,mini?"mini":"main");
        if (residentClient == null && (currentPage != "plugins" || mini)) StopForegroundFolderPlugins();
        ReleaseOverviewDrag(); ReleaseMiniPreview();
        renderCount++;
        live.Clear(); performanceButtons.Clear(); compactChargeButtons.Clear(); refreshButtons.Clear();
        miniRefreshHost=null;refreshMoreButton=null;refreshMenuItems.Clear();miniRatesSignature="";
        brightnessButtons.Clear();brightnessSlider=null;brightnessEditing=false;
        tuningPanel=null;
        tuningNumbers.Clear();desktopCpuBar=null;desktopBatteryFill=null;desktopBatteryAcIcon=null;desktopBatteryDcIcon=null;desktopBatteryChoices.Clear();detailSectionTitles.Clear();ResetOverviewBindings();
        desktopEffects.Clear();desktopColors.Clear();desktopLightToggle=null;desktopLightState=null;desktopColorHex=null;desktopCustomSwatch=null;unavailablePowerSwitches.Clear();unavailableLightingTabs.Clear();
        desktopPowerSwitches.Clear();desktopPowerHints.Clear();lightingZoneTabs.Clear();zoneEditor=null;ResetColorServiceBindings();
        gpuButtons.Clear();gpuReadButton=null;
        quickButtons.Clear();lightingModeBox=null;lightingColorBox=null;lightingLevelBar=null;lightingLevelLabel=null;lightingColorPicker=null;
        compactBoostButton=null;compactStatus=null;takeoverSwitch=null;approvedBrightnessNumber=null;approvedRefreshChoice=null;
        panelMotionAnchors.Clear();
        page.Children.Clear();
        scroll.ChangeView(0, 0, null, true);
        ArrangeDesktopViewport();
        RenderPaintedPage();
        if (residentClient == null) UpdateGpuTemperatureDemand();
        else { PublishResidentDemand(); SyncResidentPluginPage(); }
    }
    private Border Metric(string title, string key, UIElement detail) => Ui.Card(Ui.Stack(Ui.Text(title, 13, muted: true), Value(key), detail));
    private ToggleSwitch EcToggle()
    {
        var toggle = new ToggleSwitch { IsOn = ecEnabled, OnContent = "读取中", OffContent = "已关闭", IsEnabled = EcReader.IsAllowed(identity), VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) =>
        {
            ecEnabled = toggle.IsOn;
            ec = new(null, null, null, null, ecEnabled ? "正在读取…" : "EC 读取已关闭");
            lastEc = DateTimeOffset.MinValue; Tick();
        };
        return toggle;
    }
    private void ToggleMini()
    {
        if (closed) return;
        if (initialized && !mini && PaintRoute=="minicustomization" && (miniDraft!=null||miniSaving)) { _=ToggleMiniFromEditor(); return; }
        if (panelShapeChanging) { SetPanelGoal(!panelGoalMini); return; }
        CloseCaptionExpandTip();
        panelGoalMini = !mini;
        panelShapeChanging = true;
        panelShapeCompletion = RunPanelHandoff(AppWindow.IsVisible && initialized && Design.MotionEnabled);
    }
    private void UpdatePanelToggle()
    {
        if (panelToggle == null) return;
        panelToggle.Visibility=mini&&!captionModeAttached?Visibility.Visible:Visibility.Collapsed;
        panelToggle.Content = Ui.Icon(mini ? "\uE740" : "\uE73F",13);
        string label = mini ? "展开大面板" : "切换小面板";
        ToolTipService.SetToolTip(panelToggle, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panelToggle, label);
    }
    private RectInt32 MiniPanelBounds()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale=GetDpiForWindow(captionWindow)/96d;if(scale<=0)scale=1;
        int margin=(int)Math.Round(MiniEdgeMarginDip*scale),width=Math.Min((int)(400*scale),Math.Max(1,area.Width-40)),height=Math.Min((int)(PaintMiniHeight(width/scale)*scale),Math.Max(1,area.Height-40));
        return new(area.X+Math.Max(0,area.Width-width-margin),area.Y+Math.Max(0,area.Height-height-margin),width,height);
    }
    private async Task<bool> Confirm(string title, string message)
    {
        if (dialogOpen) return false;
        return await PaintDialog(title,Paint.Text(message),"确认修改","取消");
    }
    private async Task ShowMessage(string title, string message)
    {
        if (dialogOpen || closed) return;
        await PaintDialog(title,Paint.Text(message));
    }
    private async Task OpenSettings(string uri)
    {
        try { if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(uri))) await ShowMessage("未能打开", "请从 Windows 开始菜单打开设置。"); }
        catch (Exception e) { await ShowMessage("未能打开", e.Message); }
    }
    private Task Export()=>ExportDiagnosticReport();
    private async Task ChangeApplicationTheme(int theme)
    {
        if(theme is <0 or >2)return;
        if (residentClient != null) { await SetResidentPreferenceAsync(new(PanelPreferenceKind.Theme, Number: theme)); return; }
        selectedTheme=theme;root.RequestedTheme=(ElementTheme)theme;UpdateThemeToggle();await SavePreferences(true);
    }
    private async Task SavePreferences(bool themeChanged=false)
    {
        if (residentClient != null) throw new InvalidOperationException("候选面板的设置须由后台保存。");
        if(uiReviewMode&&!AppData.IsIsolatedReviewData)return;
        try {AppData.Update(previous=>previous with{Theme=themeChanged?selectedTheme:previous.Theme,SampleSeconds=interval,LowRefreshOnBattery=lowRefreshOnBattery,OsdEnabled=osdEnabled});}
        catch (Exception e) { await ShowMessage("设置未保存", e.Message); }
    }
    private void ResizeLogical(int width, int height)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (scale <= 0) scale = 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int targetWidth=Math.Min((int)(width*scale),Math.Max(1,area.Width-40)),targetHeight=Math.Min((int)(height*scale),Math.Max(1,area.Height-40));
        if(!mini){var minimum=MinimumDesktopPixels();targetWidth=Math.Max(targetWidth,minimum.Width);targetHeight=Math.Max(targetHeight,minimum.Height);}
        AppWindow.Resize(new(targetWidth,targetHeight));
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}







