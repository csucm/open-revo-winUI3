using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private sealed record ServiceChoice(string Id,string Name){public override string ToString()=>Name;}
    private ComboBox? iccProfileChoice;
    private ToggleSwitch? colorServiceToggle,iccServiceToggle;
    private TextBlock? colorServiceStatus,iccServiceStatus;
    private Button? iccImport,colorEditorButton;
    private readonly Dictionary<ColorParameter,NumberBox> colorNumbers=[];
    private readonly Dictionary<ColorParameter,Slider> colorSliders=[];
    private readonly Dictionary<string,Button> colorPresetButtons=[];
    private ColorPreset? colorDraft;
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
        if(colorDraft==null&&serviceColor!=null)colorDraft=serviceColor!.ActivePreset.Copy();
        var presets=serviceColor?.Presets??[];
        approvedColorPresetChoice=new ComboBox{Width=180,PlaceholderText="待读取",ItemsSource=presets.Select(p=>new ServiceChoice(p.Id!,p.Name!)).ToArray()};
        approvedColorPresetChoice.SelectionChanged+=async(_,_)=>{if(syncingColorServices||approvedColorPresetChoice.SelectedItem is not ServiceChoice choice)return;var selected=presets.First(p=>p.Id==choice.Id);colorDraft=selected.Copy();SyncColorDraft();await SubmitColor(enabledOverride:true);};
        var tools=new SuiteWrapPanel();tools.Children.Add(Ui.Button("导入",async(_,_)=>await ImportColorDraft()));tools.Children.Add(Ui.Button("导出",async(_,_)=>{try{if(colorDraft!=null)await SaveJsonFile("色彩方案.json",colorDraft.Export());}catch(Exception error){await ShowMessage("导出未完成",error.Message);}}));
        tools.Children.Add(Ui.Button("重置",async(_,_)=>{if(colorDraft==null||serviceColor==null)return;string? id=colorDraft.Id;var factory=serviceColor.FactoryPresets.FirstOrDefault(p=>p.Id==id);if(factory==null)return;colorDraft=factory.Copy();SyncColorDraft();await SubmitColor();}));
        colorServiceToggle=ServiceToggle("全局滤镜",serviceColor?.RuntimeEnabled==true,async(_,_)=>{if(!syncingColorServices)await SubmitColor();});
        page.Children.Add(DetailSection("色彩预设",SuiteVisuals.Stack(0,SuiteSettingRow("预设方案","选择后实时更新。",approvedColorPresetChoice),SuiteVisuals.Rule(tools),SuiteSettingRow("全局滤镜","影响所有显示器。",colorServiceToggle))));
        FrameworkElement Adjust(ColorParameter key,string title,double step,string unit="")
        {
            var (min,max)=ColorPreset.Range(key);
            double? known=colorDraft?.Get(key);
            var number=new NumberBox{Minimum=min,Maximum=max,SmallChange=step,LargeChange=step*10,Width=84,Value=known??double.NaN,FontSize=14,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Hidden};colorNumbers[key]=number;
            var slider=new Slider{Minimum=min,Maximum=max,StepFrequency=step,Value=known??min,MinHeight=32};colorSliders[key]=slider;bool linking=false;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(number,title+"数值");Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider,title);
            number.ValueChanged+=async(_,args)=>{if(syncingColorServices||double.IsNaN(args.NewValue))return;linking=true;slider.Value=args.NewValue;linking=false;if(colorDraft==null)return;colorDraft.Set(key,args.NewValue);await SubmitColor(true);};
            slider.ValueChanged+=(_,_)=>{if(!linking&&!syncingColorServices)number.Value=slider.Value;};
            var header=Ui.Row(Ui.Text(title),SettingsControls(number,Ui.Text(unit,12,muted:true)));
            return SuiteVisuals.Rule(SuiteVisuals.Stack(8,header,slider,Ui.Row(Ui.Text(min.ToString("0.##")+" "+unit,12,muted:true),Ui.Text(max.ToString("0.##")+" "+unit,12,muted:true))),14);
        }
        page.Children.Add(DetailGrid(DetailSection("色调与饱和度",SuiteVisuals.Stack(0,Adjust(ColorParameter.TemperatureKelvin,"色温",50,"K"),Adjust(ColorParameter.Saturation,"饱和度",.05))),DetailSection("亮度与对比",SuiteVisuals.Stack(0,Adjust(ColorParameter.Contrast,"对比度",.05),Adjust(ColorParameter.Gamma,"Gamma",.05)))));
        var gains=SuiteVisuals.Stack(0,Adjust(ColorParameter.RedGain,"红色增益",.02),Adjust(ColorParameter.GreenGain,"绿色增益",.02),Adjust(ColorParameter.BlueGain,"蓝色增益",.02));gains.Visibility=Visibility.Collapsed;
        var expand=Ui.Button("展开微调",(_,_)=>gains.Visibility=gains.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible);SuiteVisuals.Flat(expand,34);page.Children.Add(DetailSection("RGB 独立增益",SuiteVisuals.Stack(0,expand,gains)));
        colorServiceStatus=Ui.Text("状态待读取",12,muted:true);page.Children.Add(DetailSection("生效状态",SuiteSettingRow("滤镜回读","矩阵和 Gamma 分别确认。",colorServiceStatus)));SyncColorServices();SyncColorDraft();
    }
    private async Task ImportColorDraft()
    {
        try
        {
            var picker=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".json");var file=await picker.PickSingleFileAsync();if(file==null)return;
            if(new FileInfo(file.Path).Length>1024*1024)throw new IOException("色彩方案不能超过1MiB。");
            if(colorDraft==null)throw new IOException("请先读取色彩预设。");
            colorDraft.ImportParameters(await File.ReadAllTextAsync(file.Path));SyncColorDraft();await SubmitColor();
        }
        catch(Exception error){await ShowMessage("导入未完成",error.Message);}
    }
    private void SyncColorDraft()
    {
        if(colorDraft==null)return;syncingColorServices=true;
        try{if(approvedColorPresetChoice!=null)approvedColorPresetChoice.SelectedItem=approvedColorPresetChoice.Items.OfType<ServiceChoice>().FirstOrDefault(p=>p.Id==colorDraft.Id);foreach(var(key,box)in colorNumbers)if(colorDraft.Get(key) is double value){box.Value=value;if(colorSliders.TryGetValue(key,out var slider))slider.Value=value;}foreach(var(id,button)in colorPresetButtons)SetChoiceVisual(button,colorDraft.Id==id);}
        finally{syncingColorServices=false;}
    }
    private void SyncColorServices()
    {
        RefreshPaintedState();
        syncingColorServices=true;
        try
        {
            bool known=serviceColor?.RuntimeEnabled.HasValue==true,enabled=serviceColor?.RuntimeEnabled==true;
            string? engineError=serviceColor?.EngineError;
            if(known&&colorDraft==null)colorDraft=serviceColor!.ActivePreset.Copy();
            if(colorServiceToggle!=null){colorServiceToggle.IsEnabled=known&&!colorServicesBusy;colorServiceToggle.IsOn=enabled;}
            if(colorEditorButton!=null)colorEditorButton.IsEnabled=known&&enabled&&!colorServicesBusy;
            foreach(var box in colorNumbers.Values)box.IsEnabled=known;
            if(colorServiceStatus!=null)colorServiceStatus.Text=known?(colorPending?"正在更新色彩…":enabled?"全局滤镜已开启 · 影响所有显示器。"+(serviceColor?.GammaApplied==true?"":" Gamma 本次未确认，色彩矩阵已确认。"):serviceColor?.SavedEnabled==true?"上次保存为开启，本次尚未生效。":"色彩增强已关闭。") :"读取失败："+desktopServiceError;
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
        if(colorDraft==null||(residentClient==null?upstream==null:closed||residentDetaching))return;
        bool enabled=enabledOverride??colorServiceToggle?.IsOn??residentColorEdit?.Enabled??PaintColorEnabled==true;
        var preset=colorDraft.Copy();
        var intent=residentClient==null?null:ResidentColorIntent(preset,enabled);
        colorDebounce?.Cancel();var requestToken=colorDebounce=new CancellationTokenSource();colorPending=true;
        if(intent!=null)residentColorEdit=intent;
        try
        {
            if(debounce)await Task.Delay(350,requestToken.Token);
            while(colorServicesBusy)await Task.Delay(60,requestToken.Token);
            requestToken.Token.ThrowIfCancellationRequested();
            if(closed)return;
            if(intent!=null)
            {
                if(!residentDetaching)await SendResidentColorAsync(intent);
            }
            else await DesktopServiceAction("display-color",()=>hardwareRuntime.Desktop.ApplyColor(enabled,preset));
        }
        catch(OperationCanceledException){}
        finally{if(ReferenceEquals(requestToken,colorDebounce)){colorPending=false;if(!closed)SyncColorServices();}}
    }
    private async Task ApplyIcc(string profile)
    {
        if(residentClient!=null)
        {
            if(ResidentIccAvailable&&residentColor?.Icc is { } icc)
                await RunResidentActionAsync(PanelOperation.Desktop,desktop:new PanelApplyIcc(icc.Device,profile));
            return;
        }
        if(upstream==null)return;
        string? expectedDevice=displayState?.DeviceName;
        await DesktopServiceAction("icc-profile",()=>hardwareRuntime.Desktop.ApplyIcc(profile,expectedDevice));
    }
    private async Task ImportIcc()
    {
        if(residentClient==null?upstream==null||colorServicesBusy:PanelActionBusy(PanelOperation.Desktop,typeof(PanelApplyIcc))||residentSnapshot?.Ready!=true)return;
        try
        {
            var picker=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".icc");picker.FileTypeFilter.Add(".icm");
            var file=await picker.PickSingleFileAsync();if(file==null)return;
            await using var stream=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,useAsync:true);
            if(stream.Length is <1 or >PanelProtocol.MaximumIccBytes)throw new IOException("ICC 文件不能为空且不能超过 4 MiB。");
            byte[] bytes=new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes);
            string name=Path.GetFileName(file.Path);
            if(residentClient!=null)
            {
                var result=await SendResidentAsync(PanelOperation.Desktop,desktop:new PanelImportIcc(name,bytes.Length),icc:bytes);
                RequireResidentResult(result,PanelOutcome.ConfigurationSaved);
                if(!closed)await ShowMessage("ICC 已导入",result.Message??"请选择配置文件以应用。");
            }
            else await DesktopServiceAction("icc-import",()=>hardwareRuntime.Desktop.ImportIcc(name,bytes));
        }
        catch(Exception error){if(!closed)await ShowMessage("导入未完成",error.Message);}
    }
}
