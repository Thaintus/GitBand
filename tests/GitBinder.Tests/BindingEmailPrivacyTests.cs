using System.Text.Json;
using GitBinder.Application.Bindings;
using GitBinder.Application.GlobalMode;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.GlobalMode;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>仅使用内存 Fake 验证邮箱快照与生效账号路由，不访问真实 Git 配置。</summary>
public sealed class BindingEmailPrivacyTests
{
    [Fact]
    public async Task FirstBindAndUnbind_PreservesMissingEmptyAndExplicitEmailValues()
    {
        using var f = new Fixture();
        var original = new GitEmailConfigSnapshot(null, "", "original@example.invalid");
        f.Config.StoredEmailConfig = original;
        Assert.True((await f.Service.BindAsync(f.Project.Id, f.Account.Id)).IsSuccess);

        var snapshot = await f.Snapshots.GetByProjectIdAsync(f.Project.Id);
        Assert.Equal(original, snapshot!.EmailConfig);
        Assert.Equal("", f.Config.AppliedIdentity.Email);

        Assert.True((await f.Service.UnbindAsync(f.Project.Id)).IsSuccess);
        Assert.Equal(original, f.Config.LastRestoredEmailConfig);
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("before@example.invalid", "before@example.invalid")]
    public async Task StartupReapply_UpgradesLegacySnapshotWithoutReplacingOriginalEmail(string oldEmail, string? expected)
    {
        using var f = new Fixture();
        await f.Bindings.AddAsync(new Binding { ProjectId = f.Project.Id, AccountId = f.Account.Id });
        var old = new RepositorySnapshot { ProjectId = f.Project.Id, UserEmail = oldEmail };
        await f.Snapshots.SaveAsync(old);
        f.Config.StoredEmailConfig = new("current-account@example.invalid", "author@example.invalid", "");

        Assert.True((await f.Service.RestoreBoundAccountsAsync()).IsSuccess);
        var upgraded = await f.Snapshots.GetByProjectIdAsync(f.Project.Id);
        Assert.Equal(old.Id, upgraded!.Id);
        Assert.Equal(oldEmail, upgraded.UserEmail);
        Assert.Equal(new GitEmailConfigSnapshot(expected, "author@example.invalid", ""), upgraded.EmailConfig);
        Assert.Equal(1, f.Config.ApplyIdentityCount);
    }

    [Fact]
    public async Task Reapply_PreservesNewSnapshotAndDoesNotReconfigureAuthentication()
    {
        using var f = new Fixture();
        var original = new GitEmailConfigSnapshot("", null, "");
        await f.Snapshots.SaveAsync(new RepositorySnapshot { ProjectId = f.Project.Id, EmailConfig = original });
        await f.Bindings.AddAsync(new Binding { ProjectId = f.Project.Id, AccountId = f.Account.Id });

        Assert.True((await f.Service.ReapplyAccountIdentityAsync(f.Account.Id)).IsSuccess);
        Assert.Equal(original, (await f.Snapshots.GetByProjectIdAsync(f.Project.Id))!.EmailConfig);
        Assert.Equal("", f.Config.AppliedIdentity.Email);
        Assert.Equal(0, f.Config.ApplySshCommandCount);
        Assert.Equal(0, f.Config.ApplyCredentialHelperCount);
    }

    [Fact]
    public async Task Reapply_UnrelatedAccountDoesNotChangeBoundRepository()
    {
        using var f = new Fixture();
        await f.Bindings.AddAsync(new Binding { ProjectId = f.Project.Id, AccountId = f.Account.Id });
        Assert.True((await f.Service.ReapplyAccountIdentityAsync(Guid.NewGuid())).IsSuccess);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
    }

