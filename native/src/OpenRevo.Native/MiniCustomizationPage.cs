using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using Windows.System;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private MiniLayout? miniDraft;
    private readonly Stack<MiniLayout> miniUndo = new();
    private readonly Stack<string[]> miniUndoPools = new();
    private List<string> miniQuickPoolOrder = [];
    private MiniEditorBoard? miniQuickLibraryBoard;
    private MiniPreviewScope? miniPreview;
    private bool miniSaving, miniLeavePending, miniReplaceUnrecognized;
    private string miniEditorNotice = "点击编辑布局，整理常用功能。";
    private string miniLibraryGroup = "top";
    private (string Group, int Index)? miniSelection;
    private bool MiniDirty => miniDraft != null && (miniReplaceUnrecognized || !miniDraft.SameAs(MiniSaved));
    private bool MiniEditorBusy => miniSaving || miniPreview?.Busy == true;
    private MiniLayout MiniEditingLayout => miniDraft ?? MiniSaved;

    private sealed class MiniPreviewScope : IDisposable
    {
        internal readonly List<Action> Bindings = [];
        internal readonly List<MiniEditorDrag> Drags = [];
        internal bool Busy => Drags.Any(d => d.Busy);
        internal void Refresh() { if (!Busy) foreach (var bind in Bindings) bind(); }
        public void Dispose() { foreach (var drag in Drags) drag.Dispose(); Drags.Clear(); Bindings.Clear(); }
    }
    private void ReleaseMiniPreview() { miniPreview?.Dispose(); miniPreview = null; }
    private void CancelMiniDrags() { if (miniPreview != null) foreach (var drag in miniPreview.Drags) drag.Cancel(); }
    private void MiniAnnounce(string text) { miniEditorNotice = text; RefreshPaintedState(); }

    // Move only bindings made by this readonly subtree into its disposable page scope.
    private FrameworkElement MiniReadOnly(Func<FrameworkElement> create)
    {
        int start = paintBindings.Count;
        var body = create();
        miniPreview!.Bindings.AddRange(paintBindings.Skip(start)); paintBindings.RemoveRange(start, paintBindings.Count - start);
        body.IsHitTestVisible = false;
        void NoInput(DependencyObject item)
        {
            if (item is Control control) control.IsTabStop = false;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) NoInput(VisualTreeHelper.GetChild(item, i));
        }
        body.Loaded += (_, _) => NoInput(body);
        return body;
    }
    private void BeginMiniEdit()
    {
        if (MiniEditorBusy) return;
        miniDraft = MiniSaved.Copy(); miniUndo.Clear(); miniUndoPools.Clear(); miniSelection = null; miniReplaceUnrecognized = false;
        miniQuickPoolOrder = MiniLayout.QuickIds.Where(id => !miniDraft.Quick.Contains(id)).ToList(); miniLibraryGroup = "quick";
        miniEditorNotice = "从控件库拖入快捷位置可双向交换 · 靠近提前让位 · Alt 加方向键排序"; Render();
    }
    private void ChangeMiniDraft(Func<MiniLayout, MiniLayout> change, string message, (string Group, int Index)? selection = null, Action? afterChange = null)
    {
        if (miniDraft == null || MiniEditorBusy) return;
        var before = miniDraft.Copy(); var next = change(before.Copy());
        if (before.SameAs(next)) return;
        miniUndo.Push(before); miniUndoPools.Push(miniQuickPoolOrder.ToArray()); miniDraft = next; miniSelection = selection; miniEditorNotice = message; afterChange?.Invoke();
        RenderMiniEditorPreservingScroll();
    }
    private void RenderMiniEditorPreservingScroll()
    {
        double offset = scroll.VerticalOffset; Render();
        DispatcherQueue.TryEnqueue(() => { if (!closed && PaintRoute == "minicustomization" && !mini) scroll.ChangeView(null, offset, null, true); });
    }
    private void MoveMini(string group, int index, int delta)
    {
        int count = group == "top" ? MiniEditingLayout.Top.Length : 6, target = index + delta;
        if (target < 0 || target >= count) return;
        ChangeMiniDraft(layout =>
        {
            if (group == "top") { var order = layout.Top.ToList(); string id = order[index]; order.RemoveAt(index); order.Insert(target, id); return layout with { Top = order.ToArray() }; }
            (layout.Quick[index], layout.Quick[target]) = (layout.Quick[target], layout.Quick[index]); return layout;
        }, "已调整顺序", (group, target));
        FocusMiniPosition(group, target);
    }
    private void FocusMiniPosition(string group, int index)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || PaintRoute != "minicustomization" || mini) return;
            foreach (var item in DetailDescendants(page).OfType<MiniEditItem>())
                if (Equals(item.Tag, (group, index))) { item.Focus(FocusState.Keyboard); break; }
        });
    }
    private void RemoveMini(string group, int index)
    {
        ChangeMiniDraft(layout =>
        {
            if (group == "top") return layout with { Top = layout.Top.Where((_, i) => i != index).ToArray() };
            layout.Quick[index] = null; return layout;
        }, "已移回控件库");
    }
    private void PickMini(string group, string id, int? target)
    {
        if (!MiniSupported(id)) return;
        if (group == "quick" && target == null)
        {
            target = miniSelection is { Group: "quick" } selected ? selected.Index : Array.IndexOf(MiniEditingLayout.Quick, null);
            if (target < 0) { MiniAnnounce("六个位置已满，请先点击要替换的位置。"); return; }
        }
        int selectedIndex = target ?? MiniEditingLayout.Top.Length;
        ChangeMiniDraft(layout =>
        {
            if (group == "top")
            {
                int from = Array.IndexOf(layout.Top, id);
                if (!target.HasValue) return from >= 0 ? layout : layout with { Top = [.. layout.Top, id] };
                if (from >= 0) (layout.Top[from], layout.Top[target.Value]) = (layout.Top[target.Value], layout.Top[from]);
                else layout.Top[target.Value] = id;
            }
            else
            {
                int from = Array.IndexOf(layout.Quick, id);
                if (from >= 0) (layout.Quick[from], layout.Quick[target!.Value]) = (layout.Quick[target.Value], layout.Quick[from]);
                else layout.Quick[target!.Value] = id;
            }
            return layout;
        }, "布局草稿已更新 · 保存后生效", (group, selectedIndex));
    }
    private async Task OpenMiniPicker(string group, int index)
    {
        if (miniDraft == null || MiniEditorBusy || dialogOpen) return;
        miniSelection = (group, index); miniLibraryGroup = group;
        var choices = Paint.Stack(6);
        foreach (string id in (group == "top" ? MiniLayout.TopIds : MiniLayout.QuickIds).Where(MiniSupported))
        {
            string key = id;
            var b = new PaintButton(MiniName(id), () =>
            {
                if (paintDialog != null) paintDialog.IsOpen = false;
                PickMini(group, key, index);
            });
            bool present = group == "top" ? MiniEditingLayout.Top.Contains(id) : MiniEditingLayout.Quick.Contains(id);
            b.Content = Paint.Between(Paint.Inline(8, Paint.Icon(MiniIcon(id), 24), Paint.Text(MiniName(id), 12)), Paint.Text(present ? "交换" : "替换", 12, muted: true));
            b.StretchContent(); choices.Children.Add(b);
        }
        var remove = new PaintButton(group == "quick" ? "清空位置" : "移回控件库", () =>
        { if (paintDialog != null) paintDialog.IsOpen = false; RemoveMini(group, index); });
        choices.Children.Add(remove);
        await PaintDialog(group == "quick" ? $"快捷位置 {index + 1}" : "替换上方磁贴",
            new ScrollViewer { Content = choices, MaxHeight = Math.Max(120, Math.Min(430, root.XamlRoot.Size.Height - 200)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (!closed && PaintRoute=="minicustomization" && !mini) RenderMiniEditorPreservingScroll();
        FocusMiniPosition(group, index);
    }
    private async Task<bool> SaveMiniLayout(bool render = true)
    {
        if (miniDraft == null || MiniEditorBusy) return false;
        miniSaving = true; RefreshPaintedState();
        var next = miniDraft.Copy();
        try
        {
            if (residentClient != null)
                await SaveResidentLayoutAsync(new(Mini: new(next.Top, next.Quick), ReplaceUnrecognized: miniReplaceUnrecognized));
            else if (!uiReviewMode) await Task.Run(() => MiniLayout.Save(next, miniReplaceUnrecognized));
            if (closed) return false;
            miniLayoutRead = new(next.Copy(), null); miniDraft = null; miniUndo.Clear(); miniUndoPools.Clear(); miniSelection = null; miniReplaceUnrecognized = false;
            miniEditorNotice = "小面板布局已保存。"; if (render) RenderMiniEditorPreservingScroll(); return true;
        }
        catch (Exception error)
        {
            miniEditorNotice = "保存失败 · 草稿已保留：" + error.Message; RefreshPaintedState();
            await ShowMessage("小面板布局未保存", error.Message + "\n草稿仍在本页，可以重试保存。"); return false;
        }
        finally { miniSaving = false; RefreshPaintedState(); }
    }
    private void DiscardMiniDraft()
    { miniDraft = null; miniUndo.Clear(); miniUndoPools.Clear(); miniSelection = null; miniReplaceUnrecognized = false; miniEditorNotice = "已取消，保留上次保存的布局。"; }
    private async Task ResetMiniDraft()
    {
        if (miniDraft == null || MiniEditorBusy) return;
        if (miniLayoutRead?.Issue != null && !miniReplaceUnrecognized)
        {
            if (!await Confirm("恢复默认布局", "原布局无法识别。保存时将保留原文件副本，再用默认草稿替换。现在只修改草稿。")) return;
            miniReplaceUnrecognized = true;
        }
        ChangeMiniDraft(_ => MiniLayout.Default, "默认布局已载入草稿 · 保存后生效");
        MiniAnnounce("默认布局已载入草稿 · 保存后生效");
    }
    private async Task<bool> CanLeaveMiniEditor()
    {
        if (!MiniDirty) { if (miniSaving) return false; DiscardMiniDraft(); return true; }
        if (miniLeavePending || miniSaving || dialogOpen) return false;
        miniLeavePending = true; CancelMiniDrags();
        try
        {
            bool discard = false;
            var abandon = new PaintButton("放弃更改并离开", () => { discard = true; if (paintDialog != null) paintDialog.IsOpen = false; });
            bool save = await PaintDialog("小面板布局尚未保存", Paint.Stack(12, Paint.Text("保存后离开，或放弃本次草稿。"), abandon), "保存后离开", "继续编辑");
            if (discard) { DiscardMiniDraft(); return true; }
            return save && await SaveMiniLayout(false);
        }
        finally { miniLeavePending = false; }
    }
    private async Task NavigateFromMiniEditor(string route)
    {
        if (!await CanLeaveMiniEditor() || closed) { SyncMiniNavigation(); return; }
        PaintNavigate(route);
    }
    private async Task ToggleMiniFromEditor()
    { if (await CanLeaveMiniEditor() && !closed) ToggleMini(); }
    private void SyncMiniNavigation()
    {
        syncingNavigation = true; navigation.SelectedItem = navigation.MenuItems.OfType<PaintNavItem>().FirstOrDefault(i => i.Id == "settings"); syncingNavigation = false;
    }

    private void RenderMiniCustomization()
    {
        _ = MiniSaved;
        miniPreview = new();
        var actions = new SuiteWrapPanel { ItemGap = 8 };
        if (miniDraft == null) actions.Children.Add(PB("编辑布局", () => { BeginMiniEdit(); return Task.CompletedTask; }));
        else
        {
            var undo = PB("撤销", () =>
            {
                if (!MiniEditorBusy && miniUndo.TryPop(out var before)) { miniDraft = before; if (miniUndoPools.TryPop(out var pool)) miniQuickPoolOrder = pool.ToList(); miniSelection = null; miniEditorNotice = "已撤销上一步"; RenderMiniEditorPreservingScroll(); }
                return Task.CompletedTask;
            });
            paintBindings.Add(() => undo.IsEnabled = !MiniEditorBusy && miniUndo.Count > 0); actions.Children.Add(undo);
            actions.Children.Add(PB("恢复默认", ResetMiniDraft));
            actions.Children.Add(PB("取消", () => { if (!MiniEditorBusy) { DiscardMiniDraft(); RenderMiniEditorPreservingScroll(); } return Task.CompletedTask; }));
            var save = PB("保存布局", async () => { await SaveMiniLayout(); }); save.Variant = "primary"; actions.Children.Add(save);
        }
        foreach (var button in actions.Children.OfType<PaintButton>()) if (AutomationProperties.GetName(button) != "撤销") paintBindings.Add(() => button.IsEnabled = !MiniEditorBusy);
        page.Children.Add(actions);
        page.Children.Add(PT(() => miniEditorNotice, 12, muted: true));
        if (miniLayoutRead?.Issue is string issue) page.Children.Add(Paint.Text(issue + " 当前显示默认布局，保存前请明确恢复默认。", 12, muted: true));

        var preview = Paint.Stack(6);
        preview.Children.Add(Paint.Between(Paint.Text("Open-Revo  MINI", 12, true), Paint.Text("−　□　×", 12, muted: true)));
        var top = new MiniEditorBoard(false); var quick = new MiniEditorBoard(true);
        foreach (string id in MiniEditingLayout.Top)
        {
            if (miniDraft == null && !MiniSupported(id)) continue;
            int index = top.Order.Count;
            var body = MiniReadOnly(() => PaintMiniTile(id, true));
            top.Add(MakeMiniEditItem("top", index, id, body));
        }
        if (top.Order.Count == 0) preview.Children.Add(Paint.Text("还没有磁贴，添加常用的功能。", 12, muted: true));
        preview.Children.Add(top);
        if (miniDraft != null) preview.Children.Add(PB("＋ 添加上方磁贴", () => { miniLibraryGroup = "top"; miniSelection = null; RenderMiniEditorPreservingScroll(); return Task.CompletedTask; }));
        preview.Children.Add(Paint.Text("快捷控制 · 六个位置", 12, muted: true));
        for (int i = 0; i < 6; i++)
        {
            string? id = MiniEditingLayout.Quick[i];
            FrameworkElement body = id != null && (miniDraft != null || MiniSupported(id))
                ? MiniReadOnly(() => PaintQuickRow(id, true, true)) : MiniEmptyQuickPlaceholder(miniDraft != null);
            quick.Add(MakeMiniEditItem("quick", i, id, body));
        }
        preview.Children.Add(quick);
        preview.Children.Add(Paint.Between(MiniReadOnly(() => PT(() => identity.Board == "未知" ? "机型待读取" : identity.Board, 12, muted: true)),
            MiniReadOnly(() => PT(() => PanelHardwareActive ? "● 已接管" : "● 未接管", 12, muted: true))));
        var frame = Paint.Card(preview, 11, 12); frame.Width = 400; frame.HorizontalAlignment = HorizontalAlignment.Center;
        var stage = PaintSection("小面板预览", "display", new MiniPreviewScale(frame));
        miniQuickLibraryBoard = null;
        var library = PaintMiniLibrary();
        var workspace = new MiniEditorWorkspace(stage, library); page.Children.Add(workspace);
        if (miniDraft != null)
        {
            MiniEditorDrag? quickDrag = null;
            foreach (var (group, board) in new[] { ("top", top), ("quick", quick) })
            {
                var drag = new MiniEditorDrag(board, root, this, scroll, order =>
                {
                    ChangeMiniDraft(layout => group == "top" ? layout with { Top = order.Select(item => item.Value!).ToArray() }
                        : layout with { Quick = order.Select(item => item.Value).ToArray() }, "已放置 · 可撤销整次拖动");
                }, index => { _ = OpenMiniPicker(group, index); }, MiniAnnounce, () => miniDraft!=null && !MiniEditorBusy && !dialogOpen);
                miniPreview.Drags.Add(drag); if (group == "quick") quickDrag = drag;
            }
            if (miniQuickLibraryBoard is {} pool && quickDrag != null)
            {
                var libraryDrag = new MiniEditorDrag(pool, root, this, scroll, _ => {}, index => PickMini("quick", pool.Order[index].Value!, null), MiniAnnounce, () => miniDraft != null && !MiniEditorBusy && !dialogOpen);
                quickDrag.Link(libraryDrag, (a, b) =>
                {
                    MiniEditItem quickItem = quick.Order.Contains(a) ? a : b, libraryItem = ReferenceEquals(quickItem, a) ? b : a;
                    int index = quick.Order.IndexOf(quickItem); string incoming = libraryItem.Value!; string? outgoing = MiniEditingLayout.Quick[index];
                    ChangeMiniDraft(layout => { layout.Quick[index] = incoming; return layout; }, "已双向交换 · 可一步撤销", ("quick", index), () =>
                    {
                        int at = miniQuickPoolOrder.IndexOf(incoming);
                        if (at >= 0) { if (outgoing != null) miniQuickPoolOrder[at] = outgoing; else miniQuickPoolOrder.RemoveAt(at); }
                    });
                });
                miniPreview.Drags.Add(libraryDrag);
            }
        }
        // Page unload also covers teardown that does not pass through Render().
        var scope = miniPreview; workspace.Unloaded += (_, _) => scope.Dispose();
    }
    private static FrameworkElement MiniEmptyQuickPlaceholder(bool editable)
    {
        var plus = Paint.Text(editable ? "＋" : "", 16);
        var label = Paint.Text(editable ? "添加" : "", 12);
        plus.TextAlignment = label.TextAlignment = TextAlignment.Center;
        plus.HorizontalAlignment = label.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Paint.Stack(2, plus, label);
    }
    private MiniEditItem MakeMiniEditItem(string group, int index, string? id, FrameworkElement body)
    {
        var content = Paint.Stack(3);
        TextBlock? positionLabel = null;
        if (miniDraft != null && group == "top")
        {
            var tools = Paint.Inline(2);
            foreach (var (name, action) in new (string, Action)[]
            { ("上移", () => MoveMini(group, index, -1)), ("下移", () => MoveMini(group, index, 1)), ("移除", () => RemoveMini(group, index)) })
            {
                var button = new PaintButton(name + MiniName(id!), action) { Variant = "plain", MinHeight = 22, Padding = new(6, 0, 6, 0), Content = Paint.Text(name=="上移"?"↑":name=="下移"?"↓":"×", 12) };
                if (name == "上移") button.IsEnabled = index > 0;
                if (name == "下移") button.IsEnabled = index < MiniEditingLayout.Top.Length - 1;
                tools.Children.Add(button);
            }
            positionLabel = Paint.Text("⠿  " + (index + 1), 12, muted: true);
            content.Children.Add(Paint.Between(positionLabel, tools));
        }
        if (miniDraft != null && group == "quick") { positionLabel = Paint.Text((index + 1).ToString(), 12, muted: true); positionLabel.TextAlignment=TextAlignment.Center; content.Children.Add(positionLabel); }
        content.Children.Add(body);
        if (group == "top" && miniDraft != null)
        {
            var reason = MiniReadOnly(() => PT(() => MiniAvailability(id!), 12, muted: true));
            miniPreview!.Bindings.Add(() => reason.Visibility = MiniAvailability(id!).Length > 0 ? Visibility.Visible : Visibility.Collapsed);
            content.Children.Add(reason);
        }
        UIElement surface = content;
        if (group=="top" && miniDraft!=null)
        {
            // One quiet tile frame contains the toolbar and original mini body.
            if (body is Border card) { card.Padding=new(0); card.BorderThickness=new(0); }
            surface=Paint.Card(content,7,12);
        }
        var item = new MiniEditItem(group == "quick" ? $"快捷位置 {index + 1}：{(id == null ? "空位" : MiniName(id))}" : MiniName(id!), surface)
        { Value = id, PositionLabel=positionLabel, Tag = (group, index), IsTabStop = miniDraft != null };
        if (id != null) miniPreview!.Bindings.Add(() => ToolTipService.SetToolTip(item, MiniAvailability(id)));
        if (miniDraft != null) item.KeyDown += (_, e) => MiniItemKey(group, index, e);
        return item;
    }
    private void MiniItemKey(string group, int index, KeyRoutedEventArgs e)
    {
        if (MiniEditorBusy) return;
        bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (alt && (group == "top" ? e.Key is VirtualKey.Up or VirtualKey.Down : e.Key is VirtualKey.Left or VirtualKey.Right))
        { MoveMini(group, index, e.Key is VirtualKey.Left or VirtualKey.Up ? -1 : 1); e.Handled = true; }
        else if (e.Key is VirtualKey.Enter or VirtualKey.Space) { _ = OpenMiniPicker(group, index); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete) { RemoveMini(group, index); e.Handled = true; }
    }
    private FrameworkElement PaintMiniLibrary()
    {
        var content = Paint.Stack(8);
        if (miniDraft == null)
        {
            content.Children.Add(Paint.Text("点击编辑，将常用控件放进小面板。", 12, muted: true));
            content.Children.Add(Paint.Text($"上方磁贴 {MiniSaved.Top.Count(MiniSupported)} 个 · 快捷控制 {MiniSaved.Quick.Count(id => id != null && MiniSupported(id))} / 6", 12));
            return PaintSection("你的常用布局", "settings", content);
        }
        foreach (string group in new[] { "top", "quick" })
        {
            var tab = new PaintButton(group == "top" ? "上方磁贴" : "快捷按钮", () => { if (MiniEditorBusy) return; miniLibraryGroup = group; miniSelection = null; RenderMiniEditorPreservingScroll(); }) { Selected = miniLibraryGroup == group };
            content.Children.Add(tab);
        }
        content.Children.Add(Paint.Text(miniLibraryGroup == "top" ? "点击添加，再在预览中拖动排序。" : "拖入任一快捷位置，旧控件会飞回库内腾出的位置。也可反向拖回交换。", 12, muted: true));
        if (miniLibraryGroup == "quick")
        {
            miniQuickPoolOrder = miniQuickPoolOrder.Concat(MiniLayout.QuickIds).Distinct().Where(id => MiniSupported(id) && !MiniEditingLayout.Quick.Contains(id)).ToList();
            var pool = new MiniEditorBoard(true, 3); miniQuickLibraryBoard = pool;
            foreach (string id in miniQuickPoolOrder)
            {
                string key = id;
                var item = new MiniEditItem("控件库：" + MiniName(id), MiniReadOnly(() => PaintQuickRow(key, true, true))) { Value = id };
                item.KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Space) { PickMini("quick", key, null); e.Handled = true; } };
                pool.Add(item);
            }
            content.Children.Add(pool);
            if (pool.Order.Count == 0) content.Children.Add(Paint.Text("支持的快捷控件均已放入面板。", 12, muted: true));
        }
        foreach (string id in (miniLibraryGroup == "top" ? MiniLayout.TopIds : Array.Empty<string>()).Where(MiniSupported))
        {
            string key = id; bool present = miniLibraryGroup == "top" ? MiniEditingLayout.Top.Contains(id) : MiniEditingLayout.Quick.Contains(id);
            var b = new PaintButton(MiniName(id), () => PickMini(miniLibraryGroup, key, null)); b.StretchContent();
            b.Content = Paint.Between(Paint.Inline(8, Paint.Icon(MiniIcon(id), 24), Paint.Stack(2, Paint.Text(MiniName(id), 12), PT(() => MiniAvailability(key), 12, muted: true))), Paint.Text(present ? "已添加" : "＋", 12, muted: true), 5);
            paintBindings.Add(() => b.IsEnabled = !MiniEditorBusy && !(miniLibraryGroup == "top" && present)); content.Children.Add(b);
        }
        if (miniSelection is { } selected)
        {
            string group = selected.Group; int index = selected.Index;
            content.Children.Add(Paint.Text(group == "quick" ? $"已选快捷位置 {index + 1}" : $"已选磁贴 {index + 1}", 12));
            var direction = Paint.Inline(5, PB(group == "quick" ? "左移" : "上移", () => { MoveMini(group, index, -1); return Task.CompletedTask; }),
                PB(group == "quick" ? "右移" : "下移", () => { MoveMini(group, index, 1); return Task.CompletedTask; }));
            content.Children.Add(direction); content.Children.Add(PB("替换", () => OpenMiniPicker(group, index)));
            content.Children.Add(PB("移除", () => { RemoveMini(group, index); return Task.CompletedTask; }));
        }
        content.Children.Add(Paint.Text("暂不可用的控件保留位置。编辑与保存只改变显示。", 12, muted: true));
        return PaintSection("控件库", "settings", content);
    }
}

