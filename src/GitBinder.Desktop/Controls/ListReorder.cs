using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitBinder.Application.Settings;
using GitBinder.Desktop.ViewModels;

namespace GitBinder.Desktop.Controls;

/// <summary>鼠标长按卡片的非交互区域后拖动，松开时一次性保存顺序。</summary>
public sealed class ListReorder : AvaloniaObject
{
    public static readonly AttachedProperty<ListOrderScope> ScopeProperty =
        AvaloniaProperty.RegisterAttached<ListReorder, ItemsControl, ListOrderScope>("Scope");
    public static readonly AttachedProperty<bool> IsItemProperty =
        AvaloniaProperty.RegisterAttached<ListReorder, Control, bool>("IsItem");
    private static readonly ConditionalWeakTable<ItemsControl, Session> Sessions = new();

    static ListReorder() => ScopeProperty.Changed.AddClassHandler<ItemsControl>((list, _) =>
    {
        if (GetScope(list) != ListOrderScope.None) Sessions.GetValue(list, control => new Session(control));
        else if (Sessions.TryGetValue(list, out var session)) session.Reset();
    });

    public static ListOrderScope GetScope(ItemsControl control) => control.GetValue(ScopeProperty);
    public static void SetScope(ItemsControl control, ListOrderScope value) => control.SetValue(ScopeProperty, value);
    public static bool GetIsItem(Control control) => control.GetValue(IsItemProperty);
    public static void SetIsItem(Control control, bool value) => control.SetValue(IsItemProperty, value);

    private sealed class Session
    {
        private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);
        private readonly ItemsControl _list;
        private readonly DispatcherTimer _timer = new();
        private IPointer? _pointer;
        private TopLevel? _root;
        private ScrollViewer? _scroll;
        private Control? _sourceRow;
        private object? _source;
        private IListOrderHost? _host;
        private INotifyCollectionChanged? _observedItems;
        private INotifyPropertyChanged? _observedHost;
        private ListOrderPreview? _preview;
        private ListOrderScope _scope;
        private Point _pressPoint;
        private Point _lastPoint;
        private bool _active;
        private bool _dropAllowed;
        private bool _previewUpdateQueued;
        private bool _hasPreviewSample;
        private Point _lastPreviewPoint;
        private Vector _lastPreviewOffset;
        private bool _saving;
        private Cursor? _previousCursor;
        private IDisposable? _toolTipSuppression;

