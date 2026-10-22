using GitBinder.Application.Bindings;
using GitBinder.Application.Projects;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;
using GitBinder.Domain.Services;
using GitBinder.Platform.Windows;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public sealed class ProjectTransferServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-clone-service-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeProjectRepository _projects = new();
    private readonly FakeProjectGroupRepository _groups = new();
    private readonly FakeBindingRepository _bindings = new();
    private readonly FakeGitTransfer _transfer = new();
    private readonly FakeGitService _git = new();
    private readonly FakeGitConfigApplier _config = new();
    private readonly FakeGlobalModeState _global = new();
    private readonly ProjectTransferService _sut;

    public ProjectTransferServiceTests()
    {
        Directory.CreateDirectory(_root);
        var resolver = new EffectiveAccountResolver(_global, _bindings, _accounts);
        var bindingService = new BindingService(_bindings, _accounts, _projects, new FakeSecretStore(),
            _git, _config, new FakeSnapshotRepository(), _transfer, resolver);
        var pathNormalizer = new WindowsPathNormalizer();
        var projectService = new ProjectService(_projects, _git, pathNormalizer, _accounts, bindingService);
        _sut = new ProjectTransferService(_transfer, projectService, _projects, _accounts, bindingService, resolver, pathNormalizer, _groups);
        _transfer.OnClone = path => { Directory.CreateDirectory(path); _git.RepositoryRoot = path; };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clone_UsesEffectiveAccountAndBindsSelectedWithoutDefaultIntermediate(bool global)
    {
        var selected = new Account { GitName = "selected" };
        var fallback = new Account { GitName = "default", IsDefault = true };
        await _accounts.AddAsync(selected);
        await _accounts.AddAsync(fallback);
        _global.IsEnabled = global;
        _global.GlobalAccountId = fallback.Id;

        var result = await _sut.CloneAsync("https://example.org/repo.git", _root, "new", selected.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(global ? fallback.Id : selected.Id, _transfer.UsedAccountId);
        var project = Assert.Single(await _projects.GetAllAsync());
        Assert.Equal(selected.Id, (await _bindings.GetByProjectIdAsync(project.Id))!.AccountId);
        Assert.Equal(1, _config.ApplyIdentityCount);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("bad:name")]
    public async Task Clone_InvalidFolderNeverCallsGit(string folder)
    {
        var account = new Account();
        await _accounts.AddAsync(account);
        var result = await _sut.CloneAsync("https://example.org/repo.git", _root, folder, account.Id);
        Assert.Equal("CLONE_FOLDER_INVALID", result.Error!.Code);
        Assert.Equal(0, _transfer.CloneCount);
    }

    [Fact]
    public async Task Clone_ExistingTargetPreservesContents()
    {
        var target = Path.Combine(_root, "existing");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "preserve");
        var account = new Account();
        await _accounts.AddAsync(account);

        var result = await _sut.CloneAsync("https://example.org/repo.git", _root, "existing", account.Id);

        Assert.Equal("CLONE_TARGET_EXISTS", result.Error!.Code);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Equal(0, _transfer.CloneCount);
    }

    [Fact]
    public async Task Clone_FailureLeavesPartialDirectoryButDoesNotRegister()
    {
        var account = new Account();
        await _accounts.AddAsync(account);
        _transfer.CloneResult = Result.Failure(new DomainError("CLONE_FAILED"));

        var result = await _sut.CloneAsync("https://example.org/repo.git", _root, "partial", account.Id);

        Assert.False(result.IsSuccess);
        Assert.True(Directory.Exists(Path.Combine(_root, "partial")));
        Assert.Empty(await _projects.GetAllAsync());
    }

    [Fact]
    public async Task Clone_BindingFailureIsReportedWithoutDeletingRegisteredProject()
    {
        var account = new Account();
        await _accounts.AddAsync(account);
        _config.ThrowOnApply = true;

        var result = await _sut.CloneAsync("https://example.org/repo.git", _root, "new", account.Id);

        Assert.Equal("CLONE_BIND_FAILED", result.Error!.Code);
        Assert.Single(await _projects.GetAllAsync());
        Assert.True(Directory.Exists(Path.Combine(_root, "new")));
    }

    [Fact]
    public async Task Pull_DifferentProjectsOverlap_ButSameProjectAndBatchAreRejected()
    {
        await _accounts.AddAsync(new Account { IsDefault = true });
        var first = new GitBinder.Domain.Projects.Project { RepositoryPath = "one", GitDir = "one/.git" };
        var second = new GitBinder.Domain.Projects.Project { RepositoryPath = "two", GitDir = "two/.git" };
        await _projects.AddAsync(first);
        await _projects.AddAsync(second);
        var release = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transfer.OnPull = (_, _, _) => release.Task;

        var firstPull = _sut.PullAsync(first.Id);
        var secondPull = _sut.PullAsync(second.Id);
        try
        {
            Assert.Equal(2, _transfer.PullCount);
            Assert.False(firstPull.IsCompleted);
            Assert.False(secondPull.IsCompleted);
            var duplicate = _sut.PullAsync(first.Id);
            var batch = _sut.PullGroupAsync(null, true);
            Assert.True(duplicate.IsCompletedSuccessfully);
            Assert.True(batch.IsCompletedSuccessfully);
            Assert.Equal("TRANSFER_BUSY", duplicate.Result.Error!.Code);
            Assert.Equal("TRANSFER_BUSY", batch.Result.Error!.Code);
        }
        finally { release.TrySetResult(Result.Success()); }

        Assert.True((await firstPull).IsSuccess);
        Assert.True((await secondPull).IsSuccess);
        Assert.True((await _sut.PullGroupAsync(null, true)).IsSuccess);
    }

    [Fact]
    public async Task Clone_CanRunDuringPull_ButAnotherCloneAndBatchAreRejected()
    {
        var account = new Account { IsDefault = true };
        await _accounts.AddAsync(account);
        var project = new GitBinder.Domain.Projects.Project { RepositoryPath = "one", GitDir = "one/.git" };
        await _projects.AddAsync(project);
        var release = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transfer.OnPull = (_, _, _) => release.Task;
        _transfer.CloneResult = Result.Failure(new DomainError("CLONE_FAILED"));
        Task<Result>? repeatedClone = null;
        Task<Result<GroupPullReport>>? batchDuringClone = null;
        _transfer.OnClone = _ =>
        {
            _transfer.OnClone = null;
            repeatedClone = _sut.CloneAsync("https://example.org/repo.git", _root, "another", account.Id);
            batchDuringClone = _sut.PullGroupAsync(null, true);
        };

        var pull = _sut.PullAsync(project.Id);
        try
        {
            Assert.Equal("CLONE_FAILED", (await _sut.CloneAsync("https://example.org/repo.git", _root, "new", account.Id)).Error!.Code);
            Assert.Equal(1, _transfer.CloneCount);
            Assert.NotNull(repeatedClone);
            Assert.NotNull(batchDuringClone);
            Assert.True(repeatedClone.IsCompletedSuccessfully);
            Assert.True(batchDuringClone.IsCompletedSuccessfully);
            Assert.Equal("TRANSFER_BUSY", repeatedClone.Result.Error!.Code);
            Assert.Equal("TRANSFER_BUSY", batchDuringClone.Result.Error!.Code);
            Assert.False(pull.IsCompleted);
        }
        finally { release.TrySetResult(Result.Success()); }
        Assert.True((await pull).IsSuccess);
        Assert.True((await _sut.PullGroupAsync(null, true)).IsSuccess);
        Assert.Equal("CLONE_FAILED", (await _sut.CloneAsync("https://example.org/repo.git", _root, "retry", account.Id)).Error!.Code);
        Assert.Equal(2, _transfer.CloneCount);
    }

    [Fact]
    public async Task Pull_ExceptionAndCancellationReleaseProjectReservation()
    {
        await _accounts.AddAsync(new Account { IsDefault = true });
        var project = new GitBinder.Domain.Projects.Project { RepositoryPath = "one", GitDir = "one/.git" };
        await _projects.AddAsync(project);
        _transfer.OnPull = (_, _, _) => throw new IOException("fixture failure");
        Assert.Equal("PULL_FAILED", (await _sut.PullAsync(project.Id)).Error!.Code);

        using var cancellation = new CancellationTokenSource();
        _transfer.OnPull = (_, _, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        Assert.Equal("TRANSFER_CANCELLED", (await _sut.PullAsync(project.Id, cancellation.Token)).Error!.Code);

        _transfer.OnPull = (_, _, _) => Task.FromResult(Result.Success());
        Assert.True((await _sut.PullAsync(project.Id)).IsSuccess);
        Assert.True((await _sut.PullGroupAsync(null, true)).IsSuccess);
    }

    [Fact]
    public async Task GroupPull_ExcludesOtherOperations_AndReleasesReservation()
    {
        var account = new Account { IsDefault = true };
        await _accounts.AddAsync(account);
        var project = new GitBinder.Domain.Projects.Project { RepositoryPath = "one", GitDir = "one/.git" };
        await _projects.AddAsync(project);
        var release = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transfer.OnPull = (_, _, _) => release.Task;

        var batch = _sut.PullGroupAsync(null, true);
        try
        {
            Assert.False(batch.IsCompleted);
            var pull = _sut.PullAsync(project.Id);
            var clone = _sut.CloneAsync("https://example.org/repo.git", _root, "new", account.Id);
            var anotherBatch = _sut.PullGroupAsync(null, true);
            Assert.True(pull.IsCompletedSuccessfully);
            Assert.True(clone.IsCompletedSuccessfully);
            Assert.True(anotherBatch.IsCompletedSuccessfully);
            Assert.Equal("TRANSFER_BUSY", pull.Result.Error!.Code);
            Assert.Equal("TRANSFER_BUSY", clone.Result.Error!.Code);
            Assert.Equal("TRANSFER_BUSY", anotherBatch.Result.Error!.Code);
            Assert.Equal(1, _transfer.PullCount);
            Assert.Equal(0, _transfer.CloneCount);
        }
        finally { release.TrySetResult(Result.Success()); }

        Assert.True((await batch).IsSuccess);
        Assert.True((await _sut.PullAsync(project.Id)).IsSuccess);
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task GroupPull_ContinuesAfterFailure_AndExcludesOtherGroups()
    {
        var group = new GitBinder.Domain.Projects.ProjectGroup(Guid.NewGuid(), "Work");
        _groups.Groups.Add(group.Id, group);
        await _accounts.AddAsync(new Account { IsDefault = true });
        var first = new GitBinder.Domain.Projects.Project { Name = "one", RepositoryPath = "one", GitDir = "one/.git" };
        var second = new GitBinder.Domain.Projects.Project { Name = "two", RepositoryPath = "two", GitDir = "two/.git" };
        await _projects.AddAsync(first);
        await _projects.AddAsync(second);
        await _projects.AddAsync(new GitBinder.Domain.Projects.Project { Name = "outside" });
        _groups.Memberships.Add(first.Id, group.Id);
        _groups.Memberships.Add(second.Id, group.Id);
        _transfer.OnPull = (path, _, _) => Task.FromResult(path == "one"
            ? Result.Failure(new DomainError("PULL_FAILED")) : Result.Success());

        var result = await _sut.PullGroupAsync(group.Id, false);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(1, result.Value.Succeeded);
        Assert.Equal(1, result.Value.Failed);
        Assert.Equal(2, _transfer.PullCount);
    }

    [Fact]
    public async Task GroupPull_CancelStopsRemaining_AndHoldsTransferLock()
    {
        await _accounts.AddAsync(new Account { IsDefault = true });
        await _projects.AddAsync(new GitBinder.Domain.Projects.Project { Name = "one", GitDir = "one/.git" });
        await _projects.AddAsync(new GitBinder.Domain.Projects.Project { Name = "two", GitDir = "two/.git" });
        using var cancellation = new CancellationTokenSource();
        _transfer.OnPull = async (_, _, _) =>
        {
            Assert.Equal("TRANSFER_BUSY", (await _sut.PullAsync(Guid.NewGuid())).Error!.Code);
            cancellation.Cancel();
            return Result.Failure(new DomainError("TRANSFER_CANCELLED"));
        };

        var result = await _sut.PullGroupAsync(null, true, ct: cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Cancelled);
        Assert.Equal(2, result.Value.Unfinished);
        Assert.Equal(0, result.Value.Failed);
        Assert.Equal(1, _transfer.PullCount);
        Assert.Equal("PROJECT_NOT_FOUND", (await _sut.PullAsync(Guid.NewGuid())).Error!.Code);
    }

    [Fact]
    public async Task GroupPull_MissingGroupDoesNotPullAnything()
    {
        var result = await _sut.PullGroupAsync(Guid.NewGuid(), false);
        Assert.Equal("GROUP_NOT_FOUND", result.Error!.Code);
        Assert.Equal(0, _transfer.PullCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupPull_UsesEachBindingOrGlobalAccount(bool global)
    {
        var firstAccount = new Account();
        var secondAccount = new Account();
        await _accounts.AddAsync(firstAccount);
        await _accounts.AddAsync(secondAccount);
        var first = new GitBinder.Domain.Projects.Project { RepositoryPath = "one", GitDir = "one/.git" };
        var second = new GitBinder.Domain.Projects.Project { RepositoryPath = "two", GitDir = "two/.git" };
        await _projects.AddAsync(first);
        await _projects.AddAsync(second);
        await _bindings.AddAsync(new GitBinder.Domain.Bindings.Binding { ProjectId = first.Id, AccountId = firstAccount.Id });
        await _bindings.AddAsync(new GitBinder.Domain.Bindings.Binding { ProjectId = second.Id, AccountId = secondAccount.Id });
        _global.IsEnabled = global;
        _global.GlobalAccountId = secondAccount.Id;
        var actual = new Dictionary<string, Guid>();
        _transfer.OnPull = (path, account, _) =>
        {
            actual.Add(path, account.Id);
            return Task.FromResult(Result.Success());
        };
        var result = await _sut.PullGroupAsync(null, false);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Succeeded);
        Assert.Equal(global ? secondAccount.Id : firstAccount.Id, actual["one"]);
        Assert.Equal(secondAccount.Id, actual["two"]);
    }
}
