using System.Text.Json;
using OpenRevo.PluginSdk;

namespace OpenRevo.SamplePlugins;

public sealed class SystemSummaryPlugin : IPlugin
{
    public async Task RunAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        bool showMemory = true, configValid = true;
        string message = "等待宿主已有采样；藏盘时不会额外采样。";
        PluginSystemSummary? summary = null;
        PluginPowerState? power = null;
        var config = await context.LoadConfigAsync(cancellationToken);
        if (config.Success && config.Data is { } data)
        {
            if (data.TryGetProperty("showMemory", out var saved) && saved.ValueKind is JsonValueKind.True or JsonValueKind.False)
                showMemory = saved.GetBoolean();
            else { configValid = false; message = "配置格式未知，保留文件；暂停保存。"; }
        }
        else if (!config.Success) { configValid = false; message = "配置读取失败，保留原文件；暂停保存。"; }

        PluginView View() => new("系统摘要", [
            new("cpu", "text", "CPU 使用率", summary?.CpuPercent is { } cpu ? $"{cpu:0.0}%" : "未知"),
            new("battery", "text", "电池电量", summary?.BatteryPercent is { } battery ? $"{battery}%" : "未知"),
            new("power", "text", "电源通知", power?.OnAc is { } ac ? (ac ? "外接电源" : "电池供电") : "未知"),
            new("memory", "text", "已用内存", showMemory && summary?.UsedMemoryGiB is { } memory ? $"{memory:0.00} GiB" : "—"),
            new("show-memory", "toggle", "显示内存", showMemory ? "true" : "false", configValid),
            new("overview", "button", "打开状态总览"),
            new("sample-time", "text", "采样时间", summary?.SampledAt.ToLocalTime().ToString("HH:mm:ss") ?? "尚未采样"),
            new("status", "text", "状态", message)]);
        await context.SetViewAsync(View(), cancellationToken);
        await context.SubscribeSummaryAsync(cancellationToken);
        await context.SubscribePowerAsync(cancellationToken);
        await foreach (var item in context.ReadEventsAsync(cancellationToken))
        {
            if (item.Topic == PluginMethods.StateSummary)
                summary = item.Data.Deserialize<PluginSystemSummary>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            else if (item.Topic == PluginMethods.StatePower)
                power = item.Data.Deserialize<PluginPowerState>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            else if (item.Topic == "ui.event")
            {
                var input = item.Data.Deserialize<PluginUiEvent>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (input?.ControlId == "overview")
                {
                    var result = await context.ExecuteAsync("ui.navigate", JsonSerializer.SerializeToElement(new { page = "overview" }), cancellationToken);
                    message = result.Success ? "已打开总览。" : "页面跳转未完成：" + result.Code;
                }
                else if (input?.ControlId == "show-memory" && configValid)
                {
                    bool desired = input.Value == "true";
                    var saved = await context.SaveConfigAsync(new { showMemory = desired }, cancellationToken);
                    if (saved.Success) { showMemory = desired; message = "配置已保存。"; }
                    else message = "保存失败，保留原配置：" + saved.Code;
                }
            }
            var updated = await context.SetViewAsync(View(), cancellationToken);
            // The example does not retry in a loop. Future samples/events carry the latest view.
            if (!updated.Success && updated.Code != "rate_limited") break;
        }
    }
}
