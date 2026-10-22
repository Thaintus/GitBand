using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Application.GlobalMode;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;

namespace GitBinder.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel，负责导航与全局状态。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly GlobalModeService _globalModeService;
    private readonly IAccountRepository _accountRepository;
    private readonly SemaphoreSlim _globalModeLoadGate = new(1, 1);

    private readonly DashboardViewModel _dashboard;
    private readonly AccountsViewModel _accounts;
    private readonly ProjectsViewModel _projects;
    private readonly PlatformsViewModel _platforms;
    private readonly SettingsViewModel _settings;

    public ObservableCollection<NavItem> NavigationItems { get; } = [];

    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    private ViewModelBase _currentPage = null!;

    public bool IsSettingsSelected => ReferenceEquals(CurrentPage, _settings);

    public bool CanChangeGlobalMode => _projects.CanManageGroups;

    /// <summary>即使已切换页面，也不能通过 Escape 中断另一页的 Git 或持久化操作。</summary>
    public bool CanCloseWithEscape => !_projects.HasPendingOperations
        && !EnableGlobalModeCommand.IsRunning && !SwitchGlobalAccountCommand.IsRunning && !DisableGlobalModeCommand.IsRunning
        && !_accounts.SetDefaultCommand.IsRunning && !_accounts.DeleteCommand.IsRunning
        && !_platforms.DeleteCommand.IsRunning && !_platforms.ToggleEnabledCommand.IsRunning && !_platforms.RestoreDefaultsCommand.IsRunning;

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalModeStatusText))]
    [NotifyPropertyChangedFor(nameof(GlobalModeToggleText))]
    [NotifyPropertyChangedFor(nameof(GlobalModeDisabled))]
    private bool _globalModeEnabled;

    [ObservableProperty]
    private string _globalAccountAlias = string.Empty;

    [ObservableProperty]
    private Account? _selectedGlobalAccount;

    private string _globalModeFeedback = string.Empty;
    public string GlobalModeFeedback
    {
        get => _globalModeFeedback;
        set
        {
            SetNotice(ref _globalModeFeedback, value);
            OnPropertyChanged(nameof(HasGlobalModeFeedback));
        }
    }

    public MainViewModel(
        ILocalizationService localization,
        GlobalModeService globalModeService,
        IAccountRepository accountRepository,
        DashboardViewModel dashboard,
        AccountsViewModel accounts,
        ProjectsViewModel projects,
        PlatformsViewModel platforms,
        SettingsViewModel settings)
    {
        _localization = localization;
        _globalModeService = globalModeService;
        _accountRepository = accountRepository;
        _dashboard = dashboard;
        _accounts = accounts;
        _projects = projects;
        _platforms = platforms;
        _settings = settings;
        _accounts.DataRefreshed += RefreshGlobalAccountStateAsync;

        _projects.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ProjectsViewModel.CanManageGroups)) return;
            OnPropertyChanged(nameof(CanChangeGlobalMode));
            EnableGlobalModeCommand.NotifyCanExecuteChanged();
            SwitchGlobalAccountCommand.NotifyCanExecuteChanged();
            DisableGlobalModeCommand.NotifyCanExecuteChanged();
        };

        _localization.CultureChanged += (_, _) => RefreshLocalization();

        BuildNavigation();
        RefreshLocalization();

        _currentPage = _dashboard;
        SelectedNav = NavigationItems[0];
    }

    private void BuildNavigation()
    {
        NavigationItems.Add(new NavItem("Nav.Dashboard", _dashboard));
        NavigationItems.Add(new NavItem("Nav.Accounts", _accounts));
        NavigationItems.Add(new NavItem("Nav.Platforms", _platforms));
        NavigationItems.Add(new NavItem("Nav.Projects", _projects));
    }

    private void RefreshLocalization()
    {
        foreach (var item in NavigationItems)
        {
            item.Refresh(_localization);
        }

        OnPropertyChanged(nameof(GlobalModeStatusText));
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is not null)
        {
            CurrentPage = value.Page;
            _ = LoadPageAsync(value.Page);
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        SelectedNav = null;
        CurrentPage = _settings;
        await LoadPageAsync(_settings);
    }

    /// <summary>导航切换后即时加载该页面的数据，避免升级或首次进入时看到空列表。</summary>
    private async Task LoadPageAsync(ViewModelBase page)
    {
        try
        {
            switch (page)
            {
                case DashboardViewModel:
                    await _dashboard.LoadAsync();
                    break;
                case AccountsViewModel:
                    await _accounts.LoadAsync();
                    break;
                case ProjectsViewModel:
                    await _projects.LoadAsync();
                    break;
                case PlatformsViewModel:
                    await _platforms.LoadAsync();
                    break;
                case SettingsViewModel:
                    await _settings.LoadAsync();
                    break;
            }
        }
        catch (Exception)
        {
            // 页面加载异常不应让导航失效，也不能再静默显示为空列表。
            GlobalModeFeedback = _localization.GetString("Navigation.LoadFailed");
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await _dashboard.LoadAsync();
        await _accounts.LoadAsync();
        await _projects.LoadAsync();
        await _platforms.LoadAsync();
        await LoadGlobalModeAsync();
    }

    [RelayCommand]
    private async Task LoadGlobalModeAsync()
    {
        await _globalModeLoadGate.WaitAsync();
        try
        {
            // 完整读完快照再更新控件，串行读取避免旧请求最后覆盖新账号状态
            var all = await _accountRepository.GetAllAsync();
            var config = await _globalModeService.GetConfigAsync();
            Accounts.Clear();
            foreach (var account in all) Accounts.Add(account);
            GlobalModeEnabled = config.Enabled;

            if (config.Enabled && config.GlobalAccountId is Guid id)
            {
                // 下拉框以引用匹配选项，使用本次集合内的账号实例
                var account = Accounts.FirstOrDefault(a => a.Id == id);
                GlobalAccountAlias = account?.DisplayAlias ?? string.Empty;
                SelectedGlobalAccount = account;
            }
            else
            {
                var def = Accounts.FirstOrDefault(a => a.IsDefault && a.Enabled);
                GlobalAccountAlias = def?.DisplayAlias ?? string.Empty;
                SelectedGlobalAccount = def;
            }
            OnPropertyChanged(nameof(GlobalModeStatusText));
        }
        finally { _globalModeLoadGate.Release(); }
    }

    private async Task RefreshGlobalAccountStateAsync()
    {
        try { await LoadGlobalModeAsync(); }
        catch (Exception) { GlobalModeFeedback = _localization.GetString("Accounts.RefreshFailed"); }
    }

    [RelayCommand(CanExecute = nameof(CanChangeGlobalMode))]
    private Task EnableGlobalModeAsync()
    {
        var id = SelectedGlobalAccount?.Id;
        return RunGlobalModeOperationAsync(() => _globalModeService.EnableAsync(id));
    }

    [RelayCommand(CanExecute = nameof(CanChangeGlobalMode))]
    private Task SwitchGlobalAccountAsync()
    {
        var id = SelectedGlobalAccount?.Id;
        return id.HasValue
            ? RunGlobalModeOperationAsync(() => _globalModeService.SwitchAccountAsync(id.Value))
            : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanChangeGlobalMode))]
    private Task DisableGlobalModeAsync() => RunGlobalModeOperationAsync(() => _globalModeService.DisableAsync());

    private async Task RunGlobalModeOperationAsync(Func<Task<Result>> operation)
    {
        // 全局模式会重应用多个项目的身份，与项目写入互斥，但不阻止导航
        if (!_projects.TryBeginExternalOperation()) return;
        try
        {
            GlobalModeFeedback = string.Empty;
            var result = await operation();
            await LoadGlobalModeAsync();
            await _projects.ReloadAfterExternalOperationAsync();
            if (!result.IsSuccess)
                GlobalModeFeedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
        catch (Exception)
        {
            GlobalModeFeedback = _localization.GetString("GlobalMode.OperationFailed");
        }
        finally { _projects.EndExternalOperation(); }
    }

    public async Task InitializeAsync()
    {
        // 启动重应用也可能写入多个仓库，必须在首次 await 前取得与页面操作相同的门禁
        await RunGlobalModeOperationAsync(() => _globalModeService.EnsureAppliedAsync());
        try
        {
            await _dashboard.LoadAsync();
        }
        catch (Exception)
        {
            GlobalModeFeedback = _localization.GetString("Navigation.LoadFailed");
        }
    }

    public string GlobalModeStatusText =>
        GlobalModeEnabled
            ? _localization.GetString("Dashboard.On")
            : _localization.GetString("Dashboard.Off");

    public string GlobalModeToggleText =>
        GlobalModeEnabled
            ? _localization.GetString("GlobalMode.Disable")
            : _localization.GetString("GlobalMode.Enable");

    public bool GlobalModeDisabled => !GlobalModeEnabled;

    public bool HasGlobalModeFeedback => !string.IsNullOrWhiteSpace(GlobalModeFeedback);
}

