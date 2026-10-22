using GitBinder.Application.Bindings;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Projects;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public class GlobalModeServiceTests
{
    private static (GlobalModeService, FakeAccountRepository, FakeSettingsRepository) CreateSut()
    {
        var accountRepo = new FakeAccountRepository();
        var settingsRepo = new FakeSettingsRepository();
        var service = new GlobalModeService(settingsRepo, accountRepo);
        return (service, accountRepo, settingsRepo);
    }

    [Fact]
    public void Enable_WithAccountId_SetsEnabledAndAccount()
    {
        var (service, accountRepo, settingsRepo) = CreateSut();
        var account = new Account { Alias = "company", Username = "company", Enabled = true };
        accountRepo.AddAsync(account).GetAwaiter().GetResult();

        var result = service.EnableAsync(account.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal("true", settingsRepo.GetAsync("global_mode.enabled").GetAwaiter().GetResult());
        Assert.Equal(account.Id.ToString(), settingsRepo.GetAsync("global_mode.account_id").GetAwaiter().GetResult());
    }

    [Fact]
    public void Enable_NoAccountId_UsesDefault()
    {
        var (service, accountRepo, _) = CreateSut();
        var defaultAccount = new Account { Alias = "default", Username = "default", IsDefault = true, Enabled = true };
        accountRepo.AddAsync(defaultAccount).GetAwaiter().GetResult();

        var result = service.EnableAsync(null).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Enable_NoAccounts_Fails()
    {
        var (service, _, _) = CreateSut();

        var result = service.EnableAsync(null).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal("GLOBALMODE_ACCOUNT_INVALID", result.Error!.Code);
    }

    [Fact]
    public void Disable_SetsEnabledFalse()
    {
        var (service, accountRepo, settingsRepo) = CreateSut();
        accountRepo.AddAsync(new Account { Alias = "d", Username = "d", IsDefault = true, Enabled = true }).GetAwaiter().GetResult();
        service.EnableAsync(null).GetAwaiter().GetResult();

        var result = service.DisableAsync().GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal("false", settingsRepo.GetAsync("global_mode.enabled").GetAwaiter().GetResult());
    }

    [Fact]
    public void SwitchAccount_UpdatesAccountId()
    {
        var (service, accountRepo, settingsRepo) = CreateSut();
        var a = new Account { Alias = "a", Username = "a", Enabled = true };
        var b = new Account { Alias = "b", Username = "b", Enabled = true };
        accountRepo.AddAsync(a).GetAwaiter().GetResult();
        accountRepo.AddAsync(b).GetAwaiter().GetResult();
        service.EnableAsync(a.Id).GetAwaiter().GetResult();

        var result = service.SwitchAccountAsync(b.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(b.Id.ToString(), settingsRepo.GetAsync("global_mode.account_id").GetAwaiter().GetResult());
    }

    [Fact]
    public void EnableAndDisable_AppliesAndRestoresGitGlobalConfiguration()
    {
        var accountRepo = new FakeAccountRepository();
        var settingsRepo = new FakeSettingsRepository();
        var globalConfig = new FakeGlobalGitConfigApplier();
        var service = new GlobalModeService(settingsRepo, accountRepo, globalGitConfigApplier: globalConfig);
        var account = new Account
        {
            Alias = "global",
            Username = "global",
            GitName = "Global Identity",
            GitEmail = "global@example.com",
            Enabled = true,
        };
        accountRepo.AddAsync(account).GetAwaiter().GetResult();

        var enabled = service.EnableAsync(account.Id).GetAwaiter().GetResult();

        Assert.True(enabled.IsSuccess);
        Assert.Same(account, globalConfig.AppliedAccount);
        Assert.False(string.IsNullOrWhiteSpace(
            settingsRepo.GetAsync("global_mode.git_config_snapshot").GetAwaiter().GetResult()));

        var disabled = service.DisableAsync().GetAwaiter().GetResult();

        Assert.True(disabled.IsSuccess);
        Assert.Equal(globalConfig.CurrentSnapshot, globalConfig.RestoredSnapshot);
        Assert.Null(settingsRepo.GetAsync("global_mode.git_config_snapshot").GetAwaiter().GetResult());
    }

    [Fact]
    public void EnsureApplied_UpgradesExistingGlobalModeStateToGitGlobalConfiguration()
    {
        var accountRepo = new FakeAccountRepository();
        var settingsRepo = new FakeSettingsRepository();
        var globalConfig = new FakeGlobalGitConfigApplier();
        var service = new GlobalModeService(settingsRepo, accountRepo, globalGitConfigApplier: globalConfig);
        var account = new Account
        {
            Alias = "existing-global",
            Username = "existing-global",
            GitName = "Existing Global",
            GitEmail = "existing@example.com",
            Enabled = true,
        };
        accountRepo.AddAsync(account).GetAwaiter().GetResult();
        settingsRepo.SetAsync("global_mode.enabled", "true").GetAwaiter().GetResult();
        settingsRepo.SetAsync("global_mode.account_id", account.Id.ToString()).GetAwaiter().GetResult();

        var result = service.EnsureAppliedAsync().GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Same(account, globalConfig.AppliedAccount);
        Assert.False(string.IsNullOrWhiteSpace(
            settingsRepo.GetAsync("global_mode.git_config_snapshot").GetAwaiter().GetResult()));
    }

    [Fact]
    public void EnsureApplied_WhenGlobalModeDisabled_ReappliesExistingBindingWithNoFallbackSshCommand()
    {
        var accountRepo = new FakeAccountRepository();
        var settingsRepo = new FakeSettingsRepository();
        var bindingRepo = new FakeBindingRepository();
        var projectRepo = new FakeProjectRepository();
        var gitConfig = new FakeGitConfigApplier();
        var bindingService = new BindingService(
            bindingRepo,
            accountRepo,
            projectRepo,
            new FakeSecretStore(),
            new FakeGitService(),
            gitConfig,
            new FakeSnapshotRepository(),
            new FakeGitTransfer());
        var service = new GlobalModeService(settingsRepo, accountRepo, bindingService);
        var github = new Account
        {
            Alias = "github",
            Username = "github",
            GitName = "GitHub Identity",
            GitEmail = "github@example.com",
            Enabled = true,
        };
        accountRepo.AddAsync(github).GetAwaiter().GetResult();

        var repositoryPath = Path.Combine(Path.GetTempPath(), $"gitbinder-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var project = new Project { RepositoryPath = repositoryPath, RemoteProtocol = RemoteProtocol.Ssh };
            projectRepo.AddAsync(project).GetAwaiter().GetResult();
            bindingRepo.AddAsync(new Binding { ProjectId = project.Id, AccountId = github.Id }).GetAwaiter().GetResult();

            var result = service.EnsureAppliedAsync().GetAwaiter().GetResult();

            Assert.True(result.IsSuccess);
            Assert.Equal(("GitHub Identity", "github@example.com"), gitConfig.AppliedIdentity);
            Assert.Contains("-F NUL", gitConfig.AppliedSshCommand);
            Assert.Contains("-i NUL", gitConfig.AppliedSshCommand);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public void EnableAndDisable_ReappliesBoundRepositoryIdentity()
    {
        var accountRepo = new FakeAccountRepository();
        var settingsRepo = new FakeSettingsRepository();
        var bindingRepo = new FakeBindingRepository();
        var projectRepo = new FakeProjectRepository();
        var gitConfig = new FakeGitConfigApplier();
        var bindingService = new BindingService(
            bindingRepo,
            accountRepo,
            projectRepo,
            new FakeSecretStore(),
            new FakeGitService(),
            gitConfig,
            new FakeSnapshotRepository(),
            new FakeGitTransfer());
        var service = new GlobalModeService(settingsRepo, accountRepo, bindingService);

        var bound = new Account
        {
            Alias = "bound",
            Username = "bound",
            GitName = "Bound Identity",
            GitEmail = "bound@example.com",
            Enabled = true,
        };
        var global = new Account
        {
            Alias = "global",
            Username = "global",
            GitName = "Global Identity",
            GitEmail = "global@example.com",
            Enabled = true,
        };
        accountRepo.AddAsync(bound).GetAwaiter().GetResult();
        accountRepo.AddAsync(global).GetAwaiter().GetResult();

        var repositoryPath = Path.Combine(Path.GetTempPath(), $"gitbinder-global-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var project = new Project { RepositoryPath = repositoryPath, GitDir = Path.Combine(repositoryPath, ".git") };
            projectRepo.AddAsync(project).GetAwaiter().GetResult();
            bindingRepo.AddAsync(new Binding { ProjectId = project.Id, AccountId = bound.Id }).GetAwaiter().GetResult();

            var enabled = service.EnableAsync(global.Id).GetAwaiter().GetResult();

            Assert.True(enabled.IsSuccess);
            Assert.Equal(("Global Identity", "global@example.com"), gitConfig.AppliedIdentity);

            var disabled = service.DisableAsync().GetAwaiter().GetResult();

            Assert.True(disabled.IsSuccess);
            Assert.Equal(("Bound Identity", "bound@example.com"), gitConfig.AppliedIdentity);
            Assert.Equal("false", settingsRepo.GetAsync("global_mode.enabled").GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }
}
