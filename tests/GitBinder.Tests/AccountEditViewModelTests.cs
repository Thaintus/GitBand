using System.Globalization;
using System.Security.Cryptography;
using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Application.Security;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public sealed class AccountEditViewModelTests
{
    [Theory]
    [InlineData(false, "directory")]
    [InlineData(true, "directory")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    [InlineData(false, "encryption")]
    [InlineData(true, "encryption")]
    public async Task Save_SecretWriteFailure_KeepsFormOpenAndAllowsRetry(bool editing, string failureKind)
    {
        var accounts = new FakeAccountRepository();
        var secrets = new RecoverableSecretStore
        {
            Failure = failureKind switch
            {
                "directory" => new DirectoryNotFoundException("fictional-sensitive-error-detail"),
                "permission" => new UnauthorizedAccessException("fictional-sensitive-error-detail"),
                "io" => new IOException("fictional-sensitive-error-detail"),
                _ => new CryptographicException("fictional-sensitive-error-detail"),
            },
        };
        var viewModel = CreateViewModel(accounts, secrets);
        if (editing)
        {
            var existing = new Account { Username = "Existing", GitName = "Existing" };
            await accounts.AddAsync(existing);
            viewModel.InitializeForEdit(existing);
        }
        else
        {
            viewModel.InitializeForCreate();
        }

        viewModel.GitName = "Test Committer";
        viewModel.GitEmail = "test@example.invalid";
        viewModel.HttpsUsername = "test-user";
        viewModel.HttpsCredential = "fictional-test-credential";
        var closeCount = 0;
        viewModel.CloseRequested += () => closeCount++;

        var failure = await Record.ExceptionAsync(() => viewModel.SaveCommand.ExecuteAsync(null));

        Assert.Null(failure);
        Assert.Equal("Accounts.Edit.SaveFailed", viewModel.Error);
        Assert.DoesNotContain("fictional-sensitive-error-detail", viewModel.Error);
        Assert.DoesNotContain(viewModel.HttpsCredential, viewModel.Error);
        Assert.False(viewModel.Saved);
        Assert.Equal(0, closeCount);
        Assert.Equal("Test Committer", viewModel.GitName);
        Assert.Equal("test@example.invalid", viewModel.GitEmail);
        Assert.Equal("test-user", viewModel.HttpsUsername);
        Assert.Equal("fictional-test-credential", viewModel.HttpsCredential);
        Assert.Equal(editing ? 1 : 0, (await accounts.GetAllAsync()).Count);

        secrets.Failure = null;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.Saved);
        Assert.Equal(1, closeCount);
        Assert.Equal(string.Empty, viewModel.Error);
        var savedAccount = Assert.Single(await accounts.GetAllAsync());
        Assert.Equal(viewModel.HttpsCredential, await secrets.GetAsync(savedAccount.HttpsSecretId));
    }

    [Fact]
    public async Task Save_MissingName_KeepsValidationFeedbackAndDoesNotPersist()
    {
        var accounts = new FakeAccountRepository();
        var viewModel = CreateViewModel(accounts, new FakeSecretStore());
        viewModel.InitializeForCreate();

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.Saved);
        Assert.Equal("ACCOUNT_NAME_REQUIRED", viewModel.Error);
        Assert.Empty(await accounts.GetAllAsync());
    }

    [Fact]
    public async Task Save_WithoutCredentials_PreservesIdentityOnlyAccountCreation()
    {
        var accounts = new FakeAccountRepository();
        var viewModel = CreateViewModel(accounts, new FakeSecretStore());
        viewModel.InitializeForCreate();
        viewModel.GitName = "Identity Only";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.Saved);
        Assert.Equal(AuthenticationType.None, Assert.Single(await accounts.GetAllAsync()).AuthenticationType);
    }

    [Theory]
    [InlineData(AuthenticationType.Https, false, "", AuthenticationType.Https)]
    [InlineData(AuthenticationType.Https, true, "", AuthenticationType.Both)]
    [InlineData(AuthenticationType.Both, true, "", AuthenticationType.Both)]
    [InlineData(AuthenticationType.Both, false, "", AuthenticationType.Https)]
    [InlineData(AuthenticationType.None, false, "", AuthenticationType.Https)]
    [InlineData(AuthenticationType.Ssh, true, "", AuthenticationType.Both)]
    [InlineData(AuthenticationType.Https, false, "  ", AuthenticationType.Https)]
    public async Task Save_EditingWithBlankCredential_PreservesStoredHttpsCapability(
        AuthenticationType originalType, bool keepSsh, string credential, AuthenticationType expectedType)
    {
        var accounts = new FakeAccountRepository();
        var existing = new Account
        {
            Username = "existing-user",
            GitName = "Existing Committer",
            HttpsUsername = "existing-user",
            HttpsSecretId = "fictional-existing-https-reference",
            AuthenticationType = originalType,
            SshPrivateKeyPath = originalType is AuthenticationType.Ssh or AuthenticationType.Both
                ? "fictional-existing-key" : string.Empty,
        };
        await accounts.AddAsync(existing);
        var viewModel = CreateViewModel(accounts, new InaccessibleSecretStore());
        viewModel.InitializeForEdit(existing);
        Assert.Empty(viewModel.HttpsCredential);
        viewModel.GitName = "Updated Committer";
        viewModel.HttpsCredential = credential;
        viewModel.SshPrivateKeyPath = keepSsh ? "fictional-selected-key" : string.Empty;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.Saved);
        var saved = Assert.Single(await accounts.GetAllAsync());
        Assert.Equal(expectedType, saved.AuthenticationType);
        Assert.Equal("fictional-existing-https-reference", saved.HttpsSecretId);
        Assert.Equal("existing-user", saved.HttpsUsername);
        Assert.Equal("Updated Committer", saved.GitName);
        Assert.Equal(keepSsh ? "fictional-selected-key" : string.Empty, saved.SshPrivateKeyPath);
    }

    [Theory]
    [InlineData(false, AuthenticationType.None)]
    [InlineData(true, AuthenticationType.Ssh)]
    public async Task Save_EditingWithoutHttpsUsername_DoesNotEnableHttpsOrDeleteStoredCredential(
        bool keepSsh, AuthenticationType expectedType)
    {
        var accounts = new FakeAccountRepository();
        var existing = new Account
        {
            Username = "existing-user",
            GitName = "Existing Committer",
            HttpsUsername = "existing-user",
            HttpsSecretId = "fictional-existing-https-reference",
            AuthenticationType = AuthenticationType.Both,
            SshPrivateKeyPath = "fictional-existing-key",
        };
        await accounts.AddAsync(existing);
        var viewModel = CreateViewModel(accounts, new InaccessibleSecretStore());
        viewModel.InitializeForEdit(existing);
        viewModel.HttpsUsername = string.Empty;
        viewModel.SshPrivateKeyPath = keepSsh ? existing.SshPrivateKeyPath : string.Empty;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.Saved);
        var saved = Assert.Single(await accounts.GetAllAsync());
        Assert.Equal(expectedType, saved.AuthenticationType);
        Assert.Equal("fictional-existing-https-reference", saved.HttpsSecretId);
    }

    [Theory]
    [InlineData(false, false, AuthenticationType.None)]
    [InlineData(false, true, AuthenticationType.Ssh)]
    [InlineData(true, false, AuthenticationType.None)]
    [InlineData(true, true, AuthenticationType.Ssh)]
    public async Task Reinitialize_DoesNotCarryStoredHttpsCredentialToAnotherAccount(
        bool editing, bool useSsh, AuthenticationType expectedType)
    {
        var accounts = new FakeAccountRepository();
        var viewModel = CreateViewModel(accounts, new InaccessibleSecretStore());
        viewModel.InitializeForEdit(new Account
        {
            HttpsUsername = "previous-user",
            HttpsSecretId = "fictional-previous-https-reference",
            AuthenticationType = AuthenticationType.Https,
        });
        viewModel.HttpsCredential = "fictional-unsaved-credential";
        if (editing)
        {
            var next = new Account { Username = "next-user", GitName = "Next Committer" };
            await accounts.AddAsync(next);
            viewModel.InitializeForEdit(next);
        }
        else
        {
            viewModel.InitializeForCreate();
        }

        Assert.Empty(viewModel.HttpsCredential);
        viewModel.GitName = "Next Committer";
        viewModel.HttpsUsername = "next-user";
        viewModel.SshPrivateKeyPath = useSsh ? "fictional-next-key" : string.Empty;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.Saved);
        var saved = Assert.Single(await accounts.GetAllAsync());
        Assert.Equal(expectedType, saved.AuthenticationType);
        Assert.Empty(saved.HttpsSecretId);
    }

    [Fact]
    public async Task Save_EditingSshAccountWithNewHttpsCredential_EnablesBothAndStoresNewCredential()
    {
        var accounts = new FakeAccountRepository();
        var secrets = new FakeSecretStore();
        var existing = new Account
        {
            Username = "existing-user",
            GitName = "Existing Committer",
            AuthenticationType = AuthenticationType.Ssh,
            SshPrivateKeyPath = "fictional-existing-key",
        };
        await accounts.AddAsync(existing);
        var viewModel = CreateViewModel(accounts, secrets);
        viewModel.InitializeForEdit(existing);
        viewModel.HttpsUsername = "existing-user";
        viewModel.HttpsCredential = "fictional-new-credential";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.Saved);
        var saved = Assert.Single(await accounts.GetAllAsync());
        Assert.Equal(AuthenticationType.Both, saved.AuthenticationType);
        Assert.Equal(SecretService.BuildSecretId(existing.Id, "https"), saved.HttpsSecretId);
        Assert.Equal("fictional-new-credential", await secrets.GetAsync(saved.HttpsSecretId));
    }

    private static AccountEditViewModel CreateViewModel(FakeAccountRepository accounts, ISecretStore secrets)
        => new(
            new AccountService(accounts, new DeletionGuard(), new SecretService(secrets), new FakePrivateKeyImporter()),
            new PlatformService(new EmptyPlatformRepository(), new FakeSettingsRepository()),
            new TestLocalization(),
            () => Task.FromResult<string?>(null));

    // 留空保存不应读回、重写或删除秘密；任何访问都会使 Saved 断言失败。
    private sealed class InaccessibleSecretStore : ISecretStore
    {
        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => throw new InvalidOperationException("Secret writes must not occur for unchanged credentials.");

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("Secret reads must not occur in the account editor.");

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("Secret deletion must not occur for unchanged credentials.");
    }

    private sealed class RecoverableSecretStore : ISecretStore
    {
        private readonly FakeSecretStore _inner = new();
        public Exception? Failure { get; set; }

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => Failure is { } failure ? Task.FromException(failure) : _inner.SetAsync(key, value, ct);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) => _inner.GetAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);
    }

    private sealed class DeletionGuard : IAccountDeletionGuard
    {
        public Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class TestDataPath : IApplicationDataPath
    {
        public string DataDirectory => Path.Combine(Path.GetTempPath(), "gitbinder-editor-unused");
        public string DatabasePath => Path.Combine(DataDirectory, "gitbinder.db");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
        public string KeysDirectory => Path.Combine(DataDirectory, "keys");
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