/// <summary>
/// 导航项。
/// </summary>
public sealed partial class NavItem : ViewModelBase
{
    public string Key { get; }

    public ViewModelBase Page { get; }

    private readonly string _iconPath;
    private Geometry? _icon;
    public Geometry Icon => _icon ??= Geometry.Parse(_iconPath);

    public NavItem(string key, ViewModelBase page)
    {
        Key = key;
        Page = page;
        // 导航图标采用同一套 24px 线条几何，不依赖符号字体或额外图标包。
        _iconPath = key switch
        {
            "Nav.Dashboard" => "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z",
            "Nav.Accounts" => "M16,7 A4,4 0 1 1 8,7 A4,4 0 1 1 16,7 M4,21 V19 A8,8 0 0 1 20,19 V21",
            "Nav.Projects" => "M3,6 H9 L12,9 H21 V20 H3 Z M3,6 V4 H10 L13,7 H21 V9",
            "Nav.Platforms" => "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M3,12 H21 M12,3 C6,8 6,16 12,21 C18,16 18,8 12,3",
            _ => "M3,6 H9 M15,6 H21 M3,18 H15 M21,18 H21 M15,6 A3,3 0 1 1 9,6 A3,3 0 1 1 15,6 M21,18 A3,3 0 1 1 15,18 A3,3 0 1 1 21,18"
        };
    }

    [ObservableProperty]
    private string _label = string.Empty;

    public void Refresh(ILocalizationService localization)
    {
        Label = localization.GetString(Key);
    }
}
