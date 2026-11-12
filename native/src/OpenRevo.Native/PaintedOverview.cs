using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;
using Windows.Foundation;
using Windows.System;
using System.Numerics;
namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private readonly List<Action> paintBindings=[];
    private bool refreshingPaint;
    private void RefreshPaintedState()
    {
        if(refreshingPaint||closed)return;refreshingPaint=true;
        try{foreach(var bind in paintBindings.ToArray())bind();miniPreview?.Refresh();navigation.SetStatus(PanelHardwareActive,identity.Board=="未知"?"设备状态待读取":identity.Board);}finally{refreshingPaint=false;}
    }
    private TextBlock PT(Func<string> value,double size=14,bool bold=false,bool muted=false)
    {var text=Paint.Text(value(),size,bold,muted);paintBindings.Add(()=>{string next=value();if(text.Text!=next)text.Text=next;});return text;}
    private PaintButton PB(string name,Func<Task> action,bool hardwareAction=false,bool createDefaultContent=true)
    {
        bool running = false;
        return new(name, async () =>
        {
            if (running || uiReviewMode && hardwareAction) return;
            running = true;
            try { await action(); }
            catch (Exception error) { await ShowMessage("操作未完成", error.Message); }
            finally { running = false; RefreshPaintedState(); }
        }, createDefaultContent);
    }
    private PaintButton PL(string name,Action action)
    {var b=new PaintButton(name,action,createDefaultContent:false){Variant="plain",Padding=new(0),MinHeight=28};b.Content=Paint.Between(Paint.Text(name,12),Paint.Utility("chevron",14));b.StretchContent();return b;}
    private ToggleSwitch PS(string name,Func<bool?> read,Func<bool,Task> action,Func<bool>? enabled=null,string family="blue",bool hardwareAction=false)
    {
        bool changing = false;
        var toggle = PaintSwitch.Create(name, read() == true, async value =>
        {
            if (changing || uiReviewMode && hardwareAction) { RefreshPaintedState(); return; }
            changing = true;
            try { await action(value); }
            catch (Exception error) { await ShowMessage("设置未完成", error.Message); }
            finally { changing = false; RefreshPaintedState(); }
        }, family);
        paintBindings.Add(() =>
        {
            var value = read();
            if (!changing) PaintSwitch.SetReadback(toggle, value == true);
            toggle.IsEnabled = !changing && (enabled?.Invoke() ?? true) && value.HasValue;
        });
        return toggle;
    }
    private FrameworkElement PaintHeader(string route)
    {
        string key=route=="overview"?"home":route;string title=route switch{"overview"=>"状态总览","devices"=>"灯光与设备",_=>route},subtitle="";
        if(route=="updates"){key="info";title="软件更新";subtitle="查看社区版发布与下载状态。";}
        if(route=="minicustomization"){key="settings";title="小面板自定义";subtitle="把常用功能放在顺手的位置。";}
        if(ApprovedCopy.Pages.TryGetValue(route,out var copy)){title=copy.Title;subtitle=copy.Subtitle;key=copy.Icon switch{"tuning"=>"mode-custom","color"=>"lighting",_=>copy.Icon};}
        if(route=="overview")subtitle="运行状态与常用控制，一眼掌握。";
        if(route=="devices")key="keyboard";
        var detail=route=="overview"?PT(()=>
        {
            if (residentClient != null)
            {
                if (residentOverview is not { } detail) return subtitle;
                var readAt = detail.At.ToLocalTime();
                return detail.Fresh ? $"更新于 {readAt:HH:mm:ss}" : $"上次读数 {readAt:HH:mm:ss} · {(detail.Reading ? "更新中" : "待刷新")}";
            }
            var state = overviewSampling.ReadState();
            if (state.Sample is not { } sample) return subtitle;
            var at = sample.At.ToLocalTime();
            return state.IsFresh ? $"更新于 {at:HH:mm:ss}" : $"上次读数 {at:HH:mm:ss} · {(state.IsReading ? "更新中" : "待刷新")}";
        },12,muted:true):Paint.Text(subtitle,12,muted:true);
        if(IsSecondaryPaintRoute(route))return Paint.Stack(6,Paint.Text(title,24,true),detail);
        return Paint.Inline(10,Paint.Icon(key,40),Paint.Stack(route=="overview"?11:6,Paint.Text(title,24,true),detail));
    }
    private void PaintNavigate(string route)
    {if(PaintRoute=="minicustomization"&&route!="minicustomization"&&(miniDraft!=null||miniSaving)){_=NavigateFromMiniEditor(route);return;}if(route=="tuning"){OpenTuning();return;}customTuningOpen=false;colorTuningOpen=route=="color";if(route=="lightingzone"){selectedLightingZone="front";currentPage="devices";}else currentPage=route=="color"?"display":route;Render();}
    private string PaintRoute=>customTuningOpen?"tuning":colorTuningOpen?"color":currentPage=="devices"&&selectedLightingZone!="keyboard"&&!(keyboard.Kind=="White"&&(residentClient!=null||hardware.CoreDelegated))?"lightingzone":currentPage;
    private static bool IsSecondaryPaintRoute(string route)=>route is "minicustomization" or "sleep" or "lightingzone" or "tuning" or "color" or "osdsettings" or "storage" or "diagnostics" or "updates";
    private void RenderPaintedPage()
    {
        paintBindings.Clear();string route=PaintRoute;
        syncingNavigation=true;navigation.SelectedItem=navigation.MenuItems.OfType<PaintNavItem>().FirstOrDefault(i=>i.Id==(route is "tuning" or "color"?"display":route=="lightingzone"?"devices":route is "minicustomization" or "osdsettings" or "diagnostics" or "storage" or "updates"?"settings":route=="sleep"?"power":currentPage));syncingNavigation=false;
        scroll.VerticalScrollMode=mini?ScrollMode.Disabled:ScrollMode.Auto;scroll.VerticalScrollBarVisibility=mini?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
        if(mini){RenderPaintedMini();RefreshPaintedState();return;}
        if(IsSecondaryPaintRoute(route))
        {string parent=route=="sleep"?"power":route=="lightingzone"?"devices":route is "tuning" or "color"?"display":"settings";var back=PB("返回",()=>{if(route=="tuning"){BackFromTuning();return Task.CompletedTask;}if(route=="lightingzone")selectedLightingZone="keyboard";PaintNavigate(parent);return Task.CompletedTask;});back.HorizontalAlignment=HorizontalAlignment.Left;back.Variant="plain";back.MinHeight=22;back.Padding=new(0);back.Content=Paint.Inline(6,Paint.Utility("back",16),Paint.Text("返回 "+(parent=="power"?"电源与电池":parent=="devices"?"灯光与设备":parent=="display"?TuningReturnLabel:"设置与诊断"),12));page.Children.Add(back);}
        if(route=="overview"){page.Children.Add(Paint.Between(PaintHeader(route),PaintEditActions()));PaintHome();}
        else {page.Children.Add(PaintHeader(route));if(residentClient==null)RenderPaintedDetails(route);else RenderResidentDetails(route);}
        if(route=="overview")UpdateOverviewValues();else RefreshPaintedState();ArrangeDesktopViewport();
    }
    private FrameworkElement PaintEditActions()
    {
        if(overviewDraft==null){var edit=PB("编辑磁贴",()=>{overviewDraft=[..overviewSaved];Render();return Task.CompletedTask;},createDefaultContent:false);edit.Padding=new(10,5,10,5);edit.Content=Paint.Inline(8,Paint.Icon("edit-tiles",24),Paint.Text("编辑磁贴"));return edit;}
        PaintButton Icon(string icon,string name,Func<Task> action){var b=PB(name,action,createDefaultContent:false);b.Content=Paint.Icon(icon);b.Width=b.Height=34;b.MinHeight=0;b.Padding=new(0);b.Variant="icon";paintBindings.Add(()=>b.IsEnabled=overviewDrag?.Busy!=true);return b;}
        return Paint.Inline(8,Icon("save-layout","保存磁贴布局",async()=>{var order=overviewDraft!.ToArray();if(residentClient!=null)await SaveResidentLayoutAsync(new(Overview:order));else if(!uiReviewMode)await Task.Run(()=>OverviewLayout.Save(order));overviewSaved=[..order];overviewDraft=null;Render();}),Icon("cancel-layout","取消编辑磁贴",()=>{overviewDraft=null;Render();return Task.CompletedTask;}));
    }
    private void PaintHome()
    {
        var ids=overviewDraft??overviewSaved;overviewBoard=new OverviewBoard{Order=[..ids]};
        foreach(string id in ids)
        {
            var tile=new Grid{Tag=id,Background=Paint.Brush("00000000")};var body=PaintTile(id);tile.Children.Add(body);
            if(overviewDraft!=null)
            {
                body.IsHitTestVisible=false;foreach(var input in DetailDescendants(body).OfType<Control>())input.IsTabStop=false;
                var remove=new PaintButton("移除 "+tileTitles[id],()=>{if(overviewDrag?.Busy==true)return;overviewDraft.Remove(id);Render();},createDefaultContent:false){Content=Paint.Utility("minus",14),Width=24,Height=24,MinHeight=0,Padding=new(0),CornerRadius=new(12),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};tile.Children.Add(remove);paintBindings.Add(()=>remove.IsEnabled=overviewDrag?.Busy!=true);
                var handle=new PaintButton("拖动 "+tileTitles[id],createDefaultContent:false){IsHitTestVisible=false,Content=Paint.Text("⠿",16),Width=24,Height=24,MinHeight=0,Padding=new(0),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};tile.Children.Add(handle);
                handle.KeyDown+=(_,e)=>{if(overviewDrag?.Busy==true)return;if((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0&&e.Key is VirtualKey.Left or VirtualKey.Right){int from=ids.IndexOf(id),to=Math.Clamp(from+(e.Key==VirtualKey.Left?-1:1),0,ids.Count-1);ids.RemoveAt(from);ids.Insert(to,id);Render();e.Handled=true;}};
            }
            overviewBoard.Children.Add(tile);
        }
        if(overviewDraft!=null)
        {
            string[] missing=OverviewLayout.BuiltIns.Except(ids).ToArray();var add=new PaintSelect("添加面板",missing.Select(id=>tileTitles[id]).ToArray(),-1,i=>{if(i>=0&&overviewDrag?.Busy!=true){overviewDraft.Add(missing[i]);Render();}},createDefaultContent:false){MinHeight=92};add.Content=Paint.Stack(4,Paint.Text("＋",24),Paint.Text("添加面板",12));overviewBoard.Children.Add(add);paintBindings.Add(()=>add.IsEnabled=overviewDrag?.Busy!=true);
        }
        StartOverviewDrag();
        page.Children.Add(overviewBoard);page.Children.Add(Paint.Text("快捷控制",16,true));
        var groups=new DesktopTiles{PreferredColumns=3,MinimumItemWidth=0,SingleColumnBelow=639,ColumnSpacing=12,RowSpacing=12};
        foreach(var(title,keys)in new[]{("键盘与触控",new[]{"touchpad","win","fn"}),("隐私与提示",new[]{"camera","mic","osd"}),("散热与唤醒",new[]{"boost","awake","screen"})})
        {
            FrameworkElement heading=Paint.Text(title,16,true);if(title=="键盘与触控")heading=Paint.Between(heading,PL("灯光",()=>PaintNavigate("devices")));
            heading.Margin=new(4,0,4,4);var stack=Paint.Stack(0,heading);foreach(string key in keys)stack.Children.Add(PaintQuickRow(key));groups.Children.Add(Paint.StackCard(stack,13));
        }page.Children.Add(groups);
    }
    private FrameworkElement PaintTile(string id)
    {
        if(id=="fan")return PaintMinHeight(Paint.StackCard(Paint.Stack(8,Paint.Text("散热",14,true),Paint.Between(Paint.Text("CPU 风扇",12,muted:true),PT(()=>ec.CpuFan is int n?$"{n} RPM":"— RPM",16)),Paint.Between(Paint.Text("GPU 风扇",12,muted:true),PT(()=>ec.GpuFan is int n?$"{n} RPM":"— RPM",16)))),120);
        if(id is "cpu" or "gpu" or "vram")
        {
            FrameworkElement Live(string key,double size=12){var t=Paint.Text("—",size);live[key]=t;return t;}
            string prefix=id=="vram"?"overviewMemory":id=="cpu"?"overviewCpu":"overviewGpu";
            var title = id == "vram" ? PT(() => OverviewMemoryTitle, 14, true) : Paint.Text(tileTitles[id], 14, true);
            var head=Paint.Between(title,Paint.Inline(3,Live(prefix+"Mhz"),Paint.Text("MHz",12,muted:true)),id=="gpu"?8:16);
            var value=id=="vram"?Paint.Inline(3,Live("overviewVramUsed",24),Live("overviewVramTotal")):Paint.Inline(2,Live(prefix+"Temp",24),Paint.Text("°C",14));
            var foot=Paint.Between(Paint.Inline(5,Paint.Text("占用",12,muted:true),Live(id=="vram"?"overviewVramUsage":prefix+"Usage")),id=="vram"?PT(()=>OverviewMemoryNote,12,muted:true):Live(prefix+"Power"),8);
            var meter=new Border{Height=3,CornerRadius=new(2),Background=Paint.Brush(Paint.Line(root))};var fill=new Border{Height=3,CornerRadius=new(2),Background=Paint.Brush(Paint.Accent(root)),HorizontalAlignment=HorizontalAlignment.Left};meter.Child=fill;
            paintBindings.Add(()=>{double ratio=id=="cpu"?latest?.CpuPercent??0:id=="gpu"?CurrentOverviewGpu?.Usage??0:CurrentOverviewGpu?.DisplayMemoryPercent??0;fill.Width=Math.Max(0,meter.ActualWidth)*Math.Clamp(ratio/100,0,1);});
            var content=Paint.Stack(8,head,value,foot,meter);
            if(id is "cpu" or "gpu")
            {
                string Model()=>id=="cpu"?(string.IsNullOrWhiteSpace(identity.Processor)||identity.Processor=="未知"?"型号待读取":identity.Processor):
                    CurrentOverviewGpu?.AdapterName is { Length: > 0 } name ? name : "型号待读取";
                var model=PT(Model,12,muted:true);model.TextWrapping=TextWrapping.NoWrap;model.TextTrimming=TextTrimming.CharacterEllipsis;
                ToolTipService.SetToolTip(model,model.Text);
                paintBindings.Add(()=>{if(ToolTipService.GetToolTip(model) as string!=model.Text)ToolTipService.SetToolTip(model,model.Text);});
                content.Children.Insert(1,model);
            }
            return PaintMinHeight(Paint.StackCard(content),120);
        }
        string icon=id=="performance"?"performance":id=="battery"?"battery":"display";var heading=Paint.Inline(8,Paint.Icon(icon,28,true,false),Paint.Text(tileTitles[id],16,true));
        if(id=="performance")
        {
            var target=Paint.Text("—",12,muted:true);live["overviewPowerTarget"]=target;var grid=new Grid{RowSpacing=8};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.Children.Add(heading);var meta=Paint.Between(PT(()=>PerformanceName,14,muted:true),target);Grid.SetRow(meta,1);grid.Children.Add(meta);var modes=PaintModes(false);Grid.SetRow(modes,2);grid.Children.Add(modes);return PaintMinHeight(Paint.Card(grid),180);
        }
        FrameworkElement number=id=="battery"?Paint.Inline(12,PaintBatteryPicture(44),PT(()=>latest?.BatteryPercent is int n?n+"%":"—",24)):PT(()=>DisplayedRefreshHertz is int hz?hz+" Hz":"— Hz",24);
        var caption=PT(()=>id=="battery"?latest?.OnAc switch{true=>"已连接电源",false=>"正在使用电池",_=>"供电状态未知"}:"当前刷新率",12,muted:true);
        FrameworkElement controls;
        if(id=="battery")
        {
            var cap=new PaintSelect("充电上限",new[]{"100%","80%","60%"},-1,async i=>{if(!uiReviewMode)await SetChargeLimit(new[]{100,80,60}[i]);},createDefaultContent:false){Variant="plain",MinWidth=0,MinHeight=28,Padding=new(0)};
            cap.Content=Paint.Between(Paint.Text("充电上限",12),Paint.Inline(6,PT(()=>PanelBatteryLimit is int limit?limit+"%":"—",12),Paint.Utility("chevron",14)));cap.StretchContent();BindCapability(cap,()=>PanelSupportsBattery);
            paintBindings.Add(() => cap.IsEnabled = PanelHardwareActive && PanelSupportsBattery && !PanelHardwareActionBusy(PanelOperation.SetBatteryLimit));
            controls=Paint.Stack(0,cap,PL("Windows 电源计划",()=>PaintNavigate("settings")));
        }
        else
        {
            var choice = PaintRefreshChoice("调整刷新率", overview: true);
            var automatic=PB("拔电低刷",async()=>{if(residentClient!=null){await ToggleResidentLowRefreshAsync();return;}lowRefreshOnBattery=!lowRefreshOnBattery;await SavePreferences();if(!uiReviewMode)await CheckAutomaticRefresh(true);ConfirmedActionOsd("拔电自动低刷 · "+(lowRefreshOnBattery?"已开启":"已关闭"),"refresh");},createDefaultContent:false);automatic.Variant="plain";automatic.Padding=new(0);automatic.MinHeight=28;automatic.Content=Paint.Between(Paint.Text("拔电低刷",12),PT(()=>lowRefreshOnBattery?"已开启":"已关闭",12,muted:true));automatic.StretchContent();controls=Paint.Stack(0,choice,automatic);
            if (residentClient != null) paintBindings.Add(() => automatic.IsEnabled = !PanelActionBusy(PanelOperation.Desktop, typeof(PanelSetLowRefresh)));
        }
        var body=TileBody(heading,number,caption,Paint.Rule(controls,4));return PaintMinHeight(Paint.Card(body),180);
    }

    private PaintSelect PaintRefreshChoice(string label, bool overview = false)
    {
        int[] rates = [];
        var choice = new PaintSelect(label, [], -1, async index =>
        {
            if (uiReviewMode || index < 0 || index >= rates.Length) return;
            await ChangeRefresh(rates[index]);
        }, createDefaultContent: !overview);
        if (overview)
        {
            choice.Variant = "plain";
            choice.MinHeight = 28;
            choice.MinWidth = 0;
            choice.Padding = new(0);
            choice.Content = Paint.Between(Paint.Text(label, 12), Paint.Utility("chevron", 14));
            choice.StretchContent();
        }
        else choice.Width = 180;

        void Refresh()
        {
            int[] next = displayState?.Rates.Distinct().Order().ToArray() ?? [];
            int selected = Array.IndexOf(next, DisplayedRefreshHertz ?? 0);
            choice.UnselectedText = DisplayedRefreshHertz is int hertz ? $"{hertz} Hz" : "—";
            if (!rates.SequenceEqual(next))
            {
                choice.DismissPopup();
                rates = next;
                choice.Replace(rates.Select(hz => $"{hz} Hz").ToArray(), selected);
            }
            else choice.SelectedIndex = selected;
            choice.IsEnabled = rates.Length > 0 && displayState?.Active == true && !displayBusy;
        }
        Refresh();
        paintBindings.Add(Refresh);
        return choice;
    }
    private FrameworkElement PaintModes(bool compact,bool gpu=false,bool readOnly=false)
    {
        int count=gpu?3:4;var grid=new Grid{ColumnSpacing=6};for(int i=0;i<count;i++)grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        for(int i=0;i<count;i++)
        {
            int mode=i+1;string name=gpu?OriginalGpuName(mode):new[]{"办公","均衡","狂暴","自定义"}[i],icon=gpu?new[]{"gpu-integrated","gpu-hybrid","gpu-discrete"}[i]:new[]{"mode-office","mode-balanced","mode-performance","mode-custom"}[i];
            double iconSize = compact ? 28 : mode == 4 ? 34 : 24;
            bool compactIcon = compact || !gpu && mode != 4;
            var performanceIcon = gpu ? null : new PerformanceModeIcon(icon, iconSize, compactIcon, PanelMode == mode);
            FrameworkElement image = performanceIcon ?? (FrameworkElement)Paint.Icon(icon, iconSize, false, compactIcon);
            var label=Paint.Text(name,12);label.TextWrapping=TextWrapping.NoWrap;label.LineHeight=15;label.TextAlignment=TextAlignment.Center;
            var b=readOnly?new PaintButton(name,createDefaultContent:false):PB(name,()=>gpu?RunGpuRequest(mode):ApplySavedPerformance(mode),true,createDefaultContent:false);
            var content = Paint.Stack(4, image, label);
            b.Content=content;b.Padding=new(3);b.Height=b.MinHeight=compact?56:68;b.HorizontalAlignment=HorizontalAlignment.Stretch;
            bool lastGpuSelected = false;
            paintBindings.Add(() =>
            {
                bool selected = gpu ? gpuState?.Mode == mode : PanelMode == mode;
                b.Selected = selected;
                bool available = PanelHardwareActive && (gpu
                    ? gpuReadFresh && !PaintGpuBusy && gpuState?.Supports(mode) == true
                    : !PanelTuningBusy && (mode == 4 ? PanelSupportsCustom : PanelSupportsPerformance));
                b.IsEnabled = available;
                if (performanceIcon != null)
                {
                    performanceIcon.SetSelected(selected);
                }
                else if (selected != lastGpuSelected)
                {
                    content.Children[0] = Paint.Icon(icon, iconSize, selected, compactIcon);
                    lastGpuSelected = selected;
                }
            });
            FrameworkElement item=b;if(!gpu&&mode==4){var cell=new Grid();cell.Children.Add(b);var gear=new PaintButton("编辑自定义预设",readOnly?null:OpenTuning,createDefaultContent:false){Content=Paint.Utility("settings",16),Variant="icon",Width=24,Height=24,MinHeight=0,Padding=new(2),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};cell.Children.Add(gear);item=cell;}
            if(!gpu&&mode==4)
            {
                BindCapability(item,()=>PanelSupportsCustom);
                var column=grid.ColumnDefinitions[i];
                paintBindings.Add(()=>column.Width=ShowDeclaredControl(PanelSupportsCustom)?new GridLength(1,GridUnitType.Star):new GridLength(0));
            }
            Grid.SetColumn(item,i);grid.Children.Add(item);
        }
        return gpu?PaintGpuAvailability(grid):grid;
    }

    private sealed class PerformanceModeIcon : Grid
    {
        private readonly string asset;
        private readonly bool compact;
        private readonly double size;
        private Image visibleImage;
        private Image? pendingImage;
        private bool visibleSelected, requestedSelected;

        internal PerformanceModeIcon(string asset, double size, bool compact, bool selected)
        {
            this.asset = asset;
            this.size = size;
            this.compact = compact;
            Width = Height = size;
            IsHitTestVisible = false;
            visibleSelected = requestedSelected = selected;
            visibleImage = Paint.Icon(asset, size, selected, compact);
            Children.Add(visibleImage);
        }

        internal void SetSelected(bool selected)
        {
            if (requestedSelected == selected) return;
            requestedSelected = selected;
            if (pendingImage is { } previous)
            {
                ClearPending(previous);
                Children.Remove(previous);
            }
            if (visibleSelected == selected) return;

            // Keep the old icon in front until the new SVG has decoded in the live tree.
            var next = Paint.Icon(asset, size, selected, compact, loadOnAttach: true);
            pendingImage = next;
            next.ImageOpened += OnImageOpened;
            next.ImageFailed += OnImageFailed;
            Children.Insert(0, next);
        }

        private void OnImageOpened(object sender, RoutedEventArgs args)
        {
            if (pendingImage is not { } next || !ReferenceEquals(sender, next)) return;
            ClearPending(next);
            Children.Remove(visibleImage);
            visibleImage = next;
            visibleSelected = requestedSelected;
        }

        private void OnImageFailed(object sender, ExceptionRoutedEventArgs args)
        {
            if (pendingImage is not { } failed || !ReferenceEquals(sender, failed)) return;
            ClearPending(failed);
            Children.Remove(failed);
            DiagnosticLog.Event("ui.icon.failed", "error", new { id = asset, error = args.ErrorMessage }, throttle: true);
        }

        private void ClearPending(Image image)
        {
            image.ImageOpened -= OnImageOpened;
            image.ImageFailed -= OnImageFailed;
            pendingImage = null;
        }
    }

    private bool ShowDeclaredControl(bool supported)=>uiReviewMode||(residentClient!=null?residentSnapshot?.Device?.Declared!=true||supported:!hardware.CoreDelegated||!CoreMachine.Current.Ready||supported);
    private void BindCapability(FrameworkElement control,Func<bool> supported)
    {
        void Update()=>control.Visibility=ShowDeclaredControl(supported())?Visibility.Visible:Visibility.Collapsed;
        Update();
        paintBindings.Add(Update);
    }
    private bool? PaintQuickState(string key)=>key switch
    {
        "touchpad"=>extraSwitches.Touchpad,"fn"=>extraSwitches.FnLock,"win"=>quickState.WinLocked,
        "camera"=>quickState.Camera,"mic"=>quickState.Microphone,"wifi"=>extraSwitches.Wifi,"bluetooth"=>extraSwitches.Bluetooth,
        "osd"=>osdEnabled,"boost"=>PanelFanBoost,"awake"=>keepAwakeEnabled,_=>null
    };
    private bool PaintQuickEnabled(string key) => PaintQuickAvailable(key) && !PaintQuickBusy(key);
    private bool PaintQuickBusy(string key) => residentClient != null ? ResidentQuickBusy(key) : hardwareBusy && key is "fn" or "boost";
    private bool PaintQuickAvailable(string key)=>residentClient!=null?ResidentQuickAvailable(key):key switch{"camera"=>hardware.Active&&quickState.CameraAvailable&&quickState.Camera.HasValue&&upstream!=null,"mic"=>quickState.Microphone.HasValue&&upstream!=null,"win"=>upstream!=null&&quickState.WinLocked.HasValue,"touchpad"=>hardware.SupportsTouchpad&&hardware.Active&&upstream!=null&&PaintQuickState(key).HasValue,"fn"=>hardware.Active&&(hardware.SupportsDeclaredFnLock&&(!(CoreMachine.Current.FnLockViaEc||CoreMachine.Current.FnLockReadback)||extraSwitches.FnLock.HasValue)||!hardware.CoreDelegated&&upstream!=null&&PaintQuickState(key).HasValue),"boost"=>hardware.Active&&hardware.SupportsFanBoost,_=>true};
    private async Task PaintQuickAction(string key)
    {if(uiReviewMode)return;if(residentClient!=null){await RunResidentQuickActionAsync(key);return;}if(key=="fn"&&hardware.SupportsDeclaredFnLock&&!extraSwitches.FnLock.HasValue){if(!CoreMachine.Current.FnLockViaEc&&!CoreMachine.Current.FnLockReadback)await SelectDeclaredFnLock();return;}if(key is "touchpad" or "fn")await SetExtraSwitch(key);else if(key=="osd"){osdEnabled=!osdEnabled;await SavePreferences();if(!osdEnabled)nativeOsd?.Hide();else ConfirmedActionOsd("屏幕提示 · 已开启","osd");}else if(key=="boost")await ToggleFanBoost(PaintQuickState(key));else await RunQuickAction(key);}
    private async Task SelectDeclaredFnLock()
    {
        var commands = Paint.Inline(8,
            PB("开启 Fn 锁", () => RunDeviceCommand(() => hardwareRuntime.Switches.SetDeclaredFnLockAsync(true), "Fn 锁状态已回读确认。", "Core 已发送开启 Fn 锁命令。"), true),
            PB("关闭 Fn 锁", () => RunDeviceCommand(() => hardwareRuntime.Switches.SetDeclaredFnLockAsync(false), "Fn 锁状态已回读确认。", "Core 已发送关闭 Fn 锁命令。"), true));
        await PaintDialog("F1–F12 模式", Paint.Stack(8, Paint.Text("请选择命令。当前固件状态未知。"), commands));
    }

    private FrameworkElement PaintQuickRow(string key,bool compact=false,bool readOnly=false)
    {
        string name=key switch{"touchpad"=>"触摸板","win"=>"Win 键锁","fn"=>"F1–F12 模式","camera"=>"相机","mic"=>"麦克风","osd"=>"OSD 提示","boost"=>"一键强冷","awake"=>"防休眠",_=>"关闭屏幕"};
        string id=key switch{"win"=>"win-lock","fn"=>"fn-lock","boost"=>"fan","screen"=>"display",_=>key};string family=key switch{"win"=>"rose","fn"=>"violet","camera"=>"green","mic"=>"orange","boost"=>"cyan",_=>"blue"};
        var picture=new Grid{Width=compact?24:34,Height=compact?24:34};bool? last=null;
        void Icon(){bool on=PaintQuickState(key)==true||key=="screen";if(last==on&&picture.Children.Count>0)return;last=on;picture.Children.Clear();picture.Children.Add(Paint.Icon(id,compact?24:34,on));}
        string State(){if(key=="win")return requestedWinLocked.HasValue?"正在切换 Windows 软件锁":quickState.WinLocked switch{true=>"软件锁已开启 · 退出程序解除",false=>"软件锁已关闭",_=>"软件锁状态未知"};if(residentClient!=null?residentSnapshot?.Device?.Declared==true:hardware.CoreDelegated){if(key=="camera")return quickState.Camera.HasValue?(quickState.Camera.Value?"Windows 相机已启用":"Windows 相机已禁用"):"Windows设备状态未知";if(key=="touchpad")return extraSwitches.Touchpad.HasValue?(extraSwitches.Touchpad.Value?"Windows已开启 · 固件未回读":"Windows已关闭 · 固件未回读"):"触摸板状态暂不可用";if(key=="fn"&&!extraSwitches.FnLock.HasValue&&(residentClient!=null?residentSnapshot?.Device?.CanSendFnLock==true:hardware.SupportsDeclaredFnLock&&!CoreMachine.Current.FnLockViaEc&&!CoreMachine.Current.FnLockReadback))return "可发送 Fn 锁命令 · 固件状态未知";if(key=="fn"&&!extraSwitches.FnLock.HasValue)return "Fn 锁状态暂不可用";}bool? on=PaintQuickState(key);return key=="screen"?"仅息屏，电脑继续运行":!on.HasValue?"状态未知":key=="win"?(on.Value?"已锁定":"已解锁"):key=="fn"?(on.Value?"Fn Lock 已开启":"Fn Lock 已关闭"):key=="mic"?(on.Value?"收音已开启":"已静音"):key=="boost"?(on.Value?"强冷已开启":"自动温控"):key=="awake"?(on.Value?"保持唤醒":"随系统休眠"):(on.Value?"已开启":"已关闭");}
        var button=readOnly?new PaintButton(name,createDefaultContent:false):PB(name,()=>PaintQuickAction(key),true,createDefaultContent:false);
        string? tooltip = null;
        paintBindings.Add(() =>
        {
            button.IsEnabled = PaintQuickAvailable(key) && !PaintQuickBusy(key);
            string next = State();
            if (tooltip == next) return;
            ToolTipService.SetToolTip(button, next);
            if (!compact) AutomationProperties.SetHelpText(button, next);
            tooltip = next;
        });
        button.StretchContent();
        if(!readOnly){if(key=="boost")BindCapability(button,()=>PanelSupportsBoost);
        else if(key=="fn")BindCapability(button,()=>PanelSupportsFn);
        else if(key=="touchpad")BindCapability(button,()=>CanShowTouchpadControl);}
        if(compact){Icon();paintBindings.Add(Icon);var text=Paint.Text(key=="fn"?"F1–F12":name.Replace(" ",""),12);text.TextAlignment=TextAlignment.Center;text.TextWrapping=TextWrapping.NoWrap;picture.HorizontalAlignment=HorizontalAlignment.Center;button.Content=Paint.Stack(2,picture,text);button.Height=50;button.Padding=new(1,2,1,2);return button;}
        bool hasIndicator = key != "screen";
        var tail = hasIndicator ? new Border { Width = 40, Height = 24 } : Paint.Card(Paint.Text("息屏", 12), 0, 8, true);
        if (!hasIndicator) tail.Padding = new(8, 2, 8, 2);
        var row = Paint.Between(Paint.Inline(8, picture, Paint.Stack(2, Paint.Text(name), PT(State, 12, muted: true))), tail, 10);
        button.Content = row;
        button.Variant = "plain";
        button.Padding = new(4, 2, 4, 2);
        button.MinHeight = 46;

        // Keep the real command and its focus target in the tree. Only defer
        // the decorative children whose sizes are already reserved.
        int ownerRender = renderCount;
        button.EffectiveViewportChanged += ViewportChanged;
        button.GettingFocus += GettingFocus;
        return button;

        void ViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            var viewport = args.EffectiveViewport;
            if (viewport.Width > 0 && viewport.Height > 0 && viewport.Right > 0 && viewport.Bottom > 0 &&
                viewport.Left < button.ActualWidth && viewport.Top < button.ActualHeight)
                RealizeVisuals();
        }

        void GettingFocus(object sender, GettingFocusEventArgs args) => RealizeVisuals();

        void RealizeVisuals()
        {
            button.EffectiveViewportChanged -= ViewportChanged;
            button.GettingFocus -= GettingFocus;
            if (ownerRender != renderCount) return;
            Icon();
            paintBindings.Add(Icon);
            if (!hasIndicator) return;
            var indicator = PaintSwitch.CreateIndicator(family, () => PaintQuickState(key) == true, out var refresh);
            indicator.VerticalAlignment = tail.VerticalAlignment;
            Grid.SetColumn(indicator, 1);
            row.Children[1] = indicator;
            paintBindings.Add(refresh);
        }
    }
    private FrameworkElement PaintBatteryPicture(double width)
    {
        var canvas=new Canvas{Width=160,Height=50};var shell=new Border{Width=150,Height=48,BorderThickness=new(2),CornerRadius=new(6),BorderBrush=Paint.Brush(Paint.Muted(root))};Canvas.SetLeft(shell,1);Canvas.SetTop(shell,1);canvas.Children.Add(shell);var tip=new Border{Width=5,Height=16,CornerRadius=new(2),Background=Paint.Brush(Paint.Muted(root))};Canvas.SetLeft(tip,154);Canvas.SetTop(tip,17);canvas.Children.Add(tip);var fill=new Border{Height=38,CornerRadius=new(3),Background=Paint.Brush(Paint.Accent(root,"green"))};Canvas.SetLeft(fill,6);Canvas.SetTop(fill,6);canvas.Children.Add(fill);paintBindings.Add(()=>fill.Width=latest?.BatteryPercent is int p?140*Math.Clamp(p,5,100)/100d:0);return new Viewbox{Child=canvas,Width=width,Height=width*50/160,Stretch=Stretch.Uniform};
    }
    private static FrameworkElement PaintMinHeight(FrameworkElement item,double height){item.MinHeight=height;return item;}
    private static Grid PaintColumns(FrameworkElement[] children,double gap=6)
    {var grid=new Grid{ColumnSpacing=gap};for(int i=0;i<children.Length;i++){grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});children[i].HorizontalAlignment=HorizontalAlignment.Stretch;Grid.SetColumn(children[i],i);grid.Children.Add(children[i]);}return grid;}
    // Both the current painted page and the retained approved renderer use this adapter.
    private MobileDragController? overviewDrag;
    private void ReleaseOverviewDrag() { overviewDrag?.Dispose(); overviewDrag=null; }
    private void StartOverviewDrag()
    {
        if(overviewDraft==null||overviewBoard==null)return;
        var board=overviewBoard;
        IReadOnlyList<FrameworkElement> Items()=>board.Children.OfType<Grid>().OrderBy(item=>board.Order.IndexOf((string)item.Tag)).Cast<FrameworkElement>().ToArray();
        overviewDrag=new(new(board,Items,item=>board.Slot(item),order=>{board.Order.Clear();board.Order.AddRange(order.Select(item=>(string)item.Tag));board.InvalidateMeasure();board.UpdateLayout();},null),root,this,scroll,
            order=>{if(overviewDraft!=null){overviewDraft=order.Select(item=>(string)item.Tag).ToList();Render();}},_=>{},_=>RefreshPaintedState(),()=>overviewDraft!=null&&!dialogOpen);
        var scope=overviewDrag;board.Unloaded+=(_,_)=>scope.Dispose();
    }
}
