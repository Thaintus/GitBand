using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitBinder.Application.Settings;
using GitBinder.Desktop;
using GitBinder.Desktop.Controls;
using GitBinder.Desktop.ViewModels;
using GitBinder.Desktop.Views;

namespace GitBinder.UiSmoke;

public sealed partial class SmokeApp
{
    /// <summary>
    /// 仅向隔离验证窗口发送托管路由事件，不向操作系统注入鼠标或键盘。
    /// 实际经过 ListReorder 的 500ms 等待、指针捕获、命中、预览及保存/取消路径。
    /// </summary>
    private async Task VerifyDragPreviewAsync()
    {
        var vm = _fixture.Accounts;
        vm.SearchText = string.Empty;
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(nav => nav.Key == "Nav.Accounts");
        await SettleAsync();
        Check(vm.FilteredItems.Count >= 2, "拖动验证至少有两个内存账号条目");
        var view = _window.GetVisualDescendants().OfType<AccountsView>().Single();
        var list = view.GetVisualDescendants().OfType<ListBox>()
            .Single(control => ListReorder.GetScope(control) == ListOrderScope.Accounts);
        list.ScrollIntoView(0);
        await SettleAsync();
        var original = vm.FilteredItems.Select(item => item.Account.Id).ToArray();
        var first = vm.FilteredItems[0];
        var second = vm.FilteredItems[1];
        Check(vm.CanReorder(ListOrderScope.Accounts, first), "真实账号列表已接入排序服务且当前可拖动");

        // 普通点击不能进入排序，也不能改变原顺序。
        var shortPointer = new Pointer(79001, PointerType.Mouse, true);
        try
        {
            var row = DragAccountRow(list, first);
            var point = DragPoint(row, 0.25);
            DragPress(row, shortPointer, point);
            await Task.Delay(80);
            DragRelease(list, shortPointer, point);
            await SettleAsync();
            Check(vm.FilteredItems.Select(item => item.Account.Id).SequenceEqual(original)
                && !DragAccountRow(list, first).Classes.Contains("reorder-preview"),
                "账号短按不触发排序或预览");
        }
        finally { shortPointer.Capture(null); }

        // 长按后移动到第二项下半部，列表必须先实时让位；Escape 恢复而不保存。
        var cancelPointer = new Pointer(79002, PointerType.Mouse, true);
        try
        {
            var source = DragAccountRow(list, first);
            DragPress(source, cancelPointer, DragPoint(source, 0.25));
            await WaitForDragAsync(() => ReferenceEquals(cancelPointer.Captured, list)
                && DragAccountRow(list, first).Classes.Contains("reorder-preview"), "账号长按进入真实拖动状态");
            var target = DragAccountRow(list, second);
            DragMove(list, cancelPointer, DragPoint(target, 0.8));
            await WaitForDragAsync(() => ReferenceEquals(vm.FilteredItems[0], second)
                && ReferenceEquals(vm.FilteredItems[1], first), "长按移动后账号集合实时预览新顺序");
            await SettleAsync();
            var previewCard = DragAccountRow(list, first);
            Check(previewCard.Classes.Contains("reorder-preview") && ReferenceEquals(cancelPointer.Captured, list),
                "移动后源卡片保持预览标记和指针捕获");
            Check(previewCard.Background is ISolidColorBrush background
                && Avalonia.Application.Current!.Resources["PrimarySoftBrush"] is ISolidColorBrush expectedBackground
                && background.Color == expectedBackground.Color,
                "源卡片预览实际使用主色浅背景，未被普通卡片样式覆盖");
            Check(previewCard.BorderBrush is ISolidColorBrush border
                && Avalonia.Application.Current!.Resources["PrimaryBrush"] is ISolidColorBrush expectedBorder
                && border.Color == expectedBorder.Color,
                "源卡片预览实际使用主色描边");
            Check(DragAccountRow(list, second).TranslatePoint(default, list)!.Value.Y
                < DragAccountRow(list, first).TranslatePoint(default, list)!.Value.Y,
                "实时预览实际改变已实现卡片的屏幕排列");
            Check(vm.Items.Select(item => item.Account.Id).SequenceEqual(original),
                "预览期间全量账号顺序尚未提交");
            Capture(_window, "accounts-drag-preview");
            PressEscape(_window);
            await SettleAsync();
            Check(vm.FilteredItems.Select(item => item.Account.Id).SequenceEqual(original)
                && cancelPointer.Captured is null && _window.IsVisible,
                "拖动 Escape 还原账号顺序、释放捕获并保留主窗口");
            await vm.LoadAsync();
            await SettleAsync();
            Check(vm.FilteredItems.Select(item => item.Account.Id).SequenceEqual(original),
                "取消拖动后重载仍为原始顺序");
        }
        finally { cancelPointer.Capture(null); }

        // 源卡片已经移动到鼠标下，松开仍须保存当前预览，不得误判为无目标。
        var commitPointer = new Pointer(79003, PointerType.Mouse, true);
        try
        {
            var source = DragAccountRow(list, first);
            DragPress(source, commitPointer, DragPoint(source, 0.25));
            await WaitForDragAsync(() => ReferenceEquals(commitPointer.Captured, list)
                && DragAccountRow(list, first).Classes.Contains("reorder-preview"), "第二次账号长按可重新进入拖动");
            var target = DragAccountRow(list, second);
            DragMove(list, commitPointer, DragPoint(target, 0.8));
            await WaitForDragAsync(() => ReferenceEquals(vm.FilteredItems[0], second)
                && ReferenceEquals(vm.FilteredItems[1], first), "保存前先展示目标顺序");
            await SettleAsync();
            var preview = vm.FilteredItems.Select(item => item.Account.Id).ToArray();
            var sourceInNewPosition = DragAccountRow(list, first);
            DragRelease(list, commitPointer, DragPoint(sourceInNewPosition, 0.5));
            await WaitForDragAsync(() => vm.Items.Select(item => item.Account.Id).SequenceEqual(preview)
                && vm.CanReorder(ListOrderScope.Accounts, first), "松开源卡片预览位置后提交该顺序");
            await vm.LoadAsync();
            await SettleAsync();
            Check(vm.FilteredItems.Select(item => item.Account.Id).SequenceEqual(preview)
                && commitPointer.Captured is null,
                "拖动提交的顺序可从内存设置重新恢复，指针已释放");
            Capture(_window, "accounts-drag-committed");

            // 恢复同一 Fixture 的原顺序，后续 UI 套件仍使用原演示数据。
            var restore = vm.BeginReorderPreview(ListOrderScope.Accounts, first);
            Check(restore is not null && restore.Move(second, after: false), "排序验证可恢复原演示顺序");
            await restore!.CommitAsync();
            await SettleAsync();
            Check(vm.Items.Select(item => item.Account.Id).SequenceEqual(original), "排序验证结束恢复原账号顺序");
        }
        finally { commitPointer.Capture(null); }

        Check(App.Services is null, "真实拖动路由验证未建立产品服务容器或用户数据库");
    }

    private static Border DragAccountRow(ListBox list, AccountItemViewModel item)
        => list.GetVisualDescendants().OfType<Border>().Single(row => ListReorder.GetIsItem(row)
            && ReferenceEquals(row.DataContext, item)
            && row.GetVisualAncestors().OfType<ItemsControl>().FirstOrDefault() == list);

    private Point DragPoint(Control row, double heightRatio)
        => row.TranslatePoint(new Point(row.Bounds.Width * 0.35, row.Bounds.Height * heightRatio), _window)
            ?? throw new InvalidOperationException("拖动测试卡片不在验证窗口中。");

    private void DragPress(Control row, IPointer pointer, Point point)
        => row.RaiseEvent(new PointerPressedEventArgs(row, pointer, _window, point, DragTimestamp(),
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));

    private void DragMove(Control list, IPointer pointer, Point point)
        => list.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, list, pointer, _window, point,
            DragTimestamp(), new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

    private void DragRelease(Control list, IPointer pointer, Point point)
        => list.RaiseEvent(new PointerReleasedEventArgs(list, pointer, _window, point, DragTimestamp(),
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

    private static ulong DragTimestamp() => (ulong)Environment.TickCount64;

    private async Task WaitForDragAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Check(condition(), message);
    }
}
