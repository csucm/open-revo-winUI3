using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool DeveloperLayoutPreview=>debugSettingsVisible&&!debugSettingsHidden&&developerShape!="real"&&!mini;
    private bool DeveloperThinPreview=>DeveloperLayoutPreview&&developerShape=="thin_light";
    private FrameworkElement DeveloperLightingPreview()
    {
        var slider=new Slider{Minimum=0,Maximum=4,Value=3,IsEnabled=false};
        var content=Ui.Stack(Ui.Text(DeveloperThinPreview?"单色背光 · 0–4档":"逐键 RGB · 全功能布局",13,true),slider);
        if(!DeveloperThinPreview)
        {
            var keys=new DesktopTiles{PreferredColumns=12,MinimumItemWidth=30,ColumnSpacing=5,RowSpacing=5};
            foreach(string key in new[]{"Q","W","E","R","T","Y","U","I","O","P","A","S","D","F","G","H","J","K","L","Z","X","C","V","B"})keys.Children.Add(new Button{Content=key,IsEnabled=false,HorizontalAlignment=HorizontalAlignment.Stretch});content.Children.Add(keys);
        }
        return ReplicaCard("devices","键盘灯光（布局预览）",content);
    }
    private void EnforceDeveloperPreviewReadOnly()
    {
        if(!DeveloperLayoutPreview||currentPage is "settings" or "plugins")return;
        foreach(var control in DetailDescendants(page).OfType<Control>())control.IsEnabled=false;
    }
    private void ApplyDeveloperLayoutPreview()
    {
        if(!DeveloperLayoutPreview||currentPage is "settings" or "plugins")return;
        if(DeveloperThinPreview)
        {
            if(overviewMetrics is{} metrics&&metrics.Children.Count==4){metrics.Children.RemoveAt(3);metrics.Children.RemoveAt(1);}
            if(overviewModes is{} modes&&modes.Children.Count==4)modes.Children.RemoveAt(3);
            if(currentPage=="display")
            {
                var gpu=page.Children.OfType<Border>().FirstOrDefault(b=>DetailDescendants(b).OfType<TextBlock>().Any(t=>t.Text=="显卡模式"));if(gpu!=null)page.Children.Remove(gpu);
            }
        }
        var content=page.Children.ToArray();page.Children.Clear();
        foreach(var child in content)page.Children.Add(new ContentControl{Content=child,IsEnabled=false,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        page.Children.Insert(1,Ui.Notice("开发者布局预览",(DeveloperThinPreview?"轻薄本：隐藏独显/显存和自定义入口，使用单色背光布局。":"游戏本：完整模块与逐键RGB布局。")+" 读数仍来自真实本机，预览中的控制均禁用；到设置选择“真实本机”恢复。"));
    }
}
