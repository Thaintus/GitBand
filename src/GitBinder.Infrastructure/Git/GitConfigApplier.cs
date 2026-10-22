using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.GlobalMode;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;
using GitBinder.Domain.GlobalMode;
using GitBinder.Infrastructure.Common;

namespace GitBinder.Infrastructure.Git;

/// <summary>
/// Git Config 应用器实现。
/// </summary>
public sealed class GitConfigApplier : IGitConfigApplier, IGlobalGitConfigApplier
{
    private readonly ICommandExecutor _executor;
    private readonly IGitLocator? _gitLocator;

    public GitConfigApplier(ICommandExecutor executor, IGitLocator? gitLocator = null)
    {
        _executor = executor;
        _gitLocator = gitLocator;
    }

    public string BuildSshCommand(Account account)
    {
        if (account.AuthenticationType is AuthenticationType.Ssh or AuthenticationType.Both
            && !string.IsNullOrWhiteSpace(account.SshPrivateKeyPath))
        {
            var path = GitCommandPaths.ToShellPath(account.SshPrivateKeyPath);
            return $"{GitCommandPaths.SshExecutable} -i {GitCommandPaths.Quote(path)} -o IdentitiesOnly=yes";
        }

        // 空身份阻断其他账号密钥，且不依赖 Windows 专用的 NUL 文件名。
        var empty = GitCommandPaths.NullDevice;
        return $"{GitCommandPaths.SshExecutable} -F {empty} -i {empty} -o BatchMode=yes -o IdentitiesOnly=yes"
            + " -o IdentityAgent=none -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no";
    }

    public async Task<(string Name, string Email)> ReadIdentityAsync(string repositoryPath, CancellationToken ct = default)
    {
        var name = await ReadConfigAsync(repositoryPath, "user.name", ct);
        var email = await ReadConfigAsync(repositoryPath, "user.email", ct);
        return (name, email);
    }

    public Task<GitEmailConfigSnapshot> ReadEmailConfigAsync(string repositoryPath, CancellationToken ct = default)
        => ReadEmailConfigAsync("--local", repositoryPath, ct);

    public async Task<string> ReadSshCommandAsync(string repositoryPath, CancellationToken ct = default)
        => await ReadConfigAsync(repositoryPath, "core.sshCommand", ct);

    public async Task<string> ReadCredentialHelperAsync(string repositoryPath, CancellationToken ct = default)
        => await ReadConfigAsync(repositoryPath, "credential.helper", ct);

    public async Task<GitLocalConfigSnapshot> CaptureLocalConfigAsync(string repositoryPath, CancellationToken ct = default)
    {
        var name = await ReadExactLocalConfigValueAsync(repositoryPath, "user.name", ct);
        var userEmail = await ReadExactLocalConfigValueAsync(repositoryPath, "user.email", ct);
        var authorEmail = await ReadExactLocalConfigValueAsync(repositoryPath, "author.email", ct);
        var committerEmail = await ReadExactLocalConfigValueAsync(repositoryPath, "committer.email", ct);
        var sshCommand = await ReadExactLocalConfigValueAsync(repositoryPath, "core.sshCommand", ct);
        var helpers = await ReadExactLocalConfigValuesAsync(repositoryPath, "credential.helper", true, ct);
        return new GitLocalConfigSnapshot(name, new GitEmailConfigSnapshot(userEmail, authorEmail, committerEmail),
            sshCommand, helpers);
    }

