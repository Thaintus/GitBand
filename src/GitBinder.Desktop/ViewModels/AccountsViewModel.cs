using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Desktop.Views;
using GitBinder.Domain.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace GitBinder.Desktop.ViewModels;

/// <summary>
/// 账号页 ViewModel。
/// </summary>
public partial class AccountsViewModel : ViewModelBase
{
    private readonly IAccountRepository _repository;
    private readonly AccountService _accountService;
    private readonly ILocalizationService _localization;
    private readonly IServiceProvider _services;
    private readonly ProjectsViewModel? _projects;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public event Func<Task>? DataRefreshed;

    public ObservableCollection<AccountItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _hasItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private IReadOnlyList<AccountItemViewModel> _filteredItems = [];

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public bool HasNoMatches => HasItems && FilteredItems.Count == 0;

    [ObservableProperty]
    private AccountItemViewModel? _selectedItem;

    private string _feedback = string.Empty;
    public string Feedback
    {
        get => _feedback;
        set => SetNotice(ref _feedback, value);
    }

    public AccountsViewModel(
        IAccountRepository repository,
        AccountService accountService,
        ILocalizationService localization,
        IServiceProvider services,
        ProjectsViewModel? projects = null)
    {
        _repository = repository;
        _accountService = accountService;
        _localization = localization;
        _services = services;
        _projects = projects;
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            var accounts = await _repository.GetAllAsync();
            Items.Clear();
            foreach (var account in accounts)
                Items.Add(new AccountItemViewModel(account, _localization));
            HasItems = Items.Count > 0;
            ApplyFilter();
        }
        finally { _loadGate.Release(); }

        if (DataRefreshed is { } refreshed)
            foreach (Func<Task> handler in refreshed.GetInvocationList()) await handler();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void ApplyFilter()
    {
        // 保留完整集合与条目引用，输入仅影响展示，不重新读取账号或访问凭据。
        FilteredItems = Items.Where(item => ListSearch.Matches(
            SearchText,
            item.DisplayAlias,
            item.Account.GitName,
            item.EmailText,
            item.PlatformText,
            item.HostText)).ToList();
    }

    public async Task<Account?> FindByIdAsync(Guid id)
        => await _repository.GetByIdAsync(id);

    [RelayCommand]
    private async Task AddAsync()
    {
        Feedback = string.Empty;
        await OpenEditorAsync(null);
    }

    [RelayCommand]
    private async Task EditAsync(AccountItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        Feedback = string.Empty;
        await OpenEditorAsync(item.Account);
    }

    private async Task OpenEditorAsync(Account? account)
    {
        try
        {
            var editor = _services.GetRequiredService<AccountEditViewModel>();
            await editor.LoadPlatformsAsync();
            if (account is null)
            {
                editor.InitializeForCreate();
            }
            else
            {
                editor.InitializeForEdit(account);
            }

            var window = new AccountEditWindow(editor);
            var owner = GetParentWindow();
            var result = owner is null
                ? await window.ShowDialog<bool?>(window)
                : await window.ShowDialog<bool?>(owner);
            if ((result is true || editor.HasPersistedChanges) && !editor.HasRefreshedAccountList)
            {
                await LoadAsync();
            }
        }
        catch (Exception)
        {
            // 编辑器打开失败时留在列表页提示，避免 AsyncRelayCommand 未处理异常直接终止桌面程序。
            Feedback = _localization.GetString("Accounts.Edit.OpenFailed");
        }
    }

    private Avalonia.Controls.Window? GetParentWindow()
    {
        // 通过 App 当前生命周期获取主窗口。
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }

    [RelayCommand]
    private async Task SetDefaultAsync(AccountItemViewModel? item)
    {
        if (item is null || !TryBeginAccountOperation()) return;
        try
        {
            Feedback = string.Empty;
            var result = await _accountService.SetDefaultAsync(item.Account.Id);
            if (!result.IsSuccess) Feedback = _localization.GetString(result.Error!.Code);
        }
        catch (Exception) { Feedback = _localization.GetString("Accounts.OperationFailed"); }
        finally { await CompleteAccountOperationAsync(refresh: true); }
    }

    [RelayCommand]
    private async Task DeleteAsync(AccountItemViewModel? item)
    {
        if (item is null || !TryBeginAccountOperation()) return;
        var refresh = false;
        try
        {
            Feedback = string.Empty;
            var confirmed = await DialogHelper.ConfirmAsync(
                _localization.GetString("Common.Confirm"),
                _localization.GetString("Accounts.Delete.Confirm", item.DisplayAlias),
                _localization.GetString("Common.Delete"));
            if (!confirmed) return;
            refresh = true;
            var result = await _accountService.DeleteAsync(item.Account.Id);
            if (!result.IsSuccess)
                Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
        catch (Exception) { Feedback = _localization.GetString("Accounts.OperationFailed"); }
        finally { await CompleteAccountOperationAsync(refresh); }
    }

    private bool TryBeginAccountOperation()
    {
        if (_projects is null || _projects.TryBeginExternalOperation()) return true;
        Feedback = _localization.GetString("Accounts.OperationBusy");
        return false;
    }

    private async Task CompleteAccountOperationAsync(bool refresh)
    {
        try
        {
            if (!refresh) return;
            var refreshFailed = false;
            try { await LoadAsync(); }
            catch (Exception) { refreshFailed = true; }
            if (_projects is not null)
            {
                try { await _projects.ReloadAfterExternalOperationAsync(); }
                catch (Exception) { refreshFailed = true; }
            }
            if (refreshFailed)
            {
                var refreshError = _localization.GetString("Accounts.RefreshFailed");
                Feedback = string.IsNullOrWhiteSpace(Feedback) ? refreshError : Feedback + " " + refreshError;
            }
        }
        finally { _projects?.EndExternalOperation(); }
    }
}

/// <summary>
/// 账号列表项 ViewModel。
/// </summary>
public partial class AccountItemViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;

    public Account Account { get; }

    public AccountItemViewModel(Account account, ILocalizationService localization)
    {
        Account = account;
        _localization = localization;
        _localization.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PlatformText));
        };
    }

    public string DisplayAlias => Account.DisplayAlias;

    public string PlatformText => Account.PlatformName;

    public string HostText => Account.Host;

    public string EmailText => Account.GitEmail;

    public bool IsDefault => Account.IsDefault;

    public bool Enabled => Account.Enabled;
}