        public Session(ItemsControl list)
        {
            _list = list;
            list.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, true);
            list.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, true);
            list.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel, true);
            list.PointerCaptureLost += CaptureLost;
            list.PointerExited += Exited;
            list.AddHandler(InputElement.PointerWheelChangedEvent, WheelChanged, RoutingStrategies.Tunnel, true);
            list.DetachedFromVisualTree += (_, _) => Reset();
            list.DataContextChanged += (_, _) => Reset();
            list.PropertyChanged += ListPropertyChanged;
            _timer.Tick += Tick;
        }

        private void Pressed(object? sender, PointerPressedEventArgs e)
        {
            if (_saving || e.Pointer.Type != PointerType.Mouse
                || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
            Reset();
            var row = FindRow(e.Source as Visual);
            if (row?.DataContext is not { } item || _list.DataContext is not IListOrderHost host
                || !host.CanReorder(GetScope(_list), item)) return;
            // 不劫持按钮点击、文本编辑、账号/分组选择以及滚动条。
            for (var visual = e.Source as Visual; visual is not null && visual != row; visual = visual.GetVisualParent())
                if (visual is Button or TextBox or SelectingItemsControl or ScrollBar) return;
            _root = TopLevel.GetTopLevel(_list);
            if (_root is null) return;
            _host = host;
            _scope = GetScope(_list);
            _sourceRow = row;
            _source = item;
            _pointer = e.Pointer;
            _pressPoint = _lastPoint = e.GetPosition(_root);
            _root.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel, true);
            _root.AddHandler(InputElement.PointerReleasedEvent, RootReleased, RoutingStrategies.Tunnel, true);
            _root.PropertyChanged += RootPropertyChanged;
            if (_root is Window window) window.Deactivated += Deactivated;
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Start();
        }

        private void Moved(object? sender, PointerEventArgs e)
        {
            if (e.Pointer != _pointer || _root is null) return;
            var point = e.GetPosition(_root);
            var positionChanged = point != _lastPoint;
            _lastPoint = point;
            if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) { Reset(); return; }
            if (!_active)
            {
                var dx = _lastPoint.X - _pressPoint.X;
                var dy = _lastPoint.Y - _pressPoint.Y;
                if (dx * dx + dy * dy > 36) Reset();
                return;
            }
            e.Handled = true;
            if (!positionChanged) return;
            UpdateAutoScrollTimer();
            QueuePreviewUpdate();
        }

        private void Tick(object? sender, EventArgs e)
        {
            if (!ValidateSession()) return;
            if (!_active)
            {
                // 长按等待只触发一次，普通拖动由指针/集合/滚动事件推动，不常驻轮询。
                _timer.Stop();
                _preview = _host!.BeginReorderPreview(_scope, _source!);
                if (_preview is null) { Reset(); return; }
                _active = true;
                _previousCursor = _list.Cursor;
                _list.Cursor = DragCursor;
                SuppressToolTips();
                RefreshSourceRow();
                _scroll = _list.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(scroll => scroll.GetVisualAncestors().OfType<ItemsControl>().FirstOrDefault() == _list)
                    ?? _list.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                if (_scroll is not null) _scroll.ScrollChanged += ScrollChanged;
                _observedItems = _list.ItemsSource as INotifyCollectionChanged;
                if (_observedItems is not null) _observedItems.CollectionChanged += ItemsChanged;
                _observedHost = _host as INotifyPropertyChanged;
                if (_observedHost is not null) _observedHost.PropertyChanged += HostPropertyChanged;
                _pointer!.Capture(_list);
                _timer.Interval = TimeSpan.FromMilliseconds(30);
                if (!_active) return; // 捕获转移可能同步取消本次会话。
                UpdateAutoScrollTimer();
                QueuePreviewUpdate();
                return;
            }

            var step = GetAutoScrollStep();
            if (step == 0 || _scroll is null) { _timer.Stop(); return; }
            var offset = _scroll.Offset;
            _scroll.Offset = new Vector(offset.X, Math.Clamp(offset.Y + step, 0,
                Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
            if (_scroll.Offset != offset)
            {
                // 等滚动后的布局完成再命中目标；多个事件共用同一排队回调。
                QueuePreviewUpdate();
            }
            UpdateAutoScrollTimer();
        }

        private bool ValidateSession()
        {
            if (_source is null || _root is null || _pointer is null || !_list.IsEffectivelyVisible
                || (!_active && (_sourceRow is null || !ReferenceEquals(_sourceRow.DataContext, _source)
                    || !IsOwnedRow(_sourceRow)))
                || (_active && _preview?.IsValid != true)
                || _host?.CanReorder(_scope, _source) != true || GetScope(_list) != _scope)
            {
                Reset();
                return false;
            }
            return true;
        }

        private int GetAutoScrollStep()
        {
            if (!_active || _scroll is null || _root is null
                || _root.TranslatePoint(_lastPoint, _scroll) is not { } point
                || !new Rect(_scroll.Bounds.Size).Contains(point)) return 0;
            var maximum = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            if (point.Y < 32 && _scroll.Offset.Y > 0) return -14;
            if (point.Y > _scroll.Bounds.Height - 32 && _scroll.Offset.Y < maximum) return 14;
            return 0;
        }

        private void UpdateAutoScrollTimer()
        {
            if (!_active) return;
            if (GetAutoScrollStep() == 0) _timer.Stop();
            else if (!_timer.IsEnabled) _timer.Start();
        }

        private void QueuePreviewUpdate()
        {
            if (_previewUpdateQueued) return;
            _previewUpdateQueued = true;
            var preview = _preview;
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_preview, preview)) return;
                _previewUpdateQueued = false;
                try
                {
                    if (!_active || !ValidateSession()) return;
                    RefreshSourceRow();
                    UpdateTarget();
                    UpdateAutoScrollTimer();
                }
                catch (Exception)
                {
                    var host = _host;
                    Reset();
                    host?.ReportReorderFailure();
                }
            }, DispatcherPriority.Loaded);
        }

        private void UpdateTarget(bool move = true)
        {
            _dropAllowed = false;
            if (_preview?.IsValid != true) { Reset(); return; }
            if (_root is null || _root.TranslatePoint(_lastPoint, _list) is not { } point)
                return;
            if (_scroll is not null && (_root.TranslatePoint(_lastPoint, _scroll) is not { } viewportPoint
                || !new Rect(_scroll.Bounds.Size).Contains(viewportPoint))) return;
            var hit = _list.InputHitTest(point) as Visual;
            var row = FindRow(hit);
            // 卡片间的空隙也允许保留当前已展示的位置；滚动条与固定入口仍不可放置。
            if (row is null && _preview.HasChanges && new Rect(_list.Bounds.Size).Contains(point)
                && hit is not null && !hit.GetVisualAncestors().Prepend(hit).OfType<ScrollBar>().Any())
            { _dropAllowed = true; return; }
            if (row?.DataContext is not { } item || _host?.CanReorder(_scope, item) != true
                || _root.TranslatePoint(_lastPoint, row) is not { } rowPoint) return;
            // 源卡片已经移动到预览位置，落在自身上仍然提交当前预览。
            _dropAllowed = true;
            if (!move || ReferenceEquals(item, _source)) return;
            var after = rowPoint.Y >= row.Bounds.Height / 2;
            var offset = _scroll?.Offset ?? default;
            var dx = _lastPoint.X - _lastPreviewPoint.X;
            var dy = _lastPoint.Y - _lastPreviewPoint.Y;
            // 鼠标静止且未滚动时，不能因 Move 后鼠标下换了条目再次移动。
            if (_hasPreviewSample && dx * dx + dy * dy < 9 && offset == _lastPreviewOffset) return;
            _hasPreviewSample = true;
            _lastPreviewPoint = _lastPoint;
            _lastPreviewOffset = offset;
            // 列表让位已经展示落点；不再同时给另一张卡片添加粗插入线。
            // 集合变更会安排布局后的命中更新，保持松开位置与当前预览一致。
            _preview.Move(item, after);
        }

        private void RefreshSourceRow()
        {
            // 缓存仍属于本列表时直接使用；虚拟化回收后只搜索该项已实现的容器。
            var current = _sourceRow is not null && ReferenceEquals(_sourceRow.DataContext, _source)
                && IsOwnedRow(_sourceRow) ? _sourceRow : null;
            if (current is null && _source is not null && _list.ContainerFromItem(_source) is { } container)
            {
                current = GetIsItem(container) && ReferenceEquals(container.DataContext, _source)
                    ? container : container.GetVisualDescendants().OfType<Control>().FirstOrDefault(row =>
                        GetIsItem(row) && ReferenceEquals(row.DataContext, _source) && IsOwnedRow(row));
            }
            if (ReferenceEquals(current, _sourceRow)
                && current?.Classes.Contains("reorder-preview") == true) return;
            _sourceRow?.Classes.Remove("reorder-preview");
            _sourceRow = current;
            _sourceRow?.Classes.Add("reorder-preview");
        }

        private void SuppressToolTips()
        {
            // 只覆盖拖动会话：继承到当前及后续虚拟化生成的卡片，结束后恢复原绑定/样式。
            _toolTipSuppression = _list.SetValue(ToolTip.ServiceEnabledProperty, false, BindingPriority.Animation);
            // 禁用服务会停掉待显示计时器，但已经打开的提示仍需主动关闭。
            foreach (var control in _list.GetVisualDescendants().OfType<Control>().Prepend(_list))
                if (ToolTip.GetIsOpen(control)) control.SetCurrentValue(ToolTip.IsOpenProperty, false);
        }

        private async void Released(object? sender, PointerReleasedEventArgs e)
        {
            if (e.Pointer != _pointer || e.InitialPressMouseButton != MouseButton.Left) return;
            if (!_active) { Reset(); return; }
            e.Handled = true;
            if (_root is not null) _lastPoint = e.GetPosition(_root);
            // 松开时保存最后已展示的顺序，不再额外移动到未经预览的位置。
            UpdateTarget(move: false);
            var host = _host;
            var preview = _dropAllowed ? _preview : null;
            if (preview is not null) _preview = null;
            Reset();
            if (host is null || preview is null) return;
            _saving = true;
            try { await preview.CommitAsync(); }
            catch (Exception) { host.ReportReorderFailure(); }
            finally { _saving = false; }
        }

        private Control? FindRow(Visual? visual)
        {
            for (; visual is not null && visual != _list; visual = visual.GetVisualParent())
                if (visual is Control row && IsOwnedRow(row)) return row;
            return null;
        }

        private bool IsOwnedRow(Control row)
        {
            if (!GetIsItem(row)) return false;
            for (var parent = row.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
                if (parent is ItemsControl owner) return ReferenceEquals(owner, _list);
            return false;
        }

        private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_active) QueuePreviewUpdate();
        }

        private void HostPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_active) QueuePreviewUpdate();
        }

        private void ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!_active || !ReferenceEquals(e.Source, _scroll)) return;
            UpdateAutoScrollTimer();
            QueuePreviewUpdate();
        }

        private void ListPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_pointer is not null && (e.Property == ItemsControl.ItemsSourceProperty
                || (e.Property == Visual.IsVisibleProperty && !_list.IsEffectivelyVisible))) Reset();
        }

        private void RootPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_root?.IsVisible == false || _root is Window { WindowState: WindowState.Minimized }) Reset();
        }

        private void KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _pointer is null) return;
            e.Handled = _active;
            Reset();
        }

        private void RootReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_active && e.Pointer == _pointer) Reset();
        }
        private void CaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_pointer is not null && _pointer.Captured != _list) Reset();
        }
        private void Deactivated(object? sender, EventArgs e) => Reset();
        private void Exited(object? sender, PointerEventArgs e) { if (!_active && e.Source == _list) Reset(); }
        private void WheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_active) e.Handled = true;
            else Reset();
        }

        public void Reset()
        {
            var preview = _preview;
            _preview = null;
            _timer.Stop();
            _sourceRow?.Classes.Remove("reorder-preview");
            if (_active) _list.Cursor = _previousCursor;
            if (_root is not null)
            {
                _root.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
                _root.RemoveHandler(InputElement.PointerReleasedEvent, RootReleased);
                _root.PropertyChanged -= RootPropertyChanged;
                if (_root is Window window) window.Deactivated -= Deactivated;
            }
            if (_scroll is not null) _scroll.ScrollChanged -= ScrollChanged;
            if (_observedItems is not null) _observedItems.CollectionChanged -= ItemsChanged;
            if (_observedHost is not null) _observedHost.PropertyChanged -= HostPropertyChanged;
            _observedItems = null;
            _observedHost = null;
            var pointer = _pointer;
            _pointer = null;
            _root = null;
            _scroll = null;
            _sourceRow = null;
            _source = null;
            _host = null;
            _active = false;
            _dropAllowed = false;
            _previewUpdateQueued = false;
            _hasPreviewSample = false;
            if (pointer?.Captured == _list) pointer.Capture(null);
            _toolTipSuppression?.Dispose();
            _toolTipSuppression = null;
            preview?.Cancel();
        }
    }
}
