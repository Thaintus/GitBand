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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
    private bool _isProjectRegistrationBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
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
        ? ReloadProjectsAsync(allowPageOperation: true) : Task.CompletedTask;

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
        foreach (var item in Items) item.IsPageOperationBusy = !CanManageProjects;
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

    private async Task RefreshProjectAsync(Guid projectId)
    {
        await _reloadGate.WaitAsync();
        try
        {
            var accounts = await _accountRepository.GetAllAsync();
            await RefreshProjectCoreAsync(projectId, accounts);
            await ReloadGroupsCoreAsync();
        }
        finally { _reloadGate.Release(); }
    }

    private async Task RefreshProjectCoreAsync(Guid projectId, IReadOnlyList<Account> accounts)
    {
        var storedProject = await _repository.GetByIdAsync(projectId);
        var item = Items.FirstOrDefault(current => current.Project.Id == projectId);
        if (storedProject is null)
        {
            if (item is not null) Items.Remove(item);
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            HasItems = Items.Count > 0;
            return;
        }

        var refresh = await _projectService.RefreshMetadataAsync(projectId);
        var project = refresh.IsSuccess ? refresh.Value! : storedProject;
        var binding = await _bindingRepository.GetByProjectIdAsync(projectId);
        var boundAccount = accounts.FirstOrDefault(account => account.Id == binding?.AccountId);
        var selectableAccounts = accounts.Where(account => account.Enabled).ToList();
        var warning = refresh.IsSuccess
            ? (project.IsLocalDirectory ? _localization.GetString("Projects.LocalDirectoryHint")
                : string.IsNullOrWhiteSpace(project.OriginUrl) ? _localization.GetString("Projects.NoOrigin") : string.Empty)
            : _localization.GetString(refresh.Error!.Code, refresh.Error.Arguments)
                + " " + _localization.GetString("Projects.CachedMetadata");
        if (item is null)
        {
            item = new ProjectItemViewModel(project, binding, boundAccount,
                _effectiveAccountResolver.Resolve(project), _effectiveAccountResolver.ResolveReason(project),
                selectableAccounts, _localization);
            Items.Add(item);
        }
        item.IsPageOperationBusy = !CanManageProjects;
        item.ApplySnapshot(project, binding, boundAccount,
            _effectiveAccountResolver.Resolve(project), _effectiveAccountResolver.ResolveReason(project),
            selectableAccounts, warning, !refresh.IsSuccess);
        HasItems = Items.Count > 0;
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
        try { SelectedBindingAccount = PendingBindingAccount ?? BoundAccount; }
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
            AvailableAccounts = accounts;
            MetadataWarning = metadataWarning;
            MetadataReadFailed = metadataReadFailed;
            SelectedBindingAccount = accounts.FirstOrDefault(account => account.Id == boundAccount?.Id);
            OnPropertyChanged(string.Empty);
        }
        finally { IsRestoringSelection = false; }
    }
}
