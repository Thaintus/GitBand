using System.Globalization;
using System.Threading.Channels;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.Projects;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>加载回归仅使用内存仓储和受控 Git 端口，不创建窗口、文件、进程或网络连接。</summary>
public sealed class ProjectLoadingViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Reload_UpdatesAccountChoicesBeforeRestoringSelectedAccount()
    {
        var f = await Fixture.CreateAsync(projectCount: 1);
        await f.ViewModel.LoadStoredAsync().WaitAsync(Timeout);
        var item = Assert.Single(f.ViewModel.Items);
        var sourceUpdated = false;
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ProjectItemViewModel.AvailableAccounts)) return;
            sourceUpdated = true;
            // 模拟控件更换 ItemsSource 时清空 SelectedItem 的反馈。
            item.SelectedBindingAccount = null;
        };
        await f.ViewModel.LoadStoredAsync().WaitAsync(Timeout);
        Assert.True(sourceUpdated);
        Assert.NotNull(item.SelectedBindingAccount);
        Assert.Equal(item.Binding!.AccountId, item.SelectedBindingAccount.Id);
        Assert.Contains(item.AvailableAccounts, account => ReferenceEquals(account, item.SelectedBindingAccount));
        Assert.False(item.IsRestoringSelection);
        Assert.Equal(0, f.Git.ValidationCount);
    }

    [Fact]
    public async Task Reload_DisabledBoundAccountStillAppearsInChoicesAndRemainsSelected()
    {
        var f = await Fixture.CreateAsync(projectCount: 1, accountEnabled: false);
        await f.ViewModel.LoadStoredAsync().WaitAsync(Timeout);
        var item = Assert.Single(f.ViewModel.Items);
        Assert.NotNull(item.BoundAccount);
        Assert.False(item.BoundAccount.Enabled);
        Assert.Same(item.BoundAccount, item.SelectedBindingAccount);
        Assert.Contains(item.BoundAccount, item.AvailableAccounts);
        Assert.Empty(f.ViewModel.CloneAccounts);
        await f.ViewModel.LoadStoredAsync().WaitAsync(Timeout);
        Assert.Equal(item.Binding!.AccountId, item.SelectedBindingAccount!.Id);
        Assert.Equal(0, f.Git.ValidationCount);
    }

    [Fact]
    public async Task ExternalIdentityRefresh_ReadsCacheWithoutWaitingForGitAndKeepsRowInstance()
    {
        var f = await Fixture.CreateAsync(projectCount: 2);
        f.Git.BlockValidation();
        Assert.True(f.ViewModel.TryBeginExternalOperation());
        try
        {
            await f.ViewModel.ReloadAfterExternalOperationAsync().WaitAsync(Timeout);
            var rows = f.ViewModel.Items.ToArray();
            Assert.Equal(2, f.ViewModel.FilteredItems.Count);
            Assert.Equal(0, f.Git.ValidationCount);
            Assert.True(f.ViewModel.IsExternalOperationBusy);
            await f.ViewModel.ReloadAfterExternalOperationAsync().WaitAsync(Timeout);
            Assert.Same(rows[0], f.ViewModel.Items[0]);
            Assert.Same(rows[1], f.ViewModel.Items[1]);
            Assert.Equal(0, f.Git.ValidationCount);
            Assert.False(f.ViewModel.HasProjectOperations);
        }
        finally
        {
            f.Git.ReleaseValidation();
            f.ViewModel.EndExternalOperation();
        }
        Assert.False(f.ViewModel.HasPendingOperations);
        Assert.True(f.ViewModel.CanManageProjects);
    }

    [Fact]
    public async Task Load_ShowsCachedProjectsBeforeGitCompletes()
    {
        var f = await Fixture.CreateAsync(projectCount: 1);
        f.Git.BlockValidation();
        var load = f.ViewModel.LoadAsync();
        try
        {
            await f.Git.WaitForEntriesAsync(1);
            var item = Assert.Single(f.ViewModel.FilteredItems);
            Assert.Equal(Fixture.Origin, item.OriginUrlDisplay);
            Assert.Equal("project-0", item.Name);
            Assert.True(f.ViewModel.HasItems);
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.True(item.IsBusy);
            Assert.False(load.IsCompleted);
        }
        finally
        {
            f.Git.ReleaseValidation();
            await load.WaitAsync(Timeout);
        }

        Assert.False(f.ViewModel.HasPendingOperations);
        Assert.False(Assert.Single(f.ViewModel.Items).IsBusy);
    }

    [Fact]
    public async Task Load_RepeatedNavigationSharesRunningLoadWithoutAnotherScan()
    {
        var f = await Fixture.CreateAsync(projectCount: 2);
        f.Git.BlockValidation();
        var first = f.ViewModel.LoadAsync();
        try
        {
            await f.Git.WaitForEntriesAsync(2);
            for (var i = 0; i < 5; i++) Assert.Same(first, f.ViewModel.LoadAsync());
            Assert.Equal(1, f.Projects.ListReadCount);
            Assert.Equal(2, f.Git.ValidationCount);
        }
        finally
        {
            f.Git.ReleaseValidation();
            await first.WaitAsync(Timeout);
        }

        Assert.Equal(2, f.Git.ValidationCount);
        Assert.Equal(1, f.Projects.ListReadCount);
    }

    [Fact]
    public async Task Load_RefreshesSeveralProjectsTogetherButLimitsConcurrentGitReads()
    {
        var f = await Fixture.CreateAsync(projectCount: 9);
        f.Git.BlockValidation();
        var load = f.ViewModel.LoadAsync();
        try
        {
            var paths = await f.Git.WaitForEntriesAsync(4);
            Assert.Equal(4, paths.Distinct().Count());
            Assert.Equal(4, f.Git.ActiveValidations);
            Assert.Equal(4, f.Git.ValidationCount);
            Assert.Equal(9, f.ViewModel.FilteredItems.Count);
            Assert.Equal(4, f.ViewModel.Items.Count(item => item.IsBusy));
        }
        finally
        {
            f.Git.ReleaseValidation();
            await load.WaitAsync(Timeout);
        }

        Assert.Equal(9, f.Git.ValidationCount);
        Assert.Equal(4, f.Git.PeakActiveValidations);
        Assert.False(f.ViewModel.HasPendingOperations);
        Assert.All(f.ViewModel.Items, item => Assert.True(item.CanManageProject));
    }

    [Fact]
    public async Task Load_AfterCompletionRefreshesMetadataAndRetainsFilteredItemsAndDrafts()
    {
        var f = await Fixture.CreateAsync(projectCount: 2);
        await f.ViewModel.LoadAsync().WaitAsync(Timeout);
        f.ViewModel.SearchText = "project-0";
        var item = Assert.Single(f.ViewModel.FilteredItems);
        var filtered = f.ViewModel.FilteredItems;
        f.ViewModel.StartEditRemoteCommand.Execute(item);
        f.ViewModel.StartEditPathCommand.Execute(item);
        const string remoteDraft = "https://example.org/team/unsaved.git";
        var pathDraft = Path.Combine(item.PathText, "unsaved");
        item.EditingOriginUrl = remoteDraft;
        item.EditingPath = pathDraft;
        const string newOrigin = "https://example.org/team/changed.git";
        f.Git.Origin = newOrigin;

        await f.ViewModel.LoadAsync().WaitAsync(Timeout);

        Assert.Equal(4, f.Git.ValidationCount);
        Assert.Equal(2, f.Projects.ListReadCount);
        Assert.Same(filtered, f.ViewModel.FilteredItems);
        Assert.Same(item, Assert.Single(f.ViewModel.FilteredItems));
        Assert.Equal(newOrigin, item.OriginUrlDisplay);
        Assert.True(item.IsRemoteEditorOpen);
        Assert.True(item.IsPathEditorOpen);
        Assert.Equal(remoteDraft, item.EditingOriginUrl);
        Assert.Equal(pathDraft, item.EditingPath);
        Assert.False(f.ViewModel.HasPendingOperations);
    }

    [Fact]
    public async Task Load_MetadataFailureRetainsCacheAndAllowsNextRefresh()
    {
        var f = await Fixture.CreateAsync(projectCount: 2);
        f.Git.FailOriginRead = true;

        await f.ViewModel.LoadAsync().WaitAsync(Timeout);

        Assert.All(f.ViewModel.Items, item =>
        {
            Assert.Equal(Fixture.Origin, item.OriginUrlDisplay);
            Assert.True(item.MetadataReadFailed);
            Assert.Contains("PROJECT_METADATA_READ_FAILED", item.MetadataWarning);
            Assert.Contains("Projects.CachedMetadata", item.MetadataWarning);
            Assert.False(item.IsBusy);
        });
        Assert.False(f.ViewModel.HasPendingOperations);
        Assert.True(f.ViewModel.CanManageGroups);
        f.Git.FailOriginRead = false;

        await f.ViewModel.LoadAsync().WaitAsync(Timeout);

        Assert.Equal(4, f.Git.ValidationCount);
        Assert.All(f.ViewModel.Items, item => Assert.False(item.MetadataReadFailed));
    }

    [Fact]
    public async Task Load_UnexpectedRowFailureKeepsCacheAndContinuesOtherBatches()
    {
        var f = await Fixture.CreateAsync(projectCount: 6);
        var failedProject = (await f.Projects.GetAllAsync())[0];
        f.BindingReads.ThrowOnProjectId = failedProject.Id;
        const string newOrigin = "https://example.org/team/after-refresh.git";
        f.Git.Origin = newOrigin;

        await f.ViewModel.LoadAsync().WaitAsync(Timeout);

        var failedItem = Assert.Single(f.ViewModel.Items, item => item.Project.Id == failedProject.Id);
        Assert.True(failedItem.MetadataReadFailed);
        Assert.Equal(Fixture.Origin, failedItem.OriginUrlDisplay);
        Assert.All(f.ViewModel.Items.Where(item => item != failedItem), item =>
        {
            Assert.False(item.MetadataReadFailed);
            Assert.Equal(newOrigin, item.OriginUrlDisplay);
        });
        Assert.Equal(6, f.Git.ValidationCount);
        Assert.False(f.ViewModel.HasPendingOperations);
        Assert.False(f.ViewModel.HasProjectOperations);
        Assert.True(f.ViewModel.CanManageGroups);
        Assert.All(f.ViewModel.Items, item => Assert.False(item.IsBusy));
        f.BindingReads.ThrowOnProjectId = null;

        await f.ViewModel.LoadAsync().WaitAsync(Timeout);

        Assert.Equal(6, f.ViewModel.FilteredItems.Count);
        Assert.False(failedItem.MetadataReadFailed);
        Assert.Equal(newOrigin, failedItem.OriginUrlDisplay);
        Assert.False(f.ViewModel.HasPendingOperations);
    }

    [Fact]
    public async Task Load_InitialStorageFailureReleasesPendingLoadAndAllowsRetry()
    {
        var f = await Fixture.CreateAsync(projectCount: 1);
        f.Projects.ThrowOnListRead = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ViewModel.LoadAsync().WaitAsync(Timeout));

        Assert.False(f.ViewModel.HasPendingOperations);
        Assert.True(f.ViewModel.CanManageGroups);
        f.Projects.ThrowOnListRead = false;

        await f.ViewModel.LoadAsync().WaitAsync(Timeout);

        Assert.Single(f.ViewModel.FilteredItems);
        Assert.False(f.ViewModel.HasPendingOperations);
    }

    private sealed class Fixture
    {
        public const string Origin = "https://example.org/team/demo.git";
        public SafeProjectRepository Projects { get; } = new();
        public ControlledGitService Git { get; } = new();
        public FaultingBindingRepository BindingReads { get; private set; } = null!;
        public ProjectsViewModel ViewModel { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(int projectCount, bool accountEnabled = true)
        {
            var f = new Fixture();
            // 这些仓储只在启动加载前写入；后台阶段仅并发读取。
            var accounts = new FakeAccountRepository();
            var bindings = new FakeBindingRepository();
            var account = new Account { Alias = "test", GitName = "test", GitEmail = "test@example.org", IsDefault = true, Enabled = accountEnabled };
            await accounts.AddAsync(account);
            var root = Path.Combine(Path.GetTempPath(), "gitbinder-load-fake-" + Guid.NewGuid().ToString("N"));
            for (var i = 0; i < projectCount; i++)
            {
                var path = Path.Combine(root, "project-" + i);
                var project = new Project
                {
                    Name = "project-" + i, RepositoryPath = path, CanonicalPath = path,
                    GitDir = Path.Combine(path, ".git"), CurrentBranch = "main", OriginUrl = Origin,
                    RemoteProtocol = RemoteProtocol.Https, RemoteHost = "example.org",
                };
                await f.Projects.AddAsync(project);
                await bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = account.Id });
            }
            f.BindingReads = new FaultingBindingRepository(bindings);
            var resolver = new EffectiveAccountResolver(new FakeGlobalModeState(), bindings, accounts);
            var transfer = new FakeGitTransfer();
            var bindingService = new BindingService(f.BindingReads, accounts, f.Projects, new FakeSecretStore(),
                f.Git, new FakeGitConfigApplier(), new FakeSnapshotRepository(), transfer, resolver);
            var paths = new IdentityPathNormalizer();
            var projectService = new ProjectService(f.Projects, f.Git, paths, accounts, bindingService);
            var groups = new FakeProjectGroupRepository();
            var transfers = new ProjectTransferService(transfer, projectService, f.Projects, accounts,
                bindingService, resolver, paths, groups);
            f.ViewModel = new ProjectsViewModel(f.Projects, projectService, f.BindingReads, accounts,
                bindingService, resolver, new TestLocalization(), () => throw new InvalidOperationException("No picker expected"),
                transfers, new ProjectGroupService(groups), (_, _, _) => Task.FromResult(false));
            return f;
        }
    }

    /// <summary>复用现有 Fake，所有同步完成的字典访问放在同一锁内，允许不同项目并行更新缓存。</summary>
    private sealed class SafeProjectRepository : IProjectRepository
    {
        private readonly FakeProjectRepository _inner = new();
        private readonly object _sync = new();
        private int _listReadCount;
        public int ListReadCount => Volatile.Read(ref _listReadCount);
        public bool ThrowOnListRead { get; set; }
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default)
        { lock (_sync) return _inner.GetByIdAsync(id, ct); }
        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listReadCount);
            if (ThrowOnListRead) throw new InvalidOperationException("Controlled initial storage failure");
            lock (_sync) return _inner.GetAllAsync(ct);
        }
        public Task<Project?> FindByPathAsync(string canonicalPath, CancellationToken ct = default)
        { lock (_sync) return _inner.FindByPathAsync(canonicalPath, ct); }
        public Task AddAsync(Project project, CancellationToken ct = default)
        { lock (_sync) return _inner.AddAsync(project, ct); }
        public Task UpdateAsync(Project project, CancellationToken ct = default)
        { lock (_sync) return _inner.UpdateAsync(project, ct); }
        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        { lock (_sync) return _inner.DeleteAsync(id, ct); }
    }

    private sealed class FaultingBindingRepository(FakeBindingRepository inner) : IBindingRepository
    {
        public Guid? ThrowOnProjectId { get; set; }
        public Task<Binding?> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
            => ThrowOnProjectId == projectId ? throw new InvalidOperationException("Controlled snapshot failure")
                : inner.GetByProjectIdAsync(projectId, ct);
        public Task<IReadOnlyList<Binding>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
        public Task<IReadOnlyList<Binding>> GetByAccountIdAsync(Guid accountId, CancellationToken ct = default)
            => inner.GetByAccountIdAsync(accountId, ct);
        public Task<int> CountByAccountIdAsync(Guid accountId, CancellationToken ct = default) => inner.CountByAccountIdAsync(accountId, ct);
        public Task AddAsync(Binding binding, CancellationToken ct = default) => inner.AddAsync(binding, ct);
        public Task UpdateAsync(Binding binding, CancellationToken ct = default) => inner.UpdateAsync(binding, ct);
        public Task DeleteByProjectIdAsync(Guid projectId, CancellationToken ct = default) => inner.DeleteByProjectIdAsync(projectId, ct);
    }

    private sealed class ControlledGitService : IGitService
    {
        private readonly FakeGitService _inner = new();
        private readonly Channel<string> _entries = Channel.CreateUnbounded<string>();
        private readonly object _sync = new();
        private TaskCompletionSource? _validationRelease;
        private int _validationCount;
        private int _activeValidations;
        private int _peakActiveValidations;
        public string Origin { get; set; } = Fixture.Origin;
        public bool FailOriginRead { get; set; }
        public int ValidationCount { get { lock (_sync) return _validationCount; } }
        public int ActiveValidations { get { lock (_sync) return _activeValidations; } }
        public int PeakActiveValidations { get { lock (_sync) return _peakActiveValidations; } }
        public void BlockValidation() => _validationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseValidation() => _validationRelease?.TrySetResult();

        public async Task<IReadOnlyList<string>> WaitForEntriesAsync(int count)
        {
            var result = new List<string>();
            using var timeout = new CancellationTokenSource(Timeout);
            for (var i = 0; i < count; i++) result.Add(await _entries.Reader.ReadAsync(timeout.Token));
            return result;
        }

        public async Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default)
        {
            lock (_sync)
            {
                _validationCount++;
                _activeValidations++;
                _peakActiveValidations = Math.Max(_peakActiveValidations, _activeValidations);
            }
            _entries.Writer.TryWrite(path);
            try
            {
                if (_validationRelease is { } release) await release.Task.WaitAsync(ct);
                return true;
            }
            finally { lock (_sync) _activeValidations--; }
        }

        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(path);
        public Task<string?> GetGitDirAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(Path.Combine(path, ".git"));
        public Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(FailOriginRead ? null : Origin);
        public Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default) => GetOriginUrlAsync(path, ct);
        public Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>("main");
        public Task<string?> GetGitVersionAsync(CancellationToken ct = default) => _inner.GetGitVersionAsync(ct);
        public (RemoteProtocol Protocol, string Host) ParseRemote(string url) => _inner.ParseRemote(url);
        public Task<Result> InitializeRepositoryAsync(string path, CancellationToken ct = default)
            => throw new InvalidOperationException("Loading must not initialize repositories");
        public Task<Result> SetTransferOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("Loading must not change Git configuration");
        public Task<Result> SetOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("Loading must not change Git configuration");
    }

    private sealed class IdentityPathNormalizer : IPathNormalizer
    {
        public string Normalize(string path) => path;
        public string Canonicalize(string path) => path;
        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        public string ToKey(string path) => path.ToUpperInvariant();
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
