using GitBinder.Application.Settings;

namespace GitBinder.Desktop.ViewModels;

public partial class ProjectsViewModel : IListOrderHost
{
    private readonly ListOrderService? _listOrder;

    public bool CanReorder(ListOrderScope scope, object item) => _listOrder is not null
        && CanManageGroups && _reloadGate.CurrentCount > 0 && !IsCloneEditorOpen
        && (scope switch
        {
            ListOrderScope.Projects => item is ProjectItemViewModel row && Items.Contains(row)
                && FilteredItems.Contains(row) && !row.IsRemoteEditorOpen && !row.IsPathEditorOpen,
            ListOrderScope.Groups => item is ProjectGroupItemViewModel { Id: not null } group && Groups.Contains(group),
            _ => false,
        });

    public ListOrderPreview? BeginReorderPreview(ListOrderScope scope, object source)
    {
        if (!CanReorder(scope, source)) return null;
        if (scope == ListOrderScope.Groups && source is ProjectGroupItemViewModel group)
            return new ListOrderPreview<ProjectGroupItemViewModel>(Groups, group,
                () => CanReorder(scope, group),
                (original, preview) => SaveGroupOrderAsync(group, original, preview), item => item.Id.HasValue);
        if (source is not ProjectItemViewModel row) return null;
        var all = Items.ToArray();
        var search = SearchText;
        var selectedGroup = SelectedGroup;
        return new ListOrderPreview<ProjectItemViewModel>(FilteredItems, row,
            () => CanReorder(scope, row) && SearchText == search && SelectedGroup == selectedGroup
                && Items.SequenceEqual(all, ReferenceEqualityComparer.Instance),
            (original, preview) => SaveProjectOrderAsync(row, all, original, preview));
    }

    private async Task<bool> SaveProjectOrderAsync(ProjectItemViewModel source,
        IReadOnlyList<ProjectItemViewModel> all, IReadOnlyList<ProjectItemViewModel> original,
        IReadOnlyList<ProjectItemViewModel> preview)
    {
        if (!CanReorder(ListOrderScope.Projects, source)
            || !Items.SequenceEqual(all, ReferenceEqualityComparer.Instance) || !TryBeginExternalOperation()) return false;
        await _reloadGate.WaitAsync();
        try
        {
            var allIds = all.Select(item => item.Project.Id).ToArray();
            var originalIds = original.Select(item => item.Project.Id).ToArray();
            var previewIds = preview.Select(item => item.Project.Id).ToArray();
            var ids = await Task.Run(() => _listOrder!.SaveVisibleAsync(ListOrderScope.Projects, allIds, originalIds, previewIds));
            ListOrdering.Apply(Items, ids, item => item.Project.Id);
            ApplyFilter();
            return true;
        }
        catch (Exception) { ReportReorderFailure(); return false; }
        finally { _reloadGate.Release(); EndExternalOperation(); }
    }

    private async Task<bool> SaveGroupOrderAsync(ProjectGroupItemViewModel source,
        IReadOnlyList<ProjectGroupItemViewModel> original, IReadOnlyList<ProjectGroupItemViewModel> preview)
    {
        if (!CanReorder(ListOrderScope.Groups, source) || !TryBeginExternalOperation()) return false;
        await _reloadGate.WaitAsync();
        try
        {
            var originalIds = original.Where(group => group.Id.HasValue).Select(group => group.Id!.Value).ToArray();
            var previewIds = preview.Where(group => group.Id.HasValue).Select(group => group.Id!.Value).ToArray();
            await Task.Run(() => _listOrder!.SaveVisibleAsync(ListOrderScope.Groups, originalIds, originalIds, previewIds));
            // 分组预览已经显示最终顺序；保存后才同步各项目的下拉框，不改分组归属。
            var ungrouped = new ProjectGroupItemViewModel(null, _localization.GetString("Groups.Ungrouped"), 0, false);
            var choices = new[] { ungrouped }.Concat(Groups.Where(group => group.Id.HasValue)).ToArray();
            foreach (var item in Items) item.SetGroups(choices, item.GroupId);
            return true;
        }
        catch (Exception) { ReportReorderFailure(); return false; }
        finally { _reloadGate.Release(); EndExternalOperation(); }
    }

    public void ReportReorderFailure() => Feedback = _localization.GetString("Common.Reorder.Failed");
}
