using GitBinder.Application.Settings;

namespace GitBinder.Desktop.ViewModels;

public partial class AccountsViewModel : IListOrderHost
{
    private readonly ListOrderService? _listOrder;
    private bool _isOrderSaving;
    private bool _isAccountEditorOpen;

    public bool CanReorder(ListOrderScope scope, object item) => scope == ListOrderScope.Accounts
        && _listOrder is not null && !_isOrderSaving && !_isAccountEditorOpen && _loadGate.CurrentCount > 0
        && (_projects?.CanManageGroups ?? true)
        && item is AccountItemViewModel row && Items.Contains(row) && FilteredItems.Contains(row);

    public ListOrderPreview? BeginReorderPreview(ListOrderScope scope, object source)
    {
        if (!CanReorder(scope, source) || source is not AccountItemViewModel row) return null;
        var all = Items.ToArray();
        var search = SearchText;
        return new ListOrderPreview<AccountItemViewModel>(FilteredItems, row,
            () => CanReorder(scope, row) && SearchText == search
                && Items.SequenceEqual(all, ReferenceEqualityComparer.Instance),
            (original, preview) => SaveOrderAsync(scope, row, all, original, preview));
    }

    private async Task<bool> SaveOrderAsync(ListOrderScope scope, AccountItemViewModel source,
        IReadOnlyList<AccountItemViewModel> all, IReadOnlyList<AccountItemViewModel> original,
        IReadOnlyList<AccountItemViewModel> preview)
    {
        if (!CanReorder(scope, source) || !Items.SequenceEqual(all, ReferenceEqualityComparer.Instance)) return false;
        _isOrderSaving = true;
        await _loadGate.WaitAsync();
        try
        {
            var allIds = all.Select(item => item.Account.Id).ToArray();
            var visibleIds = original.Select(item => item.Account.Id).ToArray();
            var previewIds = preview.Select(item => item.Account.Id).ToArray();
            var ids = await Task.Run(() => _listOrder!.SaveVisibleAsync(scope, allIds, visibleIds, previewIds));
            ListOrdering.Apply(Items, ids, item => item.Account.Id);
            ApplyFilter();
            return true;
        }
        catch (Exception) { ReportReorderFailure(); return false; }
        finally { _loadGate.Release(); _isOrderSaving = false; }
    }

    public void ReportReorderFailure() => Feedback = _localization.GetString("Common.Reorder.Failed");
}
