using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Application.Settings;
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
    private ObservableCollection<AccountItemViewModel> _filteredItems = [];

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
        ProjectsViewModel? projects = null,
        ListOrderService? listOrder = null)
    {
        _repository = repository;
        _accountService = accountService;
        _localization = localization;
        _services = services;
        _projects = projects;
        _listOrder = listOrder;
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            var accounts = await Task.Run(async () =>
            {
                var all = await _repository.GetAllAsync();
                return _listOrder is null ? all
                    : await _listOrder.ApplyAsync(ListOrderScope.Accounts, all, account => account.Id);
            });
            var existing = Items.ToDictionary(item => item.Account.Id);
            var next = new List<AccountItemViewModel>(accounts.Count);
            foreach (var account in accounts)
            {
                if (existing.TryGetValue(account.Id, out var item)) item.ApplySnapshot(account);
                else item = new AccountItemViewModel(account, _localization);
                next.Add(item);
            }
            ListOrdering.Synchronize(Items, next);
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
        ListOrdering.Synchronize(FilteredItems, Items.Where(item => ListSearch.Matches(
            SearchText,
            item.DisplayAlias,
            item.Account.GitName,
            item.EmailText,
            item.PlatformText,
            item.HostText)).ToList());
        OnPropertyChanged(nameof(HasNoMatches));
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
        if (_isAccountEditorOpen || _isOrderSaving) return;
        _isAccountEditorOpen = true;
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
        finally { _isAccountEditorOpen = false; }
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
        if (_isOrderSaving) return false;
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
    private (string Alias, string Platform, string Host, string Email, bool Default, bool Enabled) _display;

    public Account Account { get; private set; }

    public AccountItemViewModel(Account account, ILocalizationService localization)
    {
        Account = account;
        _display = CaptureDisplay(account);
        // 当前字段来自账号本身，不随 UI 语言变化，不订阅全局语言事件。
    }

    public string DisplayAlias => _display.Alias;

    public string PlatformText => _display.Platform;

    public string HostText => _display.Host;

    public string EmailText => _display.Email;

    public bool IsDefault => _display.Default;

    public bool Enabled => _display.Enabled;

    public void ApplySnapshot(Account account)
    {
        var old = _display;
        var replaced = !ReferenceEquals(Account, account);
        Account = account;
        _display = CaptureDisplay(account);
        if (replaced) OnPropertyChanged(nameof(Account));
        if (old.Alias != _display.Alias) OnPropertyChanged(nameof(DisplayAlias));
        if (old.Platform != _display.Platform) OnPropertyChanged(nameof(PlatformText));
        if (old.Host != _display.Host) OnPropertyChanged(nameof(HostText));
        if (old.Email != _display.Email) OnPropertyChanged(nameof(EmailText));
        if (old.Default != _display.Default) OnPropertyChanged(nameof(IsDefault));
        if (old.Enabled != _display.Enabled) OnPropertyChanged(nameof(Enabled));
    }

    private static (string Alias, string Platform, string Host, string Email, bool Default, bool Enabled)
        CaptureDisplay(Account account) =>
        (account.DisplayAlias, account.PlatformName, account.Host, account.GitEmail, account.IsDefault, account.Enabled);
}
