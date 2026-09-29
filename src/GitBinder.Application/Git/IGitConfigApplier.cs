using GitBinder.Domain.Accounts;
using GitBinder.Domain.GlobalMode;

namespace GitBinder.Application.Git;

/// <summary>
/// Git 配置写入器（非侵入式？见 <see cref="GitConfigApplier"/>）。
/// 负责对临时仓库应用账号并支持回滚。
/// </summary>
public interface IGitConfigApplier
{
    /// <summary>构造平台适配的 SSH 命令；账号无 SSH 私钥时禁止回退到其他密钥。</summary>
    string BuildSshCommand(Account account);

    /// <summary>读取当前仓库用户身份。</summary>
    Task<(string Name, string Email)> ReadIdentityAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>读取仓库本地三个邮箱配置项，保留缺失与显式空值的区别。</summary>
    Task<GitEmailConfigSnapshot> ReadEmailConfigAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>读取当前仓库 SSH Command。</summary>
    Task<string> ReadSshCommandAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>读取当前仓库 credential.helper。</summary>
    Task<string> ReadCredentialHelperAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>应用账号身份（写入仓库本地 config，用于 Materialize 兼容模式）。</summary>
    Task ApplyIdentityAsync(
        string repositoryPath,
        string gitName,
        string gitEmail,
        CancellationToken ct = default);

    /// <summary>应用 SSH Command。</summary>
    Task ApplySshCommandAsync(
        string repositoryPath,
        string sshCommand,
        CancellationToken ct = default);

    /// <summary>
    /// 仅为指定账号配置 HTTPS Credential Helper。传入空账号时清空本仓库的 Helper 链，
    /// 防止 Git 回退到用户已有的全局凭据。
    /// </summary>
    Task ApplyCredentialHelperAsync(
        string repositoryPath,
        Guid? accountId,
        CancellationToken ct = default);

    /// <summary>将 Git 配置还原为指定值。</summary>
    Task RestoreAsync(
        string repositoryPath,
        string? name,
        string? email,
        string? sshCommand,
        string? credentialHelper,
        CancellationToken ct = default,
        GitEmailConfigSnapshot? emailConfig = null);
}
