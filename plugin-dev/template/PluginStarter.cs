using System.Text.Json;
using OpenRevo.PluginSdk;

namespace PluginStarter;

public sealed class Plugin : IPlugin
{
    public async Task RunAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        int count = 0;
        PluginView View() => new("我的原生页面", [
            new("message", "text", "消息", $"已收到 {count} 次点击。"),
            new("hello", "button", "点击示例")]);
        await context.SetViewAsync(View(), cancellationToken);
        await foreach (var item in context.ReadEventsAsync(cancellationToken))
        {
            if (item.Topic != "ui.event") continue;
            var input = item.Data.Deserialize<PluginUiEvent>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (input?.ControlId != "hello") continue;
            count++;
            await context.SetViewAsync(View(), cancellationToken);
        }
    }
}
