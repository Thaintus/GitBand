using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.GlobalMode;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;

namespace GitBinder.Application.Bindings;

/// <summary>
/// 绑定应用服务：负责 Project → Account 一对一绑定、改绑、解绑、测试与状态校验。
/// 绑定时会将账号身份应用到仓库，并在此之前做配置快照以便回滚。
/// </summary>
public sealed partial class BindingService : IGlobalModeApplier
{
    private readonly IBindingRepository _repository;
    private readonly IAccountRepository _accountRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly ISecretStore _secretStore;
    private readonly IGitService _gitService;
    private readonly IGitConfigApplier _gitConfigApplier;
    private readonly IRepositorySnapshotRepository _snapshotRepository;
    private readonly IGitTransfer _gitTransfer;
    private readonly EffectiveAccountResolver? _effectiveAccountResolver;

    public BindingService(
        IBindingRepository repository,
        IAccountRepository accountRepository,
        IProjectRepository projectRepository,
        ISecretStore secretStore,
        IGitService gitService,
        IGitConfigApplier gitConfigApplier,
        IRepositorySnapshotRepository snapshotRepository,
        IGitTransfer gitTransfer,
        EffectiveAccountResolver? effectiveAccountResolver = null)
    {
        _repository = repository;
        _accountRepository = accountRepository;
        _projectRepository = projectRepository;
        _secretStore = secretStore;
        _gitService = gitService;
        _gitConfigApplier = gitConfigApplier;
        _snapshotRepository = snapshotRepository;
        _gitTransfer = gitTransfer;
        _effectiveAccountResolver = effectiveAccountResolver;
    }

    public Task<IReadOnlyList<Binding>> GetAllAsync(CancellationToken ct = default)
        => _repository.GetAllAsync(ct);

    public Task<Binding?> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        => _repository.GetByProjectIdAsync(projectId, ct);

    /// <summary>绑定或改绑（一个 Project 只绑定一个 Account），并将账号身份应用到仓库。</summary>
    public async Task<Result<Binding>> BindAsync(Guid projectId, Guid accountId, CancellationToken ct = default)
    {
        var account = await _accountRepository.GetByIdAsync(accountId, ct);
        if (account is null || !account.Enabled)
        {
            return Result<Binding>.Failure(new DomainError("BINDING_ACCOUNT_INVALID", accountId.ToString()));
        }

        var project = await _projectRepository.GetByIdAsync(projectId, ct);
        if (project is null)
        {
            return Result<Binding>.Failure(new DomainError("PROJECT_NOT_FOUND", projectId.ToString()));
        }

        // 普通目录只能登记；待元数据刷新确认已初始化 Git 后，才允许用户显式绑定
        if (project.IsLocalDirectory)
            return Result<Binding>.Failure(new DomainError("PROJECT_GIT_NOT_INITIALIZED"));

        // 先保留旧绑定的独立副本，避免保存失败时提前修改仓储缓存中的对象
        var storedBinding = await _repository.GetByProjectIdAsync(projectId, ct);
        var existing = storedBinding is null ? null : CopyBinding(storedBinding);
        var appliedAt = DateTimeOffset.UtcNow;
        if (existing is not null && appliedAt <= existing.AppliedAt)
            appliedAt = existing.AppliedAt.AddTicks(1);
        var binding = new Binding
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            ProjectId = projectId,
            AccountId = accountId,
            Status = BindingStatus.Active,
            AppliedAt = appliedAt,
            VerifiedAt = existing?.VerifiedAt,
            LastTestResult = existing?.LastTestResult ?? string.Empty,
        };

        // 仓库存在时应用配置，缺失时仅记录绑定
        var repoExists = !string.IsNullOrWhiteSpace(project.RepositoryPath)
            && Directory.Exists(project.RepositoryPath);
        GitLocalConfigSnapshot? previousConfig = null;
        var configMayHaveChanged = false;

