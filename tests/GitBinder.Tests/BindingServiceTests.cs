using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Git;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public class BindingServiceTests
{
    [Fact]
    public async Task SynchronizeRecordedBinding_UniqueMatchUpdatesRecordWithoutWritingGit()
    {
        var (service, accounts, projects, bindings, snapshots, config) = CreateSut();
        var project = MakeProject("X:/not-created"); project.RemoteHost = "example.org";
        await projects.AddAsync(project);
        var old = MakeAccount("old");
        var match = MakeAccount("match");
        match.GitName = "current"; match.GitEmail = "current@example.org"; match.Host = "example.org";
        await accounts.AddAsync(old); await accounts.AddAsync(match);
        var original = new Binding { ProjectId = project.Id, AccountId = old.Id,
            VerifiedAt = DateTimeOffset.UtcNow, LastTestResult = "old result" };
        await bindings.AddAsync(original);
        var snapshot = new GitBinder.Domain.GlobalMode.RepositorySnapshot { ProjectId = project.Id,
            EmailConfig = new GitBinder.Domain.GlobalMode.GitEmailConfigSnapshot("original@example.org", null, null) };
        await snapshots.SaveAsync(snapshot);
        config.ThrowOnApply = true;
        var result = await service.SynchronizeRecordedBindingAsync(project.Id, new RepositoryIdentity("current", "current@example.org"));
        Assert.True(result.IsSuccess);
        var recorded = await bindings.GetByProjectIdAsync(project.Id);
        Assert.Equal(original.Id, recorded!.Id);
        Assert.Equal(match.Id, recorded.AccountId);
        Assert.Equal(old.Id, original.AccountId);
        Assert.Null(recorded.VerifiedAt);
        Assert.Empty(recorded.LastTestResult);
        Assert.Same(snapshot, await snapshots.GetByProjectIdAsync(project.Id));
        Assert.Equal(0, config.ApplyIdentityCount);
        Assert.Equal(0, config.ApplySshCommandCount);
        Assert.Equal(0, config.ApplyCredentialHelperCount);
        Assert.Equal(0, config.RestoreCount);
    }

    [Theory]
    [InlineData(0, "BINDING_REFRESH_NO_MATCH")]
    [InlineData(2, "BINDING_REFRESH_AMBIGUOUS")]
    public async Task SynchronizeRecordedBinding_NonUniqueMatchPreservesBinding(int count, string error)
    {
        var (service, accounts, projects, bindings, _, config) = CreateSut();
        var project = MakeProject("X:/not-created"); project.RemoteHost = "example.org";
        await projects.AddAsync(project);
        var original = new Binding { ProjectId = project.Id, AccountId = Guid.NewGuid() };
        await bindings.AddAsync(original);
        for (var i = 0; i < count; i++)
        {
            var candidate = MakeAccount("candidate-" + i);
            candidate.GitName = "current"; candidate.GitEmail = "current@example.org"; candidate.Host = "example.org";
            await accounts.AddAsync(candidate);
        }
        var result = await service.SynchronizeRecordedBindingAsync(project.Id, new RepositoryIdentity("current", "current@example.org"));
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error!.Code);
        Assert.Same(original, await bindings.GetByProjectIdAsync(project.Id));
        Assert.Equal(0, config.ApplyIdentityCount);
        Assert.Equal(0, config.RestoreCount);
    }

    private static (
        BindingService,
        FakeAccountRepository,
        FakeProjectRepository,
        FakeBindingRepository,
        FakeSnapshotRepository,
        FakeGitConfigApplier
    ) CreateSut(FakeGitTransfer? transfer = null)
    {
        var accountRepo = new FakeAccountRepository();
        var projectRepo = new FakeProjectRepository();
        var bindingRepo = new FakeBindingRepository();
        var snapshotRepo = new FakeSnapshotRepository();
        var gitService = new FakeGitService();
        var gitConfig = new FakeGitConfigApplier();
        var secretStore = new FakeSecretStore();

        var service = new BindingService(
            bindingRepo,
            accountRepo,
            projectRepo,
            secretStore,
            gitService,
            gitConfig,
            snapshotRepo,
            transfer ?? new FakeGitTransfer());

        return (service, accountRepo, projectRepo, bindingRepo, snapshotRepo, gitConfig);
    }

    private static Account MakeAccount(string name) => new() { Alias = name, Username = name, Enabled = true };

    private static string MakeRepoPath() => Path.Combine(Path.GetTempPath(), "gitbinder-test-" + Guid.NewGuid().ToString("N"));

    private static Project MakeProject(string path) => new()
    {
        Id = Guid.NewGuid(), RepositoryPath = path, GitDir = Path.Combine(path, ".git"),
    };

    [Fact]
    public void Bind_AppliesIdentityAndSshCommand()
    {
        var (service, accountRepo, projectRepo, _, _, gitConfig) = CreateSut();
        var account = MakeAccount("company");
        account.GitName = "Zhang San";
        account.GitEmail = "zhang@company.com";
        account.AuthenticationType = AuthenticationType.Ssh;
        account.SshPrivateKeyPath = @"C:\keys\company";
        accountRepo.AddAsync(account).GetAwaiter().GetResult();

        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        project.RemoteProtocol = RemoteProtocol.Ssh;
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        var result = service.BindAsync(project.Id, account.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, gitConfig.ApplyIdentityCount);
        Assert.Equal("Zhang San", gitConfig.AppliedIdentity.Name);
        Assert.Equal("zhang@company.com", gitConfig.AppliedIdentity.Email);
        Assert.Equal(1, gitConfig.ApplySshCommandCount);
        Assert.Contains("IdentitiesOnly=yes", gitConfig.AppliedSshCommand);
    }

    [Fact]
    public void Bind_CreatesSnapshotBeforeApply()
    {
        var (service, accountRepo, projectRepo, _, snapshotRepo, _) = CreateSut();
        var account = MakeAccount("company");
        accountRepo.AddAsync(account).GetAwaiter().GetResult();

        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        service.BindAsync(project.Id, account.Id).GetAwaiter().GetResult();

        var snapshot = snapshotRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult();
        Assert.NotNull(snapshot);
        Assert.Equal("orig-name", snapshot!.UserName);
        Assert.Equal("orig@example.com", snapshot.UserEmail);
    }

    [Fact]
    public void Unbind_RestoresConfigAndDeletesBindingAndSnapshot()
    {
        var (service, accountRepo, projectRepo, bindingRepo, snapshotRepo, gitConfig) = CreateSut();
        var account = MakeAccount("company");
        accountRepo.AddAsync(account).GetAwaiter().GetResult();

        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        service.BindAsync(project.Id, account.Id).GetAwaiter().GetResult();
        Assert.NotNull(bindingRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult());

        var result = service.UnbindAsync(project.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, gitConfig.RestoreCount);
        Assert.Equal("orig-name", gitConfig.LastRestoredName);
        Assert.Null(bindingRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult());
        Assert.Null(snapshotRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void Bind_InvalidAccount_Fails()
    {
        var (service, _, projectRepo, _, _, _) = CreateSut();
        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        var result = service.BindAsync(project.Id, Guid.NewGuid()).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal("BINDING_ACCOUNT_INVALID", result.Error!.Code);
    }

    [Fact]
    public void Rebind_SameProject_UpdatesAccountNotCreatesSecond()
    {
        var (service, accountRepo, projectRepo, bindingRepo, _, gitConfig) = CreateSut();
        var a = MakeAccount("a");
        var b = MakeAccount("b");
        a.GitName = "First Identity";
        a.GitEmail = "first@example.com";
        b.GitName = "Second Identity";
        b.GitEmail = "second@example.com";
        accountRepo.AddAsync(a).GetAwaiter().GetResult();
        accountRepo.AddAsync(b).GetAwaiter().GetResult();

        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        service.BindAsync(project.Id, a.Id).GetAwaiter().GetResult();
        service.BindAsync(project.Id, b.Id).GetAwaiter().GetResult();

        var all = bindingRepo.GetAllAsync().GetAwaiter().GetResult();
        Assert.Single(all);
        var binding = bindingRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult();
        Assert.Equal(b.Id, binding!.AccountId);
        Assert.Equal(("Second Identity", "second@example.com"), gitConfig.AppliedIdentity);
    }

    [Fact]
    public void Rebind_DifferentRemoteHost_IsAllowedAndDisablesSshCredentialFallback()
    {
        var (service, accountRepo, projectRepo, bindingRepo, _, gitConfig) = CreateSut();
        var codeup = MakeAccount("codeup");
        codeup.Host = "codeup.aliyun.com";
        codeup.GitName = "Codeup Identity";
        var github = MakeAccount("github");
        github.Host = "github.com";
        github.GitName = "GitHub Identity";
        accountRepo.AddAsync(codeup).GetAwaiter().GetResult();
        accountRepo.AddAsync(github).GetAwaiter().GetResult();

        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        project.RemoteHost = "codeup.aliyun.com";
        project.RemoteProtocol = RemoteProtocol.Ssh;
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        Assert.True(service.BindAsync(project.Id, codeup.Id).GetAwaiter().GetResult().IsSuccess);

        var result = service.BindAsync(project.Id, github.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(github.Id, bindingRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult()!.AccountId);
        Assert.Equal(("GitHub Identity", string.Empty), gitConfig.AppliedIdentity);
        Assert.Contains("-F NUL", gitConfig.AppliedSshCommand);
        Assert.Contains("-i NUL", gitConfig.AppliedSshCommand);
        Assert.Contains("IdentityAgent=none", gitConfig.AppliedSshCommand);
        Assert.Contains("BatchMode=yes", gitConfig.AppliedSshCommand);
    }

    [Fact]
    public void Bind_HttpsProject_UsesOnlySelectedAccountCredentialHelper()
    {
        var (service, accountRepo, projectRepo, _, _, gitConfig) = CreateSut();
        var account = MakeAccount("github");
        account.AuthenticationType = AuthenticationType.Https;
        account.HttpsSecretId = "secret-github";
        accountRepo.AddAsync(account).GetAwaiter().GetResult();

        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        project.RemoteProtocol = RemoteProtocol.Https;
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        var result = service.BindAsync(project.Id, account.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, gitConfig.ApplyCredentialHelperCount);
        Assert.Equal(account.Id, gitConfig.AppliedCredentialAccountId);
    }

    [Fact]
    public void Rebind_WhenLocalConfigWriteFails_KeepsExistingBinding()
    {
        var (service, accountRepo, projectRepo, bindingRepo, _, gitConfig) = CreateSut();
        var first = MakeAccount("first");
        var second = MakeAccount("second");
        accountRepo.AddAsync(first).GetAwaiter().GetResult();
        accountRepo.AddAsync(second).GetAwaiter().GetResult();

        var repoPath = MakeRepoPath();
        Directory.CreateDirectory(repoPath);
        var project = MakeProject(repoPath);
        projectRepo.AddAsync(project).GetAwaiter().GetResult();

        Assert.True(service.BindAsync(project.Id, first.Id).GetAwaiter().GetResult().IsSuccess);

        gitConfig.ThrowOnApply = true;
        var result = service.BindAsync(project.Id, second.Id).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal("BINDING_APPLY_FAILED", result.Error!.Code);
        Assert.Equal(first.Id, bindingRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult()!.AccountId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Test_UsesEffectiveAccountWithoutWritingConfig(bool globalModeEnabled)
    {
        var accountRepo = new FakeAccountRepository();
        var projectRepo = new FakeProjectRepository();
        var bindingRepo = new FakeBindingRepository();
        var gitService = new FakeGitService();
        var transfer = new FakeGitTransfer();
        var gitConfig = new FakeGitConfigApplier { ThrowOnApply = true };
        var snapshots = new FakeSnapshotRepository();
        var global = MakeAccount("global");
        global.AuthenticationType = AuthenticationType.Ssh;
        global.SshPrivateKeyPath = @"C:\keys\global";
        var bound = MakeAccount("bound");
        bound.AuthenticationType = AuthenticationType.Ssh;
        bound.SshPrivateKeyPath = @"C:\keys\bound";
        accountRepo.AddAsync(global).GetAwaiter().GetResult();
        accountRepo.AddAsync(bound).GetAwaiter().GetResult();

        var state = new FakeGlobalModeState { IsEnabled = globalModeEnabled, GlobalAccountId = global.Id };
        var resolver = new EffectiveAccountResolver(state, bindingRepo, accountRepo);
        var service = new BindingService(
            bindingRepo,
            accountRepo,
            projectRepo,
            new FakeSecretStore(),
            gitService,
            gitConfig,
            snapshots,
            transfer,
            resolver);
        var project = MakeProject(@"D:\repo");
        project.RemoteProtocol = RemoteProtocol.Ssh;
        projectRepo.AddAsync(project).GetAwaiter().GetResult();
        var binding = new Binding { ProjectId = project.Id, AccountId = bound.Id };
        bindingRepo.AddAsync(binding).GetAwaiter().GetResult();

        var result = service.TestAsync(binding).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.Equal(globalModeEnabled ? global.Id : bound.Id, transfer.UsedAccountId);
        Assert.Equal(1, transfer.TestCount);
        Assert.Equal(0, transfer.PullCount);
        Assert.Equal(0, transfer.CloneCount);
        Assert.Equal(0, gitConfig.ApplyIdentityCount);
        Assert.Equal(0, gitConfig.ApplySshCommandCount);
        Assert.Equal(0, gitConfig.ApplyCredentialHelperCount);
        Assert.Equal(0, gitConfig.RestoreCount);
        Assert.Null(snapshots.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult());
        Assert.Equal(bound.Id, bindingRepo.GetByProjectIdAsync(project.Id).GetAwaiter().GetResult()!.AccountId);
    }

    [Fact]
    public async Task Test_WithoutResolver_UsesBoundAccountAndPreservesRepositoryMetadata()
    {
        var transfer = new FakeGitTransfer();
        var (service, accounts, projects, bindings, snapshots, config) = CreateSut(transfer);
        var account = MakeAccount("current-bound");
        var project = MakeProject(@"X:\fictional-test-repository");
        project.OriginUrl = "https://example.invalid/team/project.git";
        project.RemoteProtocol = RemoteProtocol.Https;
        project.RemoteHost = "example.invalid";
        var binding = new Binding { ProjectId = project.Id, AccountId = account.Id, LastTestResult = "previous-result" };
        await accounts.AddAsync(account);
        await projects.AddAsync(project);
        await bindings.AddAsync(binding);
        string? receivedPath = null;
        transfer.OnTest = (path, _, _) => { receivedPath = path; return Task.FromResult(Result.Success()); };
        config.ThrowOnApply = true;

        var result = await service.TestAsync(binding);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(account.Id, transfer.UsedAccountId);
        Assert.Equal(project.RepositoryPath, receivedPath);
        Assert.Equal("https://example.invalid/team/project.git", project.OriginUrl);
        Assert.Equal(RemoteProtocol.Https, project.RemoteProtocol);
        Assert.Equal("example.invalid", project.RemoteHost);
        Assert.Equal("previous-result", binding.LastTestResult);
        Assert.Null(binding.VerifiedAt);
        Assert.Null(await snapshots.GetByProjectIdAsync(project.Id));
        Assert.Equal(0, config.ApplyIdentityCount);
        Assert.Equal(0, config.ApplySshCommandCount);
        Assert.Equal(0, config.ApplyCredentialHelperCount);
        Assert.Equal(0, config.RestoreCount);
    }

    [Theory]
    [InlineData("TRANSFER_PROTOCOL_MISMATCH")]
    [InlineData("TRANSFER_HTTPS_REQUIRED")]
    [InlineData("TRANSFER_CANCELLED")]
    public async Task Test_PreservesTransferDomainError(string errorCode)
    {
        var error = new DomainError(errorCode, arguments: ["HTTPS", "SSH"]);
        var transfer = new FakeGitTransfer { TestResult = Result.Failure(error) };
        var (service, accounts, projects, _, _, config) = CreateSut(transfer);
        var account = MakeAccount("bound");
        var project = MakeProject(@"X:\fictional-test-repository");
        await accounts.AddAsync(account);
        await projects.AddAsync(project);

        var result = await service.TestAsync(new Binding { ProjectId = project.Id, AccountId = account.Id });

        Assert.False(result.Success);
        Assert.Same(error, result.Error);
        Assert.Equal(new[] { "HTTPS", "SSH" }, result.Error!.Arguments);
        Assert.Equal(1, transfer.TestCount);
        Assert.Equal(0, config.ApplyIdentityCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_ConvertsExceptionsToSafeErrorAndHonorsCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken? receivedToken = null;
        var transfer = new FakeGitTransfer
        {
            OnTest = (_, _, token) =>
            {
                receivedToken = token;
                if (cancel)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<Result>(token);
                }

                return Task.FromException<Result>(new IOException("fictional-sensitive-stderr"));
            },
        };
        var (service, accounts, projects, _, _, _) = CreateSut(transfer);
        var account = MakeAccount("bound");
        var project = MakeProject(@"X:\fictional-test-repository");
        await accounts.AddAsync(account);
        await projects.AddAsync(project);

        var result = await service.TestAsync(
            new Binding { ProjectId = project.Id, AccountId = account.Id }, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(cancel ? "TRANSFER_CANCELLED" : "TEST_FAILED", result.Error!.Code);
        Assert.Null(result.Error.TechnicalDetails);
        Assert.Empty(result.Error.Arguments);
        Assert.Equal(1, transfer.TestCount);
        Assert.Equal(cancellation.Token, receivedToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_IncompleteBinding_DoesNotCallTransfer(bool missingAccount)
    {
        var transfer = new FakeGitTransfer();
        var (service, accounts, projects, _, _, _) = CreateSut(transfer);
        var account = MakeAccount("bound");
        var project = MakeProject(@"X:\fictional-test-repository");
        if (!missingAccount) await accounts.AddAsync(account);
        if (missingAccount) await projects.AddAsync(project);

        var result = await service.TestAsync(new Binding { ProjectId = project.Id, AccountId = account.Id });

        Assert.False(result.Success);
        Assert.Equal("BINDING_INCOMPLETE", result.Error!.Code);
        Assert.Equal(0, transfer.TestCount);
    }

    [Fact]
    public async Task Test_DisabledBoundAccountWithoutResolver_DoesNotCallTransfer()
    {
        var transfer = new FakeGitTransfer();
        var (service, accounts, projects, _, _, _) = CreateSut(transfer);
        var account = MakeAccount("disabled");
        account.Enabled = false;
        var project = MakeProject(@"X:\fictional-test-repository");
        await accounts.AddAsync(account);
        await projects.AddAsync(project);

        var result = await service.TestAsync(new Binding { ProjectId = project.Id, AccountId = account.Id });

        Assert.False(result.Success);
        Assert.Equal("TRANSFER_ACCOUNT_INVALID", result.Error!.Code);
        Assert.Equal(0, transfer.TestCount);
    }
}
