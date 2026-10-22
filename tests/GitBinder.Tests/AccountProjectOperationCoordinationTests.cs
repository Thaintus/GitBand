using System.Globalization;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Git;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Application.Settings;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>账号与项目操作协调仅使用内存仓储及可控异步节点，不访问用户配置、凭据或 Git</summary>
public sealed class AccountProjectOperationCoordinationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AccountWrites_AreRejectedDuringProjectOperation_WithoutChangingData()
    {
        var f = await Fixture.CreateAsync();
        var projectTask = f.Projects.BrowsePathCommand.ExecuteAsync(f.Item);
        try
        {
            Assert.True(f.Item.IsBusy);
            f.Editor.GitName = "not persisted";
            await f.Editor.SaveCommand.ExecuteAsync(null).WaitAsync(Timeout);
            await f.AccountList.SetDefaultCommand.ExecuteAsync(f.AccountItem).WaitAsync(Timeout);
            await f.AccountList.DeleteCommand.ExecuteAsync(f.AccountItem).WaitAsync(Timeout);

            Assert.Equal("Accounts.OperationBusy", f.Editor.Error);
            Assert.Equal("Accounts.OperationBusy", f.AccountList.Feedback);
            Assert.False(f.Editor.Saved);
            Assert.False(f.Editor.HasPersistedChanges);
            Assert.Equal("original", f.Account.GitName);
            Assert.False(f.Account.IsDefault);
            Assert.Equal(0, f.AccountStore.UpdateCount);
            Assert.Equal(0, f.AccountStore.DeleteCount);
            Assert.True(f.Projects.HasProjectOperations);
            Assert.False(f.Projects.IsExternalOperationBusy);
        }
        finally
        {
            f.Picker.TrySetResult(null);
            await projectTask.WaitAsync(Timeout);
        }
        Assert.False(f.Projects.HasPendingOperations);
    }

    [Fact]
    public async Task RejectedAccountSave_DoesNotReleaseAnotherExternalOperation()
    {
        var f = await Fixture.CreateAsync();
        Assert.True(f.Projects.TryBeginExternalOperation());
        try
        {
            await f.Editor.SaveCommand.ExecuteAsync(null).WaitAsync(Timeout);
            Assert.Equal("Accounts.OperationBusy", f.Editor.Error);
            Assert.True(f.Projects.IsExternalOperationBusy);
            Assert.False(f.Projects.CanManageGroups);
            Assert.Equal(0, f.AccountStore.UpdateCount);
        }
        finally { f.Projects.EndExternalOperation(); }
    }

    [Fact]
    public async Task Save_HoldsGateAcrossPersistenceIdentitySyncAndProjectRefresh()
    {
        var f = await Fixture.CreateAsync();
        var write = f.AccountStore.BeforeUpdate = new Barrier();
        var sync = f.Settings.BeforeGet = new Barrier();
        var refresh = f.ProjectStore.BeforeGetAll = new Barrier();
        var closed = 0;
        f.Editor.CloseRequested += () => closed++;
        f.Editor.GitName = "updated";
        var task = f.Editor.SaveCommand.ExecuteAsync(null);
        try
        {
            await write.Entered.Task.WaitAsync(Timeout);
            AssertExclusive(f);
            write.Release.TrySetResult();

            await sync.Entered.Task.WaitAsync(Timeout);
            Assert.True(f.Editor.HasPersistedChanges);
            AssertExclusive(f);
            sync.Release.TrySetResult();

            await refresh.Entered.Task.WaitAsync(Timeout);
            AssertExclusive(f);
            Assert.Equal(0, closed);
            refresh.Release.TrySetResult();

            await task.WaitAsync(Timeout);
            Assert.True(f.Editor.Saved);
            Assert.Equal(1, closed);
            Assert.Equal("updated", f.Account.GitName);
            Assert.False(f.Projects.HasPendingOperations);
            Assert.True(f.Projects.CanManageGroups);
        }
        finally
        {
            write.Release.TrySetResult();
            sync.Release.TrySetResult();
            refresh.Release.TrySetResult();
            await task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task SetDefault_HoldsGateUntilAccountListAndProjectRefreshFinish()
    {
        var f = await Fixture.CreateAsync();
        var accountRefresh = f.AccountStore.BeforeGetAll = new Barrier();
        var projectRefresh = f.ProjectStore.BeforeGetAll = new Barrier();
        var task = f.AccountList.SetDefaultCommand.ExecuteAsync(f.AccountItem);
        try
        {
            await accountRefresh.Entered.Task.WaitAsync(Timeout);
            Assert.True(f.Account.IsDefault);
            AssertExclusive(f);
            accountRefresh.Release.TrySetResult();

            await projectRefresh.Entered.Task.WaitAsync(Timeout);
            AssertExclusive(f);
            projectRefresh.Release.TrySetResult();

            await task.WaitAsync(Timeout);
            Assert.False(f.Projects.HasPendingOperations);
            Assert.True(f.Projects.CanManageGroups);
            Assert.True(Assert.Single(f.AccountList.Items).IsDefault);
        }
        finally
        {
            accountRefresh.Release.TrySetResult();
            projectRefresh.Release.TrySetResult();
            await task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Save_RefreshFailurePreservesPersistedFactAndReleasesGateForRetry()
    {
        var f = await Fixture.CreateAsync();
        f.ProjectStore.FailGetAll = true;
        f.Editor.GitName = "saved despite refresh failure";

        await f.Editor.SaveCommand.ExecuteAsync(null).WaitAsync(Timeout);

        Assert.True(f.Editor.HasPersistedChanges);
        Assert.False(f.Editor.Saved);
        Assert.Equal("Accounts.RefreshFailed", f.Editor.Error);
        Assert.Equal("saved despite refresh failure", f.Account.GitName);
        Assert.False(f.Projects.HasPendingOperations);
        Assert.True(f.Projects.TryBeginExternalOperation());
        f.Projects.EndExternalOperation();
        f.ProjectStore.FailGetAll = false;

        await f.Editor.SaveCommand.ExecuteAsync(null).WaitAsync(Timeout);

        Assert.True(f.Editor.Saved);
        Assert.Single(await f.Accounts.GetAllAsync());
        Assert.False(f.Projects.HasPendingOperations);
    }

    [Fact]
    public async Task Save_ValidationFailureDoesNotLeakGate()
    {
        var f = await Fixture.CreateAsync();
        f.Editor.GitName = string.Empty;

        await f.Editor.SaveCommand.ExecuteAsync(null).WaitAsync(Timeout);

        Assert.Equal("ACCOUNT_NAME_REQUIRED", f.Editor.Error);
        Assert.False(f.Editor.HasPersistedChanges);
        Assert.Equal(0, f.AccountStore.UpdateCount);
        Assert.False(f.Projects.HasPendingOperations);
        Assert.True(f.Projects.CanManageGroups);
    }

    [Fact]
    public async Task AccountListRefresh_UpdatesGlobalSelectorWithoutReapplyingGitConfiguration()
    {
        var f = await Fixture.CreateAsync();
        f.Account.IsDefault = true;
        var global = new Account { Username = "global", Alias = "new global", GitName = "global", Enabled = true };
        await f.Accounts.AddAsync(global);
        await f.Settings.SetAsync("global_mode.enabled", "true");
        await f.Settings.SetAsync("global_mode.account_id", global.Id.ToString());

        await f.AccountList.LoadAsync().WaitAsync(Timeout);

        Assert.Equal(2, f.Main.Accounts.Count);
        Assert.True(f.Main.GlobalModeEnabled);
        Assert.Equal("new global", f.Main.GlobalAccountAlias);
        Assert.Same(f.Main.Accounts.Single(account => account.Id == global.Id), f.Main.SelectedGlobalAccount);
        Assert.Null(f.GlobalConfig.AppliedAccount);
        Assert.Null(f.GlobalConfig.RestoredSnapshot);

        await f.Settings.SetAsync("global_mode.enabled", "false");
        await f.Accounts.DeleteAsync(global.Id);
        await f.AccountList.LoadAsync().WaitAsync(Timeout);

        Assert.Single(f.Main.Accounts);
        Assert.Equal(f.Account.Id, f.Main.SelectedGlobalAccount!.Id);
        Assert.False(f.Main.GlobalModeEnabled);
        Assert.Null(f.GlobalConfig.AppliedAccount);
    }

    [Fact]
    public async Task GlobalSelectorRefresh_SerializesOlderAndNewerSnapshots()
    {
        var f = await Fixture.CreateAsync();
        f.Account.IsDefault = true;
        var blocker = f.Settings.BeforeGet = new Barrier();
        var older = f.Main.LoadGlobalModeCommand.ExecuteAsync(null);
        Task newer = Task.CompletedTask;
        try
        {
            await blocker.Entered.Task.WaitAsync(Timeout);
            await f.Accounts.UpdateAsync(new Account
            {
                Id = f.Account.Id, Username = "updated", Alias = "latest alias", GitName = "updated",
                IsDefault = true, Enabled = true,
            });
            newer = f.AccountList.LoadAsync();
            Assert.Equal(1, f.Settings.GetCount);

            blocker.Release.TrySetResult();
            await Task.WhenAll(older, newer).WaitAsync(Timeout);

            Assert.Equal("latest alias", f.Main.GlobalAccountAlias);
            Assert.Equal("latest alias", f.Main.SelectedGlobalAccount!.DisplayAlias);
            Assert.Null(f.GlobalConfig.AppliedAccount);
        }
        finally
        {
            blocker.Release.TrySetResult();
            await Task.WhenAll(older, newer).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task GlobalSelectorRefresh_FailureIsSafeAndSubsequentRefreshCanRetry()
    {
        var f = await Fixture.CreateAsync();
        f.Settings.FailGet = true;

        var failure = await Record.ExceptionAsync(() => f.AccountList.LoadAsync().WaitAsync(Timeout));

        Assert.Null(failure);
        Assert.Equal("Accounts.RefreshFailed", f.Main.GlobalModeFeedback);
        Assert.DoesNotContain("fictional", f.Main.GlobalModeFeedback);
        f.Settings.FailGet = false;
        await f.AccountList.LoadAsync().WaitAsync(Timeout);
        Assert.Single(f.Main.Accounts);
        Assert.False(f.Projects.HasPendingOperations);
    }

    private static void AssertExclusive(Fixture f)
    {
        Assert.True(f.Projects.IsExternalOperationBusy);
        Assert.True(f.Projects.HasPendingOperations);
        Assert.False(f.Projects.CanManageProjects);
        Assert.False(f.Projects.CanManageGroups);
        Assert.False(f.Projects.TryBeginExternalOperation());
        Assert.False(f.Item.CanManageProject);
    }

    private sealed class Barrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class Fixture
    {
        public FakeAccountRepository Accounts { get; } = new();
        public ControlledAccountRepository AccountStore { get; }
        public ControlledProjectRepository ProjectStore { get; } = new();
        public ControlledSettingsRepository Settings { get; } = new();
        public TaskCompletionSource<string?> Picker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Account Account { get; } = new() { Username = "original", GitName = "original", Enabled = true };
        public AccountItemViewModel AccountItem { get; private set; } = null!;
        public ProjectItemViewModel Item { get; private set; } = null!;
        public ProjectsViewModel Projects { get; private set; } = null!;
        public AccountsViewModel AccountList { get; private set; } = null!;
        public AccountEditViewModel Editor { get; private set; } = null!;
        public MainViewModel Main { get; private set; } = null!;
        public FakeGlobalGitConfigApplier GlobalConfig { get; } = new();

        private Fixture() => AccountStore = new ControlledAccountRepository(Accounts);

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.Accounts.AddAsync(f.Account);
            var localization = new TestLocalization();
            var bindingRepository = new FakeBindingRepository();
            var resolver = new EffectiveAccountResolver(new FakeGlobalModeState(), bindingRepository, f.Accounts);
            var secrets = new FakeSecretStore();
            var git = new FakeGitService();
            var transfer = new FakeGitTransfer();
            var binding = new BindingService(bindingRepository, f.AccountStore, f.ProjectStore, secrets,
                git, new FakeGitConfigApplier(), new FakeSnapshotRepository(), transfer, resolver);
            var paths = new IdentityPathNormalizer();
            var projectService = new ProjectService(f.ProjectStore, git, paths, f.AccountStore, binding);
            var groups = new FakeProjectGroupRepository();
            var transferService = new ProjectTransferService(transfer, projectService, f.ProjectStore,
                f.AccountStore, binding, resolver, paths, groups);
            f.Projects = new ProjectsViewModel(f.ProjectStore, projectService, bindingRepository,
                f.AccountStore, binding, resolver, localization, () => f.Picker.Task,
                transferService, new ProjectGroupService(groups));
            f.Item = new ProjectItemViewModel(new Project { Name = "in-memory project" }, null, null, null,
                EffectiveReason.Default, [f.Account], localization);
            f.Projects.Items.Add(f.Item);
            var accountService = new AccountService(f.AccountStore, new DeletionGuard(),
                new SecretService(secrets), new FakePrivateKeyImporter());
            var global = new GlobalModeService(f.Settings, f.AccountStore, binding, f.GlobalConfig);
            var identitySync = new AccountIdentitySyncService(binding, global, resolver);
            f.AccountList = new AccountsViewModel(f.AccountStore, accountService, localization, new EmptyServices(), f.Projects);
            f.AccountItem = new AccountItemViewModel(f.Account, localization);
            var platforms = new PlatformService(new EmptyPlatforms(), new FakeSettingsRepository());
            f.Editor = new AccountEditViewModel(accountService,
                platforms, localization, () => Task.FromResult<string?>(null), identitySync, f.Projects, f.AccountList);
            f.Editor.InitializeForEdit(f.Account);
            f.Main = new MainViewModel(localization, global, f.AccountStore,
                new DashboardViewModel(f.AccountStore, f.ProjectStore, bindingRepository), f.AccountList,
                f.Projects, new PlatformsViewModel(platforms, localization),
                new SettingsViewModel(new UnusedDataPath(), new UnusedLocator(), new UnusedLocator(), localization, f.Settings));
            return f;
        }
    }

    private sealed class ControlledAccountRepository(FakeAccountRepository inner) : IAccountRepository
    {
        public Barrier? BeforeUpdate { get; set; }
        public Barrier? BeforeGetAll { get; set; }
        public int UpdateCount { get; private set; }
        public int DeleteCount { get; private set; }
        public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<Account?> GetDefaultAsync(CancellationToken ct = default) => inner.GetDefaultAsync(ct);
        public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
        {
            if (BeforeGetAll is { } barrier) await barrier.WaitAsync(ct);
            return await inner.GetAllAsync(ct);
        }
        public Task AddAsync(Account account, CancellationToken ct = default) => inner.AddAsync(account, ct);
        public async Task UpdateAsync(Account account, CancellationToken ct = default)
        {
            UpdateCount++;
            if (BeforeUpdate is { } barrier) await barrier.WaitAsync(ct);
            await inner.UpdateAsync(account, ct);
        }
        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            DeleteCount++;
            return inner.DeleteAsync(id, ct);
        }
    }

    private sealed class ControlledProjectRepository : IProjectRepository
    {
        public Barrier? BeforeGetAll { get; set; }
        public bool FailGetAll { get; set; }
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Project?>(null);
        public async Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default)
        {
            if (BeforeGetAll is { } barrier) await barrier.WaitAsync(ct);
            if (FailGetAll) throw new InvalidOperationException("fictional refresh failure");
            return [];
        }
        public Task<Project?> FindByPathAsync(string canonicalPath, CancellationToken ct = default) => Task.FromResult<Project?>(null);
        public Task AddAsync(Project project, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Project project, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ControlledSettingsRepository : ISettingsRepository
    {
        private readonly FakeSettingsRepository _inner = new();
        public Barrier? BeforeGet { get; set; }
        public bool FailGet { get; set; }
        public int GetCount { get; private set; }
        public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            GetCount++;
            if (BeforeGet is { } barrier) await barrier.WaitAsync(ct);
            if (FailGet) throw new InvalidOperationException("fictional setting read failure");
            return await _inner.GetAsync(key, ct);
        }
        public Task SetAsync(string key, string value, CancellationToken ct = default) => _inner.SetAsync(key, value, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);
    }

    private sealed class DeletionGuard : IAccountDeletionGuard
    {
        public Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class IdentityPathNormalizer : IPathNormalizer
    {
        public string Normalize(string path) => path;
        public string Canonicalize(string path) => path;
        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);
        public string ToKey(string path) => path;
    }

    private sealed class UnusedLocator : IGitLocator, ISshLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => throw new InvalidOperationException("No executable lookup expected");
    }

    private sealed class UnusedDataPath : IApplicationDataPath
    {
        public string DataDirectory => throw new InvalidOperationException("No user directory expected");
        public string DatabasePath => throw new InvalidOperationException("No database expected");
        public string LogDirectory => throw new InvalidOperationException("No log directory expected");
        public string KeysDirectory => throw new InvalidOperationException("No key directory expected");
    }

    private sealed class EmptyPlatforms : IPlatformRepository
    {
        public Task<GitPlatform?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<GitPlatform?>(null);
        public Task<GitPlatform?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<GitPlatform?>(null);
        public Task<IReadOnlyList<GitPlatform>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GitPlatform>>([]);
        public Task AddAsync(GitPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(GitPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestLocalization : ILocalizationService
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
