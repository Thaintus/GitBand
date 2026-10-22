using GitBinder.Application.Accounts;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public class AccountServiceTests
{
    private static (AccountService, FakeAccountRepository, FakeBindingRepository, FakeSecretStore) CreateSut()
    {
        var accountRepo = new FakeAccountRepository();
        var bindingRepo = new FakeBindingRepository();
        var secretStore = new FakeSecretStore();
        var secretService = new SecretService(secretStore);
        var service = new AccountService(accountRepo, new Guard(bindingRepo), secretService, new FakePrivateKeyImporter());
        return (service, accountRepo, bindingRepo, secretStore);
    }

    [Fact]
    public async Task ImportPrivateKey_MissingSource_ReturnsFailure()
    {
        var (service, _, _, _) = CreateSut();
        var missing = Path.Combine(Path.GetTempPath(), "gitbinder-missing-key-" + Guid.NewGuid().ToString("N"));
        var result = await service.ImportPrivateKeyAsync(missing);
        Assert.False(result.IsSuccess);
        Assert.Equal("ACCOUNT_KEY_NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task ImportPrivateKey_PermissionFailure_ReturnsReadableError()
    {
        var source = Path.GetTempFileName();
        try
        {
            var service = new AccountService(new FakeAccountRepository(), new Guard(new FakeBindingRepository()),
                new SecretService(new FakeSecretStore()), new DeniedKeyImporter());
            var result = await service.ImportPrivateKeyAsync(source);
            Assert.False(result.IsSuccess);
            Assert.Equal("ACCOUNT_KEY_IMPORT_FAILED", result.Error!.Code);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public void Create_FirstAccount_BecomesDefault()
    {
        var (service, accountRepo, _, _) = CreateSut();

        var result = service.CreateAsync(new CreateAccountInput { Username = "ice" }).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsDefault);
    }

    [Fact]
    public void Create_UsernameEmpty_Fails()
    {
        var (service, _, _, _) = CreateSut();

        var result = service.CreateAsync(new CreateAccountInput { Username = " " }).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal("ACCOUNT_USERNAME_REQUIRED", result.Error!.Code);
    }

    [Fact]
    public void Create_AliasEmpty_DefaultsToUsername()
    {
        var (service, _, _, _) = CreateSut();

        var result = service.CreateAsync(new CreateAccountInput { Username = "ice", Alias = "" }).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal("ice", result.Value!.Alias);
        Assert.Equal("ice", result.Value.DisplayAlias);
    }

    [Fact]
    public void SetDefault_OnlyOneDefault_AfterSwitch()
    {
        var (service, accountRepo, _, _) = CreateSut();
        var a = service.CreateAsync(new CreateAccountInput { Username = "a" }).GetAwaiter().GetResult().Value!;
        var b = service.CreateAsync(new CreateAccountInput { Username = "b" }).GetAwaiter().GetResult().Value!;

        var result = service.SetDefaultAsync(b.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        var defaults = accountRepo.GetAllAsync().GetAwaiter().GetResult().Count(x => x.IsDefault);
        Assert.Equal(1, defaults);
        Assert.True(accountRepo.GetByIdAsync(b.Id).GetAwaiter().GetResult()!.IsDefault);
    }

    [Fact]
    public void Delete_DefaultAccount_Fails()
    {
        var (service, _, _, _) = CreateSut();
        var a = service.CreateAsync(new CreateAccountInput { Username = "a" }).GetAwaiter().GetResult().Value!;

        var result = service.DeleteAsync(a.Id).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal("ACCOUNT_DEFAULT_DELETE_FORBIDDEN", result.Error!.Code);
    }

    [Fact]
    public void Delete_AccountWithBindings_Fails()
    {
        var (service, accountRepo, bindingRepo, _) = CreateSut();
        var first = service.CreateAsync(new CreateAccountInput { Username = "a" }).GetAwaiter().GetResult().Value!;
        var second = service.CreateAsync(new CreateAccountInput { Username = "b" }).GetAwaiter().GetResult().Value!;

        bindingRepo.AddAsync(new GitBinder.Domain.Bindings.Binding
        {
            ProjectId = Guid.NewGuid(),
            AccountId = second.Id,
        }).GetAwaiter().GetResult();

        var result = service.DeleteAsync(second.Id).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal("ACCOUNT_HAS_BINDINGS", result.Error!.Code);
    }

    [Fact]
    public async Task Delete_ActiveGlobalAccountWithoutBindings_PreservesAccountAndSecrets()
    {
        var accounts = new FakeAccountRepository();
        var bindings = new FakeBindingRepository();
        var secrets = new FakeSecretStore();
        var state = new FakeGlobalModeState { IsEnabled = true };
        var service = new AccountService(accounts, new Guard(bindings), new SecretService(secrets),
            new FakePrivateKeyImporter(), state);
        await service.CreateAsync(new CreateAccountInput { Username = "default" });
        var global = (await service.CreateAsync(new CreateAccountInput
        {
            Username = "global", HttpsCredential = "fictional-global-secret", SshPassphrase = "fictional-passphrase",
        })).Value!;
        state.GlobalAccountId = global.Id;

        var result = await service.DeleteAsync(global.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("ACCOUNT_GLOBAL_DELETE_FORBIDDEN", result.Error!.Code);
        Assert.Same(global, await accounts.GetByIdAsync(global.Id));
        Assert.Equal("fictional-global-secret", await secrets.GetAsync(global.HttpsSecretId));
        Assert.Equal("fictional-passphrase", await secrets.GetAsync(global.PassphraseSecretId));
        Assert.Empty(await bindings.GetAllAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_PreviousGlobalAccount_AfterDisableOrSwitch_IsAllowed(bool switchAccount)
    {
        var accounts = new FakeAccountRepository();
        var bindings = new FakeBindingRepository();
        var secrets = new FakeSecretStore();
        var state = new FakeGlobalModeState();
        var service = new AccountService(accounts, new Guard(bindings), new SecretService(secrets),
            new FakePrivateKeyImporter(), state);
        var first = (await service.CreateAsync(new CreateAccountInput { Username = "default" })).Value!;
        var previous = (await service.CreateAsync(new CreateAccountInput
        {
            Username = "previous global", HttpsCredential = "fictional-previous-secret",
        })).Value!;
        state.IsEnabled = switchAccount;
        state.GlobalAccountId = switchAccount ? first.Id : previous.Id;

        var result = await service.DeleteAsync(previous.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(await accounts.GetByIdAsync(previous.Id));
        Assert.Null(await secrets.GetAsync(previous.HttpsSecretId));
        Assert.NotNull(await accounts.GetByIdAsync(first.Id));
    }

    [Fact]
    public void Create_WithHttpsCredential_SavesSecretAndStoresRefOnly()
    {
        var (service, accountRepo, _, secretStore) = CreateSut();

        var result = service.CreateAsync(new CreateAccountInput
        {
            Username = "ice",
            AuthenticationType = AuthenticationType.Https,
            HttpsUsername = "ice",
            HttpsCredential = "secret-token-123",
        }).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        var account = result.Value!;
        Assert.False(string.IsNullOrEmpty(account.HttpsSecretId));

        var stored = secretStore.GetAsync(account.HttpsSecretId).GetAwaiter().GetResult();
        Assert.Equal("secret-token-123", stored);

        // 数据库实体不应包含明文（只有 SecretId 引用）。
        var persisted = accountRepo.GetByIdAsync(account.Id).GetAwaiter().GetResult()!;
        Assert.NotNull(persisted.HttpsSecretId);
        Assert.DoesNotContain("secret-token-123", persisted.HttpsSecretId);
    }

    [Fact]
    public void Delete_AccountWithSecret_CleansSecret()
    {
        var (service, _, _, secretStore) = CreateSut();
        var first = service.CreateAsync(new CreateAccountInput { Username = "a" }).GetAwaiter().GetResult().Value!;
        var second = service.CreateAsync(new CreateAccountInput
        {
            Username = "b",
            HttpsCredential = "token-xyz",
        }).GetAwaiter().GetResult().Value!;

        var secretId = second.HttpsSecretId;
        Assert.NotNull(secretStore.GetAsync(secretId).GetAwaiter().GetResult());

        var result = service.DeleteAsync(second.Id).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess);
        Assert.Null(secretStore.GetAsync(secretId).GetAwaiter().GetResult());
    }

    private sealed class Guard : IAccountDeletionGuard
    {
        private readonly FakeBindingRepository _bindingRepo;

        public Guard(FakeBindingRepository bindingRepo) => _bindingRepo = bindingRepo;

        public Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default)
            => _bindingRepo.CountByAccountIdAsync(accountId, ct);
    }

    private sealed class DeniedKeyImporter : GitBinder.Application.Common.IPrivateKeyImporter
    {
        public Task<string> ImportAsync(string sourcePath, CancellationToken ct = default)
            => throw new UnauthorizedAccessException("permission denied");
    }
}
