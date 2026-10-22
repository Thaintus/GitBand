using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Projects;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;

namespace GitBinder.Desktop.ViewModels;

/// <summary>
/// 项目页 ViewModel。
/// </summary>
public partial class ProjectsViewModel : ViewModelBase
{
    private readonly IProjectRepository _repository;
    private readonly ProjectService _projectService;
    private readonly IBindingRepository _bindingRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly BindingService _bindingService;
    private readonly EffectiveAccountResolver _effectiveAccountResolver;
    private readonly ILocalizationService _localization;
    private readonly DirectoryPickerDelegate _directoryPicker;
    private readonly ProjectTransferService _transferService;
    private readonly Func<string, string, string, Task<bool>> _confirmProtocolChange;
    private readonly Func<ProjectsViewModel, Task> _showCloneDialog;
    private readonly IShellService? _shellService;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
    private bool _isBindingBusy;
    private CancellationTokenSource? _transferCancellation;
    private string _suggestedCloneFolderName = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationProgress))]
    [NotifyPropertyChangedFor(nameof(HasPageProgress))]
    private string _operationProgress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPageProgress))]
    private bool _isCloneEditorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
    private bool _isCloneBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloneFeedback))]
    private string _cloneFeedback = string.Empty;

    public bool HasCloneFeedback => !string.IsNullOrWhiteSpace(CloneFeedback);
    public bool HasOperationProgress => !string.IsNullOrWhiteSpace(OperationProgress);
    public bool HasPageProgress => !IsCloneEditorOpen && (IsTransferBusy || HasOperationProgress);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageProjects))]
    [NotifyPropertyChangedFor(nameof(HasPageProgress))]
    private bool _isTransferBusy;

    [ObservableProperty]
    private string _cloneRemoteUrl = string.Empty;

    [ObservableProperty]
    private string _cloneParentDirectory = string.Empty;

    [ObservableProperty]
    private string _cloneFolderName = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<Account> _cloneAccounts = [];

    [ObservableProperty]
    private Account? _selectedCloneAccount;

    partial void OnCloneRemoteUrlChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(CloneFolderName) && CloneFolderName != _suggestedCloneFolderName) return;
        if (!GitTransferRemote.TryParse(value, out _)) return;
        var name = value.Trim().TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        if (name.StartsWith("git@", StringComparison.Ordinal)) name = name[(name.IndexOf(':') + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!name.Contains(':') && !name.Contains('@'))
        {
            _suggestedCloneFolderName = name;
            CloneFolderName = name;
        }
    }

    public ObservableCollection<ProjectItemViewModel> Items { get; } = [];

    /// <summary>供主窗口 Escape 关闭保护查询；包括传输结束后的登记/刷新收尾。</summary>
    public bool HasPendingOperations => !CanManageProjects || HasProjectOperations || _pendingLoads > 0
        || Items.Any(item => item.IsBusy || item.IsPathSaving || item.IsSwitchingBinding || item.IsAssigningGroup);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _hasItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    [NotifyPropertyChangedFor(nameof(ProjectCountLabel))]
    private IReadOnlyList<ProjectItemViewModel> _filteredItems = [];

    [ObservableProperty]
    private ProjectItemViewModel? _selectedItem;

    private string _feedback = string.Empty;
    public string Feedback
    {
        get => _feedback;
        set => SetNotice(ref _feedback, value);
    }

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public bool HasNoMatches => HasItems && FilteredItems.Count == 0;

    public string ProjectCountLabel => _localization.GetString("Projects.VisibleCount", FilteredItems.Count, Items.Count);

    public ProjectsViewModel(
        IProjectRepository repository,
        ProjectService projectService,
        IBindingRepository bindingRepository,
        IAccountRepository accountRepository,
        BindingService bindingService,
        EffectiveAccountResolver effectiveAccountResolver,
        ILocalizationService localization,
        DirectoryPickerDelegate directoryPicker,
        ProjectTransferService transferService,
        ProjectGroupService groupService,
        Func<string, string, string, Task<bool>>? confirmProtocolChange = null,
        Func<ProjectsViewModel, Task>? showCloneDialog = null,
        IShellService? shellService = null)
    {
        _repository = repository;
        _projectService = projectService;
        _bindingRepository = bindingRepository;
        _accountRepository = accountRepository;
        _bindingService = bindingService;
        _effectiveAccountResolver = effectiveAccountResolver;
        _localization = localization;
        _directoryPicker = directoryPicker;
        _transferService = transferService;
        _groupService = groupService;
        _confirmProtocolChange = confirmProtocolChange ?? DialogHelper.ConfirmAsync;
        _showCloneDialog = showCloneDialog ?? (async viewModel =>
        {
            await DialogHelper.ShowDialogAsync<bool>(new Views.CloneRepositoryDialog(viewModel));
        });
        _shellService = shellService;
    }

    public Task LoadAsync() => ReloadProjectsAsync(allowPageOperation: false);

    private async Task ReloadProjectsAsync(bool allowPageOperation)
    {
        if (!allowPageOperation && !CanManageProjects) return;
        _pendingLoads++;
        NotifyOperationState();
        await _reloadGate.WaitAsync();
        try
        {
            var projects = await _repository.GetAllAsync();
            var accounts = await _accountRepository.GetAllAsync();
            var selectableAccounts = accounts.Where(account => account.Enabled).ToList();
            CloneAccounts = selectableAccounts;
            SelectedCloneAccount = selectableAccounts.FirstOrDefault(a => a.Id == SelectedCloneAccount?.Id)
                ?? selectableAccounts.FirstOrDefault(a => a.IsDefault) ?? selectableAccounts.FirstOrDefault();
            foreach (var storedProject in projects)
            {
                var item = Items.FirstOrDefault(current => current.Project.Id == storedProject.Id);
                using var reservation = TryReserveProject(storedProject.Id, item);
                if (reservation is null) continue;
                await RefreshProjectCoreAsync(storedProject.Id, accounts);
            }
            var storedIds = projects.Select(project => project.Id).ToHashSet();
            foreach (var item in Items.Where(item => !storedIds.Contains(item.Project.Id)).ToList())
            {
                using var reservation = TryReserveProject(item.Project.Id, item);
                if (reservation is null) continue;
                // 列表快照之后可能刚登记了项目，移除前重新确认
                if (await _repository.GetByIdAsync(item.Project.Id) is not null) continue;
                Items.Remove(item);
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

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>仅筛选已载入的项目，复用条目以保留地址编辑与账号选择状态。</summary>
    private void ApplyFilter()
    {
        FilteredItems = Items.Where(item => (SelectedGroup is null || SelectedGroup.IsAll || item.GroupId == SelectedGroup.Id)
            && ListSearch.Matches(
            SearchText,
            item.Name,
            item.PathText,
            item.OriginUrlDisplay,
            item.RemoteHost,
            item.ProtocolText,
            item.BoundAccount?.DisplayAlias,
            item.EffectiveAccount?.DisplayAlias)).ToList();
    }

    [RelayCommand]
    private async Task OpenCloneAsync()
    {
        if (IsCloneEditorOpen || !CanManageProjects) return;
        CloneFeedback = string.Empty;
        IsCloneEditorOpen = true;
        try { await _showCloneDialog(this); }
        catch (Exception) { Feedback = _localization.GetString("Projects.CloneDialogFailed"); }
        finally { IsCloneEditorOpen = false; }
    }

    [RelayCommand]
    private void CloseClone()
    {
        if (!IsCloneBusy && !IsTransferBusy) IsCloneEditorOpen = false;
    }

    [RelayCommand]
    private async Task BrowseCloneDirectoryAsync()
    {
        if (IsCloneBusy) return;
        try
        {
            var path = await _directoryPicker();
            if (!string.IsNullOrWhiteSpace(path)) CloneParentDirectory = path;
        }
        catch (Exception) { CloneFeedback = _localization.GetString("CLONE_PARENT_INVALID"); }
    }

    [RelayCommand]
    private void CancelTransfer() => _transferCancellation?.Cancel();

    [RelayCommand]
    private async Task CloneAsync()
    {
        if (IsCloneBusy || !CanManageProjects) return;
        CloneFeedback = string.Empty;
        if (SelectedCloneAccount is null)
        {
            CloneFeedback = _localization.GetString("TRANSFER_ACCOUNT_INVALID");
            return;
        }
        var selected = SelectedCloneAccount;
        IsCloneBusy = true;
        IsTransferBusy = true;
        GroupPullDetails = string.Empty;
        _transferCancellation = new CancellationTokenSource();
        var succeeded = false;
        var message = string.Empty;
        try
        {
            OperationProgress = _localization.GetString("Projects.TransferRunning",
                _effectiveAccountResolver.ResolveForClone(selected)?.DisplayAlias ?? string.Empty);
            var result = await _transferService.CloneAsync(CloneRemoteUrl, CloneParentDirectory, CloneFolderName,
                selected.Id, _transferCancellation.Token);
            succeeded = result.IsSuccess;
            message = succeeded ? _localization.GetString("Projects.CloneSuccess")
                : _localization.GetString(result.Error!.Code, result.Error.Arguments.Cast<object>().ToArray());
        }
        catch (Exception)
        {
            // 不显示原始异常，避免 Git/路径/认证异常中的敏感信息进入弹窗。
            message = _localization.GetString(_transferCancellation.IsCancellationRequested ? "TRANSFER_CANCELLED" : "TRANSFER_FAILED");
        }
        finally
        {
            _transferCancellation.Dispose();
            _transferCancellation = null;
            IsTransferBusy = false;
        }

        // 失败也可能已完成克隆或登记；刷新列表，但不清理任何本地目录。
        // IsCloneBusy 延续到刷新结束，避免这个窗口关闭或重复提交。
        try { await ReloadProjectsAsync(allowPageOperation: true); }
        catch (Exception) { message += " " + _localization.GetString("Projects.TransferReloadFailed"); }
        finally
        {
            OperationProgress = string.Empty;
            IsCloneBusy = false;
        }
        if (succeeded)
        {
            CloneRemoteUrl = string.Empty;
            CloneFolderName = string.Empty;
            IsCloneEditorOpen = false;
            Feedback = message;
        }
        else CloneFeedback = message;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PullAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null || item is null) return;
        if (!EnsureRepositoryAvailable(item, requireRemote: false)) return;
        var cancellationToken = operation.EnableCancellation();
        string message;
        try
        {
            item.OperationProgress = _localization.GetString("Projects.TransferRunning",
                _effectiveAccountResolver.Resolve(item.Project)?.DisplayAlias ?? string.Empty);
            // 拉取目标可能是非 origin 跟踪远程，不能预先改写 origin 来匹配账号协议
            var result = await _transferService.PullAsync(item.Project.Id, cancellationToken);
            message = result.IsSuccess ? _localization.GetString("Projects.PullSuccess")
                : _localization.GetString(result.Error!.Code, result.Error.Arguments.Cast<object>().ToArray());
        }
        catch (Exception) { message = _localization.GetString(cancellationToken.IsCancellationRequested ? "TRANSFER_CANCELLED" : "TRANSFER_FAILED"); }
        finally { operation.FinishCancellation(); }
        try { await RefreshProjectAsync(item.Project.Id); }
        catch (Exception) { message += " " + _localization.GetString("Projects.TransferReloadFailed"); }
        Feedback = item.Name + "：" + message;
    }

    /// <summary>选择并登记本地项目目录，尚未初始化 Git 的项目也可先登记</summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanManageProjects) return;
        IsProjectRegistrationBusy = true;
        Feedback = string.Empty;
        try
        {
            var path = await _directoryPicker();
            if (string.IsNullOrWhiteSpace(path)) return;
            var result = await _projectService.AddAsync(path);
            if (result.IsSuccess)
            {
                await ReloadProjectsAsync(allowPageOperation: true);
            }
            else
            {
                Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
            }
        }
        catch (Exception)
        {
            Feedback = _localization.GetString("PROJECT_ADD_FAILED");
        }
        finally { IsProjectRegistrationBusy = false; }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RemoveAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null || item is null) return;
        try
        {
            var confirmed = await DialogHelper.ConfirmAsync(
                _localization.GetString("Common.Confirm"),
                _localization.GetString("Projects.Remove.Confirm", item.Name),
                _localization.GetString("Common.Delete"));
            if (!confirmed) return;
            var result = await _projectService.RemoveAsync(item.Project.Id);
            if (result.IsSuccess)
            {
                Feedback = string.Empty;
                await RefreshProjectAsync(item.Project.Id);
            }
            else Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
        catch (Exception) { Feedback = _localization.GetString("PROJECT_REMOVE_FAILED"); }
    }

    [RelayCommand]
    private void StartEditRemote(ProjectItemViewModel? item)
    {
        if (item?.CanManageProject == true && CanManageProjects) item.BeginRemoteEdit();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveRemoteAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null || item is null) return;
        var editingUrl = item.EditingOriginUrl ?? string.Empty;
        if (item.IsLocalDirectory)
        {
            var normalizedUrl = editingUrl.Trim();
            if (string.IsNullOrWhiteSpace(normalizedUrl))
            {
                Feedback = _localization.GetString("PROJECT_REMOTE_URL_REQUIRED");
                return;
            }
            if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(uri.UserInfo))
            {
                Feedback = _localization.GetString("PROJECT_REMOTE_CREDENTIAL_FORBIDDEN");
                return;
            }
            if (!GitTransferRemote.TryParse(normalizedUrl, out _))
            {
                Feedback = _localization.GetString("PROJECT_REMOTE_URL_INVALID");
                return;
            }
        }

        var remoteSaved = false;
        string? saveFailureFeedback = null;
        try
        {
            if (!await ConfirmRepositoryInitializationAsync(item)) return;
            var result = await _projectService.UpdateOriginUrlAsync(item.Project.Id, editingUrl);
            remoteSaved = result.IsSuccess || result.Error?.Code == "PROJECT_REMOTE_SAVED_BINDING_APPLY_FAILED";
            if (!result.IsSuccess)
                saveFailureFeedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
            // 写入失败也可能已成功初始化，刷新状态但保留地址输入供用户修正
            await RefreshProjectAsync(item.Project.Id);
            if (result.IsSuccess)
            {
                item.CancelRemoteEdit();
                Feedback = string.Empty;
            }
            else
            {
                var refreshedItem = Items.FirstOrDefault(current => current.Project.Id == item.Project.Id);
                if (refreshedItem is not null)
                {
                    refreshedItem.BeginRemoteEdit();
                    refreshedItem.EditingOriginUrl = editingUrl;
                }
                Feedback = saveFailureFeedback!;
            }
        }
        catch (Exception)
        {
            var refreshedItem = Items.FirstOrDefault(current => current.Project.Id == item.Project.Id) ?? item;
            refreshedItem.BeginRemoteEdit();
            refreshedItem.EditingOriginUrl = editingUrl;
            Feedback = saveFailureFeedback
                ?? _localization.GetString(remoteSaved ? "Projects.RemoteReloadFailed" : "PROJECT_REMOTE_SAVE_FAILED");
        }
    }

    [RelayCommand]
    private void CancelEditRemote(ProjectItemViewModel? item)
    {
        if (item?.CanManageProject == true) item.CancelRemoteEdit();
    }

    [RelayCommand]
    private void SwitchRemoteProtocol(ProjectItemViewModel? item)
    {
        if (item?.CanManageProject != true)
        {
            return;
        }

        if (item.TrySwitchEditingOriginProtocol())
        {
            Feedback = string.Empty;
            return;
        }

        Feedback = _localization.GetString("Projects.ProtocolSwitch.Unsupported");
    }

    [RelayCommand]
    private void OpenDirectory(ProjectItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            var path = item.Project.RepositoryPath;
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            {
                Feedback = _localization.GetString("PROJECT_DIRECTORY_NOT_FOUND", path);
                return;
            }
            if (_shellService is null) throw new InvalidOperationException("Shell service unavailable");
            _shellService.OpenDirectory(Path.GetFullPath(path));
            Feedback = string.Empty;
        }
        catch (Exception) { Feedback = _localization.GetString("PROJECT_OPEN_DIRECTORY_FAILED"); }
    }

    [RelayCommand]
    private void StartEditPath(ProjectItemViewModel? item)
    {
        if (item?.CanManageProject != true || !CanManageProjects) return;
        item.EditingPath = item.PathText;
        item.IsPathEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEditPath(ProjectItemViewModel? item)
    {
        if (item?.CanManageProject != true) return;
        item.EditingPath = item.PathText;
        item.IsPathEditorOpen = false;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BrowsePathAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null || item is null) return;
        try
        {
            var path = await _directoryPicker();
            if (!string.IsNullOrWhiteSpace(path)) item.EditingPath = path;
        }
        catch (Exception)
        {
            Feedback = _localization.GetString("PROJECT_PATH_UPDATE_FAILED");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SavePathAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null || item is null) return;
        item.IsPathSaving = true;
        try
        {
            var newPath = item.EditingPath.Trim();
            var confirmed = await DialogHelper.ConfirmAsync(
                _localization.GetString("Common.Confirm"),
                _localization.GetString("Projects.PathConfirm", item.PathText, newPath),
                _localization.GetString("Projects.SavePath"));
            if (!confirmed) return;

            var result = await _projectService.UpdatePathAsync(item.Project.Id, newPath);
            if (result.IsSuccess)
            {
                await RefreshProjectAsync(item.Project.Id);
                item.EditingPath = item.PathText;
                item.IsPathEditorOpen = false;
                Feedback = _localization.GetString("Projects.PathUpdated");
            }
            else
            {
                Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
            }
        }
        catch (Exception)
        {
            Feedback = _localization.GetString("PROJECT_PATH_UPDATE_FAILED");
        }
        finally
        {
            item.IsPathSaving = false;
        }
    }

    /// <summary>账号下拉选择后立即创建或修改项目绑定。</summary>
    public async Task SwitchBindingAccountAsync(ProjectItemViewModel item, Account account)
    {
        if (item.IsRestoringSelection) return;
        if (item.IsSwitchingBinding) { item.RestorePendingBindingSelection(); return; }
        if (account.Id == item.BoundAccount?.Id) return;
        using var operation = TryBeginProjectOperation(item);
        if (operation is null) { item.RestoreBoundAccountSelection(); return; }
        item.PendingBindingAccount = account;
        item.IsSwitchingBinding = true;
        var bindingSaved = false;
        var initializedForBinding = false;
        try
        {
            var hasRemote = item.HasRemote;
            if (item.IsLocalDirectory)
            {
                if (!await ConfirmRepositoryInitializationAsync(item))
                {
                    item.RestoreBoundAccountSelection();
                    return;
                }
                var initialization = await _projectService.InitializeRepositoryAsync(item.Project.Id);
                if (!initialization.IsSuccess)
                {
                    item.RestoreBoundAccountSelection();
                    await RefreshProjectAsync(item.Project.Id);
                    Feedback = _localization.GetString(initialization.Error!.Code, initialization.Error.Arguments);
                    return;
                }
                initializedForBinding = true;
                hasRemote = !string.IsNullOrWhiteSpace(initialization.Value!.OriginUrl);
            }
            item.RestorePendingBindingSelection();
            var result = await _bindingService.BindAsync(item.Project.Id, account.Id);
            if (result.IsSuccess)
            {
                bindingSaved = true;
                Feedback = string.Empty;
                // 先保留用户已选择的绑定；地址协议变更必须再次确认，取消不撤销绑定。
                // 本地 Git 仓库尚无远程地址时，提交身份仍可绑定，无需发起协议切换
                if (hasRemote) await EnsureCompatibleProtocolAsync(item.Project.Id);
                await RefreshProjectAsync(item.Project.Id);
            }
            else
            {
                item.RestoreBoundAccountSelection();
                if (initializedForBinding) await RefreshProjectAsync(item.Project.Id);
                Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
            }
        }
        catch (Exception)
        {
            if (!bindingSaved) item.RestoreBoundAccountSelection();
            if (initializedForBinding && !bindingSaved)
            {
                // 绑定异常不会撤销已初始化的仓库，刷新失败也保留原绑定错误提示
                try { await RefreshProjectAsync(item.Project.Id); }
                catch (Exception) { }
            }
            Feedback = _localization.GetString(bindingSaved ? "Projects.BindingReloadFailed" : "BINDING_APPLY_FAILED");
        }
        finally
        {
            item.IsSwitchingBinding = false;
            item.PendingBindingAccount = null;
        }
    }

    /// <summary>从项目卡片直接解除账号绑定。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task UnbindAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null || item?.Binding is null) return;
        try
        {
            var result = await _bindingService.UnbindAsync(item.Project.Id);
            if (result.IsSuccess)
            {
                Feedback = string.Empty;
                await RefreshProjectAsync(item.Project.Id);
            }
            else Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
        catch (Exception) { Feedback = _localization.GetString("BINDING_UNBIND_FAILED"); }
    }

    /// <summary>使用当前项目的生效账号测试远程认证。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestBindingAsync(ProjectItemViewModel? item)
    {
        using var operation = TryBeginProjectOperation(item);
        if (operation is null) return;
        if (item is not null && !EnsureRepositoryAvailable(item, requireRemote: true)) return;
        if (item?.Binding is null)
        {
            Feedback = _localization.GetString("Projects.BindingRequired");
            return;
        }

        try
        {
            if (!await EnsureCompatibleProtocolAsync(item.Project.Id)) return;
            var cancellationToken = operation.EnableCancellation();
            item.OperationProgress = _localization.GetString("Test.Running");
            var result = await _bindingService.TestAsync(item.Binding, cancellationToken);
            operation.FinishCancellation();
            Feedback = item.Name + "：" + (result.Success
                ? _localization.GetString("Test.Success") + $"（{result.Duration.TotalMilliseconds:0} ms）"
                : _localization.GetString("Test.Failed") + "\n" + (result.Error is { } error
                    ? _localization.GetString(error.Code, error.Arguments.Cast<object>().ToArray())
                    : _localization.GetString("TEST_FAILED")));
        }
        catch (OperationCanceledException) { Feedback = item.Name + "：" + _localization.GetString("TRANSFER_CANCELLED"); }
        catch (Exception) { Feedback = item.Name + "：" + _localization.GetString("Test.Failed"); }
        finally { operation.FinishCancellation(); }
    }

    private Task<bool> ConfirmRepositoryInitializationAsync(ProjectItemViewModel item)
        => item.IsLocalDirectory
            ? _confirmProtocolChange(_localization.GetString("Projects.InitializeGitConfirmTitle"),
                _localization.GetString("Projects.InitializeGitConfirmMessage", item.Project.RepositoryPath),
                _localization.GetString("Projects.InitializeGitConfirm"))
            : Task.FromResult(true);

    private bool EnsureRepositoryAvailable(ProjectItemViewModel item, bool requireRemote = false)
    {
        if (item.IsLocalDirectory)
        {
            Feedback = _localization.GetString("PROJECT_GIT_NOT_INITIALIZED");
            return false;
        }
        if (requireRemote && !item.HasRemote)
        {
            Feedback = _localization.GetString("PROJECT_ORIGIN_NOT_CONFIGURED");
            return false;
        }
        return true;
    }

    /// <summary>协议不匹配时仅生成预览，明确确认后才写 origin；取消不执行网络测试或传输。</summary>
    private async Task<bool> EnsureCompatibleProtocolAsync(Guid projectId)
    {
        var preview = await _bindingService.GetProtocolChangeAsync(projectId);
        if (!preview.IsSuccess)
        {
            Feedback = _localization.GetString(preview.Error!.Code, preview.Error.Arguments.Cast<object>().ToArray());
            return false;
        }
        if (preview.Value is not { } change) return true;

        var confirmed = await _confirmProtocolChange(
            _localization.GetString("Projects.ProtocolSwitch.ConfirmTitle"),
            _localization.GetString("Projects.ProtocolSwitch.Confirm", change.AccountName,
                change.TargetProtocol.ToString().ToUpperInvariant(), change.CurrentUrl, change.TargetUrl),
            _localization.GetString("Projects.ProtocolSwitch.ConfirmAction"));
        if (!confirmed) return false;

        var result = await _bindingService.ChangeProtocolAsync(change);
        if (!result.IsSuccess)
        {
            Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments.Cast<object>().ToArray());
            return false;
        }
        await RefreshProjectAsync(projectId);
        return true;
    }

}

