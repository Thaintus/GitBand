using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Application.Settings;

namespace GitBinder.Desktop.ViewModels;

public partial class ProjectsViewModel
{
    private const int MetadataRefreshConcurrency = 4;
    private Task? _navigationLoad;

    // 快速切换页签时共用当前加载，不把完整 Git 扫描反复排入队列。
    public Task LoadAsync() => _navigationLoad is { IsCompleted: false }
        ? _navigationLoad : _navigationLoad = ReloadProjectsAsync(allowPageOperation: false);

    /// <summary>启动只读取已登记信息，不启动 Git、不自动覆盖仓库配置或外部改动。</summary>
    public Task LoadStoredAsync() => ReloadProjectsAsync(allowPageOperation: false, refreshMetadata: false);

    private async Task ReloadProjectsAsync(bool allowPageOperation, bool refreshMetadata = true)
    {
        if (!allowPageOperation && !CanManageProjects) return;
        _pendingLoads++;
        NotifyOperationState();
        await _reloadGate.WaitAsync();
        try
        {
            var existingIds = Items.Select(item => item.Project.Id).ToHashSet();
            // SQLite 的异步接口仍会同步打开连接；账号解析器也会同步读库。
            // 后台只读取数据，集合、占用和属性通知始终在调用方 UI 上下文更新。
            var stored = await Task.Run(async () =>
            {
                var projects = await _repository.GetAllAsync();
                if (_listOrder is not null)
                    projects = await _listOrder.ApplyAsync(ListOrderScope.Projects, projects, project => project.Id);
                var accounts = await _accountRepository.GetAllAsync();
                var identity = _effectiveAccountResolver.CaptureDisplaySnapshot(accounts);
                var newProjects = projects.Where(project => !refreshMetadata || !existingIds.Contains(project.Id)).ToList();
                var snapshots = new List<ProjectSnapshot>();
                if (newProjects.Count > 0)
                {
                    var bindings = (await _bindingRepository.GetAllAsync()).ToDictionary(binding => binding.ProjectId);
                    foreach (var project in newProjects)
                        snapshots.Add(CreateProjectSnapshot(project, bindings.GetValueOrDefault(project.Id), accounts, identity));
                }
                return (Projects: projects, Accounts: accounts, NewSnapshots: snapshots, Identity: identity);
            });

            var selectableAccounts = stored.Accounts.Where(account => account.Enabled).ToList();
            CloneAccounts = selectableAccounts;
            SelectedCloneAccount = selectableAccounts.FirstOrDefault(a => a.Id == SelectedCloneAccount?.Id)
                ?? selectableAccounts.FirstOrDefault(a => a.IsDefault) ?? selectableAccounts.FirstOrDefault();
            foreach (var snapshot in stored.NewSnapshots)
            {
                // 已有行由下面的新读取刷新，不用较早的数据库快照覆盖编辑或操作结果。
                var existing = Items.FirstOrDefault(item => item.Project.Id == snapshot.Id);
                if (refreshMetadata && existing is not null) continue;
                using var reservation = TryReserveProject(snapshot.Id, existing);
                if (reservation is not null) ApplyProjectSnapshot(snapshot, selectableAccounts);
            }
            ListOrdering.Apply(Items, stored.Projects.Select(project => project.Id).ToArray(), item => item.Project.Id);
            HasItems = Items.Count > 0;
            // 首屏只等待本地存储读取，不等待任何 Git 进程。
            await ReloadGroupsCoreAsync();

            // 身份应用已完成后的展示同步只读缓存，不为 Git 元数据扫描延长全页写入门禁。
            if (!refreshMetadata) return;

            foreach (var batch in stored.Projects.Chunk(MetadataRefreshConcurrency))
            {
                var pending = new List<(ProjectOperation Reservation, Task<ProjectSnapshot> Read)>();
                try
                {
                    foreach (var project in batch)
                    {
                        var item = Items.FirstOrDefault(current => current.Project.Id == project.Id);
                        var reservation = TryReserveProject(project.Id, item);
                        if (reservation is null) continue;
                        var cached = item is null
                            ? new ProjectSnapshot(project.Id, project, null, null, null, EffectiveReason.Default)
                            : new ProjectSnapshot(project.Id, item.Project, item.Binding, item.BoundAccount,
                                item.EffectiveAccount, item.EffectiveReason);
                        pending.Add((reservation, Task.Run(async () =>
                        {
                            try { return await ReadProjectSnapshotAsync(project.Id, stored.Accounts, stored.Identity); }
                            catch (Exception)
                            {
                                // 单个仓库或账号读取异常只影响该行，其余仓库继续刷新。
                                return cached with { MetadataError = new DomainError("PROJECT_METADATA_READ_FAILED") };
                            }
                        })));
                    }
                    var snapshots = await Task.WhenAll(pending.Select(entry => entry.Read));
                    foreach (var snapshot in snapshots) ApplyProjectSnapshot(snapshot, selectableAccounts);
                    ApplyFilter();
                }
                finally
                {
                    foreach (var entry in pending) entry.Reservation.Dispose();
                }
            }

            var storedIds = stored.Projects.Select(project => project.Id).ToHashSet();
            foreach (var item in Items.Where(item => !storedIds.Contains(item.Project.Id)).ToList())
            {
                using var reservation = TryReserveProject(item.Project.Id, item);
                if (reservation is null) continue;
                // 列表快照之后可能刚登记了项目，移除前重新确认。
                if (await Task.Run(() => _repository.GetByIdAsync(item.Project.Id)) is not null) continue;
                Items.Remove(item);
                item.Dispose();
                if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            }
            HasItems = Items.Count > 0;
            await ReloadGroupsCoreAsync();
        }
        finally
        {
            _reloadGate.Release();
            _pendingLoads--;
            NotifyOperationState();
        }
    }

