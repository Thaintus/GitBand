using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Projects;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public class ProjectServiceTests
{
    private sealed class FakePathNormalizer : IPathNormalizer
    {
        public string Canonicalize(string path) => path.TrimEnd('\\').ToUpperInvariant();

        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        public string Normalize(string path) => path;

        public string ToKey(string path) => Canonicalize(path);
    }

    private static (
        ProjectService,
        FakeAccountRepository,
        FakeProjectRepository,
        FakeBindingRepository,
        FakeGitService
    ) CreateSut()
    {
        var accountRepo = new FakeAccountRepository();
        var projectRepo = new FakeProjectRepository();
        var bindingRepo = new FakeBindingRepository();
        var gitService = new FakeGitService();
        var snapshotRepo = new FakeSnapshotRepository();
        var secretStore = new FakeSecretStore();
        var gitConfig = new FakeGitConfigApplier();

        var accountService = new AccountService(accountRepo, new Guard(bindingRepo), new SecretService(secretStore), new FakePrivateKeyImporter());
        var bindingService = new BindingService(bindingRepo, accountRepo, projectRepo, secretStore, gitService, gitConfig, snapshotRepo, new FakeGitTransfer());
        var service = new ProjectService(projectRepo, gitService, new FakePathNormalizer(), accountRepo, bindingService);

        return (service, accountRepo, projectRepo, bindingRepo, gitService);
    }

    [Fact]
    public void Add_ThenBindingToDefaultAccount_Applied()
    {
        var (service, accountRepo, projectRepo, bindingRepo, _) = CreateSut();
        var defaultAccount = new Account { Alias = "default", Username = "default", IsDefault = true, Enabled = true };
        accountRepo.AddAsync(defaultAccount).GetAwaiter().GetResult();

        var result = service.AddAsync(@"D:\repo").GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        var project = projectRepo.GetByIdAsync(result.Value!.Id).GetAwaiter().GetResult();
        Assert.NotNull(project);
        Assert.Equal("repo", project!.Name);

        // 自动绑定默认账号。
        var binding = bindingRepo.GetByProjectIdAsync(result.Value.Id).GetAwaiter().GetResult();
        Assert.NotNull(binding);
        Assert.Equal(defaultAccount.Id, binding!.AccountId);
    }

    [Fact]
    public void Add_MissingDirectory_Fails()
    {
        var (service, _, _, _, gitService) = CreateSut();
        gitService.ValidateResult = false;

        var result = service.AddAsync(@"D:\not-a-repo").GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal("PROJECT_DIRECTORY_NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public void Add_DuplicatePath_Fails()
    {
        var (service, accountRepo, _, _, _) = CreateSut();
        accountRepo.AddAsync(new Account { Alias = "d", Username = "d", IsDefault = true, Enabled = true }).GetAwaiter().GetResult();

        var first = service.AddAsync(@"D:\repo").GetAwaiter().GetResult();
        Assert.True(first.IsSuccess);

        var second = service.AddAsync(@"D:\repo").GetAwaiter().GetResult();
        Assert.False(second.IsSuccess);
        Assert.Equal("PROJECT_ALREADY_EXISTS", second.Error!.Code);
    }

    [Fact]
    public void Remove_CleansBindingAndSnapshot()
    {
        var (service, accountRepo, projectRepo, bindingRepo, _) = CreateSut();
        accountRepo.AddAsync(new Account { Alias = "d", Username = "d", IsDefault = true, Enabled = true }).GetAwaiter().GetResult();

        var added = service.AddAsync(@"D:\repo").GetAwaiter().GetResult();
        Assert.True(added.IsSuccess);

        var result = service.RemoveAsync(added.Value!.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Null(projectRepo.GetByIdAsync(added.Value.Id).GetAwaiter().GetResult());
        Assert.Null(bindingRepo.GetByProjectIdAsync(added.Value.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void UpdateOriginUrl_UpdatesGitOriginAndStoredRemoteMetadata()
    {
        var (service, _, projectRepo, _, gitService) = CreateSut();
        var project = new Project
        {
            RepositoryPath = @"D:\repo",
            OriginUrl = "git@codeup.aliyun.com:team/project.git",
        };
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        const string githubUrl = "https://github.com/company/project.git";
        var result = service.UpdateOriginUrlAsync(project.Id, githubUrl).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(githubUrl, gitService.LastSetOriginUrl);
        var updated = projectRepo.GetByIdAsync(project.Id).GetAwaiter().GetResult();
        Assert.Equal(githubUrl, updated!.OriginUrl);
        Assert.Equal(RemoteProtocol.Https, updated.RemoteProtocol);
        Assert.Equal("github.com", updated.RemoteHost);
    }

    [Fact]
    public async Task RefreshMetadata_RepairsEmptyOriginAndUnknownProtocol()
    {
        var (service, _, projects, _, git) = CreateSut();
        var project = new Project { RepositoryPath = @"D:\repo" };
        await projects.AddAsync(project);
        git.OriginUrl = "https://gitee.com/team/repo.git";

        var result = await service.RefreshMetadataAsync(project.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(RemoteProtocol.Https, result.Value!.RemoteProtocol);
        Assert.Equal("gitee.com", result.Value.RemoteHost);
        Assert.Equal(git.OriginUrl, (await projects.GetByIdAsync(project.Id))!.OriginUrl);
        Assert.Null(git.LastSetOriginUrl);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshMetadata_ReadFailureKeepsCachedMetadata(bool repositoryMissing)
    {
        var (service, _, projects, _, git) = CreateSut();
        var project = new Project { RepositoryPath = @"D:\repo", OriginUrl = "https://gitee.com/team/repo.git", RemoteProtocol = RemoteProtocol.Https };
        await projects.AddAsync(project);
        git.ValidateResult = !repositoryMissing;
        git.OriginReadFails = !repositoryMissing;

        var result = await service.RefreshMetadataAsync(project.Id);

        Assert.False(result.IsSuccess);
        Assert.Same(project, await projects.GetByIdAsync(project.Id));
        Assert.Equal(RemoteProtocol.Https, project.RemoteProtocol);
    }

    [Fact]
    public async Task RefreshMetadata_NoOriginClearsStaleProtocol()
    {
        var (service, _, projects, _, git) = CreateSut();
        var project = new Project { RepositoryPath = @"D:\repo", OriginUrl = "https://gitee.com/team/repo.git", RemoteProtocol = RemoteProtocol.Https };
        await projects.AddAsync(project);
        git.OriginUrl = string.Empty;

        var result = await service.RefreshMetadataAsync(project.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(RemoteProtocol.Unknown, result.Value!.RemoteProtocol);
        Assert.Empty(result.Value.OriginUrl);
    }

    [Fact]
    public async Task UpdatePath_PreservesProjectAndBindingIdentity_RefreshesMetadata()
    {
        var (service, _, projects, bindings, git) = CreateSut();
        var project = new Project { Name = "Original", RepositoryPath = @"D:\old\repo", LastTestResult = "old result", LastTestAt = DateTimeOffset.UtcNow };
        var binding = new GitBinder.Domain.Bindings.Binding { ProjectId = project.Id, AccountId = Guid.NewGuid() };
        await projects.AddAsync(project);
        await bindings.AddAsync(binding);
        git.RepositoryRoot = @"D:\new\repo";
        git.OriginUrl = "https://gitee.com/team/repo.git";

        var result = await service.UpdatePathAsync(project.Id, @"D:\new\repo\src");

        Assert.True(result.IsSuccess);
        Assert.Equal(project.Id, result.Value!.Id);
        Assert.Equal(project.CreatedAt, result.Value.CreatedAt);
        Assert.Equal("Original", result.Value.Name);
        Assert.Equal(git.RepositoryRoot, result.Value.RepositoryPath);
        Assert.Equal(Path.Combine(git.RepositoryRoot, ".git"), result.Value.GitDir);
        Assert.Equal(RemoteProtocol.Https, result.Value.RemoteProtocol);
        Assert.Null(result.Value.LastTestAt);
        Assert.Same(binding, await bindings.GetByProjectIdAsync(project.Id));
        Assert.Null(git.LastSetOriginUrl);
    }

    [Fact]
    public async Task UpdatePath_RejectsDuplicateRepositoryRootEvenWhenSelectingSubdirectory()
    {
        var (service, _, projects, _, git) = CreateSut();
        var project = new Project { RepositoryPath = @"D:\old\repo" };
        await projects.AddAsync(project);
        await projects.AddAsync(new Project { RepositoryPath = @"D:\new\repo", CanonicalPath = @"D:\NEW\REPO" });
        git.RepositoryRoot = @"D:\new\repo";

        var result = await service.UpdatePathAsync(project.Id, @"D:\new\repo\src");

        Assert.False(result.IsSuccess);
        Assert.Equal("PROJECT_ALREADY_EXISTS", result.Error!.Code);
        Assert.Equal(@"D:\old\repo", (await projects.GetByIdAsync(project.Id))!.RepositoryPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/repo")]
    [InlineData(@"D:\missing")]
    public async Task UpdatePath_InvalidPathDoesNotChangeRegistration(string path)
    {
        var (service, _, projects, _, git) = CreateSut();
        var project = new Project { RepositoryPath = @"D:\old\repo" };
        await projects.AddAsync(project);
        git.ValidateResult = false;

        Assert.False((await service.UpdatePathAsync(project.Id, path)).IsSuccess);
        Assert.Same(project, await projects.GetByIdAsync(project.Id));
    }

    [Fact]
    public async Task UpdatePath_RemoteReadFailureDoesNotChangeRegistration()
    {
        var (service, _, projects, _, git) = CreateSut();
        var project = new Project { RepositoryPath = @"D:\old\repo" };
        await projects.AddAsync(project);
        git.RepositoryRoot = @"D:\new\repo";
        git.OriginReadFails = true;

        Assert.False((await service.UpdatePathAsync(project.Id, git.RepositoryRoot)).IsSuccess);
        Assert.Same(project, await projects.GetByIdAsync(project.Id));
    }

    private sealed class Guard : IAccountDeletionGuard
    {
        private readonly FakeBindingRepository _bindingRepo;

        public Guard(FakeBindingRepository bindingRepo) => _bindingRepo = bindingRepo;

        public Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default)
            => _bindingRepo.CountByAccountIdAsync(accountId, ct);
    }

    private sealed class FakeDataPath : GitBinder.Application.Common.IApplicationDataPath
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "gitbinder-test");

        public string DatabasePath => Path.Combine(DataDirectory, "gitbinder.db");

        public string LogDirectory => Path.Combine(DataDirectory, "logs");

        public string KeysDirectory => Path.Combine(DataDirectory, "keys");
    }
}