        if (repoExists)
        {
            try
            {
                previousConfig = await _gitConfigApplier.CaptureLocalConfigAsync(project.RepositoryPath, ct);
                await EnsureSnapshotAsync(project, ct);
                // 常规模式立即应用新账号，全局模式仍优先使用全局账号
                var effectiveAccount = _effectiveAccountResolver is not null
                    && _effectiveAccountResolver.ResolveReason(project) is EffectiveReason.GlobalMode
                    ? _effectiveAccountResolver.Resolve(project) ?? account
                    : account;
                ct.ThrowIfCancellationRequested();
                configMayHaveChanged = true;
                await ApplyAccountToRepositoryAsync(project, effectiveAccount, ct);
            }
            catch (CommandTerminationUnconfirmedException)
            {
                // 旧进程可能仍在写配置，不能启动补偿与它并发修改同一仓库
                return BindingFailure("TRANSFER_TERMINATION_UNCONFIRMED", project);
            }
            catch (Exception)
            {
                var restoreError = configMayHaveChanged
                    ? await TryRestoreBindingConfigAsync(project, previousConfig!)
                    : null;
                return BindingFailure(restoreError ?? "BINDING_APPLY_FAILED", project);
            }
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            if (existing is null)
                await _repository.AddAsync(binding, ct);
            else
                await _repository.UpdateAsync(binding, ct);
            return Result<Binding>.Success(binding);
        }
        catch (Exception)
        {
            Binding? persisted;
            try
            {
                // 写入可能已提交后才抛错或取消，先回查结果再决定是否补偿
                persisted = await _repository.GetByProjectIdAsync(projectId, CancellationToken.None);
            }
            catch (Exception)
            {
                // 提交状态无法确认时不盲目反写配置，提示用户核对实际状态
                return BindingFailure("BINDING_ROLLBACK_FAILED", project);
            }

            if (SameBinding(persisted, binding))
                return Result<Binding>.Success(persisted!);

            // 其他写入改变了绑定时，也不能把仓库强行恢复到更早的配置
            if (!SameBinding(persisted, existing))
                return BindingFailure("BINDING_ROLLBACK_FAILED", project);

            var restoreError = configMayHaveChanged
                ? await TryRestoreBindingConfigAsync(project, previousConfig!)
                : null;
            return BindingFailure(restoreError ?? "BINDING_SAVE_FAILED", project);
        }
    }

    private static Binding CopyBinding(Binding binding) => new()
    {
        Id = binding.Id,
        ProjectId = binding.ProjectId,
        AccountId = binding.AccountId,
        Status = binding.Status,
        AppliedAt = binding.AppliedAt,
        VerifiedAt = binding.VerifiedAt,
        LastTestResult = binding.LastTestResult,
    };

    private static bool SameBinding(Binding? first, Binding? second)
        => first is null || second is null
            ? first is null && second is null
            : first.Id == second.Id && first.ProjectId == second.ProjectId
                && first.AccountId == second.AccountId && first.Status == second.Status
                && first.AppliedAt == second.AppliedAt && first.VerifiedAt == second.VerifiedAt
                && first.LastTestResult == second.LastTestResult;

    private static Result<Binding> BindingFailure(string code, Project project)
        => Result<Binding>.Failure(new DomainError(code, project.RepositoryPath));

    private async Task<string?> TryRestoreBindingConfigAsync(Project project, GitLocalConfigSnapshot snapshot)
    {
        try
        {
            // 补偿使用本次操作前配置，且不受调用方取消影响，不替换最早的解绑快照
            await _gitConfigApplier.RestoreLocalConfigAsync(project.RepositoryPath, snapshot, CancellationToken.None);
            return null;
        }
        catch (CommandTerminationUnconfirmedException)
        {
            return "TRANSFER_TERMINATION_UNCONFIRMED";
        }
        catch (Exception)
        {
            return "BINDING_ROLLBACK_FAILED";
        }
    }

    /// <summary>解除绑定：恢复仓库原始配置，删除绑定与快照。</summary>
    public async Task<Result> UnbindAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _projectRepository.GetByIdAsync(projectId, ct);
        if (project is not null)
        {
            var snapshot = await _snapshotRepository.GetByProjectIdAsync(projectId, ct);
            if (snapshot is not null && !project.IsLocalDirectory && !string.IsNullOrWhiteSpace(project.RepositoryPath)
                && Directory.Exists(project.RepositoryPath))
            {
                await _gitConfigApplier.RestoreAsync(
                    project.RepositoryPath,
                    snapshot.UserName,
                    snapshot.UserEmail,
                    snapshot.SshCommand,
                    snapshot.CredentialHelper,
                    ct,
                    snapshot.EmailConfig);
            }
        }

        await _snapshotRepository.DeleteByProjectIdAsync(projectId, ct);
        await _repository.DeleteByProjectIdAsync(projectId, ct);
        return Result.Success();
    }

    /// <summary>校验绑定状态（私钥/凭据是否存在等）。</summary>
    public async Task<BindingStatus> ValidateAsync(Binding binding, CancellationToken ct = default)
    {
        var account = await _accountRepository.GetByIdAsync(binding.AccountId, ct);
        if (account is null || !account.Enabled)
        {
            return BindingStatus.AccountDisabled;
        }

        var project = await _projectRepository.GetByIdAsync(binding.ProjectId, ct);
        if (project is null || project.IsLocalDirectory || !System.IO.Directory.Exists(project.RepositoryPath))
        {
            return BindingStatus.RepositoryMissing;
        }

        // SSH Key 缺失检查。
        if (account.AuthenticationType is AuthenticationType.Ssh or AuthenticationType.Both
            && !string.IsNullOrWhiteSpace(account.SshPrivateKeyPath)
            && !System.IO.File.Exists(account.SshPrivateKeyPath))
        {
            return BindingStatus.SshKeyMissing;
        }

        // HTTPS Credential 缺失检查。
        if (account.AuthenticationType is AuthenticationType.Https or AuthenticationType.Both
            && !string.IsNullOrWhiteSpace(account.HttpsSecretId))
        {
            var secret = await _secretStore.GetAsync(account.HttpsSecretId, ct);
            if (string.IsNullOrEmpty(secret))
            {
                return BindingStatus.CredentialMissing;
            }
        }

        return BindingStatus.Active;
    }

    /// <summary>校验绑定。供 AccountService 删除守卫使用。</summary>
    public Task<int> CountByAccountIdAsync(Guid accountId, CancellationToken ct = default)
        => _repository.CountByAccountIdAsync(accountId, ct);

    /// <summary>执行绑定连接测试（只读认证测试）。</summary>
    public async Task<TestRemoteResult> TestAsync(
        Binding binding,
        CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var account = await _accountRepository.GetByIdAsync(binding.AccountId, ct);
        var project = await _projectRepository.GetByIdAsync(binding.ProjectId, ct);
        if (account is null || project is null)
        {
            return new TestRemoteResult { Error = new DomainError("BINDING_INCOMPLETE") };
        }

        if (project.IsLocalDirectory)
            return new TestRemoteResult { Error = new DomainError("PROJECT_GIT_NOT_INITIALIZED") };

        var effectiveAccount = _effectiveAccountResolver is null ? account : _effectiveAccountResolver.Resolve(project);
        if (effectiveAccount is null || !effectiveAccount.Enabled)
            return new TestRemoteResult { Error = new DomainError("TRANSFER_ACCOUNT_INVALID") };

        try
        {
            // 与实际传输共用指定账号认证，只读检查实际 origin，不持久写入 Git 配置。
            var result = await _gitTransfer.TestAsync(project.RepositoryPath, effectiveAccount, ct);
            return new TestRemoteResult
            {
                Success = result.IsSuccess,
                Error = result.Error,
                Duration = started.Elapsed,
            };
        }
        catch (Exception)
        {
            return new TestRemoteResult
            {
                Error = new DomainError(ct.IsCancellationRequested ? "TRANSFER_CANCELLED" : "TEST_FAILED"),
                Duration = started.Elapsed,
            };
        }
    }

    /// <summary>开启或切换全局模式时，以同一账号重写所有已登记仓库的本地 Git 身份。</summary>
    public Task<Result> ApplyGlobalAccountAsync(Account account, CancellationToken ct = default)
        => ApplyToBoundRepositoriesAsync(_ => Task.FromResult<Account?>(account), ct);

    /// <summary>关闭全局模式时，按各仓库原有绑定重新应用身份。</summary>
    public Task<Result> RestoreBoundAccountsAsync(CancellationToken ct = default)
        => ApplyToBoundRepositoriesAsync(
            binding => _accountRepository.GetByIdAsync(binding.AccountId, ct),
            ct);

    /// <summary>账号编辑后仅同步当前实际使用该账号的绑定仓库，不更改远程地址或认证配置。</summary>
    public async Task<Result> ReapplyAccountIdentityAsync(Guid accountId, CancellationToken ct = default)
    {
        DomainError? firstError = null;
        foreach (var binding in await _repository.GetAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var project = await _projectRepository.GetByIdAsync(binding.ProjectId, ct);
            if (project is null || project.IsLocalDirectory)
                continue;

            var account = _effectiveAccountResolver is null
                ? await _accountRepository.GetByIdAsync(binding.AccountId, ct)
                : _effectiveAccountResolver.Resolve(project);
            if (account is null || !account.Enabled || account.Id != accountId)
                continue;

            if (string.IsNullOrWhiteSpace(project.RepositoryPath) || !Directory.Exists(project.RepositoryPath))
            {
                firstError ??= new DomainError("PROJECT_REPOSITORY_NOT_FOUND", project.RepositoryPath);
                continue;
            }

            try
            {
                await EnsureSnapshotAsync(project, ct);
                await _gitConfigApplier.ApplyIdentityAsync(project.RepositoryPath, account.GitName, account.GitEmail, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                firstError ??= new DomainError("BINDING_APPLY_FAILED", project.RepositoryPath);
            }
        }

        return firstError is null ? Result.Success() : Result.Failure(firstError);
    }

    /// <summary>确保仓库已有配置快照（保留最初的原始配置，供解绑时回滚）。</summary>
    private async Task EnsureSnapshotAsync(Project project, CancellationToken ct)
    {
        var existing = await _snapshotRepository.GetByProjectIdAsync(project.Id, ct);
        if (existing is not null)
        {
            if (existing.EmailConfig is null)
            {
                // 旧快照已保存原 user.email；仅补采集此前从未托管的作者/提交者邮箱。
                // 旧空字符串无法区分缺失与显式空，沿用旧版解绑时删除该键的语义。
                var current = await _gitConfigApplier.ReadEmailConfigAsync(project.RepositoryPath, ct);
                existing.EmailConfig = current with
                {
                    UserEmail = string.IsNullOrWhiteSpace(existing.UserEmail) ? null : existing.UserEmail,
                };
                await _snapshotRepository.SaveAsync(existing, ct);
            }
            return;
        }

        var (name, email) = await _gitConfigApplier.ReadIdentityAsync(project.RepositoryPath, ct);
        var emailConfig = await _gitConfigApplier.ReadEmailConfigAsync(project.RepositoryPath, ct);
        var sshCommand = await _gitConfigApplier.ReadSshCommandAsync(project.RepositoryPath, ct);
        var credentialHelper = await _gitConfigApplier.ReadCredentialHelperAsync(project.RepositoryPath, ct);

        var snapshot = new RepositorySnapshot
        {
            ProjectId = project.Id,
            UserName = name,
            UserEmail = email,
            EmailConfig = emailConfig,
            SshCommand = sshCommand,
            CredentialHelper = credentialHelper,
            CapturedAt = DateTimeOffset.UtcNow,
        };

        await _snapshotRepository.SaveAsync(snapshot, ct);
    }

    /// <summary>把账号身份及所选认证方式应用到目标仓库，禁止回退到系统已有凭据。</summary>
    private async Task ApplyAccountToRepositoryAsync(Project project, Account account, CancellationToken ct)
    {
        var protocol = GetRemoteProtocol(project);
        if (protocol is RemoteProtocol.Https or RemoteProtocol.Http)
        {
            Guid? credentialAccountId = account.AuthenticationType is AuthenticationType.Https or AuthenticationType.Both
                && !string.IsNullOrWhiteSpace(account.HttpsSecretId)
                ? account.Id
                : null;
            await _gitConfigApplier.ApplyCredentialHelperAsync(project.RepositoryPath, credentialAccountId, ct);
        }

        await _gitConfigApplier.ApplySshCommandAsync(project.RepositoryPath, BuildSshCommand(project, account), ct);
        await _gitConfigApplier.ApplyIdentityAsync(project.RepositoryPath, account.GitName, account.GitEmail, ct);
    }

    private async Task<Result> ApplyToBoundRepositoriesAsync(
        Func<Binding, Task<Account?>> accountResolver,
        CancellationToken ct)
    {
        var bindings = await _repository.GetAllAsync(ct);
        foreach (var binding in bindings)
        {
            var project = await _projectRepository.GetByIdAsync(binding.ProjectId, ct);
            if (project is null || project.IsLocalDirectory || string.IsNullOrWhiteSpace(project.RepositoryPath)
                || !Directory.Exists(project.RepositoryPath))
            {
                continue;
            }

            var account = await accountResolver(binding);
            if (account is null || !account.Enabled)
            {
                return Result.Failure(new DomainError("GLOBALMODE_ACCOUNT_INVALID"));
            }

            try
            {
                await EnsureSnapshotAsync(project, ct);
                await ApplyAccountToRepositoryAsync(project, account, ct);
            }
            catch (Exception)
            {
                return Result.Failure(new DomainError("GLOBALMODE_APPLY_FAILED", project.RepositoryPath));
            }
        }

        return Result.Success();
    }

    private string BuildSshCommand(Project project, Account account)
    {
        if (GetRemoteProtocol(project) is not RemoteProtocol.Ssh)
        {
            return string.Empty;
        }

        // 平台路径、空设备及 Shell 转义交给 Git 适配层，绑定用例只决定是否应用 SSH。
        return _gitConfigApplier.BuildSshCommand(account);
    }

    private RemoteProtocol GetRemoteProtocol(Project project)
    {
        return project.RemoteProtocol is not RemoteProtocol.Unknown
            ? project.RemoteProtocol
            : _gitService.ParseRemote(project.OriginUrl).Protocol;
    }
}
