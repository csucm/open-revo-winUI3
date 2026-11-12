using Microsoft.UI.Xaml;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.ResidentPanel;

public partial class App : Application
{
    private ResidentPanelWindow? window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        PanelClient? client = null;
        try
        {
            if (Environment.GetCommandLineArgs() is not [_, "--resident-panel", var pipe, var parent] ||
                !int.TryParse(parent, out int parentId) || parentId <= 0)
                throw new ArgumentException("候选面板只能从托盘后台打开。");
            client = await PanelClient.ConnectAsync(pipe, parentId);
            window = new ResidentPanelWindow(client);
            window.Activate();
        }
        catch
        {
            client?.Dispose();
            Environment.ExitCode = 1;
            Exit();
        }
    }
}
