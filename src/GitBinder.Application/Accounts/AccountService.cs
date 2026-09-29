using GitBinder.Application.Common;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;

namespace GitBinder.Application.Accounts;

/// <summary>
/// 账号应用服务：负责账号增删改查、默认账号管理及业务规则校验。
/// </summary>
public sealed class AccountService
{
    private readonly IAccountRepository _repository;
    private readonly IAccountDeletionGuard _deletionGuard;
    private readonly SecretService _secretService;
    private readonly IPrivateKeyImporter _privateKeyImporter;

    public AccountService(
        IAccountRepository repository,
        IAccountDeletionGuard deletionGuard,
        SecretService secretService,
        IPrivateKeyImporter privateKeyImporter)
    {
        _repository = repository;
        _deletionGuard = deletionGuard;
        _secretService = secretService;
        _privateKeyImporter = privateKeyImporter;
    }

    /// <summary>导入 SSH 私钥：复制到应用 keys 目录并返回新路径，便于后续读取。</summary>
    public async Task<Result<string>> ImportPrivateKeyAsync(string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return Result<string>.Failure(new DomainError("ACCOUNT_KEY_NOT_FOUND", sourcePath));
        }

        try
        {
            var dest = await _privateKeyImporter.ImportAsync(sourcePath, ct);
            return Result<string>.Success(dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(new DomainError("ACCOUNT_KEY_IMPORT_FAILED", ex.Message));
        }
    }

    public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
        => _repository.GetAllAsync(ct);

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _repository.GetByIdAsync(id, ct);

    /// <summary>创建账号。系统首个账号自动成为默认账号。</summary>
    public async Task<Result<Account>> CreateAsync(CreateAccountInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Username))
        {
            return Result<Account>.Failure(new DomainError("ACCOUNT_USERNAME_REQUIRED"));
        }

        var existing = await _repository.GetAllAsync(ct);
        var isFirst = existing.Count == 0;

        var account = AccountFactory.Create(
            input.Username,
            input.Alias,
            input.PlatformId,
            input.PlatformName,
            input.Host,
            input.GitName,
            input.GitEmail);
        account.AuthenticationType = input.AuthenticationType;
        account.SshPrivateKeyPath = input.SshPrivateKeyPath;
        account.SshPublicKeyPath = input.SshPublicKeyPath;
        account.HttpsUsername = input.HttpsUsername;
        account.IsDefault = isFirst;

        // 加密保存 HTTPS 凭据与 SSH passphrase（仅存 SecretId）。
        await SaveSecretsAsync(account, input, ct);

        await _repository.AddAsync(account, ct);
        return Result<Account>.Success(account);
    }

    /// <summary>更新账号。</summary>
    public async Task<Result<Account>> UpdateAsync(UpdateAccountInput input, CancellationToken ct = default)
    {
        var account = await _repository.GetByIdAsync(input.Id, ct);
        if (account is null)
        {
            return Result<Account>.Failure(new DomainError("ACCOUNT_NOT_FOUND", input.Id.ToString()));
        }

        if (string.IsNullOrWhiteSpace(input.Username))
        {
            return Result<Account>.Failure(new DomainError("ACCOUNT_USERNAME_REQUIRED"));
        }

        account.Username = input.Username.Trim();
        account.Alias = string.IsNullOrWhiteSpace(input.Alias) ? account.Username : input.Alias.Trim();
        account.PlatformId = input.PlatformId;
        account.PlatformName = input.PlatformName?.Trim() ?? string.Empty;
        account.Host = input.Host?.Trim() ?? string.Empty;
        account.GitName = input.GitName?.Trim() ?? string.Empty;
        account.GitEmail = input.GitEmail?.Trim() ?? string.Empty;
        account.AuthenticationType = input.AuthenticationType;
        account.SshPrivateKeyPath = input.SshPrivateKeyPath;
        account.SshPublicKeyPath = input.SshPublicKeyPath;
        account.HttpsUsername = input.HttpsUsername;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveSecretsAsync(account, input, ct);

        await _repository.UpdateAsync(account, ct);
        return Result<Account>.Success(account);
    }

    /// <summary>保存 HTTPS 凭据与 SSH passphrase 到 Secret Store（仅 SecretId 落库）。</summary>
    private async Task SaveSecretsAsync(Account account, CreateAccountInput input, CancellationToken ct)
    {
        // HTTPS 凭据。
        if (!string.IsNullOrWhiteSpace(input.HttpsCredential))
        {
            var id = SecretService.BuildSecretId(account.Id, "https");
            await _secretService.SetAsync(id, input.HttpsCredential, ct);
            account.HttpsSecretId = id;
        }

        // SSH passphrase。
        if (!string.IsNullOrWhiteSpace(input.SshPassphrase))
        {
            var id = SecretService.BuildSecretId(account.Id, "ssh");
            await _secretService.SetAsync(id, input.SshPassphrase, ct);
            account.PassphraseSecretId = id;
        }
    }

    /// <summary>设置默认账号。系统最多一个默认账号。</summary>
    public async Task<Result<Account>> SetDefaultAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _repository.GetByIdAsync(id, ct);
        if (account is null)
        {
            return Result<Account>.Failure(new DomainError("ACCOUNT_NOT_FOUND", id.ToString()));
        }

        if (!account.Enabled)
        {
            return Result<Account>.Failure(new DomainError("ACCOUNT_DEFAULT_DISABLE_FORBIDDEN"));
        }

        var current = await _repository.GetDefaultAsync(ct);
        if (current is not null)
        {
            current.IsDefault = false;
            await _repository.UpdateAsync(current, ct);
        }

        account.IsDefault = true;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.UpdateAsync(account, ct);
        return Result<Account>.Success(account);
    }

    /// <summary>删除账号。默认账号或被绑定账号不得直接删除。</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _repository.GetByIdAsync(id, ct);
        if (account is null)
        {
            return Result.Failure(new DomainError("ACCOUNT_NOT_FOUND", id.ToString()));
        }

        if (account.IsDefault)
        {
            return Result.Failure(new DomainError("ACCOUNT_DEFAULT_DELETE_FORBIDDEN"));
        }

        var boundCount = await _deletionGuard.CountBindingsAsync(id, ct);
        if (boundCount > 0)
        {
            return Result.Failure(new DomainError("ACCOUNT_HAS_BINDINGS", boundCount.ToString()));
        }

        // 清理关联的 Secret。
        if (!string.IsNullOrWhiteSpace(account.HttpsSecretId))
        {
            await _secretService.DeleteAsync(account.HttpsSecretId, ct);
        }

        if (!string.IsNullOrWhiteSpace(account.PassphraseSecretId))
        {
            await _secretService.DeleteAsync(account.PassphraseSecretId, ct);
        }

        await _repository.DeleteAsync(id, ct);
        return Result.Success();
    }
}

/// <summary>账号删除守卫，用于检查删除前是否有绑定。</summary>
public interface IAccountDeletionGuard
{
    Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default);
}
