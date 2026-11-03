using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;

namespace GitBinder.Desktop.ViewModels;

public partial class ProjectsViewModel
{
    private readonly object _projectOperationSync = new();
    private readonly Dictionary<Guid, ProjectOperation> _projectOperations = [];
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private int _pendingLoads;
    private bool _lastPageOperationBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
    private bool _isProjectRegistrationBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
    [NotifyPropertyChangedFor(nameof(HasPageProgress))]
    private bool _isExternalOperationBusy;

    public bool HasProjectOperations
    {
        get { lock (_projectOperationSync) return _projectOperations.Count > 0; }
    }

    public bool CanPullGroup => CanManageProjects && !HasProjectOperations && _pendingLoads == 0;
    public bool CanManageGroups => CanPullGroup;

    public bool TryBeginExternalOperation()
    {
        lock (_projectOperationSync)
        {
            if (!CanManageGroups) return false;
            IsExternalOperationBusy = true;
            return true;
        }
    }

    public void EndExternalOperation() => IsExternalOperationBusy = false;

    public Task ReloadAfterExternalOperationAsync() => IsExternalOperationBusy
        ? ReloadProjectsAsync(allowPageOperation: true, refreshMetadata: false) : Task.CompletedTask;

    private ProjectOperation? TryBeginProjectOperation(ProjectItemViewModel? item)
    {
        lock (_projectOperationSync)
        {
            if (item is null || !CanManageProjects || !item.CanManageProject || !Items.Contains(item)) return null;
            return TryReserveProject(item.Project.Id, item);
        }
    }

    private ProjectOperation? TryReserveProject(Guid projectId, ProjectItemViewModel? item)
    {
        ProjectOperation operation;
        lock (_projectOperationSync)
        {
            if (_projectOperations.ContainsKey(projectId)) return null;
            operation = new ProjectOperation(this, projectId, item);
            _projectOperations.Add(projectId, operation);
        }
        if (item is not null) item.IsBusy = true;
        NotifyOperationState();
        return operation;
    }

    private void NotifyOperationState()
    {
        OnPropertyChanged(nameof(HasProjectOperations));
        OnPropertyChanged(nameof(HasPendingOperations));
        OnPropertyChanged(nameof(CanPullGroup));
        OnPropertyChanged(nameof(CanManageGroups));
        var pageOperationBusy = !CanManageProjects;
        if (_lastPageOperationBusy != pageOperationBusy)
        {
            _lastPageOperationBusy = pageOperationBusy;
            foreach (var item in Items) item.IsPageOperationBusy = pageOperationBusy;
        }
    }

    partial void OnIsCloneBusyChanged(bool value) => NotifyOperationState();
    partial void OnIsTransferBusyChanged(bool value) => NotifyOperationState();
    partial void OnIsGroupBusyChanged(bool value) => NotifyOperationState();
    partial void OnIsBindingBusyChanged(bool value) => NotifyOperationState();
    partial void OnIsProjectRegistrationBusyChanged(bool value) => NotifyOperationState();
    partial void OnIsExternalOperationBusyChanged(bool value) => NotifyOperationState();

    [RelayCommand]
    private void CancelProjectOperation(ProjectItemViewModel? item)
    {
        if (item is null) return;
        lock (_projectOperationSync)
        {
            if (_projectOperations.TryGetValue(item.Project.Id, out var operation))
                operation.Cancel();
        }
    }

    private async Task RefreshProjectAsync(Guid projectId, bool refreshMetadata = true)
    {
        await _reloadGate.WaitAsync();
        try
        {
            var result = await Task.Run(async () =>
            {
                var accounts = await _accountRepository.GetAllAsync();
                var identity = _effectiveAccountResolver.CaptureDisplaySnapshot(accounts);
                var snapshot = refreshMetadata
                    ? await ReadProjectSnapshotAsync(projectId, accounts, identity)
                    : await ReadStoredProjectSnapshotAsync(projectId, accounts, identity);
                return (Snapshot: snapshot, Accounts: accounts.Where(account => account.Enabled).ToList());
            });
            ApplyProjectSnapshot(result.Snapshot, result.Accounts);
            await ReloadGroupsCoreAsync();
        }
        finally { _reloadGate.Release(); }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshConfigurationAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null || item is null) return;
        item.OperationProgress = _localization.GetString("Projects.RefreshConfigurationRunning");
        var synchronized = false;
        try
        {
            await RefreshProjectAsync(item.Project.Id);
            if (!Items.Contains(item)) return;
            if (item.MetadataReadFailed)
            {
                Feedback = _localization.GetString("PROJECT_CONFIGURATION_READ_FAILED");
                return;
            }
            var result = await Task.Run(() => _projectService.ReadCurrentIdentityAsync(item.Project.Id));
            if (!result.IsSuccess)
            {
                Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
                return;
            }
            var synced = await Task.Run(() => _bindingService.SynchronizeRecordedBindingAsync(item.Project.Id, result.Value!));
            if (!synced.IsSuccess)
            {
                Feedback = _localization.GetString(synced.Error!.Code, synced.Error.Arguments);
                return;
            }
            synchronized = true;
            await RefreshProjectAsync(item.Project.Id, refreshMetadata: false);
        }
        catch (Exception) { Feedback = _localization.GetString(synchronized
            ? "BINDING_REFRESH_RELOAD_FAILED" : "PROJECT_CONFIGURATION_READ_FAILED"); }
    }

    private sealed class ProjectOperation(ProjectsViewModel owner, Guid projectId, ProjectItemViewModel? item) : IDisposable
    {
        private CancellationTokenSource? _cancellation;
        private bool _disposed;

        public CancellationToken EnableCancellation()
        {
            _cancellation = new CancellationTokenSource();
            if (item is not null) item.CanCancelOperation = true;
            return _cancellation.Token;
        }

        public void Cancel()
        {
            _cancellation?.Cancel();
            if (item is not null) item.CanCancelOperation = false;
        }

        public void FinishCancellation()
        {
            if (item is not null) item.CanCancelOperation = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FinishCancellation();
            lock (owner._projectOperationSync) owner._projectOperations.Remove(projectId);
            if (item is not null)
            {
                item.OperationProgress = string.Empty;
                item.IsBusy = false;
            }
            owner.NotifyOperationState();
        }
    }
}

