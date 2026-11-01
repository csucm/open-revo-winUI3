using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunDesktopReviewChecks(Action<string,bool> check)
    {
        bool capture=Environment.GetCommandLineArgs().Contains("--ui-layout-checks");
        desktopAlignmentSamples.Clear();
        functionOwnershipSamples.Clear();
        var theme=root.RequestedTheme;
        var snapshot=hardware.TuningState;int? editedPl1=tuningDraft?.Pl1;
        check("Performance is merged into Display with one native navigation entry",navigation.MenuItems.OfType<NavigationViewItem>().Select(i=>i.Tag?.ToString()).SequenceEqual(new[]{"overview","display","power","devices","plugins","settings"})&&navigation.MenuItems.OfType<NavigationViewItem>().Single(i=>Equals(i.Tag,"display")).Content?.ToString()=="显卡与屏幕"&&navigation.MenuItems.OfType<NavigationViewItem>().Single(i=>Equals(i.Tag,"display")).Icon is ImageIcon&&navigation.MenuItems.OfType<NavigationViewItem>().Single(i=>Equals(i.Tag,"settings")).Icon is SymbolIcon{Symbol:Symbol.Setting});
        RefreshTuningPanel();root.UpdateLayout();
        check("Combined tuning retains CPU edits, both fan curves and hardware state",tuningNumbers.Count>=37&&tuningDraft?.Pl1==editedPl1&&ReferenceEquals(snapshot,hardware.TuningState));
        BackFromTuning();root.UpdateLayout();
        check("Tuning returns to the merged Display page without duplicated factory modes",!customTuningOpen&&currentPage=="display"&&performanceButtons.Count==0&&gpuButtons.Count==3&&tuningPanel==null&&tuningNumbers.Count==0&&!detailSectionTitles.Contains("自定义性能调校"));
        OpenTuning();root.UpdateLayout();
        check("Reopening custom subpage keeps the unsaved draft",customTuningOpen&&tuningDraft?.Pl1==editedPl1&&tuningPanel!=null&&tuningNumbers[0].Value==editedPl1);
        try
        {
            await RunOverviewIntegrationChecks(check,capture);
            foreach(var mode in new[]{ElementTheme.Light,ElementTheme.Dark})
            {
                root.RequestedTheme=mode;
                foreach(string id in new[]{"overview","display","power","devices","plugins","settings"})
                {
                    currentPage=id;customTuningOpen=false;Render();root.UpdateLayout();await Task.Delay(250);
                    bool protectedPage=id=="plugins";
                    check($"{mode} {id} page retains its original content groups",protectedPage?detailSectionTitles.Count==0:detailSectionTitles.Count>0);
                    AuditDesktopAlignment($"{mode}-{id}",check);
                    AuditFunctionOwnership($"{mode}-{id}",check);
                    if(capture&&!protectedPage)await CaptureDesktopReview($"{mode}-{id}");
                }
                if(capture)
                {
                    OpenTuning();for(int i=0;i<60&&tuningLoading;i++)await Task.Delay(50);
                    root.UpdateLayout();await Task.Delay(80);await CaptureDesktopReview($"{mode}-custom-combined");
                }
            }
            var bounds=new Windows.Graphics.RectInt32(AppWindow.Position.X,AppWindow.Position.Y,AppWindow.Size.Width,AppWindow.Size.Height);
            try
            {
                root.RequestedTheme=ElementTheme.Light;ResizeLogical(850,780);
                currentPage="power";customTuningOpen=false;Render();await Task.Delay(150);root.UpdateLayout();
                check("Narrow detail grids reflow without losing battery choices",compactChargeButtons.Count==3&&compactChargeButtons.Values.All(b=>b.ActualWidth>0&&b.ActualWidth<=page.ActualWidth));
                if(capture)await CaptureDesktopReview("Light-power-narrow");
            }
            finally{AppWindow.MoveAndResize(bounds);}
            await RunOverviewLayoutChecks(check,capture);
            await RunDetailReplicaChecks(check,capture);
        }
        finally
        {
            root.RequestedTheme=theme;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"desktop-alignment.json"),System.Text.Json.JsonSerializer.Serialize(desktopAlignmentSamples,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"function-ownership.json"),System.Text.Json.JsonSerializer.Serialize(functionOwnershipSamples,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
    }
    private async Task CaptureDesktopReview(string name,FrameworkElement? surface=null)
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"layout-review");Directory.CreateDirectory(folder);
        var old=root.Background;
        try
        {
            // The OS Mica backdrop is outside the XAML render target. Supply
            // its neutral surface for this client-area capture only.
            root.Background=new SolidColorBrush(root.ActualTheme==ElementTheme.Dark?Windows.UI.Color.FromArgb(255,32,32,32):Windows.UI.Color.FromArgb(255,243,243,243));
            root.UpdateLayout();await Task.Delay(50);
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(surface??root);
            byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();
            string path=Path.Combine(folder,name+".png");await File.WriteAllBytesAsync(path,[]);
            var file=await StorageFile.GetFileFromPathAsync(path);
            using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);
            await encoder.FlushAsync();
        }
        finally{root.Background=old;}
    }
}
