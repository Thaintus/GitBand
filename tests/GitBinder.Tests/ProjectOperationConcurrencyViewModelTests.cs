using System.Globalization;
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

/// <summary>项目并发只使用内存服务和独立空目录，不启动窗口、Git 或网络请求</summary>
public sealed class ProjectOperationConcurrencyViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Pull_DifferentProjectsRemainAvailableAndCanUseTheSameCommandConcurrently()
    {
        using var f = await Fixture.CreateAsync();
        var first = f.FirstItem;
        var second = f.SecondItem;
        var firstTask = f.ViewModel.PullCommand.ExecuteAsync(first);
        Task secondTask = Task.CompletedTask;
        try
        {
            await f.FirstPull.Entered.Task.WaitAsync(Timeout);

            Assert.True(first.IsBusy);
            Assert.False(first.CanManageProject);
            Assert.False(first.CanBindAccount);
            Assert.False(first.CanTest);
            Assert.False(first.CanPull);
            Assert.False(first.CanUnbind);
            Assert.False(first.CanAssignGroup);
            Assert.True(first.HasOperationProgress);
            Assert.True(first.CanCancelOperation);
            Assert.False(second.IsBusy);
            Assert.True(second.CanManageProject);
            Assert.True(second.CanBindAccount);
            Assert.True(second.CanTest);
            Assert.True(second.CanPull);
            Assert.True(second.CanUnbind);
            Assert.True(second.CanAssignGroup);
            Assert.True(f.ViewModel.CanManageProjects);
            Assert.False(f.ViewModel.CanPullGroup);
            Assert.True(f.ViewModel.HasProjectOperations);
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.False(f.ViewModel.IsTransferBusy);
            Assert.True(f.ViewModel.PullCommand.CanExecute(second));

            secondTask = f.ViewModel.PullCommand.ExecuteAsync(second);
            await f.SecondPull.Entered.Task.WaitAsync(Timeout);

            Assert.Equal(2, f.Transfer.PullCount);
            Assert.True(first.IsBusy);
            Assert.True(second.IsBusy);
            f.FirstPull.Complete();
            await firstTask.WaitAsync(Timeout);

            Assert.False(first.IsBusy);
            Assert.True(first.CanPull);
            Assert.True(second.IsBusy);
            Assert.True(second.CanCancelOperation);
            Assert.True(second.HasOperationProgress);
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.True(f.ViewModel.HasProjectOperations);
            Assert.False(f.ViewModel.CanPullGroup);

            f.SecondPull.Complete();
            await secondTask.WaitAsync(Timeout);

            Assert.False(second.IsBusy);
            Assert.False(f.ViewModel.HasPendingOperations);
            Assert.False(f.ViewModel.HasProjectOperations);
            Assert.True(f.ViewModel.CanPullGroup);
        }
        finally
        {
            f.FirstPull.Complete();
            f.SecondPull.Complete();
            await Task.WhenAll(firstTask, secondTask).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Pull_SameProjectRejectsDuplicatePullAndAuthenticationTest()
    {
        using var f = await Fixture.CreateAsync();
        var item = f.FirstItem;
        var task = f.ViewModel.PullCommand.ExecuteAsync(item);
        try
        {
            await f.FirstPull.Entered.Task.WaitAsync(Timeout);

            await f.ViewModel.PullCommand.ExecuteAsync(item).WaitAsync(Timeout);
            await f.ViewModel.TestBindingCommand.ExecuteAsync(item).WaitAsync(Timeout);

            Assert.Equal(1, f.Transfer.PullCount);
            Assert.Equal(0, f.Transfer.TestCount);
            Assert.True(item.IsBusy);
            Assert.True(item.CanCancelOperation);
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.Same(item, f.FirstItem);
        }
        finally
        {
            f.FirstPull.Complete();
            await task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task CancelProjectOperation_CancelsOnlyTheChosenProject()
    {
        using var f = await Fixture.CreateAsync();
        var firstTask = f.ViewModel.PullCommand.ExecuteAsync(f.FirstItem);
        var secondTask = f.ViewModel.PullCommand.ExecuteAsync(f.SecondItem);
        try
        {
            await Task.WhenAll(f.FirstPull.Entered.Task, f.SecondPull.Entered.Task).WaitAsync(Timeout);

            f.ViewModel.CancelProjectOperationCommand.Execute(f.FirstItem);
            await firstTask.WaitAsync(Timeout);

            Assert.True(f.FirstPull.Token.IsCancellationRequested);
            Assert.False(f.SecondPull.Token.IsCancellationRequested);
            Assert.False(f.FirstItem.IsBusy);
            Assert.True(f.SecondItem.IsBusy);
            Assert.True(f.SecondItem.CanCancelOperation);
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.False(secondTask.IsCompleted);

            f.SecondPull.Complete();
            await secondTask.WaitAsync(Timeout);

            Assert.False(f.ViewModel.HasPendingOperations);
            Assert.False(f.SecondItem.IsBusy);
            Assert.Equal(2, f.Transfer.PullCount);
        }
        finally
        {
            f.FirstPull.Complete();
            f.SecondPull.Complete();
            await Task.WhenAll(firstTask, secondTask).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task PullAndReload_KeepOtherProjectDraftsAndItemInstances()
    {
        using var f = await Fixture.CreateAsync();
        var first = f.FirstItem;
        var second = f.SecondItem;
        var task = f.ViewModel.PullCommand.ExecuteAsync(first);
        try
        {
            await f.FirstPull.Entered.Task.WaitAsync(Timeout);
            f.ViewModel.StartEditRemoteCommand.Execute(second);
            f.ViewModel.StartEditPathCommand.Execute(second);
            const string draftOrigin = "https://example.org/team/not-saved.git";
            var draftPath = Path.Combine(second.PathText, "not-saved");
            second.EditingOriginUrl = draftOrigin;
            second.EditingPath = draftPath;

            await f.ViewModel.LoadAsync().WaitAsync(Timeout);

            Assert.Same(first, f.FirstItem);
            Assert.Same(second, f.SecondItem);
            Assert.True(first.IsBusy);
            Assert.True(second.IsRemoteEditorOpen);
            Assert.True(second.IsPathEditorOpen);
            Assert.Equal(draftOrigin, second.EditingOriginUrl);
            Assert.Equal(draftPath, second.EditingPath);

            f.FirstPull.Complete();
            await task.WaitAsync(Timeout);

            Assert.Same(first, f.FirstItem);
            Assert.Same(second, f.SecondItem);
            Assert.True(second.IsRemoteEditorOpen);
            Assert.True(second.IsPathEditorOpen);
            Assert.Equal(draftOrigin, second.EditingOriginUrl);
            Assert.Equal(draftPath, second.EditingPath);
            Assert.Equal(Fixture.Origin, second.Project.OriginUrl);
            Assert.Equal(f.SecondProject.RepositoryPath, second.PathText);
        }
        finally
        {
            f.FirstPull.Complete();
            await task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Pull_KeepsProjectBusyUntilItsFinalCardRefreshCompletes()
    {
        using var f = await Fixture.CreateAsync();
        var item = f.FirstItem;
        var task = f.ViewModel.PullCommand.ExecuteAsync(item);
        try
        {
            await f.FirstPull.Entered.Task.WaitAsync(Timeout);
            f.BindingReads.BlockedProjectId = item.Project.Id;
            f.FirstPull.Complete();
            await f.BindingReads.Entered.Task.WaitAsync(Timeout);

            Assert.True(item.IsBusy);
            Assert.True(f.ViewModel.HasProjectOperations);
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.False(f.ViewModel.CanPullGroup);
            Assert.False(task.IsCompleted);
            await f.ViewModel.PullCommand.ExecuteAsync(item).WaitAsync(Timeout);
            Assert.Equal(1, f.Transfer.PullCount);

            f.BindingReads.Release.TrySetResult();
            await task.WaitAsync(Timeout);

            Assert.Same(item, f.FirstItem);
            Assert.False(item.IsBusy);
            Assert.False(f.ViewModel.HasPendingOperations);
            Assert.True(f.ViewModel.CanPullGroup);
        }
        finally
        {
            f.FirstPull.Complete();
            f.BindingReads.Release.TrySetResult();
            await task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task ExternalOperation_CannotOverlapRowWorkAndKeepsItsGateThroughReload()
    {
        using var f = await Fixture.CreateAsync();
        var first = f.FirstItem;
        var second = f.SecondItem;
        var task = f.ViewModel.PullCommand.ExecuteAsync(first);
        var externalStarted = false;
        try
        {
            await f.FirstPull.Entered.Task.WaitAsync(Timeout);
            Assert.False(f.ViewModel.TryBeginExternalOperation());
            Assert.True(second.CanPull);

            f.FirstPull.Complete();
            await task.WaitAsync(Timeout);
            externalStarted = f.ViewModel.TryBeginExternalOperation();

            Assert.True(externalStarted);
            Assert.False(f.ViewModel.TryBeginExternalOperation());
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.False(f.ViewModel.CanManageProjects);
            Assert.False(first.CanPull);
            Assert.False(second.CanTest);
            await f.ViewModel.PullCommand.ExecuteAsync(second).WaitAsync(Timeout);
            await f.ViewModel.TestBindingCommand.ExecuteAsync(first).WaitAsync(Timeout);
            Assert.Equal(1, f.Transfer.PullCount);
            Assert.Equal(0, f.Transfer.TestCount);

            await f.ViewModel.ReloadAfterExternalOperationAsync().WaitAsync(Timeout);

            Assert.Same(first, f.FirstItem);
            Assert.Same(second, f.SecondItem);
            Assert.True(f.ViewModel.HasPendingOperations);
            Assert.False(f.ViewModel.CanManageProjects);
            Assert.False(second.CanPull);

            f.ViewModel.EndExternalOperation();
            externalStarted = false;
            Assert.False(f.ViewModel.HasPendingOperations);
            Assert.True(first.CanPull);
            Assert.True(second.CanTest);
        }
        finally
        {
            f.FirstPull.Complete();
            if (externalStarted) f.ViewModel.EndExternalOperation();
            await task.WaitAsync(Timeout);
        }
    }

    private sealed class ControlledPull
    {
        private readonly TaskCompletionSource<Result> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public Task<Result> RunAsync(CancellationToken ct)
        {
            Token = ct;
            Entered.TrySetResult();
            return _completion.Task.WaitAsync(ct);
        }

        public void Complete() => _completion.TrySetResult(Result.Success());
    }

    private sealed class Fixture : IDisposable
    {
        public const string Origin = "https://example.org/team/demo.git";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-project-concurrency-" + Guid.NewGuid().ToString("N"));
        public FakeProjectRepository Projects { get; } = new();
        public FakeAccountRepository Accounts { get; } = new();
        public FakeBindingRepository Bindings { get; } = new();
        public DeferredBindingRepository BindingReads { get; }
        public FakeGitTransfer Transfer { get; } = new();
        public ControlledPull FirstPull { get; } = new();
        public ControlledPull SecondPull { get; } = new();
        public Project FirstProject { get; } = new() { Name = "first" };
        public Project SecondProject { get; } = new() { Name = "second" };
        public ProjectsViewModel ViewModel { get; private set; } = null!;
        public ProjectItemViewModel FirstItem => ViewModel.Items.Single(item => item.Project.Id == FirstProject.Id);
        public ProjectItemViewModel SecondItem => ViewModel.Items.Single(item => item.Project.Id == SecondProject.Id);

        private Fixture() => BindingReads = new DeferredBindingRepository(Bindings);

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            var account = new Account
            {
                Alias = "test account", GitName = "test", GitEmail = "test@example.org", IsDefault = true,
                Enabled = true, AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fake-secret-id",
            };
            await f.Accounts.AddAsync(account);
            foreach (var project in new[] { f.FirstProject, f.SecondProject })
            {
                var path = Path.Combine(f._root, project.Name);
                Directory.CreateDirectory(path);
                project.RepositoryPath = project.CanonicalPath = path;
                project.GitDir = Path.Combine(path, ".git");
                project.OriginUrl = Origin;
                project.RemoteProtocol = RemoteProtocol.Https;
                project.RemoteHost = "example.org";
                project.CurrentBranch = "main";
                await f.Projects.AddAsync(project);
                await f.Bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = account.Id });
            }

            f.Transfer.OnPull = (path, _, ct) => path == f.FirstProject.RepositoryPath
                ? f.FirstPull.RunAsync(ct) : f.SecondPull.RunAsync(ct);
            var git = new PathAwareGitService();
            var paths = new IdentityPathNormalizer();
            var resolver = new EffectiveAccountResolver(new FakeGlobalModeState(), f.Bindings, f.Accounts);
            var bindings = new BindingService(f.BindingReads, f.Accounts, f.Projects, new FakeSecretStore(),
                git, new FakeGitConfigApplier(), new FakeSnapshotRepository(), f.Transfer, resolver);
            var projects = new ProjectService(f.Projects, git, paths, f.Accounts, bindings);
            var groups = new FakeProjectGroupRepository();
            var transfers = new ProjectTransferService(f.Transfer, projects, f.Projects, f.Accounts,
                bindings, resolver, paths, groups);
            f.ViewModel = new ProjectsViewModel(f.Projects, projects, f.BindingReads, f.Accounts, bindings,
                resolver, new TestLocalization(), () => throw new InvalidOperationException("No picker expected"),
                transfers, new ProjectGroupService(groups), (_, _, _) => Task.FromResult(false));
            await f.ViewModel.LoadAsync();
            return f;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class DeferredBindingRepository(FakeBindingRepository inner) : IBindingRepository
    {
        public Guid? BlockedProjectId { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Binding?> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        {
            if (projectId == BlockedProjectId)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await inner.GetByProjectIdAsync(projectId, ct);
        }

        public Task<IReadOnlyList<Binding>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
        public Task<IReadOnlyList<Binding>> GetByAccountIdAsync(Guid accountId, CancellationToken ct = default)
            => inner.GetByAccountIdAsync(accountId, ct);
        public Task<int> CountByAccountIdAsync(Guid accountId, CancellationToken ct = default)
            => inner.CountByAccountIdAsync(accountId, ct);
        public Task AddAsync(Binding binding, CancellationToken ct = default) => inner.AddAsync(binding, ct);
        public Task UpdateAsync(Binding binding, CancellationToken ct = default) => inner.UpdateAsync(binding, ct);
        public Task DeleteByProjectIdAsync(Guid projectId, CancellationToken ct = default)
            => inner.DeleteByProjectIdAsync(projectId, ct);
    }

    private sealed class PathAwareGitService : IGitService
    {
        private readonly FakeGitService _inner = new() { OriginUrl = Fixture.Origin };
        public Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(path);
        public Task<string?> GetGitDirAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(Path.Combine(path, ".git"));
        public Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default) => _inner.GetOriginUrlAsync(path, ct);
        public Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default) => _inner.GetTransferOriginUrlAsync(path, ct);
        public Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default) => _inner.GetCurrentBranchAsync(path, ct);
        public Task<string?> GetGitVersionAsync(CancellationToken ct = default) => _inner.GetGitVersionAsync(ct);
        public (RemoteProtocol Protocol, string Host) ParseRemote(string url) => _inner.ParseRemote(url);
        public Task<Result> InitializeRepositoryAsync(string path, CancellationToken ct = default)
            => throw new InvalidOperationException("Initialization is outside concurrency tests");
        public Task<Result> SetTransferOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("Remote writes are outside concurrency tests");
        public Task<Result> SetOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("Remote writes are outside concurrency tests");
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
        public string GetString(string key, params object[] args)
            => args.Length == 0 ? key : key + " | " + string.Join(" | ", args);
        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
