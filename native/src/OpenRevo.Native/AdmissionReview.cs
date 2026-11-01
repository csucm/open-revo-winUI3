using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunAdmissionReview()
    {
        var checks=new List<object>();bool passed=true;
        void Check(string name,bool ok){checks.Add(new{name,passed=ok});passed&=ok;}
        try
        {
            Check("Review data isolated",AppData.IsIsolatedReviewData);
            if(!AppData.IsIsolatedReviewData)throw new InvalidOperationException("Isolated data required");
            var report=JsonSerializer.SerializeToElement(BuildDiagnosticReport());
            Check("Diagnostic schema contains BIOS admission reason and default folder",report.GetProperty("diagnosticsSchema").GetInt32()==3&&report.TryGetProperty("biosVersion",out _)&&report.GetProperty("admission").TryGetProperty("Reason",out _)&&report.GetProperty("defaultDiagnosticsDirectory").GetString()==AppData.DiagnosticsPath);
            if(mini)ToggleMini();currentPage="settings";
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            foreach(int width in new[]{1180,840})
            {
                root.RequestedTheme=theme;ResizeLogical(width,850);Render();await Task.Delay(250);root.UpdateLayout();
                Check($"{theme} {width} diagnostic folder and save actions present",DetailDescendants(page).OfType<TextBlock>().Any(t=>t.Text.Contains(AppData.DiagnosticsPath))&&DetailDescendants(page).OfType<Button>().Any(b=>b.Content?.ToString()=="选择位置并导出"));
                var controls=DetailDescendants(page).OfType<Control>().Where(c=>c is Button or ToggleSwitch or ComboBox).Where(c=>c.ActualWidth>0).ToArray();
                Check($"{theme} {width} settings controls stay within page",controls.All(c=>{double x=c.TransformToVisual(page).TransformPoint(new Point()).X;return x>=page.Padding.Left-2&&x+c.ActualWidth<=page.ActualWidth-page.Padding.Right+2;}));
                scroll.ChangeView(null,scroll.ScrollableHeight,null,true);await Task.Delay(80);await CaptureDesktopReview($"Admission-{theme}-{width}");
            }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"admission-probe.json"),JsonSerializer.Serialize(new{core=CoreMachine.Current,active=hardware.Active,state=hardware.State,report},new JsonSerializerOptions{WriteIndented=true}));
            if(File.Exists(Path.Combine(AppContext.BaseDirectory,"picker-review")))
            {
                await ExportDiagnosticReport();Check("Cancelling first picker writes no report",diagnosticExportLastPath==null);
                await ExportDiagnosticReport();Check("Second picker saves selected path",diagnosticExportLastPath!=null&&File.Exists(diagnosticExportLastPath));
                if(diagnosticExportLastPath!=null){using var saved=JsonDocument.Parse(await File.ReadAllTextAsync(diagnosticExportLastPath));Check("Saved JSON includes Core admission",saved.RootElement.GetProperty("diagnosticsSchema").GetInt32()==3&&saved.RootElement.TryGetProperty("admission",out _));}
            }
        }
        catch(Exception error){checks.Add(new{passed=false,error=error.ToString()});passed=false;}
        finally
        {
            exitRequested=true;await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"admission-ui-review.json"),JsonSerializer.Serialize(new{allPassed=passed,hardwareSettersInvoked=false,fullUiSuiteRun=false,checks},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=passed?0:1;
        }
    }
}
