using System.Text.Json;
using OpenRevo.PluginSdk;

namespace OpenRevo.SamplePlugins;

// Construction, load, rendering and changing draft choices perform no hardware writes.
public sealed class HardwareControlsPlugin : IPlugin
{
    public async Task RunAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var caps = await context.QueryCapabilitiesAsync(cancellationToken);
        bool Has(string permission) => caps.Capabilities.Any(c => c.Id == permission && c.Available);
        string mode = "office", limit = "80", status = "选择草稿后，点击对应应用按钮。";
        bool fan = false;
        PluginView View() => new("硬件控制", [
            new("mode", "choice", "性能模式草稿", mode, Has(PluginPermissions.Performance),
                [new("office", "办公"), new("balanced", "均衡"), new("performance", "狂暴")]),
            new("apply-mode", "button", "应用性能模式", Enabled: Has(PluginPermissions.Performance)),
            new("fan", "toggle", "强冷草稿", fan ? "true" : "false", Has(PluginPermissions.FanBoost)),
            new("apply-fan", "button", "应用强冷", Enabled: Has(PluginPermissions.FanBoost)),
            new("limit", "choice", "充电上限草稿", limit, Has(PluginPermissions.BatteryLimit),
                [new("60", "60%"), new("80", "80%"), new("100", "100%")]),
            new("apply-limit", "button", "应用充电上限", Enabled: Has(PluginPermissions.BatteryLimit)),
            new("status", "text", "结果", status)]);
        await context.SetViewAsync(View(), cancellationToken);
        await foreach (var item in context.ReadEventsAsync(cancellationToken))
        {
            if (item.Topic != "ui.event") continue;
            var input = item.Data.Deserialize<PluginUiEvent>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (input == null) continue;
            PluginResult? result = null;
            switch (input.ControlId)
            {
                case "mode": mode = input.Value!; status = "性能草稿已修改。"; break;
                case "fan": fan = input.Value == "true"; status = "强冷草稿已修改。"; break;
                case "limit": limit = input.Value!; status = "限充草稿已修改。"; break;
                case "apply-mode": result = await context.SetPerformanceAsync(mode switch
                    { "office" => PluginPerformanceMode.Office, "balanced" => PluginPerformanceMode.Balanced, _ => PluginPerformanceMode.Performance }, cancellationToken); break;
                case "apply-fan": result = await context.SetFanBoostAsync(fan, cancellationToken); break;
                case "apply-limit": result = await context.SetBatteryLimitAsync(limit switch
                    { "60" => PluginBatteryLimit.Workstation, "80" => PluginBatteryLimit.Balanced, _ => PluginBatteryLimit.Full }, cancellationToken); break;
            }
            if (result != null) status = result.Code switch
            {
                "readback_confirmed" => "设备状态已回读确认。",
                "command_sent" => "命令已发送，尚未确认实际效果。",
                _ => "未完成：" + result.Code
            };
            caps = await context.QueryCapabilitiesAsync(cancellationToken);
            await context.SetViewAsync(View(), cancellationToken);
        }
    }
}
