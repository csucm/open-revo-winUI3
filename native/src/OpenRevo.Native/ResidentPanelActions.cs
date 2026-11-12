using Microsoft.UI.Xaml;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private bool ResidentMiniSupported(string id)
    {
        if (id == "keyboard") return keyboard.Kind != "None";
        if (residentSnapshot?.Device?.Declared != true) return true;
        return id switch
        {
            "performance" => PanelSupportsPerformance || !PanelHardwareActive,
            "battery" => PanelSupportsBattery || !PanelHardwareActive,
            "boost" => PanelSupportsBoost || !PanelHardwareActive,
            "fn" => PanelSupportsFn,
            "touchpad" => CanShowTouchpadControl,
            _ => true
        };
    }

    private bool ResidentQuickAvailable(string key)
    {
        if (residentSnapshot?.Ready != true) return false;
        return key switch
        {
            "camera" => PanelHardwareActive && PaintQuickState(key).HasValue,
            "touchpad" => PanelHardwareActive && CanShowTouchpadControl && PaintQuickState(key).HasValue,
            "fn" => PanelHardwareActive && (extraSwitches.FnLock.HasValue || residentSnapshot.Device?.CanSendFnLock == true),
            "mic" or "win" or "wifi" or "bluetooth" => PaintQuickState(key).HasValue,
            "boost" => PanelHardwareActive && PanelSupportsBoost,
            "awake" or "osd" or "screen" => true,
            _ => false
        };
    }

    private async Task RunResidentQuickActionAsync(string key)
    {
        if (!ResidentQuickAvailable(key) || ResidentQuickBusy(key)) return;
        if (key == "screen")
        {
            await RunResidentActionAsync(PanelOperation.Desktop, desktop: new PanelScreenOff());
            return;
        }
        if (key == "fn" && !extraSwitches.FnLock.HasValue)
        {
            var controls = Paint.Inline(8, PB("开启 Fn 锁", () => RunResidentActionAsync(PanelOperation.SetDeclaredFnLock, 1), true),
                PB("关闭 Fn 锁", () => RunResidentActionAsync(PanelOperation.SetDeclaredFnLock, 0), true));
            await PaintDialog("F1–F12 模式", Paint.Stack(8, Paint.Text("请选择命令。当前固件状态未知。"), controls));
            return;
        }
        await RunResidentActionAsync(ResidentQuickOperation(key), key == "osd" ? (osdEnabled ? 0 : 1) : null);
    }

    private bool ResidentQuickBusy(string key) => key switch
    {
        "screen" => PanelActionBusy(PanelOperation.Desktop, typeof(PanelScreenOff)),
        "camera" or "touchpad" or "fn" or "boost" => PanelHardwareActionBusy(ResidentQuickOperation(key)),
        _ => PanelActionBusy(ResidentQuickOperation(key))
    };

    private static PanelOperation ResidentQuickOperation(string key) => key switch
        {
            "camera" => PanelOperation.ToggleCamera, "mic" => PanelOperation.ToggleMicrophone,
            "touchpad" => PanelOperation.ToggleTouchpad, "fn" => PanelOperation.ToggleFnLock,
            "win" => PanelOperation.ToggleWinLock, "boost" => PanelOperation.ToggleFanBoost,
            "awake" => PanelOperation.ToggleKeepAwake, "osd" => PanelOperation.SetOsdEnabled,
            "wifi" => PanelOperation.ToggleWifi, "bluetooth" => PanelOperation.ToggleBluetooth,
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };

    private async Task ChangeResidentRefreshAsync(int current, int target)
    {
        displayBusy = true;
        UpdateDisplayValues();
        try
        {
            var result = await SendResidentAsync(PanelOperation.BeginRefresh, target, expected: current);
            if (result.Trial is not { } trial)
            {
                await ShowMessage("刷新率试切未完成", result.Message ?? "后台未确认试切。");
                return;
            }
            if (closed || residentDetaching) return;
            int remaining = Math.Clamp((int)(trial.Deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 1, 15000);
            bool keep = await PaintDialog($"保留 {target} Hz？",
                Paint.Text("15 秒内确认，否则后台自动恢复原刷新率。"), "保留", "恢复", remaining);
            if (closed || residentDetaching) return;
            var outcome = await SendResidentAsync(keep ? PanelOperation.KeepRefresh : PanelOperation.RestoreRefresh, trial: trial.Id);
            RequireResidentResult(outcome, PanelOutcome.ReadbackConfirmed);
        }
        catch (Exception error)
        {
            if (!closed && !residentDetaching) await ShowMessage("刷新率未确认", error.Message);
        }
        finally { displayBusy = false; if (!closed) UpdateDisplayValues(); }
    }

    private bool PaintGpuBusy => gpuChanging || residentClient != null && PanelHardwareActionBusy(PanelOperation.SelectGpu);

    private async Task RunResidentGpuAsync(int target)
    {
        if (closed || PaintGpuBusy || target != 0 && (!PanelHardwareActive || gpuState?.Known != true)) return;
        gpuChanging = true;
        try
        {
            if (target == 0) await RunResidentActionAsync(PanelOperation.ReadGpu);
            else
            {
                var reply = await SendResidentAsync(PanelOperation.SelectGpu, target, expected: gpuState!.Mode);
                if (reply.Outcome == PanelOutcome.ReadbackConfirmed)
                    await GpuMessage(GpuRestartPending ? "显卡模式已保存" : "显卡设置已确认",
                        GpuRestartPending ? $"已选择{OriginalGpuName(target)}，重启后生效。" : $"已回读{OriginalGpuName(target)}。");
                else if (reply.Outcome == PanelOutcome.CommandSent)
                    await GpuMessage("显卡命令已发送", "设备尚未回读确认。");
                else if (reply.Outcome != PanelOutcome.Applied)
                    await GpuMessage("显卡设置未完成", reply.Message ?? "请刷新显卡状态。");
            }
        }
        finally { gpuChanging = false; RefreshPaintedState(); }
    }

    private Task residentKeyboardWork = Task.CompletedTask;
    private PanelSetKeyboardRgb? residentKeyboardPending, residentKeyboardInFlight;
    private long residentKeyboardSubmitAfter;
    private bool ResidentKeyboardEditing => residentKeyboardPending != null || residentKeyboardInFlight != null;

    private void QueueResidentKeyboardEdit(bool immediate)
    {
        int level = rgbMode == "关闭背光" ? 0 : rgbLevel;
        if (level > 0 && residentLighting?.Keyboard?.CommandOnly == true) level = 1;
        var requested = new PanelSetKeyboardRgb(SelectedLightingColor, level, rgbMode == "彩虹循环");
        if (requested == (residentKeyboardPending ?? residentKeyboardInFlight)) return;
        residentKeyboardPending = requested;
        residentKeyboardSubmitAfter = immediate ? 0 : Environment.TickCount64 + 100;
        if (residentKeyboardWork.IsCompleted) residentKeyboardWork = DrainResidentKeyboardEditsAsync();
    }

    private async Task DrainResidentKeyboardEditsAsync()
    {
        try
        {
            while (!closed && residentKeyboardPending is { } requested)
            {
                long remaining = residentKeyboardSubmitAfter - Environment.TickCount64;
                if (!residentDetaching && remaining > 0)
                {
                    await Task.Delay((int)remaining);
                    continue;
                }
                residentKeyboardPending = null;
                residentKeyboardInFlight = requested;
                try
                {
                    var result = await SendResidentAsync(PanelOperation.Desktop, desktop: requested);
                    if (result.Outcome != PanelOutcome.CommandSent)
                        RequireResidentResult(result, PanelOutcome.ReadbackConfirmed);
                    if (residentKeyboardPending == requested) residentKeyboardPending = null;
                }
                catch (Exception error)
                {
                    if (closed) return;
                    if (residentKeyboardPending != null) continue;
                    if (residentDetaching) throw;
                    await ShowMessage("背光设置未完成", error.Message);
                }
                finally { residentKeyboardInFlight = null; }
            }
        }
        finally
        {
            residentKeyboardPending = null;
            if (!closed)
            {
                ApplyResidentKeyboardReadback(residentLighting?.Keyboard?.ConfirmedRgb);
                RefreshPaintedState();
            }
        }
    }

    private Task UndoResidentKeyboardAsync() => !ResidentKeyboardEditing
        ? RunResidentActionAsync(PanelOperation.Desktop, desktop: new PanelUndoKeyboardRgb())
        : Task.CompletedTask;
    private Task SetResidentMonochromeAsync(int level) =>
        RunResidentActionAsync(PanelOperation.Desktop, desktop: new PanelSetMonochrome(level));

    private void RenderResidentDetails(string route)
    {
        switch (route)
        {
            case "minicustomization": RenderMiniCustomization(); break;
            case "devices": RenderPaintedLighting(); break;
            case "plugins": RenderResidentPlugins(); break;
            case "lightingzone": case "display": case "color":
            case "power": case "sleep": case "tuning": RenderPaintedDetails(route); break;
            case "storage": case "diagnostics":
                RenderPaintedDetails(route);
                _ = ReadResidentMaintenanceSettingsAsync();
                break;
            case "settings": case "osdsettings": case "updates": RenderPaintedDetails(route); break;
            default:
                page.Children.Add(Paint.Text("此页面仍在接入常驻后台。", 14, muted: true));
                break;
        }
    }
}