public partial class ProjectItemViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProject))]
    [NotifyPropertyChangedFor(nameof(CanBindAccount))]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    [NotifyPropertyChangedFor(nameof(CanPull))]
    [NotifyPropertyChangedFor(nameof(CanUnbind))]
    [NotifyPropertyChangedFor(nameof(CanAssignGroup))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProject))]
    [NotifyPropertyChangedFor(nameof(CanBindAccount))]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    [NotifyPropertyChangedFor(nameof(CanPull))]
    [NotifyPropertyChangedFor(nameof(CanUnbind))]
    [NotifyPropertyChangedFor(nameof(CanAssignGroup))]
    private bool _isPageOperationBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationProgress))]
    private string _operationProgress = string.Empty;

    [ObservableProperty] private bool _canCancelOperation;

    public bool CanManageProject => !IsBusy && !IsPageOperationBusy
        && !IsPathSaving && !IsSwitchingBinding && !IsAssigningGroup;
    public bool CanUnbind => CanManageProject && HasBinding;
    public bool CanAssignGroup => CanManageProject && !IsAssigningGroup;
    public bool HasOperationProgress => !string.IsNullOrWhiteSpace(OperationProgress);
    public bool IsRestoringSelection { get; private set; }
    internal Account? PendingBindingAccount { get; set; }

    internal void RestorePendingBindingSelection()
    {
        var wasRestoring = IsRestoringSelection;
        IsRestoringSelection = true;
        try
        {
            var requested = PendingBindingAccount ?? BoundAccount;
            SelectedBindingAccount = AvailableAccounts.FirstOrDefault(account => account.Id == requested?.Id);
            OnPropertyChanged(nameof(SelectedBindingAccount));
        }
        finally { IsRestoringSelection = wasRestoring; }
    }

    partial void OnIsPathSavingChanged(bool value) => NotifyRowAvailability();
    partial void OnIsSwitchingBindingChanged(bool value) => NotifyRowAvailability();

    private void NotifyRowAvailability()
    {
        OnPropertyChanged(nameof(CanManageProject));
        OnPropertyChanged(nameof(CanBindAccount));
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(CanPull));
        OnPropertyChanged(nameof(CanUnbind));
        OnPropertyChanged(nameof(CanAssignGroup));
    }

    internal void ApplySnapshot(Project project, Binding? binding, Account? boundAccount,
        Account? effectiveAccount, EffectiveReason effectiveReason, IReadOnlyList<Account> accounts,
        string metadataWarning, bool metadataReadFailed)
    {
        IsRestoringSelection = true;
        try
        {
            Project = project;
            Binding = binding;
            BoundAccount = boundAccount;
            EffectiveAccount = project.IsLocalDirectory ? null : effectiveAccount;
            EffectiveReason = effectiveReason;
            AvailableAccounts = IncludeBoundAccount(accounts, boundAccount);
            // 先让控件切换 ItemsSource，再设置属于新列表的账号实例。
            // 否则 SelectedItem 指向新实例时旧列表仍在使用，会清空选择。
            OnPropertyChanged(nameof(AvailableAccounts));
            MetadataWarning = metadataWarning;
            MetadataReadFailed = metadataReadFailed;
            SelectedBindingAccount = AvailableAccounts.FirstOrDefault(account => account.Id == boundAccount?.Id);
            OnPropertyChanged(nameof(SelectedBindingAccount));
            OnPropertyChanged(string.Empty);
        }
        finally { IsRestoringSelection = false; }
    }
}
