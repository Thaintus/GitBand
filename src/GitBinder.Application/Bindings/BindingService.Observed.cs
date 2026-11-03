using GitBinder.Application.Git;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Services;

namespace GitBinder.Application.Bindings;

public sealed partial class BindingService
{
    /// <summary>用户手动刷新时，按读取的姓名/邮箱及项目主机唯一匹配账号，只同步绑定记录。</summary>
    public async Task<Result<Binding>> SynchronizeRecordedBindingAsync(Guid projectId, RepositoryIdentity identity,
        CancellationToken ct = default)
    {
        try
        {
            var project = await _projectRepository.GetByIdAsync(projectId, ct);
            if (project is null) return Result<Binding>.Failure(new DomainError("PROJECT_NOT_FOUND"));
            if (project.IsLocalDirectory)
                return Result<Binding>.Failure(new DomainError("PROJECT_GIT_NOT_INITIALIZED"));
            if (string.IsNullOrWhiteSpace(identity.UserName) || string.IsNullOrWhiteSpace(identity.UserEmail))
                return Result<Binding>.Failure(new DomainError("BINDING_REFRESH_NO_MATCH"));
            var matches = (await _accountRepository.GetAllAsync(ct)).Where(account => account.Enabled
                && string.Equals(account.GitName.Trim(), identity.UserName.Trim(), StringComparison.Ordinal)
                && string.Equals(account.GitEmail.Trim(), identity.UserEmail.Trim(), StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(project.RemoteHost)
                    || string.Equals(account.Host.Trim().TrimEnd('.'), project.RemoteHost.Trim().TrimEnd('.'),
                        StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count != 1)
                return Result<Binding>.Failure(new DomainError(matches.Count == 0
                    ? "BINDING_REFRESH_NO_MATCH" : "BINDING_REFRESH_AMBIGUOUS"));
            var stored = await _repository.GetByProjectIdAsync(projectId, ct);
            if (stored?.AccountId == matches[0].Id) return Result<Binding>.Success(stored);
            // 全局覆盖不能反推项目原绑定，禁止把全局账号批量登记成项目绑定。
            if (_effectiveAccountResolver?.ResolveReason(project) == EffectiveReason.GlobalMode)
                return Result<Binding>.Failure(new DomainError("BINDING_REFRESH_GLOBAL_MODE"));

            // 仅捕获原快照，保留最早的解绑恢复点；绝不调用 Apply/Restore 或 BindAsync。
            await EnsureSnapshotAsync(project, ct);
            var updated = stored is null ? new Binding { ProjectId = projectId } : CopyBinding(stored);
            updated.AccountId = matches[0].Id;
            updated.Status = BindingStatus.Active;
            updated.AppliedAt = DateTimeOffset.UtcNow;
            updated.VerifiedAt = null;
            updated.LastTestResult = string.Empty;
            if (stored is null) await _repository.AddAsync(updated, ct);
            else await _repository.UpdateAsync(updated, ct);
            return Result<Binding>.Success(updated);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Result<Binding>.Failure(new DomainError("BINDING_REFRESH_FAILED"));
        }
    }
}
