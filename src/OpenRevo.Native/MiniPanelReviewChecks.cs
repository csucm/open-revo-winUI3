using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OpenRevo.Core;
using System.Runtime.InteropServices;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd,uint message,nuint wParam,nint lParam);
    private async Task RunMiniCaptionChecks(Action<string,bool> check)
    {
        void Step(string step)=>System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"ui-caption-steps.txt"),DateTimeOffset.Now.ToString("O")+" "+step+Environment.NewLine);
        Step("caption checks started");
        check("Mini uses enabled native maximize and hides duplicate expand button",mini&&captionModeAttached&&panelToggle?.Visibility==Visibility.Collapsed&&AppWindow.Presenter is OverlappedPresenter{IsMaximizable:true,IsResizable:false});
        ShowCaptionExpandTip();
        Step("opened hover description");
        await Task.Delay(80);
        check("Native square button hover shows the expand-panel description",captionExpandTip?.IsOpen==true&&captionExpandTip.Content?.ToString()=="展开大面板");
        Step("hover checked");
        CloseCaptionExpandTip();
        // Exercise this window's actual native command route, without mouse or
        // keyboard injection. The same command is sent by the caption button.
        SendMessageW(captionWindow,WmSysCommand,ScMaximize,0);
        Step("sent mini maximize");
        for(int i=0;i<20&&mini;i++)await Task.Delay(20);
        check("Native mini maximize opens the large panel without maximizing its frame or adding a duplicate button",!mini&&AppWindow.Presenter is OverlappedPresenter{State:OverlappedPresenterState.Restored}&&panelToggle?.Visibility==Visibility.Collapsed);
        check("Mini expand tooltip closes when the large panel opens",captionExpandTip?.IsOpen!=true);
        SendMessageW(captionWindow,WmSysCommand,ScMaximize,0);
        Step("sent large maximize");
        await Task.Delay(80);
        check("Large panel keeps its native maximize behavior",!mini&&AppWindow.Presenter is OverlappedPresenter{State:OverlappedPresenterState.Maximized});
        if(AppWindow.Presenter is OverlappedPresenter presenter)presenter.Restore();
        await Task.Delay(80);
        if(!mini)ToggleMini();root.UpdateLayout();
        check("Returning to mini reuses the native expand command",mini&&panelToggle?.Visibility==Visibility.Collapsed&&captionModeAttached);
    }
    private void RunMiniRefreshChecks(Action<string,bool> check)
    {
        var saved=displayState;
        try
        {
            displayState=new(2560,1600,120,[240,60,144,120,240,0],"review-only");UpdateDisplayValues();
            var host=miniRefreshHost;var content=host?.Content;var more=refreshMoreButton;
            check("Mini refresh uses real minimum/maximum with only intermediate rates in More",refreshButtons.Keys.Order().SequenceEqual(new[]{60,240})&&refreshMenuItems.Keys.SequenceEqual(new[]{120,144})&&more?.Content?.ToString()=="120 Hz ▾");
            displayState=displayState with{Hertz=144};UpdateDisplayValues();
            check("Changing current refresh updates selection without replacing controls",ReferenceEquals(host,miniRefreshHost)&&ReferenceEquals(content,miniRefreshHost?.Content)&&ReferenceEquals(more,refreshMoreButton)&&!refreshMenuItems[120].IsChecked&&refreshMenuItems[144].IsChecked&&more?.Content?.ToString()=="144 Hz ▾");
            displayState=displayState with{Hertz=75,Rates=[75]};UpdateDisplayValues();
            check("A single supported refresh has no duplicate or empty More button",refreshButtons.Keys.SequenceEqual(new[]{75})&&refreshMoreButton==null);
            displayState=displayState with{Hertz=165,Rates=[60,165]};UpdateDisplayValues();
            check("Two supported refresh rates hide the redundant More button",refreshButtons.Keys.Order().SequenceEqual(new[]{60,165})&&refreshMoreButton==null&&refreshMenuItems.Count==0);
            displayState=null;UpdateDisplayValues();
            check("Unknown refresh capability does not invent selectable rates",refreshButtons.Count==0&&refreshMoreButton==null);
        }
        finally{displayState=saved;UpdateDisplayValues();}
    }
}
