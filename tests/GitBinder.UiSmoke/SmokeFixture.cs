using GitBinder.Application.Accounts;
using GitBinder.Application;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Desktop;
using GitBinder.Desktop.Localization;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace GitBinder.UiSmoke;

// 所有后端端口均为内存 Fake；不构造 CompositionRoot、数据库、DPAPI 或外部命令执行器。
internal sealed class SmokeFixture
{
    public LocalizationService Localization { get; } = new();
    public FakeAccountRepository AccountRepository { get; } = new();
    public FakeProjectRepository ProjectRepository { get; } = new();
    public FakeProjectGroupRepository Groups { get; } = new();
    public SmokeGitTransfer Transfer { get; } = new();
    public FakeGitService Git { get; } = new() { OriginUrl = "https://github.com/example/ui-demo.git" };
    public SmokePlatforms PlatformRepository { get; } = new();
    public DashboardViewModel Dashboard { get; }
    public AccountsViewModel Accounts { get; }
    public ProjectsViewModel Projects { get; }
    public PlatformsViewModel Platforms { get; }
    public SettingsViewModel Settings { get; }
    public MainViewModel Main { get; }
    public AccountEditViewModel AccountEditor { get; }

    public SmokeFixture(string artifacts)
    {
        var bindings = new FakeBindingRepository();
        var settings = new FakeSettingsRepository();
        var secrets = new FakeSecretStore();
        var dataPath = new SmokeDataPath(artifacts);
        var git = Git;
        var normalizer = new SmokeNormalizer();
        var resolver = new EffectiveAccountResolver(new FakeGlobalModeState(), bindings, AccountRepository);
        var bindingService = new BindingService(bindings, AccountRepository, ProjectRepository,
            secrets, git, new FakeGitConfigApplier(), new FakeSnapshotRepository(), Transfer, resolver);
        var accountService = new AccountService(AccountRepository, new BindingDeletionGuard(bindings),
            new SecretService(secrets), new FakePrivateKeyImporter());
        var platforms = PlatformRepository;
        var platformService = new PlatformService(platforms, settings);
        var projectService = new ProjectService(ProjectRepository, git, normalizer, AccountRepository, bindingService);
        var transferService = new ProjectTransferService(Transfer, projectService, ProjectRepository,
            AccountRepository, bindingService, resolver, normalizer, Groups);
        AccountEditor = new AccountEditViewModel(accountService, platformService, Localization, () => Task.FromResult<string?>(null));
        var services = new ServiceCollection().AddTransient(_ => AccountEditor).BuildServiceProvider();
        Accounts = new AccountsViewModel(AccountRepository, accountService, Localization, services);
        Projects = new ProjectsViewModel(ProjectRepository, projectService, bindings, AccountRepository,
            bindingService, resolver, Localization, () => Task.FromResult<string?>(null),
            transferService, new ProjectGroupService(Groups));
        Platforms = new PlatformsViewModel(platformService, Localization);
        Settings = new SettingsViewModel(dataPath, new SmokeLocator(), new SmokeLocator(), Localization, settings);
        Dashboard = new DashboardViewModel(AccountRepository, ProjectRepository, bindings);
        var global = new GlobalModeService(settings, AccountRepository, bindingService, new FakeGlobalGitConfigApplier());
        Main = new MainViewModel(Localization, global, AccountRepository, Dashboard, Accounts, Projects, Platforms, Settings);
        foreach (var name in new[] { "GitHub", "阿里云云效 Codeup", "GitLab" })
            platforms.AddAsync(new GitPlatform { Name = name, Host = name == "GitHub" ? "github.com" : "example.com", Enabled = name != "GitLab" }).GetAwaiter().GetResult();
        for (var i = 0; i < 3; i++)
            AccountRepository.AddAsync(new Account { Alias = i == 0 ? "开源协作账号" : "Workspace " + i,
                GitName = "Demo Developer", GitEmail = $"demo{i}@example.com", PlatformName = "GitHub",
                Host = "github.com", IsDefault = i == 0 }).GetAwaiter().GetResult();
        var group = new ProjectGroup(Guid.NewGuid(), "工作项目");
        Groups.Groups.Add(group.Id, group);
        foreach (var name in new[] { "gitbinder-ui", "service-api", "customer-dashboard-with-a-long-project-name", "mobile-client" })
        {
            var project = new Project { Name = name, RepositoryPath = @"X:\UI-Smoke-Demo\" + name,
                CanonicalPath = @"X:\UI-Smoke-Demo\" + name, OriginUrl = "https://github.com/example/" + name + ".git",
                RemoteHost = "github.com", RemoteProtocol = RemoteProtocol.Https, CurrentBranch = "main" };
            ProjectRepository.AddAsync(project).GetAwaiter().GetResult();
            if (name is "gitbinder-ui" or "service-api")
                bindings.AddAsync(new GitBinder.Domain.Bindings.Binding { ProjectId = project.Id,
                    AccountId = AccountRepository.GetAllAsync().GetAwaiter().GetResult()[0].Id }).GetAwaiter().GetResult();
            if (name is "gitbinder-ui" or "service-api") Groups.Memberships.Add(project.Id, group.Id);
        }
    }

