using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Settings;
using GitBinder.Application.Projects;

namespace GitBinder.Desktop.ViewModels;

public partial class ProjectsViewModel
{
    private readonly ProjectGroupService _groupService;

    [ObservableProperty] private ObservableCollection<ProjectGroupItemViewModel> _groups = [];
    [ObservableProperty] private ProjectGroupItemViewModel? _selectedGroup;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
    private bool _isGroupBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupPullDetails))]
    private string _groupPullDetails = string.Empty;

    public bool CanManageProjects => !IsTransferBusy && !IsCloneBusy && !IsGroupBusy && !IsBindingBusy
        && !IsProjectRegistrationBusy && !IsExternalOperationBusy;
    public bool HasGroupPullDetails => !string.IsNullOrEmpty(GroupPullDetails);
    partial void OnSelectedGroupChanged(ProjectGroupItemViewModel? value) => ApplyFilter();

    private async Task ReloadGroupsAsync()
    {
        await _reloadGate.WaitAsync();
        try { await ReloadGroupsCoreAsync(); }
        finally { _reloadGate.Release(); }
    }

    private async Task ReloadGroupsCoreAsync()
    {
        var (definitions, membership) = await Task.Run(async () =>
        {
            var all = await _groupService.GetAllAsync();
            if (_listOrder is not null)
                all = await _listOrder.ApplyAsync(ListOrderScope.Groups, all, group => group.Id);
            return (all, await _groupService.GetMembershipsAsync());
        });
        var selectedId = SelectedGroup?.Id;
        var selectedAll = SelectedGroup?.IsAll ?? true;
        // 正在变更分组的条目保留已接受的选择，不能被较早的数据库快照回填
        var visibleMembership = Items.ToDictionary(item => item.Project.Id,
            item => item.IsAssigningGroup ? item.GroupId
                : membership.TryGetValue(item.Project.Id, out var id) ? (Guid?)id : null);
        var groupCounts = new Dictionary<Guid, int>();
        var ungroupedCount = 0;
        foreach (var groupId in visibleMembership.Values)
        {
            if (groupId is not Guid id) { ungroupedCount++; continue; }
            groupCounts[id] = groupCounts.GetValueOrDefault(id) + 1;
        }
        var ungrouped = new ProjectGroupItemViewModel(null, _localization.GetString("Groups.Ungrouped"),
            ungroupedCount, false);
        var groups = new List<ProjectGroupItemViewModel>
        {
            new(null, _localization.GetString("Groups.All"), Items.Count, true),
        };
        if (ungrouped.Count > 0) groups.Add(ungrouped);
        groups.AddRange(definitions.Select(g => new ProjectGroupItemViewModel(g.Id, g.Name,
            groupCounts.GetValueOrDefault(g.Id), false)));
        var choices = new List<ProjectGroupItemViewModel> { ungrouped };
        choices.AddRange(groups.Where(g => g.CanEdit));
        foreach (var item in Items)
            if (!item.IsAssigningGroup) item.SetGroups(choices, visibleMembership[item.Project.Id]);
        ListOrdering.Synchronize(Groups, groups);
        SelectedGroup = Groups.FirstOrDefault(g => g.IsAll == selectedAll && g.Id == selectedId) ?? Groups[0];
        ApplyFilter();
    }

    [RelayCommand]
    private Task NewGroupAsync() => EditGroupAsync(null, string.Empty);

    [RelayCommand]
    private Task RenameGroupAsync(ProjectGroupItemViewModel? group)
    {
        return group?.Id is { } id ? EditGroupAsync(id, group.Name) : Task.CompletedTask;
    }

    private async Task EditGroupAsync(Guid? groupId, string initialName)
    {
        if (!CanManageGroups) return;
        IsGroupBusy = true;
        try
        {
            var saved = await DialogHelper.EditProjectGroupAsync(
                _localization.GetString(groupId is null ? "Groups.New" : "Groups.Rename"), initialName,
                async name =>
                {
                    var result = await _groupService.SaveAsync(groupId, name);
                    return result.IsSuccess ? null
                        : _localization.GetString(result.Error!.Code, result.Error.Arguments.Cast<object>().ToArray());
                });
            if (!saved) return;
            await ReloadGroupsAsync();
            Feedback = _localization.GetString("Groups.Saved");
        }
        catch (Exception) { Feedback = _localization.GetString("GROUP_RELOAD_FAILED"); }
        finally { IsGroupBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync(ProjectGroupItemViewModel? group)
    {
        if (!CanManageGroups || group?.Id is not { } id) return;
        IsGroupBusy = true;
        try
        {
            if (!await DialogHelper.ConfirmAsync(_localization.GetString("Common.Confirm"),
                _localization.GetString("Groups.DeleteConfirm", group.Name), _localization.GetString("Common.Delete"))) return;
            var result = await _groupService.DeleteAsync(id);
            if (!result.IsSuccess) { Feedback = _localization.GetString(result.Error!.Code); return; }
            await ReloadGroupsAsync();
            Feedback = _localization.GetString("Groups.Deleted");
        }
        catch (Exception) { Feedback = _localization.GetString("GROUP_RELOAD_FAILED"); }
        finally { IsGroupBusy = false; }
    }

    public async Task SwitchProjectGroupAsync(ProjectItemViewModel item, ProjectGroupItemViewModel group)
    {
        if (item.IsRestoringSelection) return;
        if (item.IsAssigningGroup) { item.RestorePendingGroupSelection(); return; }
        if (group.Id == item.GroupId) return;
        using var operation = TryBeginProjectOperation(item);
        if (operation is null) { item.RestoreGroup(); return; }
        item.PendingProjectGroup = group;
        item.IsAssigningGroup = true;
        try
        {
            var result = await _groupService.AssignAsync(item.Project.Id, group.Id);
            if (!result.IsSuccess)
            {
                item.RestoreGroup();
                Feedback = _localization.GetString(result.Error!.Code);
                return;
            }
            item.GroupId = group.Id;
            item.RestoreGroup();
            ApplyFilter();
            await ReloadGroupsAsync();
            Feedback = _localization.GetString("Groups.Saved");
        }
        catch (Exception) { item.RestoreGroup(); Feedback = _localization.GetString("GROUP_RELOAD_FAILED"); }
        finally { item.IsAssigningGroup = false; item.PendingProjectGroup = null; }
    }

    [RelayCommand]
    private async Task PullGroupAsync(ProjectGroupItemViewModel? group)
    {
        if (!CanPullGroup || group is null || group.Count == 0) return;
        IsTransferBusy = true;
        var message = string.Empty;
        var transferStarted = false;
        try
        {
            if (!await DialogHelper.ConfirmAsync(_localization.GetString("Common.Confirm"),
                _localization.GetString("Groups.PullConfirm", group.Name, group.Count), _localization.GetString("Projects.Pull"))) return;
            GroupPullDetails = string.Empty;
            _transferCancellation = new CancellationTokenSource();
            transferStarted = true;
            var cancellation = _transferCancellation;
            var progress = new Progress<GroupPullProgress>(p =>
            {
                // 忽略取消或结束后仍排队的进度通知，避免覆盖最终汇总。
                if (_transferCancellation == cancellation)
                    OperationProgress = _localization.GetString("Groups.Progress", p.Index, p.Total, p.ProjectName);
            });
            var result = await _transferService.PullGroupAsync(group.Id, group.IsAll, progress, cancellation.Token);
            if (!result.IsSuccess) message = _localization.GetString(result.Error!.Code);
            else
            {
                var report = result.Value!;
                var summary = _localization.GetString(report.Cancelled ? "Groups.CancelledSummary" : "Groups.Summary",
                    report.Total, report.Succeeded, report.Failed, report.Unfinished);
                GroupPullDetails = string.Join(Environment.NewLine, report.Outcomes.Select(o =>
                    o.ProjectName + "：" + (o.Error is null ? _localization.GetString("Projects.PullSuccess")
                    : _localization.GetString(o.Error.Code, o.Error.Arguments.Cast<object>().ToArray()))));
                message = summary + Environment.NewLine + Environment.NewLine + GroupPullDetails;
            }
        }
        catch (Exception) { message = _localization.GetString("TRANSFER_FAILED"); }
        finally
        {
            _transferCancellation?.Dispose();
            _transferCancellation = null;
            try
            {
                if (transferStarted) await ReloadProjectsAsync(allowPageOperation: true);
            }
            catch (Exception) { message += " " + _localization.GetString("Projects.TransferReloadFailed"); }
            finally
            {
                IsTransferBusy = false;
                OperationProgress = string.Empty;
            }
        }
        Feedback = message;
    }
}

public sealed record ProjectGroupItemViewModel(Guid? Id, string Name, int Count, bool IsAll)
{
    public bool CanEdit => Id.HasValue;
    public bool CanPull => Count > 0;
}

public partial class ProjectItemViewModel
{
    public Guid? GroupId { get; internal set; }
    internal ProjectGroupItemViewModel? PendingProjectGroup { get; set; }
    [ObservableProperty] private IReadOnlyList<ProjectGroupItemViewModel> _groupChoices = [];
    [ObservableProperty] private ProjectGroupItemViewModel? _selectedProjectGroup;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProject))]
    [NotifyPropertyChangedFor(nameof(CanBindAccount))]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    [NotifyPropertyChangedFor(nameof(CanPull))]
    [NotifyPropertyChangedFor(nameof(CanUnbind))]
    [NotifyPropertyChangedFor(nameof(CanAssignGroup))]
    private bool _isAssigningGroup;

    internal void SetGroups(IReadOnlyList<ProjectGroupItemViewModel> choices, Guid? groupId)
    {
        if (GroupId == groupId && GroupChoices.SequenceEqual(choices)) return;
        var wasRestoring = IsRestoringSelection;
        IsRestoringSelection = true;
        try { GroupId = groupId; GroupChoices = choices; RestoreGroup(); }
        finally { IsRestoringSelection = wasRestoring; }
    }

    internal void RestoreGroup()
    {
        var wasRestoring = IsRestoringSelection;
        IsRestoringSelection = true;
        try { SelectedProjectGroup = GroupChoices.FirstOrDefault(g => g.Id == GroupId); }
        finally { IsRestoringSelection = wasRestoring; }
    }

    internal void RestorePendingGroupSelection()
    {
        var wasRestoring = IsRestoringSelection;
        IsRestoringSelection = true;
        try { SelectedProjectGroup = PendingProjectGroup; }
        finally { IsRestoringSelection = wasRestoring; }
    }
}