    public async Task RestoreLocalConfigAsync(string repositoryPath, GitLocalConfigSnapshot snapshot,
        CancellationToken ct = default)
    {
        await RestoreExactLocalConfigValueAsync(repositoryPath, "user.name", snapshot.UserName, ct);
        foreach (var (key, value) in GetEmailConfigValues(snapshot.EmailConfig))
        {
            await RestoreExactLocalConfigValueAsync(repositoryPath, key, value, ct);
        }
        await RestoreExactLocalConfigValueAsync(repositoryPath, "core.sshCommand", snapshot.SshCommand, ct);

        if (snapshot.CredentialHelpers.Count == 0)
        {
            await WriteLocalConfigAsync(repositoryPath, "credential.helper", null, ct);
            return;
        }

        // 首项可能是阻断全局凭据继承的空 reset，不能按空白配置删除
        await WriteLocalConfigValueAsync(repositoryPath, "credential.helper", snapshot.CredentialHelpers[0], ct);
        foreach (var helper in snapshot.CredentialHelpers.Skip(1))
        {
            await AddLocalConfigAsync(repositoryPath, "credential.helper", helper, ct);
        }
    }

    public async Task ApplyIdentityAsync(
        string repositoryPath,
        string gitName,
        string gitEmail,
        CancellationToken ct = default)
    {
        await WriteLocalConfigAsync(repositoryPath, "user.name", gitName, ct);
        // 显式空值阻断低优先级邮箱；author/committer 的独立配置也必须随账号一起覆盖。
        var email = NormalizeAccountEmail(gitEmail);
        foreach (var key in EmailConfigKeys)
        {
            await WriteLocalConfigValueAsync(repositoryPath, key, email, ct);
        }
    }

    public async Task ApplySshCommandAsync(
        string repositoryPath,
        string sshCommand,
        CancellationToken ct = default)
    {
        await WriteLocalConfigAsync(repositoryPath, "core.sshCommand", sshCommand, ct);
    }

    public async Task ApplyCredentialHelperAsync(
        string repositoryPath,
        Guid? accountId,
        CancellationToken ct = default)
    {
        // 先确认 Helper 可用，再重置原有链；若随附程序缺失，不能留下一个“看似已改绑”
        // 但仍可能回退到系统凭据的仓库配置。
        var helperCommand = accountId is Guid id ? BuildCredentialHelperCommand(id) : null;
        await WriteLocalConfigValueAsync(repositoryPath, "credential.helper", string.Empty, ct);
        if (!string.IsNullOrWhiteSpace(helperCommand))
        {
            await AddLocalConfigAsync(repositoryPath, "credential.helper", helperCommand, ct);
        }
    }

    public async Task RestoreAsync(
        string repositoryPath,
        string? name,
        string? email,
        string? sshCommand,
        string? credentialHelper,
        CancellationToken ct = default,
        GitEmailConfigSnapshot? emailConfig = null)
    {
        await RestoreKeyAsync(repositoryPath, "user.name", name, ct);
        if (emailConfig is null)
        {
            // 旧快照没有独立邮箱配置，保留其恢复语义，避免覆盖未记录的配置。
            await RestoreKeyAsync(repositoryPath, "user.email", email, ct);
        }
        else
        {
            foreach (var (key, value) in GetEmailConfigValues(emailConfig))
            {
                if (value is null)
                {
                    await WriteLocalConfigAsync(repositoryPath, key, null, ct);
                }
                else
                {
                    await WriteLocalConfigValueAsync(repositoryPath, key, value, ct);
                }
            }
        }
        await RestoreKeyAsync(repositoryPath, "core.sshCommand", sshCommand, ct);
        await RestoreKeyAsync(repositoryPath, "credential.helper", credentialHelper, ct);
    }