    public async Task LoadAsync()
    {
        await Main.InitializeAsync();
        await Accounts.LoadAsync();
        await Projects.LoadAsync();
        await Platforms.LoadAsync();
        await Settings.LoadAsync();
        await AccountEditor.LoadPlatformsAsync();
    }
}

internal sealed class SmokeDataPath(string root) : IApplicationDataPath
{
    public string DataDirectory => Path.Combine(root, "isolated-data-not-created");
    public string DatabasePath => Path.Combine(DataDirectory, "fake.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");
}

internal sealed class SmokeLocator : IGitLocator, ISshLocator
{
    public Task<string?> LocateAsync(CancellationToken ct = default) => Task.FromResult<string?>(@"X:\UI-Smoke-Demo\bin\tool.exe");
}

internal sealed class SmokeNormalizer : IPathNormalizer
{
    public string Normalize(string path) => path;
    public string Canonicalize(string path) => path;
    public string ToKey(string path) => path.ToUpperInvariant();
    public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal sealed class SmokePlatforms : IPlatformRepository
{
    private readonly Dictionary<Guid, GitPlatform> _items = [];
    public bool FailNextWrite { get; set; }
    public Task? WriteGate { get; set; }
    public int WriteAttempts { get; private set; }
    public Task<GitPlatform?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(id));
    public Task<GitPlatform?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult(_items.Values.FirstOrDefault(p => p.Name == name));
    public Task<IReadOnlyList<GitPlatform>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GitPlatform>>(_items.Values.ToList());
    public async Task AddAsync(GitPlatform platform, CancellationToken ct = default)
    {
        WriteAttempts++;
        if (WriteGate is not null) await WriteGate.WaitAsync(ct);
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("Isolated fake platform save failure.");
        }
        _items[platform.Id] = platform;
    }
    public Task UpdateAsync(GitPlatform platform, CancellationToken ct = default) => AddAsync(platform, ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _items.Remove(id); return Task.CompletedTask; }
}

// 独立于 xUnit 通用 Fake，支持进行中/取消断言；绝不构造外部命令执行器。
internal sealed class SmokeGitTransfer : IGitTransfer
{
    public int CloneCount { get; private set; }
    public int PullCount { get; private set; }
    public int TestCount { get; private set; }
    public Guid? UsedAccountId { get; private set; }
    public Func<string, string, Account, CancellationToken, Task<Result>>? OnClone { get; set; }
    public Func<string, Account, CancellationToken, Task<Result>>? OnPull { get; set; }

    public Task<Result> CloneAsync(string url, string destination, Account account, CancellationToken ct = default)
    {
        CloneCount++;
        UsedAccountId = account.Id;
        return OnClone?.Invoke(url, destination, account, ct)
            ?? Task.FromResult(Result.Failure(new DomainError("CLONE_FAILED")));
    }

    public Task<Result> PullAsync(string path, Account account, CancellationToken ct = default)
    {
        PullCount++;
        UsedAccountId = account.Id;
        return OnPull?.Invoke(path, account, ct)
            ?? Task.FromResult(Result.Failure(new DomainError("PULL_FAILED")));
    }

    public Task<Result> TestAsync(string path, Account account, CancellationToken ct = default)
    {
        TestCount++;
        return Task.FromResult(Result.Success());
    }
}
