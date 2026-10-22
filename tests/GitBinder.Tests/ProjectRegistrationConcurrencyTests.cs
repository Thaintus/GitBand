using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.Projects;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>只使用内存仓库与受控写入闸门验证登记竞争，不创建目录或执行 Git</summary>
public sealed class ProjectRegistrationConcurrencyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Registration_SameDestinationIsCommittedOnlyOnce(bool firstUpdates, bool secondUpdates)
    {
        var f = new Fixture();
        var firstProject = firstUpdates ? f.Seed("first") : null;
        var secondProject = secondUpdates ? f.Seed("second") : null;
        f.Repository.HoldFirstWrite = true;

        var first = f.RegisterAsync(firstProject, f.Target);
        await f.Repository.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<Result<Project>>? second = null;
        try
        {
            second = f.RegisterAsync(secondProject, f.Target);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, f.Repository.WriteAttempts);
        }
        finally { f.Repository.ReleaseFirstWrite.TrySetResult(); }

        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        var duplicate = await second!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("PROJECT_ALREADY_EXISTS", duplicate.Error!.Code);
        Assert.Equal(1, f.Repository.WriteAttempts);
        var stored = await f.Repository.GetAllAsync();
        Assert.Single(stored, project => f.Paths.Equals(project.RepositoryPath, f.Target));
        if (secondProject is not null)
            Assert.Equal(secondProject.RepositoryPath, (await f.Repository.GetByIdAsync(secondProject.Id))!.RepositoryPath);
    }

    [Fact]
    public async Task Registration_CancelledWaiterDoesNotReleaseAnotherOperationsReservation()
    {
        var f = new Fixture();
        var existing = f.Seed("existing");
        f.Repository.HoldFirstWrite = true;
        var first = f.RegisterAsync(null, f.Target);
        await f.Repository.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<Result<Project>>? next = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var cancelled = f.RegisterAsync(existing, f.Target, cancellation.Token);
            Assert.False(cancelled.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await cancelled.WaitAsync(TimeSpan.FromSeconds(5));
            });

            next = f.RegisterAsync(null, f.OtherTarget);
            Assert.False(next.IsCompleted);
            Assert.False(first.IsCompleted);
            Assert.Equal(1, f.Repository.WriteAttempts);
        }
        finally { f.Repository.ReleaseFirstWrite.TrySetResult(); }

        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.True((await next!.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(2, f.Repository.WriteAttempts);
        Assert.Equal(existing.RepositoryPath, (await f.Repository.GetByIdAsync(existing.Id))!.RepositoryPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_PersistenceFailureReleasesReservation(bool update)
    {
        var f = new Fixture();
        var existing = update ? f.Seed("existing") : null;
        f.Repository.FailNextWrite = true;

        if (update)
        {
            var failed = await f.RegisterAsync(existing, f.Target).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("PROJECT_PATH_UPDATE_FAILED", failed.Error!.Code);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                await f.RegisterAsync(null, f.Target).WaitAsync(TimeSpan.FromSeconds(5));
            });
        }

        var retried = await f.RegisterAsync(existing, f.Target).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(retried.IsSuccess);
        Assert.Equal(2, f.Repository.WriteAttempts);
        Assert.Single(await f.Repository.GetAllAsync(), project => f.Paths.Equals(project.RepositoryPath, f.Target));
    }

    [Fact]
    public async Task Registration_PendingWriteDoesNotBlockOtherProjectsMetadataRefresh()
    {
        var f = new Fixture();
        var existing = f.Seed("existing");
        f.Repository.HoldFirstWrite = true;
        var registration = f.RegisterAsync(null, f.Target);
        await f.Repository.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var refresh = f.Service.RefreshMetadataAsync(existing.Id);
            Assert.True(refresh.IsCompletedSuccessfully);
            Assert.True((await refresh).IsSuccess);
            Assert.False(registration.IsCompleted);
        }
        finally { f.Repository.ReleaseFirstWrite.TrySetResult(); }
        Assert.True((await registration.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    private sealed class Fixture
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-registration-" + Guid.NewGuid().ToString("N"));
        public ControlledProjectRepository Repository { get; } = new();
        public PlatformPathNormalizer Paths { get; } = new();
        public ProjectService Service { get; }
        public string Target => Path.Combine(_root, "shared");
        public string OtherTarget => Path.Combine(_root, "other");

        public Fixture()
        {
            var accounts = new FakeAccountRepository();
            var git = new PathGitService();
            var bindings = new BindingService(new FakeBindingRepository(), accounts, Repository,
                new FakeSecretStore(), git, new FakeGitConfigApplier(), new FakeSnapshotRepository(), new FakeGitTransfer());
            Service = new ProjectService(Repository, git, Paths, accounts, bindings);
        }

        public Project Seed(string name)
        {
            var path = Path.Combine(_root, name);
            var project = new Project
            {
                Name = name, RepositoryPath = path, CanonicalPath = Paths.Canonicalize(path),
                GitDir = Path.Combine(path, ".git"), CurrentBranch = "main",
            };
            Repository.Seed(project);
            return project;
        }

        public Task<Result<Project>> RegisterAsync(Project? existing, string path, CancellationToken ct = default)
            => existing is null ? Service.AddAsync(path, ct, bindDefaultAccount: false)
                : Service.UpdatePathAsync(existing.Id, path, ct);
    }

    private sealed class ControlledProjectRepository : IProjectRepository
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Project> _projects = [];
        private int _writeAttempts;
        public bool HoldFirstWrite { get; set; }
        public bool FailNextWrite { get; set; }
        public int WriteAttempts => Volatile.Read(ref _writeAttempts);
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Seed(Project project)
        {
            lock (_sync) _projects.Add(project.Id, project);
        }

        public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) return Task.FromResult(_projects.GetValueOrDefault(id));
        }

        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) return Task.FromResult<IReadOnlyList<Project>>(_projects.Values.ToArray());
        }

        public Task<Project?> FindByPathAsync(string canonicalPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) return Task.FromResult(_projects.Values.FirstOrDefault(project => project.CanonicalPath == canonicalPath));
        }

        public Task AddAsync(Project project, CancellationToken ct = default) => PersistAsync(project, ct);
        public Task UpdateAsync(Project project, CancellationToken ct = default) => PersistAsync(project, ct);

        private async Task PersistAsync(Project project, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _writeAttempts);
            if (HoldFirstWrite && attempt == 1)
            {
                FirstWriteEntered.TrySetResult();
                await ReleaseFirstWrite.Task.WaitAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("fixture registration write failed");
            }
            lock (_sync) _projects[project.Id] = project;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) _projects.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class PlatformPathNormalizer : IPathNormalizer
    {
        public string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        public string Normalize(string path) => Canonicalize(path);
        public string ToKey(string path) => Canonicalize(path);
        public bool Equals(string left, string right) => string.Equals(Canonicalize(left), Canonicalize(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed class PathGitService : IGitService
    {
        public Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(path);
        public Task<string?> GetGitDirAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(Path.Combine(path, ".git"));
        public Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(string.Empty);
        public Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default) => GetOriginUrlAsync(path, ct);
        public Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>("main");
        public (RemoteProtocol Protocol, string Host) ParseRemote(string url) => (RemoteProtocol.Unknown, string.Empty);
        public Task<string?> GetGitVersionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result> InitializeRepositoryAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result> SetOriginUrlAsync(string path, string originUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result> SetTransferOriginUrlAsync(string path, string originUrl, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
