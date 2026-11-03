using System.Globalization;
using GitBinder.Application;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>启动同步只使用内存端口，不创建窗口、数据库、目录或执行真实 Git</summary>
public sealed class MainInitializationConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_ReadsRecordedDataWithoutApplyingRepositoryIdentity(bool globalEnabled)
    {
        var f = await Fixture.CreateAsync(preloadProjects: false, globalEnabled: globalEnabled);
        f.Git.OriginUrl = "https://example.org/changed-outside-app.git";
        Assert.Empty(f.Projects.Items);
        await f.Main.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(f.Main.Accounts);
        var item = Assert.Single(f.Projects.FilteredItems);
        Assert.Equal(Fixture.OriginalOrigin, item.Project.OriginUrl);
        Assert.Equal(globalEnabled, f.Main.GlobalModeEnabled);
        Assert.Equal(0, f.Applier.RestoreCalls);
        Assert.False(f.Applier.Entered.Task.IsCompleted);
        Assert.False(f.Projects.IsExternalOperationBusy);
        Assert.False(f.Projects.HasPendingOperations);
        Assert.True(f.Projects.CanManageProjects);
        Assert.True(f.Main.CanChangeGlobalMode);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Empty(f.Main.GlobalModeFeedback);
        Assert.Equal(1, f.Dashboard.ProjectCount);
        await f.Main.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(item, Assert.Single(f.Projects.Items));
        Assert.Equal(0, f.Applier.RestoreCalls);
    }

    private sealed class Fixture
    {
        public const string OriginalOrigin = "https://example.org/team/original.git";
        public FakeGitService Git { get; } = new() { OriginUrl = OriginalOrigin };
        public FakeGitTransfer Transfer { get; } = new();
        public DeferredApplier Applier { get; } = new();
        public ProjectsViewModel Projects { get; private set; } = null!;
        public AccountsViewModel Accounts { get; private set; } = null!;
        public DashboardViewModel Dashboard { get; private set; } = null!;
        public MainViewModel Main { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(bool preloadProjects = true, bool globalEnabled = false)
        {
            var f = new Fixture();
            var accounts = new FakeAccountRepository();
            var projects = new FakeProjectRepository();
            var bindings = new FakeBindingRepository();
            var groups = new FakeProjectGroupRepository();
            var settings = new FakeSettingsRepository();
            var localization = new KeyLocalization();
            var secrets = new FakeSecretStore();
            var account = new Account
            {
                Alias = "initialization fixture", GitName = "fixture", GitEmail = "fixture@example.org",
                IsDefault = true, Enabled = true, AuthenticationType = AuthenticationType.Https,
                HttpsSecretId = "fake-secret-id",
            };
            var path = Path.Combine(Path.GetTempPath(), "gitbinder-init-not-created-" + Guid.NewGuid().ToString("N"));
            var project = new Project
            {
                Name = "initialization fixture", RepositoryPath = path, CanonicalPath = path,
                GitDir = Path.Combine(path, ".git"), OriginUrl = OriginalOrigin,
                RemoteHost = "example.org", RemoteProtocol = RemoteProtocol.Https, CurrentBranch = "main",
            };
            await settings.SetAsync("global_mode.enabled", globalEnabled.ToString());
            await settings.SetAsync("global_mode.account_id", account.Id.ToString());
            await accounts.AddAsync(account);
            await projects.AddAsync(project);
            await bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = account.Id });
            var paths = new IdentityPathNormalizer();
            var resolver = new EffectiveAccountResolver(new FakeGlobalModeState(), bindings, accounts);
            var bindingService = new BindingService(bindings, accounts, projects, secrets, f.Git,
                new FakeGitConfigApplier(), new FakeSnapshotRepository(), f.Transfer, resolver);
            var projectService = new ProjectService(projects, f.Git, paths, accounts, bindingService);
            var transferService = new ProjectTransferService(f.Transfer, projectService, projects, accounts,
                bindingService, resolver, paths, groups);
            f.Projects = new ProjectsViewModel(projects, projectService, bindings, accounts, bindingService,
                resolver, localization, () => throw new InvalidOperationException("No directory picker expected"),
                transferService, new ProjectGroupService(groups));
            if (preloadProjects) await f.Projects.LoadAsync();
            f.Accounts = new AccountsViewModel(accounts,
                new AccountService(accounts, new BindingDeletionGuard(bindings), new SecretService(secrets), new FakePrivateKeyImporter()),
                localization, new NoUiServiceProvider(), f.Projects);
            f.Dashboard = new DashboardViewModel(accounts, projects, bindings);
            var platforms = new PlatformsViewModel(new PlatformService(new EmptyPlatformRepository(), settings), localization);
            var settingsViewModel = new SettingsViewModel(new UnusedDataPath(), new UnusedLocator(), new UnusedLocator(), localization, settings);
            var global = new GlobalModeService(settings, accounts, f.Applier, new FakeGlobalGitConfigApplier());
            f.Main = new MainViewModel(localization, global, accounts, f.Dashboard, f.Accounts,
                f.Projects, platforms, settingsViewModel);
            return f;
        }
    }

    private sealed class DeferredApplier : IGlobalModeApplier
    {
        public int RestoreCalls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Result> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result> RestoreBoundAccountsAsync(CancellationToken ct = default)
        {
            RestoreCalls++;
            Entered.TrySetResult();
            return Completion.Task.WaitAsync(ct);
        }
        public Task<Result> ApplyGlobalAccountAsync(Account account, CancellationToken ct = default)
            => throw new InvalidOperationException("Only startup restore is expected");
    }

    private sealed class IdentityPathNormalizer : IPathNormalizer
    {
        public string Normalize(string path) => path;
        public string Canonicalize(string path) => path;
        public string ToKey(string path) => path.ToUpperInvariant();
        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnusedLocator : IGitLocator, ISshLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => throw new InvalidOperationException("No executable lookup expected");
    }

    private sealed class UnusedDataPath : IApplicationDataPath
    {
        public string DataDirectory => throw new InvalidOperationException("No data directory expected");
        public string DatabasePath => throw new InvalidOperationException("No database expected");
        public string LogDirectory => throw new InvalidOperationException("No log directory expected");
        public string KeysDirectory => throw new InvalidOperationException("No key directory expected");
    }

    private sealed class NoUiServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("No window expected");
    }

    private sealed class EmptyPlatformRepository : IPlatformRepository
    {
        public Task<GitPlatform?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<GitPlatform?>(null);
        public Task<GitPlatform?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<GitPlatform?>(null);
        public Task<IReadOnlyList<GitPlatform>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitPlatform>>([]);
        public Task AddAsync(GitPlatform platform, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task UpdateAsync(GitPlatform platform, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private sealed class KeyLocalization : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.InvariantCulture;
        public IReadOnlyList<CultureInfo> SupportedCultures { get; } = [CultureInfo.InvariantCulture];
        public event EventHandler? CultureChanged;
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => key;
        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