// Preserve the original 400 DIP geometry; scale the entire drawing and its hit tree.
internal sealed class MiniPreviewScale : Panel
{
    internal const double Factor = .75;
    private readonly FrameworkElement child;
    private double naturalHeight;
    internal MiniPreviewScale(FrameworkElement child)
    {
        this.child = child; Children.Add(child); HorizontalAlignment = HorizontalAlignment.Center;
        child.RenderTransform = new ScaleTransform { ScaleX = Factor, ScaleY = Factor };
    }
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        child.Measure(new(400, double.PositiveInfinity)); naturalHeight = child.DesiredSize.Height;
        return new(400 * Factor, naturalHeight * Factor);
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    { child.Arrange(new(0, 0, 400, naturalHeight)); return final; }
}

// Stack the library only when the scaled preview and catalogue cannot fit side by side.
internal sealed class MiniEditorWorkspace : Panel
{
    private readonly UIElement stage, library;
    private bool sideBySide;
    private double stageWidth, stageHeight, libraryHeight;
    internal MiniEditorWorkspace(UIElement stage, UIElement library)
    { this.stage=stage; this.library=library; Children.Add(stage); Children.Add(library); }
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : 900;
        sideBySide = width >= 640; stageWidth = sideBySide ? width - 278 : width;
        stage.Measure(new(stageWidth, double.PositiveInfinity)); stageHeight = stage.DesiredSize.Height;
        library.Measure(new(sideBySide ? 260 : width, double.PositiveInfinity)); libraryHeight = library.DesiredSize.Height;
        return new(width, sideBySide ? Math.Max(stageHeight, libraryHeight) : stageHeight + 18 + libraryHeight);
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        stage.Arrange(new(0, 0, stageWidth, stageHeight));
        library.Arrange(sideBySide ? new(stageWidth + 18, 0, 260, libraryHeight) : new(0, stageHeight + 18, final.Width, libraryHeight));
        return final;
    }
}