    private async Task<ProjectSnapshot> ReadProjectSnapshotAsync(Guid projectId, IReadOnlyList<Account> accounts,
        EffectiveAccountResolver.DisplaySnapshot identity)
    {
        var refresh = await _projectService.RefreshMetadataAsync(projectId);
        // 正常路径直接使用服务的新快照；仅失败时回读缓存，避免每项重复查项目。
        var project = refresh.IsSuccess ? refresh.Value : await _repository.GetByIdAsync(projectId);
        if (project is null) return new ProjectSnapshot(projectId, null, null, null, null, EffectiveReason.Default);
        var binding = await _bindingRepository.GetByProjectIdAsync(projectId);
        return CreateProjectSnapshot(project, binding, accounts, identity, refresh.Error);
    }

    private async Task<ProjectSnapshot> ReadStoredProjectSnapshotAsync(Guid projectId, IReadOnlyList<Account> accounts,
        EffectiveAccountResolver.DisplaySnapshot identity)
    {
        var project = await _repository.GetByIdAsync(projectId);
        if (project is null) return new(projectId, null, null, null, null, EffectiveReason.Default);
        return CreateProjectSnapshot(project, await _bindingRepository.GetByProjectIdAsync(projectId), accounts, identity);
    }

    private ProjectSnapshot CreateProjectSnapshot(Project project, Binding? binding,
        IReadOnlyList<Account> accounts, EffectiveAccountResolver.DisplaySnapshot identity, DomainError? metadataError = null)
    {
        var effective = identity.Resolve(project, binding?.AccountId);
        return new(project.Id, project, binding, accounts.FirstOrDefault(account => account.Id == binding?.AccountId),
            project.IsLocalDirectory ? null : effective.Account, effective.Reason, metadataError);
    }

    private void ApplyProjectSnapshot(ProjectSnapshot snapshot, IReadOnlyList<Account> selectableAccounts)
    {
        var item = Items.FirstOrDefault(current => current.Project.Id == snapshot.Id);
        if (snapshot.Project is not { } project)
        {
            if (item is not null) { Items.Remove(item); item.Dispose(); }
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            HasItems = Items.Count > 0;
            return;
        }
        var warning = snapshot.MetadataError is { } error
            ? _localization.GetString(error.Code, error.Arguments) + " " + _localization.GetString("Projects.CachedMetadata")
            : project.IsLocalDirectory ? _localization.GetString("Projects.LocalDirectoryHint")
                : string.IsNullOrWhiteSpace(project.OriginUrl) ? _localization.GetString("Projects.NoOrigin") : string.Empty;
        if (item is null)
        {
            item = new ProjectItemViewModel(project, snapshot.Binding, snapshot.BoundAccount,
                snapshot.EffectiveAccount, snapshot.EffectiveReason, selectableAccounts, _localization);
            Items.Add(item);
        }
        item.IsPageOperationBusy = !CanManageProjects;
        item.ApplySnapshot(project, snapshot.Binding, snapshot.BoundAccount,
            snapshot.EffectiveAccount, snapshot.EffectiveReason, selectableAccounts, warning, snapshot.MetadataError is not null);
        HasItems = Items.Count > 0;
    }

    private sealed record ProjectSnapshot(Guid Id, Project? Project, Binding? Binding, Account? BoundAccount,
        Account? EffectiveAccount, EffectiveReason EffectiveReason, DomainError? MetadataError = null);
}
