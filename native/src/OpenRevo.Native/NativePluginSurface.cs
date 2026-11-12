using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;

namespace OpenRevo.Native;

// Shared by the independent design preview and the future installed-plugin page.
// No plugin XAML, reflection, delegates or assembly code enter this presenter.
internal sealed class NativePluginViewPresenter : StackPanel
{
    private readonly string pluginName;
    private readonly TextBlock title, badge, status;
    private readonly StackPanel rows = Paint.Stack(0);
    private readonly Dictionary<string, FrameworkElement> controls = new(StringComparer.Ordinal);
    private PluginView? previous;
    internal event Action<PluginUiEvent>? Input;
    internal NativePluginViewPresenter(string pluginName, bool showHeading = true)
    {
        this.pluginName = pluginName; Spacing = 12;
        title = Paint.Text(pluginName, 16, true);
        badge = Paint.Text("原生插件 · 尚未运行", 12, muted: true);
        status = Paint.Text("等待插件页面…", 12, muted: true);
        Children.Add(title); Children.Add(badge); Children.Add(rows); Children.Add(status);
        if (!showHeading) title.Visibility = badge.Visibility = Visibility.Collapsed;
    }
    internal void Apply(PluginView? view, bool running, string? failureCode, bool preview = false)
    {
        badge.Text = preview ? "设计预览 · 按钮仅模拟反馈" : running ? "原生插件 · 沙箱运行" : "原生插件 · 已停止";
        status.Text = failureCode == null ? "" : "插件已停止：" + failureCode;
        if (view == null)
        {
            foreach (var item in controls.Values.OfType<Control>()) item.IsEnabled = false;
            return;
        }
        PluginViewPolicy.Validate(view);
        title.Text = pluginName + " · " + view.Title;
        bool same = previous != null && previous.Controls.Length == view.Controls.Length &&
            previous.Controls.Zip(view.Controls).All(pair => pair.First.Id == pair.Second.Id &&
                pair.First.Kind == pair.Second.Kind && pair.First.Label == pair.Second.Label &&
                (pair.First.Choices ?? []).SequenceEqual(pair.Second.Choices ?? []));
        if (!same)
        {
            rows.Children.Clear(); controls.Clear();
            foreach (var model in view.Controls)
            {
                string id = model.Id;
                FrameworkElement element;
                switch (model.Kind)
                {
                    case "text": element = Paint.Text(model.Value ?? "", 12, muted: true); element.MaxWidth = 440; break;
                    case "button": element = new PaintButton("执行", () => Input?.Invoke(new(id, null))); break;
                    case "toggle": element = PaintSwitch.Create(model.Label, model.Value == "true", value => Input?.Invoke(new(id, value ? "true" : "false"))); break;
                    case "choice":
                        var choices = model.Choices!;
                        element = new PaintSelect(model.Label, choices.Select(c => c.Label).ToArray(),
                            Array.FindIndex(choices, c => c.Value == model.Value), index => Input?.Invoke(new(id, choices[index].Value)));
                        break;
                    default: throw new InvalidDataException("Unknown native plugin component.");
                }
                controls.Add(id, element);
                rows.Children.Add(Paint.Rule(Paint.Between(Paint.Text(model.Label), element)));
            }
        }
        foreach (var model in view.Controls)
        {
            var element = controls[model.Id];
            if (element is Control input) input.IsEnabled = running && model.Enabled;
            switch (element)
            {
                case TextBlock text: text.Text = model.Value ?? ""; break;
                case ToggleSwitch toggle: PaintSwitch.SetReadback(toggle, model.Value == "true"); break;
                case PaintSelect choice: choice.SelectedIndex = Array.FindIndex(model.Choices!, c => c.Value == model.Value); break;
            }
        }
        previous = view;
    }
}

public sealed partial class MainWindow
{
    private FrameworkElement CreateNativePluginSurface(PluginSession session, bool showHeading = true)
    {
        var surface = new NativePluginViewPresenter(session.Manifest.Name, showHeading);
        surface.Input += value => { session.PostUiEvent(value); };
        bool loaded = false; int pending = 0;
        void Render() => surface.Apply(session.View, session.IsRunning, session.FailureCode);
        void Changed()
        {
            if (Interlocked.Exchange(ref pending, 1) != 0) return;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref pending, 0);
                if (loaded) Render();
            })) Interlocked.Exchange(ref pending, 0);
        }
        surface.Loaded += (_, _) => { if (loaded) return; loaded = true; session.Changed += Changed; Render(); };
        surface.Unloaded += (_, _) =>
        {
            loaded = false; session.Changed -= Changed;
        };
        return surface;
    }
}