    [Fact]
    public async Task Reapply_GlobalAccountTakesPriorityOverEditedBindingAccount()
    {
        using var f = new Fixture();
        await f.Bindings.AddAsync(new Binding { ProjectId = f.Project.Id, AccountId = f.Account.Id });
        var global = new Account { GitName = "Global", GitEmail = "", Enabled = true };
        await f.Accounts.AddAsync(global);
        f.GlobalMode.IsEnabled = true;
        f.GlobalMode.GlobalAccountId = global.Id;

        Assert.True((await f.Service.ReapplyAccountIdentityAsync(f.Account.Id)).IsSuccess);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.True((await f.Service.ReapplyAccountIdentityAsync(global.Id)).IsSuccess);
        Assert.Equal(("Global", ""), f.Config.AppliedIdentity);
    }

    [Fact]
    public async Task Reapply_WriteFailureReportsFailureAndPreservesBindingAndSnapshot()
    {
        using var f = new Fixture();
        await f.Bindings.AddAsync(new Binding { ProjectId = f.Project.Id, AccountId = f.Account.Id });
        f.Config.ThrowOnApply = true;
        var result = await f.Service.ReapplyAccountIdentityAsync(f.Account.Id);
        Assert.False(result.IsSuccess);
        Assert.Equal("BINDING_APPLY_FAILED", result.Error!.Code);
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        Assert.NotNull((await f.Snapshots.GetByProjectIdAsync(f.Project.Id))!.EmailConfig);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("historical@example.invalid")]
    public async Task GlobalLegacySnapshot_UsesHistoricalUserEmailAndCapturesOnlyNewKeys(string? oldEmail)
    {
        var accounts = new FakeAccountRepository();
        var account = new Account { GitName = "Global", GitEmail = "", Enabled = true };
        await accounts.AddAsync(account);
        var settings = new FakeSettingsRepository();
        await settings.SetAsync("global_mode.enabled", "true");
        await settings.SetAsync("global_mode.account_id", account.Id.ToString());
        await settings.SetAsync("global_mode.git_config_snapshot", JsonSerializer.Serialize(
            new GlobalGitConfigSnapshot("Historical", oldEmail, null, [])));
        var git = new FakeGlobalGitConfigApplier
        {
            CurrentSnapshot = new GlobalGitConfigSnapshot("Current", "current@example.invalid", null, [],
                new GitEmailConfigSnapshot("current@example.invalid", "author@example.invalid", "")),
        };
        var service = new GlobalModeService(settings, accounts, globalGitConfigApplier: git);
        Assert.True((await service.EnsureAppliedAsync()).IsSuccess);
        Assert.True((await service.DisableAsync()).IsSuccess);
        Assert.Equal(oldEmail, git.RestoredSnapshot!.UserEmail);
        Assert.Equal(new GitEmailConfigSnapshot(oldEmail, "author@example.invalid", ""), git.RestoredSnapshot.EmailConfig);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gitbinder-email-" + Guid.NewGuid().ToString("N"));
        public FakeAccountRepository Accounts { get; } = new();
        public FakeBindingRepository Bindings { get; } = new();
        public FakeSnapshotRepository Snapshots { get; } = new();
        public FakeGitConfigApplier Config { get; } = new();
        public FakeGlobalModeState GlobalMode { get; } = new();
        public Account Account { get; } = new() { GitName = "Developer", GitEmail = "", Enabled = true };
        public Project Project { get; }
        public BindingService Service { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Project = new Project { RepositoryPath = _directory, GitDir = Path.Combine(_directory, ".git") };
            var projects = new FakeProjectRepository();
            projects.AddAsync(Project).GetAwaiter().GetResult();
            Accounts.AddAsync(Account).GetAwaiter().GetResult();
            Service = new BindingService(Bindings, Accounts, projects, new FakeSecretStore(), new FakeGitService(),
                Config, Snapshots, new FakeGitTransfer(), new EffectiveAccountResolver(GlobalMode, Bindings, Accounts));
        }

        public void Dispose() => Directory.Delete(_directory);
    }
}
