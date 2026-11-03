using GitBinder.Application.Settings;

namespace GitBinder.Desktop.ViewModels;

public partial class PlatformsViewModel : IListOrderHost
{
    private readonly ListOrderService? _listOrder;
    private readonly SemaphoreSlim _orderGate = new(1, 1);
    private bool _isOrderSaving;
    private bool _isPlatformOperation;

    public bool CanReorder(ListOrderScope scope, object item) => scope == ListOrderScope.Platforms
        && _listOrder is not null && !_isOrderSaving && !_isPlatformOperation && !IsEditorOpen
        && _orderGate.CurrentCount > 0
        && item is PlatformItemViewModel row && Items.Contains(row) && FilteredItems.Contains(row);

    public ListOrderPreview? BeginReorderPreview(ListOrderScope scope, object source)
    {
        if (!CanReorder(scope, source) || source is not PlatformItemViewModel row) return null;
        var all = Items.ToArray();
        var search = SearchText;
        return new ListOrderPreview<PlatformItemViewModel>(FilteredItems, row,
            () => CanReorder(scope, row) && SearchText == search
                && Items.SequenceEqual(all, ReferenceEqualityComparer.Instance),
            (original, preview) => SaveOrderAsync(scope, row, all, original, preview));
    }

    private async Task<bool> SaveOrderAsync(ListOrderScope scope, PlatformItemViewModel source,
        IReadOnlyList<PlatformItemViewModel> all, IReadOnlyList<PlatformItemViewModel> original,
        IReadOnlyList<PlatformItemViewModel> preview)
    {
        if (!CanReorder(scope, source) || !Items.SequenceEqual(all, ReferenceEqualityComparer.Instance)) return false;
        _isOrderSaving = true;
        await _orderGate.WaitAsync();
        try
        {
            var allIds = all.Select(item => item.Platform.Id).ToArray();
            var visibleIds = original.Select(item => item.Platform.Id).ToArray();
            var previewIds = preview.Select(item => item.Platform.Id).ToArray();
            var ids = await Task.Run(() => _listOrder!.SaveVisibleAsync(scope, allIds, visibleIds, previewIds));
            ListOrdering.Apply(Items, ids, item => item.Platform.Id);
            ApplyFilter();
            return true;
        }
        catch (Exception) { ReportReorderFailure(); return false; }
        finally { _orderGate.Release(); _isOrderSaving = false; }
    }

    public void ReportReorderFailure() => Feedback = _localization.GetString("Common.Reorder.Failed");
}