/// <summary>
/// 项目列表项 ViewModel。
/// </summary>
public partial class ProjectItemViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;

    public Project Project { get; private set; }

    public Binding? Binding { get; private set; }

    public Account? BoundAccount { get; private set; }

    public Account? EffectiveAccount { get; private set; }

    public EffectiveReason EffectiveReason { get; private set; }

    public IReadOnlyList<Account> AvailableAccounts { get; private set; }

    [ObservableProperty]
    private Account? _selectedBindingAccount;

    [ObservableProperty]
    private bool _isSwitchingBinding;

    [ObservableProperty]
    private bool _isRemoteEditorOpen;

    [ObservableProperty]
    private bool _isPathEditorOpen;

    [ObservableProperty]
    private bool _isPathSaving;

    [ObservableProperty]
    private string _editingPath = string.Empty;

    public string MetadataWarning { get; internal set; } = string.Empty;

    public bool HasMetadataWarning => !string.IsNullOrEmpty(MetadataWarning);

    public bool MetadataReadFailed { get; internal set; }
    public bool HasMetadataError => HasMetadataWarning && MetadataReadFailed;
    public bool HasMetadataNotice => HasMetadataWarning && !MetadataReadFailed;

    [ObservableProperty]
    private string _editingOriginUrl = string.Empty;

    partial void OnEditingOriginUrlChanged(string value)
    {
        OnPropertyChanged(nameof(RemoteProtocolSwitchText));
    }

    public ProjectItemViewModel(
        Project project,
        Binding? binding,
        Account? boundAccount,
        Account? effectiveAccount,
        EffectiveReason effectiveReason,
        IReadOnlyList<Account> availableAccounts,
        ILocalizationService localization)
    {
        Project = project;
        Binding = binding;
        BoundAccount = boundAccount;
        // 普通目录仅被登记，不能把全局/默认账号标记为已经应用到仓库
        EffectiveAccount = project.IsLocalDirectory ? null : effectiveAccount;
        EffectiveReason = effectiveReason;
        AvailableAccounts = availableAccounts;
        SelectedBindingAccount = availableAccounts.FirstOrDefault(account => account.Id == boundAccount?.Id);
        _localization = localization;
        _localization.CultureChanged += (_, _) => RefreshLocalization();
    }

    public string Name => Project.Name;

    public string BoundAccountAlias => BoundAccount?.DisplayAlias ?? _localization.GetString("Projects.Unbound");

    public string EffectiveAccountAlias => EffectiveAccount?.DisplayAlias ?? _localization.GetString("Projects.Unbound");

    public string RemoteHost => Project.RemoteHost;

    /// <summary>展示远程地址时隐藏旧数据中可能存在的 HTTPS 用户信息或 Token。</summary>
    public string OriginUrlDisplay => SanitizeHttpUserInfo(Project.OriginUrl);

    public string ProtocolText => Project.RemoteProtocol.ToString().ToUpperInvariant();

    public string PathText => Project.RepositoryPath;

    public string BoundAccountLabel => _localization.GetString("Projects.BoundAccountLabel", BoundAccountAlias);

    public string EffectiveAccountLabel => _localization.GetString("Projects.EffectiveAccountLabel", EffectiveAccountAlias);

    public string EffectiveReasonLabel => IsLocalDirectory
        ? _localization.GetString("Projects.LocalDirectory")
        : _localization.GetString($"Projects.Reason.{EffectiveReason}");

    public bool HasBinding => Binding is not null;
    public bool IsLocalDirectory => Project.IsLocalDirectory;
    public bool HasGitRepository => !IsLocalDirectory;
    public bool HasRemote => !string.IsNullOrWhiteSpace(Project.OriginUrl);
    public bool CanBindAccount => CanManageProject && !IsSwitchingBinding;
    public bool CanTest => CanManageProject && HasGitRepository && HasRemote && HasBinding;
    public bool CanPull => CanManageProject && HasGitRepository;

    public string RemoteProtocolSwitchText
    {
        get
        {
            return GitRemoteUrlConverter.TrySwitchProtocol(EditingOriginUrl, out _, out var targetProtocol)
                ? _localization.GetString(targetProtocol is RemoteProtocol.Ssh
                    ? "Projects.SwitchToSsh"
                    : "Projects.SwitchToHttps")
                : _localization.GetString("Projects.SwitchProtocol");
        }
    }

    public void BeginRemoteEdit()
    {
        EditingOriginUrl = OriginUrlDisplay;
        IsRemoteEditorOpen = true;
    }

    public void CancelRemoteEdit()
    {
        EditingOriginUrl = OriginUrlDisplay;
        IsRemoteEditorOpen = false;
    }

    /// <summary>绑定失败时还原下拉显示，允许用户再次选择后重试。</summary>
    public void RestoreBoundAccountSelection()
    {
        var wasRestoring = IsRestoringSelection;
        IsRestoringSelection = true;
        try { SelectedBindingAccount = AvailableAccounts.FirstOrDefault(account => account.Id == BoundAccount?.Id); }
        finally { IsRestoringSelection = wasRestoring; }
    }

    /// <summary>切换编辑框中的标准 SSH/HTTPS Remote；尚未保存到仓库。</summary>
    public bool TrySwitchEditingOriginProtocol()
    {
        if (!GitRemoteUrlConverter.TrySwitchProtocol(EditingOriginUrl, out var convertedUrl, out _))
        {
            return false;
        }

        EditingOriginUrl = convertedUrl;
        return true;
    }

    private static string SanitizeHttpUserInfo(string originUrl)
    {
        if (!Uri.TryCreate(originUrl, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrEmpty(uri.UserInfo))
        {
            return originUrl;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }

    public string ProtocolLabel => IsLocalDirectory
        ? _localization.GetString("Projects.LocalDirectory")
        : !HasRemote ? _localization.GetString("Projects.LocalRepository")
        : _localization.GetString("Projects.ProtocolLabel", ProtocolText);

    public string HostLabel => _localization.GetString("Projects.HostLabel", RemoteHost);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(BoundAccountAlias));
        OnPropertyChanged(nameof(EffectiveAccountAlias));
        OnPropertyChanged(nameof(BoundAccountLabel));
        OnPropertyChanged(nameof(EffectiveAccountLabel));
        OnPropertyChanged(nameof(EffectiveReasonLabel));
        OnPropertyChanged(nameof(ProtocolLabel));
        OnPropertyChanged(nameof(HostLabel));
        OnPropertyChanged(nameof(RemoteProtocolSwitchText));
    }
}
