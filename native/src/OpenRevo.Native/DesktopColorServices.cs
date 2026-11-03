using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private sealed record ServiceChoice(string Id,string Name){public override string ToString()=>Name;}
    private ComboBox? iccProfileChoice;
    private ToggleSwitch? colorServiceToggle,iccServiceToggle;
    private TextBlock? colorServiceStatus,iccServiceStatus;
    private Button? iccImport,colorEditorButton;
    private readonly Dictionary<string,NumberBox> colorNumbers=[];
    private readonly Dictionary<string,Slider> colorSliders=[];
    private readonly Dictionary<string,Button> colorPresetButtons=[];
    private JsonObject? colorDraft;
    private bool syncingColorServices,colorTuningOpen,colorPending;
    private CancellationTokenSource? colorDebounce;
    private string lastIccProfile="std_srgb";
    private void ResetColorServiceBindings()
    {
        approvedColorPresetChoice=null;iccProfileChoice=null;colorServiceToggle=null;iccServiceToggle=null;colorServiceStatus=null;iccServiceStatus=null;iccImport=null;colorEditorButton=null;colorNumbers.Clear();colorSliders.Clear();colorPresetButtons.Clear();
    }
    private void RenderDesktopColorServices()
    {
        colorServiceToggle=ServiceToggle("屏幕色彩增强",false,async(_,_)=>{if(!syncingColorServices)await SubmitColor();});
        colorEditorButton=Ui.Button("调色控制",(_,_)=>{colorTuningOpen=true;customTuningOpen=false;Render();});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};actions.Children.Add(colorEditorButton);actions.Children.Add(colorServiceToggle);
        colorServiceStatus=Ui.Text("读取中",11,muted:true);
        var colorRow=DesktopAlignedRow(ReplicaLeading("color",Ui.Text("屏幕色彩增强",14,true),"全局滤镜 · 影响所有显示器"),actions);
        var colorBody=Ui.Stack(colorRow,colorServiceStatus);colorBody.Spacing=5;var colorCard=Ui.Card(colorBody);colorCard.Padding=new Thickness(16);page.Children.Add(colorCard);
        iccProfileChoice=new ComboBox{MinWidth=180,MaxWidth=240};
        iccProfileChoice.SelectionChanged+=async(_,_)=>{if(!syncingColorServices&&iccProfileChoice.SelectedItem is ServiceChoice selected){if(selected.Id!="default_native")lastIccProfile=selected.Id;await ApplyIcc(selected.Id);}};
        iccImport=Ui.Button("导入 ICC",async(_,_)=>await ImportIcc());iccImport.Padding=new Thickness(10,6,10,6);
        var iccActions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};iccActions.Children.Add(iccProfileChoice);iccActions.Children.Add(iccImport);
        iccServiceStatus=Ui.Text("读取中",11,muted:true);
        var iccBody=Ui.Stack(DesktopAlignedRow(ReplicaLeading("display",Ui.Text("ICC 色彩校准",14,true),"配置文件 · 物理内屏"),iccActions),iccServiceStatus);iccBody.Spacing=5;
        var iccCard=Ui.Card(iccBody);iccCard.Padding=new Thickness(16);page.Children.Add(iccCard);SyncColorServices();
    }
    private ComboBox? approvedColorPresetChoice;
    private void RenderColorTuningPage()
    {
        if(colorDraft==null&&serviceColor.ValueKind==JsonValueKind.Object)colorDraft=JsonNode.Parse(serviceColor.GetProperty("active_preset").GetRawText())!.AsObject();
        var presets=serviceColor.ValueKind==JsonValueKind.Object?serviceColor.GetProperty("presets").EnumerateArray().Select(p=>p.Clone()).ToArray():[];
        approvedColorPresetChoice=new ComboBox{Width=180,PlaceholderText="待读取",ItemsSource=presets.Select(p=>new ServiceChoice(p.GetProperty("id").GetString()!,p.GetProperty("name").GetString()!)).ToArray()};
        approvedColorPresetChoice.SelectionChanged+=async(_,_)=>{if(syncingColorServices||approvedColorPresetChoice.SelectedItem is not ServiceChoice choice)return;var selected=presets.First(p=>p.GetProperty("id").GetString()==choice.Id);colorDraft=JsonNode.Parse(selected.GetRawText())!.AsObject();SyncColorDraft();await SubmitColor(enabledOverride:true);};
        var tools=new SuiteWrapPanel();tools.Children.Add(Ui.Button("导入",async(_,_)=>await ImportColorDraft()));tools.Children.Add(Ui.Button("导出",async(_,_)=>{try{if(colorDraft!=null)await SaveJsonFile("色彩方案.json",colorDraft.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));}catch(Exception error){await ShowMessage("导出未完成",error.Message);}}));
        tools.Children.Add(Ui.Button("重置",async(_,_)=>{if(colorDraft==null||serviceColor.ValueKind!=JsonValueKind.Object)return;string? id=colorDraft["id"]?.GetValue<string>();var factory=serviceColor.GetProperty("factory_presets").EnumerateArray().FirstOrDefault(p=>p.GetProperty("id").GetString()==id);if(factory.ValueKind!=JsonValueKind.Object)return;colorDraft=JsonNode.Parse(factory.GetRawText())!.AsObject();SyncColorDraft();await SubmitColor();}));
        colorServiceToggle=ServiceToggle("全局滤镜",JsonBool(serviceColor,"runtime_enabled"),async(_,_)=>{if(!syncingColorServices)await SubmitColor();});
        page.Children.Add(DetailSection("色彩预设",SuiteVisuals.Stack(0,SuiteSettingRow("预设方案","选择后实时更新。",approvedColorPresetChoice),SuiteVisuals.Rule(tools),SuiteSettingRow("全局滤镜","影响所有显示器。",colorServiceToggle))));
        FrameworkElement Adjust(string key,string title,double min,double max,double step,string unit="")
        {
            double? known=colorDraft?[key]?.GetValue<double>();
            var number=new NumberBox{Minimum=min,Maximum=max,SmallChange=step,LargeChange=step*10,Width=84,Value=known??double.NaN,FontSize=14,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Hidden};colorNumbers[key]=number;
            var slider=new Slider{Minimum=min,Maximum=max,StepFrequency=step,Value=known??min,MinHeight=32};colorSliders[key]=slider;bool linking=false;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(number,title+"数值");Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider,title);
            number.ValueChanged+=async(_,args)=>{if(syncingColorServices||double.IsNaN(args.NewValue))return;linking=true;slider.Value=args.NewValue;linking=false;if(colorDraft==null)return;if(key=="color_temp_k")colorDraft[key]=(int)args.NewValue;else colorDraft[key]=args.NewValue;await SubmitColor(true);};
            slider.ValueChanged+=(_,_)=>{if(!linking&&!syncingColorServices)number.Value=slider.Value;};
            var header=Ui.Row(Ui.Text(title),SettingsControls(number,Ui.Text(unit,12,muted:true)));
            return SuiteVisuals.Rule(SuiteVisuals.Stack(8,header,slider,Ui.Row(Ui.Text(min.ToString("0.##")+" "+unit,12,muted:true),Ui.Text(max.ToString("0.##")+" "+unit,12,muted:true))),14);
        }
        page.Children.Add(DetailGrid(DetailSection("色调与饱和度",SuiteVisuals.Stack(0,Adjust("color_temp_k","色温",4000,9000,50,"K"),Adjust("saturation","饱和度",0,2,.05))),DetailSection("亮度与对比",SuiteVisuals.Stack(0,Adjust("contrast","对比度",.5,1.5,.05),Adjust("gamma","Gamma",.7,1.3,.05)))));
        var gains=SuiteVisuals.Stack(0,Adjust("gain_r","红色增益",.5,1.5,.02),Adjust("gain_g","绿色增益",.5,1.5,.02),Adjust("gain_b","蓝色增益",.5,1.5,.02));gains.Visibility=Visibility.Collapsed;
        var expand=Ui.Button("展开微调",(_,_)=>gains.Visibility=gains.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible);SuiteVisuals.Flat(expand,34);page.Children.Add(DetailSection("RGB 独立增益",SuiteVisuals.Stack(0,expand,gains)));
        colorServiceStatus=Ui.Text("状态待读取",12,muted:true);page.Children.Add(DetailSection("生效状态",SuiteSettingRow("滤镜回读","矩阵和 Gamma 分别确认。",colorServiceStatus)));SyncColorServices();SyncColorDraft();
    }
    private async Task ImportColorDraft()
    {
        try
        {
            var picker=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".json");var file=await picker.PickSingleFileAsync();if(file==null)return;
            if(new FileInfo(file.Path).Length>1024*1024)throw new IOException("色彩方案不能超过1MiB。");
            var imported=JsonNode.Parse(await File.ReadAllTextAsync(file.Path))?.AsObject()??throw new IOException("色彩方案为空。");
            foreach(var(key,box)in colorNumbers){double value=imported[key]?.GetValue<double>()??throw new IOException("色彩参数不完整。");if(!double.IsFinite(value)||value<box.Minimum||value>box.Maximum)throw new IOException("色彩参数超出范围。");}
            if(colorDraft==null)throw new IOException("请先读取色彩预设。");
            foreach(var key in colorNumbers.Keys)colorDraft[key]=imported[key]!.DeepClone();SyncColorDraft();await SubmitColor();
        }
        catch(Exception error){await ShowMessage("导入未完成",error.Message);}
    }
    private void SyncColorDraft()
    {
        if(colorDraft==null)return;syncingColorServices=true;
        try{if(approvedColorPresetChoice!=null)approvedColorPresetChoice.SelectedItem=approvedColorPresetChoice.Items.OfType<ServiceChoice>().FirstOrDefault(p=>p.Id==colorDraft["id"]?.GetValue<string>());foreach(var(key,box)in colorNumbers)if(colorDraft[key]!=null){box.Value=colorDraft[key]!.GetValue<double>();if(colorSliders.TryGetValue(key,out var slider))slider.Value=box.Value;}foreach(var(id,button)in colorPresetButtons)SetChoiceVisual(button,colorDraft["id"]?.GetValue<string>()==id);}
        finally{syncingColorServices=false;}
    }
    private void SyncColorServices()
    {
        RefreshPaintedState();
        syncingColorServices=true;
        try
        {
            bool known=serviceColor.ValueKind==JsonValueKind.Object,enabled=JsonBool(serviceColor,"runtime_enabled");
            string? engineError=known&&serviceColor.TryGetProperty("engine_error",out var errorValue)&&errorValue.ValueKind==JsonValueKind.String?errorValue.GetString():null;
            if(known&&colorDraft==null)colorDraft=JsonNode.Parse(serviceColor.GetProperty("active_preset").GetRawText())!.AsObject();
            if(colorServiceToggle!=null){colorServiceToggle.IsEnabled=known&&!colorServicesBusy;colorServiceToggle.IsOn=enabled;}
            if(colorEditorButton!=null)colorEditorButton.IsEnabled=known&&enabled&&!colorServicesBusy;
            foreach(var box in colorNumbers.Values)box.IsEnabled=known;
            if(colorServiceStatus!=null)colorServiceStatus.Text=known?(colorPending?"正在更新色彩…":enabled?"全局滤镜已开启 · 影响所有显示器。"+(JsonBool(serviceColor,"gamma_applied")?"":" Gamma 本次未确认，色彩矩阵已确认。"):JsonBool(serviceColor,"enabled")?"上次保存为开启，本次尚未生效。":"色彩增强已关闭。") :"读取失败："+desktopServiceError;
            if(colorServiceStatus!=null&&!colorPending&&!string.IsNullOrEmpty(engineError))colorServiceStatus.Text="色彩接口未完成："+engineError;
            bool panel=displayState?.Active==true,iccKnown=serviceIcc.ValueKind==JsonValueKind.Object,hdr=JsonBool(serviceIcc,"hdr_active");
            bool samePanel=iccKnown&&serviceIcc.GetProperty("current_device_name").GetString()==displayState?.DeviceName;
            bool canApply=panel&&samePanel&&!colorServicesBusy;
            if(iccServiceToggle!=null){iccServiceToggle.IsOn=JsonBool(serviceIcc,"enabled");iccServiceToggle.IsEnabled=canApply;}
            if(iccProfileChoice!=null)
            {
                iccProfileChoice.IsEnabled=canApply;
                if(iccKnown)
                {
                    var choices=serviceIcc.GetProperty("profiles").EnumerateArray().Select(p=>new ServiceChoice(p.GetProperty("id").GetString()!,p.GetProperty("name").GetString()!)).ToArray();
                    iccProfileChoice.ItemsSource=choices;string? id=serviceIcc.GetProperty("active_profile_id").GetString();iccProfileChoice.SelectedItem=choices.FirstOrDefault(p=>p.Id==id);if(id is not(null or "default_native"))lastIccProfile=id;
                }
            }
            if(iccImport!=null)iccImport.IsEnabled=upstream!=null&&!colorServicesBusy;
            if(iccServiceStatus!=null)iccServiceStatus.Text=!panel?"物理内屏未激活，暂不能应用配置。":iccKnown?"当前："+serviceIcc.GetProperty("active_profile_name").GetString()+(hdr?" · HDR 下 SDR 色彩管理可能被系统接管。":""):"读取中";
        }
        finally{syncingColorServices=false;}
    }
    private async Task SubmitColor(bool debounce=false,bool? enabledOverride=null)
    {
        if(upstream==null||colorDraft==null)return;
        colorDebounce?.Cancel();var requestToken=colorDebounce=new CancellationTokenSource();colorPending=true;
        var request=new{enabled=enabledOverride??colorServiceToggle?.IsOn??JsonBool(serviceColor,"runtime_enabled"),preset=colorDraft.DeepClone()};
        try
        {
            if(debounce)await Task.Delay(350,requestToken.Token);
            while(colorServicesBusy)await Task.Delay(60,requestToken.Token);
            requestToken.Token.ThrowIfCancellationRequested();if(!closed)await DesktopServiceAction("display-color",()=>upstream.ExecuteFeature(7,request));
        }
        catch(OperationCanceledException){}
        finally{if(ReferenceEquals(requestToken,colorDebounce)){colorPending=false;if(!closed)SyncColorServices();}}
    }
    private async Task ApplyIcc(string profile)
    {
        if(upstream==null)return;
        await DesktopServiceAction("icc-profile",()=>
        {
            var panel=OpenRevo.Core.DisplayControl.Read();
            if(!panel.Active||panel.DeviceName!=displayState?.DeviceName)throw new IOException("物理内屏未激活或显示拓扑已改变。");
            upstream.ExecuteFeature(4,new{device=panel.DeviceName,profile});
        });
    }
    private async Task ImportIcc()
    {
        if(upstream==null||colorServicesBusy)return;
        try
        {
            var picker=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".icc");picker.FileTypeFilter.Add(".icm");
            var file=await picker.PickSingleFileAsync();if(file==null)return;
            await DesktopServiceAction("icc-import",()=>
            {
                var info=new FileInfo(file.Path);if(info.Length>4*1024*1024)throw new IOException("ICC 文件不能超过4MiB。");
                byte[] bytes=File.ReadAllBytes(file.Path);
                string stem=string.Concat(Path.GetFileNameWithoutExtension(file.Path).Where(c=>char.IsAsciiLetterOrDigit(c)||c=='_')).Trim('_');
                if(stem.Length>40)stem=stem[..40];
                string name="openrevo_"+stem+"_"+Convert.ToHexString(SHA256.HashData(bytes))[..12]+Path.GetExtension(file.Path).ToLowerInvariant();
                upstream.ExecuteFeature(5,new{name,bytes=bytes.Select(b=>(int)b).ToArray()});
            });
        }
        catch(Exception error){if(!closed)await ShowMessage("导入未完成",error.Message);}
    }
}
