using CommunityToolkit.Mvvm.ComponentModel;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Projects;

namespace GitBinder.Desktop.ViewModels;

/// <summary>
/// 总览页 ViewModel。
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly IAccountRepository _accountRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly IBindingRepository _bindingRepository;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    [ObservableProperty]
    private int _accountCount;

    [ObservableProperty]
    private int _projectCount;

    [ObservableProperty]
    private int _bindingCount;

    [ObservableProperty]
    private string _defaultAccountAlias = string.Empty;

    public DashboardViewModel(
        IAccountRepository accountRepository,
        IProjectRepository projectRepository,
        IBindingRepository bindingRepository)
    {
        _accountRepository = accountRepository;
        _projectRepository = projectRepository;
        _bindingRepository = bindingRepository;
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            var snapshot = await Task.Run(async () =>
            {
                var accounts = await _accountRepository.GetAllAsync();
                var projects = await _projectRepository.GetAllAsync();
                var bindings = await _bindingRepository.GetAllAsync();
                return (Accounts: accounts.Count, Projects: projects.Count, Bindings: bindings.Count,
                    DefaultAlias: accounts.FirstOrDefault(account => account.IsDefault && account.Enabled)?.DisplayAlias ?? string.Empty);
            });
            AccountCount = snapshot.Accounts;
            ProjectCount = snapshot.Projects;
            BindingCount = snapshot.Bindings;
            DefaultAccountAlias = snapshot.DefaultAlias;
        }
        finally { _loadGate.Release(); }
    }
}
