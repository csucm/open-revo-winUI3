using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private int[] monochromeLevels=[0];
    private int? monochromeRequested;
    private int? monochromeReadback;
    private DateTimeOffset monochromeReadAt;
    private bool monochromeBusy;
    private string monochromeNotice="当前档位未知 · 未回读确认";
    private int? CurrentMonochromeLevel => DateTimeOffset.Now-monochromeReadAt<TimeSpan.FromSeconds(8)
        ? monochromeReadback : null;
    private string MonochromeStatus=>CurrentMonochromeLevel is int level
        ? level==0?"当前背光已关闭":$"当前背光 {level} 档"
        : !hardware.Active?"请在设置中接管硬件后调节白色背光"
        : (!CoreMachine.Current.MonochromeControl||monochromeLevels.Length<=1)?"驱动未提供可调档位":monochromeNotice;
    private void UpdateMonochromeReadback(OverviewSwitches switches)
    {
        if(keyboard.Kind!="White")return;
        monochromeReadback=switches.MonochromeLevel is int level&&monochromeLevels.Contains(level)?level:null;
        monochromeReadAt=DateTimeOffset.Now;
        lightingReadFresh=monochromeReadback.HasValue;
        lightingProbeError=lightingReadFresh?null:"白色背光当前档位未能读取。";
    }
    private bool MonochromeEnabled=>keyboard.Kind=="White"&&hardware.CoreDelegated&&CoreMachine.Current.MonochromeControl&&hardware.Active&&monochromeLevels.Length>1&&!hardwareBusy&&!monochromeBusy;
    private async Task SendMonochrome(int level)
    {
        if(uiReviewMode||!MonochromeEnabled||!monochromeLevels.Contains(level))return;
        monochromeBusy=true;extraGeneration++;monochromeReadback=null;lightingReadFresh=false;RefreshPaintedState();await operationQueue.WaitAsync();
        using var trace=DiagnosticLog.Begin("lighting.monochrome.command");
        try
        {
            if(closed)return;
            var result=await Task.Run(()=>hardware.SendMonochromeLevel(level));
            monochromeRequested=level;monochromeNotice=$"已发送{(level==0?"关闭":level+"档")} · 未回读确认";
            if(upstream!=null)
            {
                extraSwitches=await Task.Run(upstream.ReadOverviewSwitches);
                UpdateMonochromeReadback(extraSwitches);
            }
            trace.Complete(new{command=result,requestedLevel=level,readback=monochromeReadback,confirmed=monochromeReadback==level});
        }
        catch(Exception error){monochromeNotice="命令未确认发送 · 实际档位未知";trace.Fail(error);if(!closed)await ShowMessage("白色背光",error.Message);}
        finally{operationQueue.Release();monochromeBusy=false;if(!closed)RefreshPaintedState();}
    }
    private FrameworkElement PaintMonochrome(bool compact)
    {
        var slider=new PaintSlider("键盘背光亮度，最左侧关闭",0,Math.Max(1,monochromeLevels.Length-1),0,1,async v=>{int i=(int)v;if(i>=0&&i<monochromeLevels.Length)await SendMonochrome(monochromeLevels[i]);});
        paintBindings.Add(()=>
        {
            slider.IsEnabled=MonochromeEnabled;
            if(CurrentMonochromeLevel is int level)slider.Value=Array.IndexOf(monochromeLevels,level);
            ToolTipService.SetToolTip(slider,MonochromeStatus);
        });
        var levelText=PT(()=>monochromeBusy?"正在更新":CurrentMonochromeLevel is int level?level==0?"关闭":$"{level} 档":"状态未知",12,muted:true);
        var header=Paint.Between(Paint.Inline(6,Paint.Icon("keyboard",24),Paint.Text("键盘背光",compact?12:16,true)),levelText);
        return Paint.Card(Paint.Stack(compact?4:12,header,slider),compact?7:22,compact?12:16);
    }
}