    public async Task<GlobalGitConfigSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var name = await ReadGlobalConfigAsync("user.name", ct);
        var emailConfig = await ReadEmailConfigAsync("--global", null, ct);
        var sshCommand = await ReadGlobalConfigAsync("core.sshCommand", ct);
        var credentialHelpers = await ReadGlobalConfigValuesAsync("credential.helper", ct);
        return new GlobalGitConfigSnapshot(name, emailConfig.UserEmail, sshCommand, credentialHelpers, emailConfig);
    }

    public async Task<Result> ApplyAsync(Account account, CancellationToken ct = default)
    {
        var name = await WriteGlobalConfigAsync("user.name", account.GitName, ct);
        if (!name.IsSuccess)
        {
            return name;
        }

        var emailValue = NormalizeAccountEmail(account.GitEmail);
        foreach (var key in EmailConfigKeys)
        {
            var email = await WriteGlobalConfigValueAsync(key, emailValue, ct);
            if (!email.IsSuccess)
            {
                return email;
            }
        }

        var sshCommand = BuildSshCommand(account);
        var ssh = await WriteGlobalConfigAsync("core.sshCommand", sshCommand, ct);
        if (!ssh.IsSuccess)
        {
            return ssh;
        }

        Guid? credentialAccountId = account.AuthenticationType is AuthenticationType.Https or AuthenticationType.Both
            && !string.IsNullOrWhiteSpace(account.HttpsSecretId)
            ? account.Id
            : null;
        return await ApplyGlobalCredentialHelperAsync(credentialAccountId, ct);
    }

    public async Task<Result> RestoreAsync(GlobalGitConfigSnapshot snapshot, CancellationToken ct = default)
    {
        var name = await WriteGlobalConfigAsync("user.name", snapshot.UserName, ct);
        if (!name.IsSuccess)
        {
            return name;
        }

        // 旧全局快照已能区分 null 与空串，恢复时也须保留显式空邮箱。
        var emailValues = snapshot.EmailConfig is null
            ? new (string Key, string? Value)[] { ("user.email", snapshot.UserEmail) }
            : GetEmailConfigValues(snapshot.EmailConfig);
        foreach (var (key, value) in emailValues)
        {
            var email = value is null
                ? await WriteGlobalConfigAsync(key, null, ct)
                : await WriteGlobalConfigValueAsync(key, value, ct);
            if (!email.IsSuccess)
            {
                return email;
            }
        }

        var ssh = await WriteGlobalConfigAsync("core.sshCommand", snapshot.SshCommand, ct);
        if (!ssh.IsSuccess)
        {
            return ssh;
        }

        return await RestoreGlobalCredentialHelpersAsync(snapshot.CredentialHelpers, ct);
    }

    private async Task<string> ReadConfigAsync(string repositoryPath, string key, CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteLocalConfigAsync(git, ["config", "--local", "--get", key], repositoryPath, ct);
        return result.IsSuccess ? result.StdOut.Trim() : string.Empty;
    }

    private async Task<string?> ReadExactLocalConfigValueAsync(string repositoryPath, string key, CancellationToken ct)
        => (await ReadExactLocalConfigValuesAsync(repositoryPath, key, false, ct)).SingleOrDefault();

    private async Task<string[]> ReadExactLocalConfigValuesAsync(string repositoryPath, string key,
        bool allValues, CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteLocalConfigAsync(git,
            ["config", "--local", "--null", allValues ? "--get-all" : "--get", key], repositoryPath, ct);
        if (result.ExitCode == 1 && result.StdOut.Length == 0 && string.IsNullOrWhiteSpace(result.StdErr))
        {
            return [];
        }
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to read local Git config '{key}' (exit code {result.ExitCode}).");
        }
        if (!result.StdOut.EndsWith('\0'))
        {
            throw new InvalidOperationException($"Invalid null-delimited output for local Git config '{key}'.");
        }

        // 只去掉记录终止符，值内和末尾换行以及空 reset 都属于原配置
        return result.StdOut[..^1].Split('\0', StringSplitOptions.None);
    }

    private Task RestoreExactLocalConfigValueAsync(string repositoryPath, string key, string? value, CancellationToken ct)
        => value is null
            ? WriteLocalConfigAsync(repositoryPath, key, null, ct)
            : WriteLocalConfigValueAsync(repositoryPath, key, value, ct);

    private async Task<GitEmailConfigSnapshot> ReadEmailConfigAsync(
        string scope,
        string? repositoryPath,
        CancellationToken ct)
    {
        var userEmail = await ReadEmailConfigValueAsync(scope, repositoryPath, "user.email", ct);
        var authorEmail = await ReadEmailConfigValueAsync(scope, repositoryPath, "author.email", ct);
        var committerEmail = await ReadEmailConfigValueAsync(scope, repositoryPath, "committer.email", ct);
        return new GitEmailConfigSnapshot(userEmail, authorEmail, committerEmail);
    }

    private async Task<string?> ReadEmailConfigValueAsync(
        string scope,
        string? repositoryPath,
        string key,
        CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var arguments = new[] { "config", scope, "--get", key };
        var result = scope == "--local"
            ? await ExecuteLocalConfigAsync(git, arguments, repositoryPath!, ct)
            : await _executor.ExecuteAsync(git, arguments, repositoryPath, ct);
        if (result.IsSuccess)
        {
            // 仅去掉 Git 输出的行尾，保留空值和原配置中的空白。
            return result.StdOut.TrimEnd('\r', '\n');
        }

        if (result.ExitCode == 1)
        {
            return null;
        }

        throw new InvalidOperationException($"Failed to read Git config '{key}' ({scope}, exit code {result.ExitCode}).");
    }

    private static string NormalizeAccountEmail(string? email) => email?.Trim() ?? string.Empty;

    private static readonly string[] EmailConfigKeys = ["user.email", "author.email", "committer.email"];

    private static (string Key, string? Value)[] GetEmailConfigValues(GitEmailConfigSnapshot snapshot)
        => [("user.email", snapshot.UserEmail), ("author.email", snapshot.AuthorEmail), ("committer.email", snapshot.CommitterEmail)];

    private async Task<string?> ReadGlobalConfigAsync(string key, CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await _executor.ExecuteAsync(git, ["config", "--global", "--get", key], null, ct);
        return result.IsSuccess ? result.StdOut.Trim() : null;
    }

    private async Task<Result> WriteGlobalConfigAsync(string key, string? value, CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var args = string.IsNullOrWhiteSpace(value)
            ? new[] { "config", "--global", "--unset-all", key }
            : new[] { "config", "--global", key, value };
        var result = await _executor.ExecuteAsync(git, args, null, ct);

        // 关闭或恢复不存在的配置项时，git 会返回非零；此时目标状态已满足。
        if (result.IsSuccess || (string.IsNullOrWhiteSpace(value) && result.ExitCode == 5))
        {
            return Result.Success();
        }

        return Result.Failure(new DomainError("GLOBALMODE_GLOBAL_CONFIG_FAILED", key));
    }

    private async Task<Result> ApplyGlobalCredentialHelperAsync(Guid? accountId, CancellationToken ct)
    {
        string? helperCommand;
        try
        {
            helperCommand = accountId is Guid id ? BuildCredentialHelperCommand(id) : null;
        }
        catch (Exception)
        {
            return Result.Failure(new DomainError("GLOBALMODE_GLOBAL_CONFIG_FAILED", "credential.helper"));
        }

        var reset = await WriteGlobalConfigValueAsync("credential.helper", string.Empty, ct);
        if (!reset.IsSuccess || string.IsNullOrWhiteSpace(helperCommand))
        {
            return reset;
        }

        return await AddGlobalConfigAsync("credential.helper", helperCommand, ct);
    }

    private async Task<Result> RestoreGlobalCredentialHelpersAsync(
        IReadOnlyList<string>? credentialHelpers,
        CancellationToken ct)
    {
        if (credentialHelpers is null || credentialHelpers.Count == 0)
        {
            return await WriteGlobalConfigAsync("credential.helper", null, ct);
        }

        var first = await WriteGlobalConfigValueAsync("credential.helper", credentialHelpers[0], ct);
        if (!first.IsSuccess)
        {
            return first;
        }

        foreach (var helper in credentialHelpers.Skip(1))
        {
            var added = await AddGlobalConfigAsync("credential.helper", helper, ct);
            if (!added.IsSuccess)
            {
                return added;
            }
        }

        return Result.Success();
    }

    private async Task<Result> WriteGlobalConfigValueAsync(string key, string value, CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await _executor.ExecuteAsync(git, ["config", "--global", "--replace-all", key, value], null, ct);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(new DomainError("GLOBALMODE_GLOBAL_CONFIG_FAILED", key));
    }

    private async Task<Result> AddGlobalConfigAsync(string key, string value, CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await _executor.ExecuteAsync(git, ["config", "--global", "--add", key, value], null, ct);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(new DomainError("GLOBALMODE_GLOBAL_CONFIG_FAILED", key));
    }

    private async Task<string[]> ReadGlobalConfigValuesAsync(string key, CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await _executor.ExecuteAsync(git, ["config", "--global", "--get-all", key], null, ct);
        if (!result.IsSuccess)
        {
            return [];
        }

        var output = result.StdOut.Replace("\r\n", "\n").TrimEnd('\n');
        return output.Split('\n', StringSplitOptions.None);
    }

    private async Task RestoreKeyAsync(
        string repositoryPath,
        string key,
        string? value,
        CancellationToken ct)
    {
        await WriteLocalConfigAsync(repositoryPath, key, value, ct);
    }

    /// <summary>写入或清除仓库级配置；失败必须上抛，避免界面只更新绑定记录却未真正生效。</summary>
    private async Task WriteLocalConfigAsync(
        string repositoryPath,
        string key,
        string? value,
        CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var args = string.IsNullOrWhiteSpace(value)
            ? new[] { "config", "--local", "--unset-all", key }
            : new[] { "config", "--local", "--replace-all", key, value };
        var result = await ExecuteLocalConfigAsync(git, args, repositoryPath, ct);
        if (result.IsSuccess || (string.IsNullOrWhiteSpace(value) && result.ExitCode == 5))
        {
            return;
        }

        throw new InvalidOperationException($"Failed to write local Git config '{key}' (exit code {result.ExitCode}).");
    }

    private async Task WriteLocalConfigValueAsync(
        string repositoryPath,
        string key,
        string value,
        CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteLocalConfigAsync(
            git,
            ["config", "--local", "--replace-all", key, value],
            repositoryPath,
            ct);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to write local Git config '{key}' (exit code {result.ExitCode}).");
        }
    }

    private async Task AddLocalConfigAsync(
        string repositoryPath,
        string key,
        string value,
        CancellationToken ct)
    {
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteLocalConfigAsync(
            git,
            ["config", "--local", "--add", key, value],
            repositoryPath,
            ct);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to write local Git config '{key}' (exit code {result.ExitCode}).");
        }
    }

    private async Task<CommandResult> ExecuteLocalConfigAsync(string git, IReadOnlyList<string> arguments,
        string repositoryPath, CancellationToken ct)
    {
        // 本地配置按指定目录定位，不能继承父进程指向其他仓库或配置文件的环境
        ct.ThrowIfCancellationRequested();
        var result = await _executor.ExecuteWithOptionsAsync(git, arguments, repositoryPath, GitService.RepositoryOptions(), ct);
        result.ThrowIfTerminationUnconfirmed();
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static string BuildCredentialHelperCommand(Guid accountId)
    {
        var helperPath = GitCommandPaths.CredentialHelper;
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("GitBinder Credential Helper is unavailable.", helperPath);
        }

        var shellPath = GitCommandPaths.ToShellPath(Path.GetFullPath(helperPath));
        return $"!{GitCommandPaths.Quote(shellPath)} --account-id {accountId:D}";
    }

    private async Task<string> ResolveGitAsync(CancellationToken ct)
    {
        if (_gitLocator is not null)
        {
            var located = await _gitLocator.LocateAsync(ct);
            if (!string.IsNullOrWhiteSpace(located))
            {
                return located;
            }
        }

        return "git";
    }
}
