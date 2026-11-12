using System.Text.Json;
using OpenRevo.PluginSdk;

namespace OpenRevo.SamplePlugins;

public sealed class InterfaceInspector : IPlugin
{
    public async Task RunAsync(IPluginContext context, CancellationToken stop)
    {
        var catalog = await context.QueryCatalogAsync(stop);
        var theme = await context.QueryThemeSchemaAsync(stop);
        PluginSystemSummary? summary = null;
        bool subscribed = false;
        int page = 0;
        string filter = "available", message = "清单说明宿主是否接通；不代表本插件获得硬件权限。";
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        PluginView View()
        {
            var chosen = catalog.Entries.Where(c => filter == "available" ? c.Availability is "available" or "unavailable" : c.Availability is not ("available" or "unavailable")).ToArray();
            int pages = Math.Max(1, (chosen.Length + 19) / 20); page = Math.Clamp(page, 0, pages - 1);
            var controls = new List<PluginControl>
            {
                new("api", "text", "宿主接口", $"API {catalog.Api.Major}.{catalog.Api.Minor}"),
                new("theme", "text", "窗口配色接口", theme.Available
                    ? $"{theme.Runtime} · {theme.ColorFormat} · {theme.MaximumValues}项 · " + string.Join(" / ", theme.Surfaces) : "当前不可用"),
                new("tokens", "text", "主题颜色令牌", string.Join(" / ", theme.ColorTokens)),
                new("filter", "choice", "显示能力", filter, Choices: [new("available", "已接通"), new("missing", "尚未开放")]),
                new("page", "choice", "能力页", (page + 1).ToString(), Choices:
                    Enumerable.Range(1, pages).Select(p => new PluginChoice(p.ToString(), "第" + p + "页")).ToArray()),
                new("refresh", "button", "重新读取接口清单"),
                new("subscribe", "toggle", "订阅已有系统摘要", subscribed ? "true" : "false"),
                new("charging", "text", "充电状态", summary?.Charging is { } charging ? (charging ? "充电中" : "未充电") : "未知／尚未订阅"),
                new("sample", "text", "原样本时间", summary?.SampledAt.ToLocalTime().ToString("HH:mm:ss") ?? "尚未收到"),
                new("status", "text", "说明", message)
            };
            foreach (var (entry, index) in chosen.Skip(page * 20).Take(20).Select((entry, index) => (entry, index)))
                controls.Add(new("cap-" + index, "text", entry.Id,
                    (entry.Availability switch { "available" => "已接通", "unavailable" => "已接通，当前不可用", "not_wired" => "尚未接通", _ => "未提供／不开放" }) +
                    (entry.Reason == null ? "" : " · " + entry.Reason)));
            controls.Add(new("total", "text", "本组数量", $"{chosen.Length}项 · 第{page + 1}/{pages}页"));
            return new("接口能力检查", controls.ToArray());
        }
        var result = await context.SetViewAsync(View(), stop);
        if (!result.Success) throw new IOException("接口检查器页面未获宿主接受：" + result.Code);
        await foreach (var item in context.ReadEventsAsync(stop))
        {
            if (item.Topic == PluginMethods.StateSummary && subscribed) summary = item.Data.Deserialize<PluginSystemSummary>(json);
            else if (item.Topic == "ui.event")
            {
                var input = item.Data.Deserialize<PluginUiEvent>(json);
                if (input?.ControlId == "filter" && input.Value is "available" or "missing") { filter = input.Value; page = 0; }
                else if (input?.ControlId == "page" && int.TryParse(input.Value, out int selected)) page = Math.Max(0, selected - 1);
                else if (input?.ControlId == "refresh")
                { catalog = await context.QueryCatalogAsync(stop); theme = await context.QueryThemeSchemaAsync(stop); message = "已读取宿主当前清单，没有额外采样。"; }
                else if (input?.ControlId == "subscribe")
                {
                    bool desired = input.Value == "true";
                    var changed = desired ? await context.SubscribeSummaryAsync(stop) : await context.UnsubscribeSummaryAsync(stop);
                    if (changed.Success) { subscribed = desired; if (!desired) summary = null; }
                    message = changed.Success ? (desired ? "已订阅已有摘要。" : "已取消订阅。") : "订阅操作未完成：" + changed.Code;
                }
            }
            result = await context.SetViewAsync(View(), stop);
            if (!result.Success) throw new IOException("页面更新未完成：" + result.Code);
        }
    }
}
