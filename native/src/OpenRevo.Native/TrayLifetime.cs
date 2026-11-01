using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly bool trayReviewMode=Environment.GetCommandLineArgs().Contains("--ui-settings-checks");
    private const uint TrayCallback=0x8044;
    internal static readonly uint WakeMessage=RegisterWindowMessage("OpenRevo.Native.WakeWindow.v1");
    private static readonly uint TaskbarCreatedMessage=RegisterWindowMessage("TaskbarCreated");
    private bool trayAvailable,exitRequested,exitInProgress,trayHidden,sessionEnding;
    private nint trayIcon;
    private DateTime lastTrayWake;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct TrayData
    {
        public uint Size;public nint Window;public uint Id,Flags,Callback;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public nint BalloonIcon;
    }
    private TrayData TrayRecord()=>new(){Size=(uint)Marshal.SizeOf<TrayData>(),Window=captionWindow,Id=1,Flags=7,Callback=TrayCallback,Icon=trayIcon,Tip=trayReviewMode?"Open-Revo · 临时托盘检查":"Open-Revo · 点击唤醒控制面板",Info="",InfoTitle=""};
    private void InitializeTray()
    {
        if(uiReviewMode&&!trayReviewMode)return;
        trayIcon=LoadImage(0,Path.Combine(AppContext.BaseDirectory,"App.ico"),1,0,0,0x50);
        AddTrayIcon();
        Activated+=(_,e)=>
        {
            if(e.WindowActivationState==WindowActivationState.Deactivated&&mini&&systemPreferences.MiniAutoHide&&!dialogOpen&&!exitRequested&&!panelMinimizeQueued&&AppWindow.IsVisible)
                DispatcherQueue.TryEnqueue(()=>{if(mini&&!dialogOpen&&!panelMinimizeQueued)HideToTray();});
        };
    }
    private void AddTrayIcon()
    {
        if(uiReviewMode&&!trayReviewMode||trayIcon==0)return;var data=TrayRecord();trayAvailable=ShellNotify(0,ref data);
        if(trayAvailable){data.Version=4;ShellNotify(4,ref data);}else actions.Add("托盘图标创建失败，窗口保持可见。");
    }
    private void DisposeTray()
    {
        if(trayAvailable){var data=TrayRecord();ShellNotify(2,ref data);}trayAvailable=false;
        if(trayIcon!=0){DestroyIcon(trayIcon);trayIcon=0;}
    }
    private void RememberPanelShape()
    {
        if(uiReviewMode)return;
        systemPreferences=systemPreferences with{LastMini=mini};
        try{systemPreferences.Save();}catch(Exception error){actions.Add("窗口记忆："+error.Message);}
    }
    private bool HideToTray()
    {
        if(uiReviewMode&&!trayReviewMode||exitRequested||settingsDataBusy||dataStorageExitPending)return false;
        if(!trayAvailable)AddTrayIcon();if(!trayAvailable){_=ShowMessage("托盘不可用","图标创建失败，窗口保持可见；可在设置页退出程序。");return false;}
        RememberPanelShape();CloseCaptionExpandTip();trayHidden=true;AppWindow.Hide();UpdatePolling();return true;
    }
    internal void StartHiddenToTray()=>DispatcherQueue.TryEnqueue(()=>HideToTray());
    private void WakeFromTray(string? mode=null)
    {
        if(closed||exitRequested)return;
        string preferred=mode??systemPreferences.WakeMode;bool targetMini=preferred=="mini"||preferred=="remember"&&systemPreferences.LastMini;
        trayHidden=false;
        if(mini!=targetMini)ToggleMini();
        if(AppWindow.Presenter is OverlappedPresenter presenter&&presenter.State==OverlappedPresenterState.Minimized)presenter.Restore();
        AppWindow.Show();Activate();SetForegroundWindow(captionWindow);UpdatePolling();
    }
    private bool RuntimeBusy=>hardwareBusy||quickBusy||requestedWinLocked.HasValue||takeoverChanging||tuningApplying||displayBusy||brightnessApplying||gpuChanging||desktopServiceBusy||sleepOfficeBusy||settingsDataBusy||settingsPowerBusy||batteryGuardBusy;
    private async Task ExitFromTray()
    {
        if(exitInProgress||closed)return;exitInProgress=true;exitRequested=true;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        DiagnosticLog.Record("exit.requested",ExitDiagnosticState());
        try
        {
            var nextReport=TimeSpan.Zero;
            while(RuntimeBusy&&!closed)
            {
                if(watch.Elapsed>=nextReport){DiagnosticLog.Record("exit.waiting",new{elapsedMs=watch.ElapsedMilliseconds,state=ExitDiagnosticState()});nextReport=watch.Elapsed+TimeSpan.FromSeconds(5);}
                await Task.Delay(80);
            }
            if(closed)return;if(!dataStorageExitPending)RememberPanelShape();
            if(upstream!=null&&!desktopRuntimeClosed)await CloseDesktopRuntime();else Close();
        }
        catch(Exception error){DiagnosticLog.Failure("exit.failed",error,ExitDiagnosticState());}
        finally{if(!closed){exitRequested=false;exitInProgress=false;}DiagnosticLog.Record("exit.finished",new{elapsedMs=watch.ElapsedMilliseconds,closed});}
    }
    private object ExitDiagnosticState()=>new{hardwareBusy,quickBusy,winPending=requestedWinLocked.HasValue,takeoverChanging,tuningApplying,displayBusy,brightnessApplying,gpuChanging,desktopServiceBusy,sleepOfficeBusy,settingsDataBusy,settingsPowerBusy,batteryGuardBusy,desktopServicesReading,closingDesktopRuntime,desktopRuntimeClosed,exitRequested,exitInProgress,trayHidden,dialogOpen};
    private void TrayMenu()
    {
        nint menu=CreatePopupMenu();if(menu==0)return;
        try
        {
            AppendMenu(menu,0,1,"打开控制面板");AppendMenu(menu,0,2,"打开迷你面板");AppendMenu(menu,0,3,"打开完整面板");AppendMenu(menu,0,4,"设置与诊断");AppendMenu(menu,0x800,0,"");AppendMenu(menu,0,9,"退出 Open-Revo");
            GetCursorPos(out var point);SetForegroundWindow(captionWindow);uint command=TrackPopupMenu(menu,0x100|0x80,point.X,point.Y,0,captionWindow,0);
            DispatcherQueue.TryEnqueue(()=>{if(command==9){_=ExitFromTray();return;}if(command is 1 or 2 or 3 or 4){if(command==4){currentPage="settings";customTuningOpen=colorTuningOpen=false;}WakeFromTray(command==2?"mini":command is 3 or 4?"full":null);if(command==4)Render();}});
        }
        finally{DestroyMenu(menu);}
    }
    private bool HandleTrayWindowMessage(uint message,nuint wParam,nint lParam,out nint result)
    {
        result=0;
        if(message==WakeMessage){DispatcherQueue.TryEnqueue(()=>WakeFromTray());return true;}
        if(message==TaskbarCreatedMessage){trayAvailable=false;AddTrayIcon();if(!trayAvailable&&trayHidden)DispatcherQueue.TryEnqueue(()=>WakeFromTray());return false;}
        if(message==0x11){sessionEnding=true;exitRequested=true;result=1;return true;}
        if(message==0x16){if(wParam!=0)DisposeTray();else{sessionEnding=false;exitRequested=false;}return false;}
        if(message==TrayCallback)
        {
            uint action=(uint)((long)lParam&0xFFFF);
            if(action is 0x400 or 0x401 or 0x202 or 0x203)
            {if((DateTime.UtcNow-lastTrayWake).TotalMilliseconds>250){lastTrayWake=DateTime.UtcNow;DispatcherQueue.TryEnqueue(()=>WakeFromTray());}}
            else if(action is 0x205 or 0x7B)TrayMenu();return true;
        }
        if(message==0x112&&(wParam&0xFFF0)==0xF020&&(!uiReviewMode||trayReviewMode))return QueuePanelMinimize();
        return false;
    }
    internal static void WakeExistingInstance()=>PostMessage((nint)0xFFFF,WakeMessage,0,0);
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)]private static extern bool ShellNotify(uint message,ref TrayData data);
    [DllImport("user32.dll",EntryPoint="LoadImageW",CharSet=CharSet.Unicode)]private static extern nint LoadImage(nint instance,string name,uint type,int cx,int cy,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll",EntryPoint="RegisterWindowMessageW",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll",EntryPoint="PostMessageW")]private static extern bool PostMessage(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]private static extern nint CreatePopupMenu();
    [DllImport("user32.dll",EntryPoint="AppendMenuW",CharSet=CharSet.Unicode)]private static extern bool AppendMenu(nint menu,uint flags,nuint id,string text);
    [DllImport("user32.dll")]private static extern uint TrackPopupMenu(nint menu,uint flags,int x,int y,int reserved,nint window,nint rect);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out TrayPoint point);
    [DllImport("user32.dll")]private static extern bool DestroyMenu(nint menu);
    [StructLayout(LayoutKind.Sequential)]private struct TrayPoint{public int X,Y;}
}
