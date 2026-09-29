using System.Globalization;
using GitBinder.Application;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Security;
using GitBinder.Application.Settings;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>只使用内存 Fake，不执行 Git、数据库、秘密读取或文件写入。</summary>
public sealed class AccountEmailSaveTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(" new@example.invalid ", "new@example.invalid")]
    public async Task EditEmail_SynchronizesEffectiveBoundIdentityAndPreservesHttps(string input, string expected)
    {
        var fixture = new Fixture();
        var account = await fixture.AddAccountAsync("Edited");
        account.AuthenticationType = AuthenticationType.Https;
        account.HttpsUsername = "login";
        account.HttpsSecretId = "stored-secret-reference";
        await fixture.AddBindingAsync(account);
        var other = await fixture.AddAccountAsync("Unchanged");
        await fixture.AddBindingAsync(other);
        fixture.ViewModel.InitializeForEdit(account);
        fixture.ViewModel.GitEmail = input;

        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.Saved);
        Assert.Equal(1, fixture.CloseCount);
        Assert.Equal(expected, (await fixture.Accounts.GetByIdAsync(account.Id))!.GitEmail);
        Assert.Equal((account.GitName, expected), fixture.LocalConfig.AppliedIdentity);
        Assert.Equal(1, fixture.LocalConfig.ApplyIdentityCount);
        Assert.Equal(0, fixture.LocalConfig.ApplySshCommandCount);
        Assert.Equal(0, fixture.LocalConfig.ApplyCredentialHelperCount);
        Assert.Null(fixture.Git.LastSetOriginUrl);
        Assert.Equal(AuthenticationType.Https, account.AuthenticationType);
        Assert.Equal("stored-secret-reference", account.HttpsSecretId);
        Assert.Null(fixture.GlobalConfig.AppliedAccount);
    }

    [Fact]
    public async Task EditActiveGlobalAccount_UpdatesGlobalAndOverriddenBoundIdentity()
    {
        var fixture = new Fixture();
        var globalAccount = await fixture.AddAccountAsync("Global");
        var boundAccount = await fixture.AddAccountAsync("Bound");
        await fixture.AddBindingAsync(boundAccount);
        await fixture.SetGlobalAccountAsync(globalAccount);
        fixture.ViewModel.InitializeForEdit(globalAccount);
        fixture.ViewModel.GitEmail = string.Empty;

        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.Saved);
        Assert.Equal(globalAccount.Id, fixture.GlobalConfig.AppliedAccount!.Id);
        Assert.Equal(string.Empty, fixture.GlobalConfig.AppliedAccount.GitEmail);
        Assert.Equal((globalAccount.GitName, string.Empty), fixture.LocalConfig.AppliedIdentity);
        Assert.Equal(2, fixture.LocalConfig.ApplyIdentityCount);
        Assert.Null(fixture.Git.LastSetOriginUrl);
    }

    [Fact]
    public async Task EditActiveGlobalAccount_MissingBoundRepository_ReportsIncompleteSync()
    {
        var fixture = new Fixture();
        var account = await fixture.AddAccountAsync("Global");
        var project = await fixture.AddBindingAsync(account);
        project.RepositoryPath = Path.Combine(Path.GetTempPath(), "gitbinder-missing-" + Guid.NewGuid().ToString("N"));
        await fixture.SetGlobalAccountAsync(account);
        fixture.ViewModel.InitializeForEdit(account);
        fixture.ViewModel.GitEmail = string.Empty;

        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, (await fixture.Accounts.GetByIdAsync(account.Id))!.GitEmail);
        Assert.Equal(account.Id, fixture.GlobalConfig.AppliedAccount!.Id);
        Assert.Equal(0, fixture.LocalConfig.ApplyIdentityCount);
        Assert.Equal("Accounts.Edit.IdentitySyncFailed", fixture.ViewModel.Error);
        Assert.False(fixture.ViewModel.Saved);
        Assert.Equal(0, fixture.CloseCount);
    }

    [Fact]
    public async Task EditAccountOverriddenByGlobalMode_DoesNotChangeAppliedIdentity()
    {
        var fixture = new Fixture();
        var edited = await fixture.AddAccountAsync("Bound");
        var globalAccount = await fixture.AddAccountAsync("Global");
        await fixture.AddBindingAsync(edited);
        await fixture.SetGlobalAccountAsync(globalAccount);
        fixture.ViewModel.InitializeForEdit(edited);
        fixture.ViewModel.GitEmail = string.Empty;

        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.Saved);
        Assert.Equal(string.Empty, (await fixture.Accounts.GetByIdAsync(edited.Id))!.GitEmail);
        Assert.Equal(0, fixture.LocalConfig.ApplyIdentityCount);
        Assert.Equal(0, fixture.LocalConfig.ApplySshCommandCount);
        Assert.Null(fixture.GlobalConfig.AppliedAccount);
        Assert.Null(fixture.Git.LastSetOriginUrl);
    }

    [Fact]
    public async Task EditEmail_LocalSyncFailure_PreservesSavedAccountAndCredentialStateForRetry()
    {
        var fixture = new Fixture();
        var account = await fixture.AddAccountAsync("Edited");
        await fixture.AddBindingAsync(account);
        fixture.ViewModel.InitializeForEdit(account);
        fixture.ViewModel.GitEmail = string.Empty;
        fixture.ViewModel.HttpsUsername = "login";
        fixture.ViewModel.HttpsCredential = "fictional-credential";
        fixture.LocalConfig.ThrowOnApply = true;

        var exception = await Record.ExceptionAsync(() => fixture.ViewModel.SaveCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Equal(string.Empty, (await fixture.Accounts.GetByIdAsync(account.Id))!.GitEmail);
        Assert.True(fixture.ViewModel.HasPersistedChanges);
        Assert.Equal("Accounts.Edit.IdentitySyncFailed", fixture.ViewModel.Error);
        Assert.False(fixture.ViewModel.Saved);
        Assert.Equal(0, fixture.CloseCount);

        fixture.LocalConfig.ThrowOnApply = false;
        fixture.ViewModel.HttpsCredential = string.Empty;
        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.Saved);
        Assert.Equal(1, fixture.CloseCount);
        Assert.Equal(string.Empty, fixture.ViewModel.Error);
        Assert.Single(await fixture.Accounts.GetAllAsync());
        Assert.Equal(AuthenticationType.Https, account.AuthenticationType);
        Assert.Equal(SecretService.BuildSecretId(account.Id, "https"), account.HttpsSecretId);
        Assert.Equal((account.GitName, string.Empty), fixture.LocalConfig.AppliedIdentity);
    }

    [Fact]
    public async Task EditEmail_GlobalSyncFailure_ReportsAccountAlreadySaved()
    {
        var fixture = new Fixture();
        var account = await fixture.AddAccountAsync("Global");
        await fixture.SetGlobalAccountAsync(account);
        fixture.ViewModel.InitializeForEdit(account);
        fixture.ViewModel.GitEmail = string.Empty;
        fixture.GlobalConfig.FailApply = true;

        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, (await fixture.Accounts.GetByIdAsync(account.Id))!.GitEmail);
        Assert.Equal("Accounts.Edit.IdentitySyncFailed", fixture.ViewModel.Error);
        Assert.False(fixture.ViewModel.Saved);
        Assert.Equal(0, fixture.CloseCount);
    }

    [Fact]
    public async Task EditEmail_UnexpectedSyncException_IsReportedAsSavedAndCanRetry()
    {
        var fixture = new Fixture();
        var account = await fixture.AddAccountAsync("Edited");
        fixture.ViewModel.InitializeForEdit(account);
        fixture.ViewModel.GitEmail = string.Empty;
        fixture.Settings.FailReads = true;

        var exception = await Record.ExceptionAsync(() => fixture.ViewModel.SaveCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Equal(string.Empty, (await fixture.Accounts.GetByIdAsync(account.Id))!.GitEmail);
        Assert.Equal("Accounts.Edit.IdentitySyncFailed", fixture.ViewModel.Error);
        Assert.False(fixture.ViewModel.Saved);
        Assert.Equal(0, fixture.CloseCount);

        fixture.Settings.FailReads = false;
        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.Saved);
        Assert.Equal(1, fixture.CloseCount);
    }

    [Fact]
    public async Task CreateAccount_SkipsSyncAndLaterSavesUpdateSameAccount()
    {
        var fixture = new Fixture();
        fixture.Settings.FailReads = true;
        fixture.ViewModel.InitializeForCreate();
        Assert.False(fixture.ViewModel.HasPersistedChanges);
        fixture.ViewModel.GitName = "New Account";

        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.Saved);
        Assert.True(fixture.ViewModel.HasPersistedChanges);
        var createdId = Assert.Single(await fixture.Accounts.GetAllAsync()).Id;
        Assert.Equal(0, fixture.Settings.ReadCount);

        // 窗口关闭前的后续保存必须按已有账号处理，同步失败也不重复创建。
        fixture.ViewModel.GitEmail = "new@example.invalid";
        await fixture.ViewModel.SaveCommand.ExecuteAsync(null);

        var updated = Assert.Single(await fixture.Accounts.GetAllAsync());
        Assert.Equal(createdId, updated.Id);
        Assert.Equal("new@example.invalid", updated.GitEmail);
        Assert.Equal("Accounts.Edit.IdentitySyncFailed", fixture.ViewModel.Error);
        Assert.False(fixture.ViewModel.Saved);
        Assert.Equal(1, fixture.CloseCount);

        fixture.ViewModel.InitializeForEdit(updated);
        Assert.False(fixture.ViewModel.HasPersistedChanges);
        fixture.ViewModel.InitializeForCreate();
        Assert.False(fixture.ViewModel.HasPersistedChanges);
    }

    private sealed class Fixture
    {
        public FakeAccountRepository Accounts { get; } = new();
        public FakeBindingRepository Bindings { get; } = new();
        public FakeProjectRepository Projects { get; } = new();
        public FakeGitConfigApplier LocalConfig { get; } = new();
        public FakeGlobalGitConfigApplier GlobalConfig { get; } = new();
        public FakeGitService Git { get; } = new();
        public RecoverableSettingsRepository Settings { get; } = new();
        public FakeGlobalModeState State { get; } = new();
        public AccountEditViewModel ViewModel { get; }
        public int CloseCount { get; private set; }

        public Fixture()
        {
            var secrets = new FakeSecretStore();
            var resolver = new EffectiveAccountResolver(State, Bindings, Accounts);
            var bindingService = new BindingService(
                Bindings, Accounts, Projects, secrets, Git, LocalConfig,
                new FakeSnapshotRepository(), new FakeGitTransfer(), resolver);
            var globalService = new GlobalModeService(Settings, Accounts, bindingService, GlobalConfig);
            ViewModel = new AccountEditViewModel(
                new AccountService(Accounts, new BindingDeletionGuard(Bindings), new SecretService(secrets), new FakePrivateKeyImporter()),
                new PlatformService(new EmptyPlatformRepository(), Settings),
                new KeyLocalization(),
                () => Task.FromResult<string?>(null),
                new AccountIdentitySyncService(bindingService, globalService, resolver));
            ViewModel.CloseRequested += () => CloseCount++;
        }

        public async Task<Account> AddAccountAsync(string name)
        {
            var account = new Account { Username = name, GitName = name, GitEmail = "old@example.invalid" };
            await Accounts.AddAsync(account);
            return account;
        }

        public async Task<Project> AddBindingAsync(Account account)
        {
            // 只满足存在性检查；Git 服务和配置应用器都是内存 Fake。
            var project = new Project { RepositoryPath = Path.GetTempPath(), RemoteProtocol = RemoteProtocol.Https };
            await Projects.AddAsync(project);
            await Bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = account.Id });
            return project;
        }

        public async Task SetGlobalAccountAsync(Account account)
        {
            State.IsEnabled = true;
            State.GlobalAccountId = account.Id;
            await Settings.SetAsync("global_mode.enabled", "true");
            await Settings.SetAsync("global_mode.account_id", account.Id.ToString());
        }
    }

    private sealed class RecoverableSettingsRepository : ISettingsRepository
    {
        private readonly FakeSettingsRepository _inner = new();
        public bool FailReads { get; set; }
        public int ReadCount { get; private set; }

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            ReadCount++;
            return FailReads
                ? Task.FromException<string?>(new IOException("fictional-sensitive-detail"))
                : _inner.GetAsync(key, ct);
        }

        public Task SetAsync(string key, string value, CancellationToken ct = default) => _inner.SetAsync(key, value, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);
    }

    private sealed class UnusedDataPath : IApplicationDataPath
    {
        public string DataDirectory => throw new NotSupportedException();
        public string DatabasePath => throw new NotSupportedException();
        public string LogDirectory => throw new NotSupportedException();
        public string KeysDirectory => throw new NotSupportedException();
    }

    private sealed class EmptyPlatformRepository : IPlatformRepository
    {
        public Task<GitPlatform?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<GitPlatform?>(null);
        public Task<GitPlatform?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<GitPlatform?>(null);
        public Task<IReadOnlyList<GitPlatform>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitPlatform>>([]);
        public Task AddAsync(GitPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(GitPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
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
