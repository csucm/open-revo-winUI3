using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private const nuint CaptionModeSubclassId=0x4F52564F;
    private const uint WmSysCommand=0x0112,ScMaximize=0xF030;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint CaptionWindowProc(nint hwnd,uint message,nuint wParam,nint lParam,nuint id,nuint data);
    private CaptionWindowProc? captionWindowProc;
    private bool captionModeAttached,miniExpandQueued,panelMinimizeQueued;
    private nint captionWindow;
    private bool captionMaxHovered;
    private ToolTip? captionExpandTip;
    private Border? captionExpandAnchor;
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptionMouseTracking{public uint Size,Flags;public nint Window;public uint HoverTime;}
    [DllImport("user32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref CaptionMouseTracking tracking);
    [DllImport("comctl32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd,CaptionWindowProc callback,nuint id,nuint data);
    [DllImport("comctl32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd,CaptionWindowProc callback,nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd,uint message,nuint wParam,nint lParam);
    private void AttachCaptionModeCommand()
    {
        captionWindow=WinRT.Interop.WindowNative.GetWindowHandle(this);
        captionWindowProc=OnCaptionWindowMessage;
        captionModeAttached=SetWindowSubclass(captionWindow,captionWindowProc,CaptionModeSubclassId,0);
        AttachDesktopPowerEvents();
        if(!captionModeAttached)actions.Add("标题栏展开命令未连接，保留原展开入口。");
        captionExpandAnchor=new Border{Width=46,Height=32,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false,Margin=new Thickness(0,0,46,0)};
        Grid.SetRow(captionExpandAnchor,0);root.Children.Add(captionExpandAnchor);
        captionExpandTip=new ToolTip{Content="展开大面板",Placement=PlacementMode.Bottom};
        ToolTipService.SetToolTip(captionExpandAnchor,captionExpandTip);
    }
    private void DetachCaptionModeCommand()
    {
        DetachDesktopPowerEvents();
        if(captionModeAttached&&captionWindowProc!=null)RemoveWindowSubclass(captionWindow,captionWindowProc,CaptionModeSubclassId);
        captionModeAttached=false;
    }
    private void ResetCaptionHover(){CloseCaptionExpandTip();CaptionHoverState.Clear(captionWindow);}
    private void CloseCaptionExpandTip()
    {
        captionMaxHovered=false;
        if(captionExpandTip!=null)captionExpandTip.IsOpen=false;
    }
    private void ShowCaptionExpandTip()
    {
        if(closed||!mini||root.XamlRoot==null)return;
        double scale=root.XamlRoot.RasterizationScale;
        double width=AppWindow.TitleBar.RightInset/scale/3;
        if(width<=0)width=46;
        if(captionExpandTip==null||captionExpandAnchor==null)return;
        captionExpandAnchor.Width=width;captionExpandAnchor.Height=root.RowDefinitions[0].ActualHeight;
        captionExpandAnchor.Margin=new Thickness(0,0,width,0);
        captionExpandTip.XamlRoot=root.XamlRoot;
        captionExpandTip.RequestedTheme=root.ActualTheme;
        captionExpandTip.PlacementTarget=captionExpandAnchor;
        captionExpandTip.IsOpen=true;
    }
    private void QueueCaptionAction(Action action)
    {
        DispatcherQueue.TryEnqueue(()=>
        {
            try{action();}
            catch(Exception error)
            {
                actions.Add("标题栏："+error.Message);
                try{System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"caption-error.txt"),error.ToString());}catch{}
            }
        });
    }
    private bool QueuePanelMinimize()
    {
        if(closed||exitRequested||sessionEnding)return false;
        if(panelShapeChanging)
        {
            if(panelGoalMini)HideToTray();else SetPanelGoal(true);
            return true;
        }
        if(panelMinimizeQueued)return true;
        bool fromMini=mini;panelMinimizeQueued=true;
        if(DispatcherQueue.TryEnqueue(async ()=>
        {
            try
            {
                if(closed||exitRequested||sessionEnding)return;
                // ToggleMini restores full-panel bounds under the composition guard.
                if(!fromMini)
                {
                    if(!mini)ToggleMini();
                    await panelShapeCompletion;
                    if(closed||exitRequested||sessionEnding||trayHidden)return;
                    trayHidden=false;ResetCaptionHover();AppWindow.Show();Activate();SetForegroundWindow(captionWindow);
                    RememberPanelShape();UpdatePolling();
                }
                else if(mini)HideToTray();
            }
            catch(Exception error)
            {
                OpenRevo.Core.DiagnosticLog.Failure("window.minimize",error,new{fromMini,mini});
                actions.Add("最小化面板："+error.Message);
            }
            finally{panelMinimizeQueued=false;}
        }))return true;
        panelMinimizeQueued=false;return false;
    }
    private nint OnCaptionWindowMessage(nint hwnd,uint message,nuint wParam,nint lParam,nuint id,nuint data)
    {
        if(HandleTrayWindowMessage(message,wParam,lParam,out var trayResult))return trayResult;
        if (message == 0x007E || message == 0x0219 && wParam == 0x0007)
        {
            OpenRevo.Core.DisplayTopology.InvalidateGpuIdentity(); // WM_DISPLAYCHANGE / DBT_DEVNODES_CHANGED
            QueueDisplayTopologyRead();
        }
        if(message==0x0024)
        {
            // WinUI may replace the tracking limits in its own handler.
            // Apply our desktop floor after the remaining subclass chain.
            DefSubclassProc(hwnd,message,wParam,lParam);
            ApplyDesktopMinMax(lParam);
            return 0;
        }
        HandleDesktopPowerMessage(message,wParam,lParam);
        if(mini&&!closed)
        {
            if(message==0x00A0&&wParam==9) // WM_NCMOUSEMOVE, HTMAXBUTTON
            {
                if(!captionMaxHovered)
                {
                    captionMaxHovered=true;
                    var track=new CaptionMouseTracking{Size=(uint)Marshal.SizeOf<CaptionMouseTracking>(),Flags=0x13,Window=hwnd,HoverTime=400};
                    TrackMouseEvent(ref track);
                }
            }
            else if(message==0x02A0&&wParam==9) // Own this button's hover text only.
            {
                QueueCaptionAction(()=>{if(captionMaxHovered)ShowCaptionExpandTip();});
                return 0;
            }
            else if(message is 0x00A0 or 0x02A2 or 0x0200 or 0x00A1 or 0x0008)
            {
                captionMaxHovered=false;
                QueueCaptionAction(()=>{if(!captionMaxHovered)CloseCaptionExpandTip();});
            }
        }
        // Intercept only the mini window's native maximize command, before any
        // maximized frame is displayed. Full-panel system behavior stays native.
        if(message==WmSysCommand&&(wParam&0xFFF0)==ScMaximize&&mini&&!closed)
        {
            if(!miniExpandQueued)
            {
                miniExpandQueued=true;
                if(!DispatcherQueue.TryEnqueue(()=>
                {
                    miniExpandQueued=false;
                    if(!closed&&mini)
                    {
                        try{ToggleMini();}
                        catch(Exception error){actions.Add("展开面板："+error.Message);}
                    }
                }))miniExpandQueued=false;
            }
            return 0;
        }
        if(message==0x0082)DetachCaptionModeCommand(); // WM_NCDESTROY
        return DefSubclassProc(hwnd,message,wParam,lParam);
    }
}

