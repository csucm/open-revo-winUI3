using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private const string CommunityRepositoryUrl="https://github.com/"+OpenRevo.Core.ReleaseUpdater.Repository;

    private async Task OpenCommunityRepository()
    {
        await OpenCommunityPage(CommunityRepositoryUrl,"未能打开仓库");
    }

    private async Task SubmitBugReport()
    {
        string body=$"""
            ### 问题描述
            请描述发生的问题，并填写机型。

            ### 复现步骤
            1.
            2.

            ### 预期结果与实际结果

            ### 环境
            - Open-Revo 版本：{BuildInfo.VersionLabel}
            - 机型：
            - 是否连接电源 / 原装适配器：

            ### 日志与截图
            请先在软件的“设置与诊断 → 诊断与日志”导出诊断报告，再将文件拖入此输入框，等待上传完成。
            支持上传 JSON、LOG、TXT 和截图。提交内容会公开，请移除不愿公开的个人信息。
            """;
        string url=CommunityRepositoryUrl+"/issues/new?title="+Uri.EscapeDataString("[Bug] ")+"&body="+Uri.EscapeDataString(body);
        await OpenCommunityPage(url,"未能打开 Bug 提交页面");
    }

    private async Task OpenCommunityPage(string url,string errorTitle)
    {
        if(uiReviewMode||closed)return;
        try
        {
            if(!await Windows.System.Launcher.LaunchUriAsync(new Uri(url)))
                await ShowMessage(errorTitle,"请用浏览器打开："+CommunityRepositoryUrl+"/issues");
        }
        catch(Exception){await ShowMessage(errorTitle,"请用浏览器打开："+CommunityRepositoryUrl+"/issues");}
    }

    private FrameworkElement CommunityRepositoryInfo(bool painted=false)
    {
        var link=new HyperlinkButton{Content=CommunityRepositoryUrl,FontSize=12,Padding=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Left};
        link.Click+=async(_,_)=>await OpenCommunityRepository();
        var heading=new Grid{ColumnSpacing=8};
        heading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var version=painted?Paint.Text("Open-Revo · "+BuildInfo.VersionLabel,12,muted:true):Ui.Text("Open-Revo · "+BuildInfo.VersionLabel,12,muted:true);
        version.VerticalAlignment=VerticalAlignment.Center;heading.Children.Add(version);
        var sponsor=CommunityImageButton("赞助支持","sponsor-wechat.png",painted,ShowSponsorImages);Grid.SetColumn(sponsor,1);heading.Children.Add(sponsor);
        var group=CommunityImageButton("交流群","group.jpg",painted);Grid.SetColumn(group,2);heading.Children.Add(group);
        return painted?Paint.Stack(4,heading,link):Ui.Stack(heading,link);
    }

    private FrameworkElement CommunityImageButton(string title,string file,bool painted,Func<Task>? show=null)
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Assets","Community",file);
        bool available=File.Exists(path);
        show??=()=>ShowCommunityImage(title,path);
        FrameworkElement button;
        if(painted)
            button=new PaintButton(title,async()=>await show()){Content=Paint.Text(title,12),MinHeight=30,Padding=new Thickness(10,4,10,4),IsEnabled=available};
        else
        {
            var native=Ui.Button(title,async(_,_)=>await show());
            native.FontSize=12;native.MinHeight=30;native.Padding=new Thickness(10,4,10,4);native.IsEnabled=available;button=native;
        }
        ToolTipService.SetToolTip(button,available?title:title+"图片待补充");
        return button;
    }

    private async Task ShowCommunityImage(string title,string path)
    {
        if(closed||!File.Exists(path))return;
        try
        {
            var picture=CommunityPicture(path);
            if(UsePaintedUi)await PaintDialog(title,picture);
            else await new ContentDialog{XamlRoot=root.XamlRoot,Title=title,Content=picture,CloseButtonText="关闭"}.ShowAsync();
        }
        catch(Exception error){await ShowMessage("图片未能打开",error.Message);}
    }

    private Image CommunityPicture(string path,int extraHeader=0)=>new()
    {
        Source=new BitmapImage(new Uri(path)),Stretch=Stretch.Uniform,
        Width=Math.Min(400,Math.Max(120,root.XamlRoot.Size.Width-100)),
        Height=Math.Min(420,Math.Max(80,root.XamlRoot.Size.Height-220-extraHeader))
    };

    private async Task ShowSponsorImages()
    {
        if(closed)return;
        string folder=Path.Combine(AppContext.BaseDirectory,"Assets","Community");
        string wechatPath=Path.Combine(folder,"sponsor-wechat.png"),alipayPath=Path.Combine(folder,"sponsor-alipay.jpg");
        if(!File.Exists(wechatPath)&&!File.Exists(alipayPath))return;
        try
        {
            var picture=CommunityPicture(File.Exists(wechatPath)?wechatPath:alipayPath,44);
            var wechat=new PaintButton("微信"){IsEnabled=File.Exists(wechatPath),MinHeight=30};
            var alipay=new PaintButton("支付宝"){IsEnabled=File.Exists(alipayPath),MinHeight=30};
            void Choose(bool useWechat)
            {
                picture.Source=new BitmapImage(new Uri(useWechat?wechatPath:alipayPath));
                wechat.Selected=useWechat;alipay.Selected=!useWechat;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picture,useWechat?"微信赞助码":"支付宝赞助码");
            }
            wechat.Click+=()=>Choose(true);alipay.Click+=()=>Choose(false);Choose(File.Exists(wechatPath));
            var tabs=Paint.Inline(8,wechat,alipay);tabs.HorizontalAlignment=HorizontalAlignment.Center;
            var content=Paint.Stack(10,tabs,picture);
            if(UsePaintedUi)await PaintDialog("赞助支持",content);
            else await new ContentDialog{XamlRoot=root.XamlRoot,Title="赞助支持",Content=content,CloseButtonText="关闭"}.ShowAsync();
        }
        catch(Exception error){await ShowMessage("赞助图片未能打开",error.Message);}
    }
}
