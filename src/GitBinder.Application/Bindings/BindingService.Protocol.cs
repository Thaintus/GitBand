using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;

namespace GitBinder.Application.Bindings;

/// <summary>待用户确认的协议切换；保留确认时的目标、原地址和有效账号以拒绝过期操作。</summary>
public sealed record BindingProtocolChange(
    Guid ProjectId,
    Guid EffectiveAccountId,
    string AccountName,
    string RepositoryPath,
    string CurrentUrl,
    string TargetUrl,
    RemoteProtocol TargetProtocol);

public sealed partial class BindingService
{
    /// <summary>只读预览：仅在当前协议缺少凭据、而另一协议已配置时建议切换，不修改 origin 或配置。</summary>
    public async Task<Result<BindingProtocolChange?>> GetProtocolChangeAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var context = await InspectProtocolAsync(projectId, ct);
        return context.IsSuccess
            ? Result<BindingProtocolChange?>.Success(context.Value!.Change)
            : Result<BindingProtocolChange?>.Failure(context.Error!);
    }

    /// <summary>用户确认后只更新 origin，不修改 pushurl；失败时尽力恢复原地址、缓存及同一账号的配置。</summary>
    public async Task<Result> ChangeProtocolAsync(BindingProtocolChange change, CancellationToken ct = default)
    {
        var inspected = await InspectProtocolAsync(change.ProjectId, ct);
        if (!inspected.IsSuccess)
            return Result.Failure(inspected.Error!);
        var context = inspected.Value!;
        var current = context.Change;
        if (current is null
            || current.EffectiveAccountId != change.EffectiveAccountId
            || current.RepositoryPath != change.RepositoryPath
            || current.CurrentUrl != change.CurrentUrl
            || current.TargetUrl != change.TargetUrl
            || current.TargetProtocol != change.TargetProtocol)
        {
            return Result.Failure(new DomainError("PROJECT_PROTOCOL_SWITCH_STALE"));
        }

        var original = CopyProtocolProject(context.Project);
        var mutationAttempted = false;
        try
        {
            // 快照仅保存首次接管前的配置；协议切换不得覆盖已有解绑快照。
            await EnsureSnapshotAsync(context.Project, ct);
            ct.ThrowIfCancellationRequested();
            mutationAttempted = true;
            var changed = await _gitService.SetTransferOriginUrlAsync(original.RepositoryPath, current.TargetUrl, ct);
            if (!changed.IsSuccess)
                throw new InvalidOperationException("Origin update failed.");

            var actualUrl = await _gitService.GetTransferOriginUrlAsync(original.RepositoryPath, ct);
            if (actualUrl is null || !string.Equals(actualUrl, current.TargetUrl, StringComparison.Ordinal))
                throw new InvalidOperationException("Origin changed during protocol switch.");

            var updated = CopyProtocolProject(original);
            updated.OriginUrl = actualUrl;
            (updated.RemoteProtocol, updated.RemoteHost) = _gitService.ParseRemote(actualUrl);
            updated.LastTestResult = string.Empty;
            updated.LastTestAt = null;
            updated.UpdatedAt = DateTimeOffset.UtcNow;
            // 即使绑定记录早已是 B，也必须按新协议重新应用 B 的认证配置。
            await ApplyAccountToRepositoryAsync(updated, context.Account, ct);
            await _projectRepository.UpdateAsync(updated, ct);
            return Result.Success();
        }
        catch (Exception) when (mutationAttempted)
        {
            // 用户取消/超时也可能发生于 origin 写入之后，回滚不再使用已取消的 Token。
            var recovered = await RestoreProtocolAsync(original, current.CurrentUrl, current.TargetUrl, context.Account);
            return Result.Failure(new DomainError(recovered
                ? "PROJECT_PROTOCOL_SWITCH_FAILED"
                : "PROJECT_PROTOCOL_SWITCH_ROLLBACK_FAILED"));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Result.Failure(new DomainError("PROJECT_PROTOCOL_SWITCH_FAILED"));
        }
    }

    private async Task<Result<ProtocolContext>> InspectProtocolAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            var project = await _projectRepository.GetByIdAsync(projectId, ct);
            if (project is null)
                return ProtocolFailure("PROJECT_NOT_FOUND", projectId.ToString());
            // 普通目录不能通过协议预览或确认入口接管后来出现的父级仓库
            if (project.IsLocalDirectory)
                return ProtocolFailure("PROJECT_GIT_NOT_INITIALIZED");
            if (!await _gitService.ValidateRepositoryAsync(project.RepositoryPath, ct))
                return ProtocolFailure("PROJECT_REPOSITORY_NOT_FOUND", project.RepositoryPath);

            // 不能从缓存推断当前传输协议；IDE 可能刚刚修改了 origin。
            var origin = await _gitService.GetTransferOriginUrlAsync(project.RepositoryPath, ct);
            if (origin is null)
                return ProtocolFailure("PROJECT_METADATA_READ_FAILED");
            if (!GitTransferRemote.TryParse(origin, out var protocol))
                return ProtocolFailure("PROJECT_PROTOCOL_SWITCH_UNSUPPORTED");

            Account? account;
            if (_effectiveAccountResolver is not null)
            {
                account = _effectiveAccountResolver.Resolve(project);
            }
            else
            {
                // 兼容未注入 resolver 的独立调用者，仍保持绑定 > 默认账号优先级。
                var binding = await _repository.GetByProjectIdAsync(projectId, ct);
                account = binding is null ? null : await _accountRepository.GetByIdAsync(binding.AccountId, ct);
                if (account is null || !account.Enabled)
                    account = await _accountRepository.GetDefaultAsync(ct);
            }
            if (account is null || !account.Enabled)
                return ProtocolFailure("TRANSFER_ACCOUNT_INVALID");

            var hasHttps = account.AuthenticationType is AuthenticationType.Https or AuthenticationType.Both
                && !string.IsNullOrWhiteSpace(account.HttpsSecretId);
            var hasSsh = account.AuthenticationType is AuthenticationType.Ssh or AuthenticationType.Both
                && !string.IsNullOrWhiteSpace(account.SshPrivateKeyPath);
            if (protocol == RemoteProtocol.Https ? hasHttps : hasSsh)
                return Result<ProtocolContext>.Success(new(project, account, null));
            if (!(protocol == RemoteProtocol.Https ? hasSsh : hasHttps))
                return ProtocolFailure(protocol == RemoteProtocol.Https ? "TRANSFER_HTTPS_REQUIRED" : "TRANSFER_SSH_REQUIRED");

            if (!GitRemoteUrlConverter.TrySwitchProtocol(origin, out var targetUrl, out var targetProtocol)
                || !GitTransferRemote.TryParse(targetUrl, out var validatedProtocol)
                || targetProtocol != validatedProtocol)
                return ProtocolFailure("PROJECT_PROTOCOL_SWITCH_UNSUPPORTED");

            var change = new BindingProtocolChange(project.Id, account.Id, account.DisplayAlias,
                project.RepositoryPath, origin, targetUrl, targetProtocol);
            return Result<ProtocolContext>.Success(new(project, account, change));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ProtocolFailure("PROJECT_METADATA_READ_FAILED");
        }
    }

    private async Task<bool> RestoreProtocolAsync(
        Project original, string originalUrl, string attemptedUrl, Account effectiveAccount)
    {
        var recovered = true;
        string? actualUrl = null;
        try
        {
            actualUrl = await _gitService.GetTransferOriginUrlAsync(original.RepositoryPath, CancellationToken.None);
            if (actualUrl == attemptedUrl)
            {
                // 仅撤销仍属于本次操作的地址，不能覆盖 IDE 在此期间另行修改的第三个地址。
                var restored = await _gitService.SetTransferOriginUrlAsync(original.RepositoryPath, originalUrl, CancellationToken.None);
                actualUrl = null;
                actualUrl = await _gitService.GetTransferOriginUrlAsync(original.RepositoryPath, CancellationToken.None);
                recovered = restored.IsSuccess && actualUrl == originalUrl;
            }
            else
            {
                // 已是原地址则无需再写；第三方地址或读取失败均保留现场并报告回滚未完成。
                recovered = actualUrl == originalUrl;
            }
        }
        catch (Exception) { recovered = false; actualUrl = null; }

        try
        {
            // 回滚 origin 也可能失败；必须按此时实际地址保持 B 的认证隔离，不能误清 SSH 后回退系统密钥。
            if (actualUrl is not null && GitTransferRemote.TryParse(actualUrl, out _))
            {
                var configurationProject = CopyProtocolProject(original);
                configurationProject.OriginUrl = actualUrl;
                (configurationProject.RemoteProtocol, configurationProject.RemoteHost) = _gitService.ParseRemote(actualUrl);
                await ApplyAccountToRepositoryAsync(configurationProject, effectiveAccount, CancellationToken.None);
            }
            else
            {
                recovered = false;
                // 无法读取真实协议时，两类认证均禁止回退，等待用户检查配置，不猜测原账号。
                await _gitConfigApplier.ApplyCredentialHelperAsync(original.RepositoryPath, null, CancellationToken.None);
                var deniedSsh = BuildSshCommand(new Project { RemoteProtocol = RemoteProtocol.Ssh }, new Account());
                await _gitConfigApplier.ApplySshCommandAsync(original.RepositoryPath, deniedSsh, CancellationToken.None);
                await _gitConfigApplier.ApplyIdentityAsync(original.RepositoryPath,
                    effectiveAccount.GitName, effectiveAccount.GitEmail, CancellationToken.None);
            }
        }
        catch (Exception) { recovered = false; }

        try { await _projectRepository.UpdateAsync(original, CancellationToken.None); }
        catch (Exception) { recovered = false; }
        return recovered;
    }

    private static Result<ProtocolContext> ProtocolFailure(string code, params string[] args)
        => Result<ProtocolContext>.Failure(new DomainError(code, args));

    private sealed record ProtocolContext(Project Project, Account Account, BindingProtocolChange? Change);

    private static Project CopyProtocolProject(Project project) => new()
    {
        Id = project.Id, Name = project.Name,
        RepositoryPath = project.RepositoryPath, CanonicalPath = project.CanonicalPath,
        GitDir = project.GitDir, OriginUrl = project.OriginUrl,
        RemoteProtocol = project.RemoteProtocol, RemoteHost = project.RemoteHost,
        CurrentBranch = project.CurrentBranch,
        LastTestResult = project.LastTestResult, LastTestAt = project.LastTestAt,
        CreatedAt = project.CreatedAt, UpdatedAt = project.UpdatedAt,
    };
}
