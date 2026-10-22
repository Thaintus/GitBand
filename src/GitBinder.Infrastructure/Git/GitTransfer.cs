using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;

namespace GitBinder.Infrastructure.Git;

/// <summary>使用单次命令参数和环境隔离认证，不把口令/Token写入命令行或 Git 配置。</summary>
public sealed class GitTransfer(ICommandExecutor executor, IGitLocator locator, string? helperPath = null) : IGitTransfer
{
    public async Task<Result> TestAsync(string repositoryPath, Account account, CancellationToken ct = default)
    {
        var stage = "test / prepare";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(repositoryPath)) return Fail("PROJECT_REPOSITORY_NOT_FOUND", repositoryPath);
            var git = await locator.LocateAsync(ct);
            if (string.IsNullOrWhiteSpace(git)) return Fail("TRANSFER_GIT_MISSING");
            var timeout = TimeSpan.FromSeconds(60);
            stage = "git remote get-url origin";
            // 实际远程地址决定认证协议；不使用数据库缓存，也不读取继承环境指定的其他仓库。
            var origin = await executor.ExecuteWithOptionsAsync(git,
                [.. BaseArguments(), "remote", "get-url", "origin"], repositoryPath, TransferOptions(timeout: timeout), ct);
            origin.ThrowIfTerminationUnconfirmed();
            ct.ThrowIfCancellationRequested();
            if (!origin.IsSuccess) return GitTransferFailure.FromCommand(stage, origin, ct);
            var url = origin.StdOut.Trim();
            if (!GitTransferRemote.TryParse(url, out var protocol)) return Fail("TRANSFER_URL_INVALID");
            stage = "SSH/HTTPS / prepare";
            var auth = await BuildAuthenticationAsync(git, account, protocol, url, repositoryPath, ct);
            if (!auth.IsSuccess) return Result.Failure(auth.Error!);
            using var authentication = auth.Value!;
            ct.ThrowIfCancellationRequested();
            var args = BaseArguments();
            args.AddRange(auth.Value!.Arguments);
            // 使用远程名称，让 Git 只应用一次 insteadOf，避免把已改写的 URL 再次改写
            args.AddRange(["ls-remote", "--", "origin"]);
            var options = new CommandExecutionOptions { Timeout = timeout, Environment = auth.Value.Options.Environment };
            stage = "git ls-remote";
            var result = await executor.ExecuteWithOptionsAsync(git, args, repositoryPath, options, ct);
            result.ThrowIfTerminationUnconfirmed();
            ct.ThrowIfCancellationRequested();
            return result.IsSuccess ? Result.Success() : GitTransferFailure.FromCommand(stage, result, ct);
        }
        catch (Exception ex) { return GitTransferFailure.FromException(stage, ex, ct); }
    }

    public async Task<Result> CloneAsync(string remoteUrl, string destination, Account account, CancellationToken ct = default)
    {
        var stage = "clone / prepare";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!GitTransferRemote.TryParse(remoteUrl, out _)) return Fail("TRANSFER_URL_INVALID");
            if (Directory.Exists(destination) || File.Exists(destination)) return Fail("CLONE_TARGET_EXISTS");
            var git = await locator.LocateAsync(ct);
            if (string.IsNullOrWhiteSpace(git)) return Fail("TRANSFER_GIT_MISSING");
            stage = "git ls-remote --get-url";
            var resolved = await executor.ExecuteWithOptionsAsync(git,
                [.. BaseArguments(), "ls-remote", "--get-url", "--", remoteUrl], Path.GetDirectoryName(destination),
                TransferOptions(timeout: TimeSpan.FromSeconds(60)), ct);
            resolved.ThrowIfTerminationUnconfirmed();
            ct.ThrowIfCancellationRequested();
            if (!resolved.IsSuccess) return GitTransferFailure.FromCommand(stage, resolved, ct);
            var effectiveUrl = resolved.StdOut.Trim();
            if (!GitTransferRemote.TryParse(effectiveUrl, out var protocol)) return Fail("TRANSFER_URL_INVALID");
            stage = "SSH/HTTPS / prepare";
            var auth = await BuildAuthenticationAsync(git, account, protocol, effectiveUrl, Path.GetDirectoryName(destination), ct);
            if (!auth.IsSuccess) return Result.Failure(auth.Error!);
            using var authentication = auth.Value!;
            ct.ThrowIfCancellationRequested();
            var args = BaseArguments();
            args.AddRange(auth.Value!.Arguments);
            args.AddRange(["clone", "--no-recurse-submodules", "--", remoteUrl, destination]);
            stage = "git clone";
            var result = await executor.ExecuteWithOptionsAsync(git, args, Path.GetDirectoryName(destination),
                auth.Value.Options, ct);
            result.ThrowIfTerminationUnconfirmed();
            ct.ThrowIfCancellationRequested();
            return result.IsSuccess ? Result.Success() : GitTransferFailure.FromCommand(stage, result, ct);
        }
        catch (Exception ex) { return GitTransferFailure.FromException(stage, ex, ct); }
    }

    public async Task<Result> PullAsync(string repositoryPath, Account account, CancellationToken ct = default)
    {
        var stage = "pull / prepare";
        var pullCompleted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(repositoryPath)) return Fail("PROJECT_REPOSITORY_NOT_FOUND", repositoryPath);
            var git = await locator.LocateAsync(ct);
            if (string.IsNullOrWhiteSpace(git)) return Fail("TRANSFER_GIT_MISSING");
            // 优先使用当前分支的跟踪设置，本地改动是否冲突仍交给 Git 判断
            // 不 stash、不 reset、不自动生成 merge commit
            async Task<CommandResult> Read(params string[] args)
            {
                ct.ThrowIfCancellationRequested();
                stage = "git " + args[0];
                var result = await executor.ExecuteWithOptionsAsync(git, [.. BaseArguments(), .. args], repositoryPath,
                    TransferOptions(timeout: TimeSpan.FromSeconds(60)), ct);
                result.ThrowIfTerminationUnconfirmed();
                ct.ThrowIfCancellationRequested();
                return result;
            }
            var root = await Read("rev-parse", "--show-toplevel");
            if (!root.IsSuccess) return GitTransferFailure.FromCommand(stage, root, ct);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.StdOut.Trim())),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Fail("PROJECT_REPOSITORY_ROOT_MISMATCH");
            foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" })
            {
                var state = await Read("rev-parse", "--git-path", marker);
                if (!state.IsSuccess) return GitTransferFailure.FromCommand(stage, state, ct);
                var statePath = Path.GetFullPath(state.StdOut.Trim(), repositoryPath);
                if (File.Exists(statePath) || Directory.Exists(statePath)) return Fail("PULL_IN_PROGRESS");
            }
            var branch = await Read("symbolic-ref", "--quiet", "--short", "HEAD");
            if (!branch.IsSuccess && branch.ExitCode != 1) return GitTransferFailure.FromCommand(stage, branch, ct);
            if (!branch.IsSuccess || string.IsNullOrWhiteSpace(branch.StdOut)) return Fail("PULL_DETACHED_HEAD");
            var name = branch.StdOut.Trim();
            var remote = await Read("config", "--get", $"branch.{name}.remote");
            if (!remote.IsSuccess && remote.ExitCode != 1) return GitTransferFailure.FromCommand(stage, remote, ct);
            var merge = await Read("config", "--get", $"branch.{name}.merge");
            if (!merge.IsSuccess && merge.ExitCode != 1) return GitTransferFailure.FromCommand(stage, merge, ct);
            if (remote.IsSuccess != merge.IsSuccess) return Fail("PULL_NO_UPSTREAM");
            var remoteName = remote.IsSuccess ? remote.StdOut.Trim() : "origin";
            if (string.IsNullOrWhiteSpace(remoteName) || remoteName == "." || remoteName.StartsWith('-')
                || remoteName.Contains(':') || remoteName.Any(char.IsWhiteSpace) || remoteName.Any(char.IsControl))
                return Fail("PULL_NO_UPSTREAM");
            var needsTracking = !remote.IsSuccess || !merge.IsSuccess;
            var targetRef = merge.IsSuccess ? merge.StdOut.Trim() : "refs/heads/" + name;
            var origin = await Read("remote", "get-url", "--", remoteName);
            if (!origin.IsSuccess) return GitTransferFailure.FromCommand(stage, origin, ct);
            var url = origin.StdOut.Trim();
            if (!GitTransferRemote.TryParse(url, out var protocol)) return Fail("TRANSFER_URL_INVALID");
            stage = "SSH/HTTPS / prepare";
            var auth = await BuildAuthenticationAsync(git, account, protocol, url, repositoryPath, ct);
            if (!auth.IsSuccess) return Result.Failure(auth.Error!);
            using var authentication = auth.Value!;
            ct.ThrowIfCancellationRequested();
            if (needsTracking && !merge.IsSuccess)
            {
                var head = await Read("rev-parse", "--verify", "--quiet", "HEAD");
                if (!head.IsSuccess && head.ExitCode != 1) return GitTransferFailure.FromCommand(stage, head, ct);
                if (!head.IsSuccess)
                {
                    stage = "git ls-remote --symref";
                    var discovery = await executor.ExecuteWithOptionsAsync(git,
                        [.. BaseArguments(), .. authentication.Arguments, "ls-remote", "--symref", "--", remoteName, "HEAD"],
                        repositoryPath, authentication.Options, ct);
                    discovery.ThrowIfTerminationUnconfirmed();
                    ct.ThrowIfCancellationRequested();
                    if (!discovery.IsSuccess) return GitTransferFailure.FromCommand(stage, discovery, ct);
                    var refs = discovery.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.TrimEnd('\r'))
                        .Where(line => line.StartsWith("ref: refs/heads/", StringComparison.Ordinal) && line.EndsWith("\tHEAD", StringComparison.Ordinal))
                        .Select(line => line[5..^5]).Distinct(StringComparer.Ordinal).ToArray();
                    if (refs.Length != 1) return Fail("PULL_NO_UPSTREAM");
                    targetRef = refs[0];
                }
            }
            if (!targetRef.StartsWith("refs/heads/", StringComparison.Ordinal) || targetRef.Any(char.IsControl)
                || !(await Read("check-ref-format", targetRef)).IsSuccess) return Fail("PULL_NO_UPSTREAM");
            var args = BaseArguments();
            args.AddRange(auth.Value!.Arguments);
            args.AddRange(["pull", "--ff-only", "--no-rebase", "--no-recurse-submodules", "--", remoteName, targetRef]);
            stage = "git pull --ff-only";
            ct.ThrowIfCancellationRequested();
            var result = await executor.ExecuteWithOptionsAsync(git, args, repositoryPath, auth.Value.Options, ct);
            result.ThrowIfTerminationUnconfirmed();
            if (!result.IsSuccess) return GitTransferFailure.FromCommand(stage, result, ct);
            pullCompleted = true;
            if (needsTracking)
            {
                // 首次拉取成功后保存明确的目标，不在失败时留下猜测的跟踪分支
                var savedRemote = await Read("config", "--local", "--replace-all", $"branch.{name}.remote", remoteName);
                var savedMerge = savedRemote.IsSuccess
                    ? await Read("config", "--local", "--replace-all", $"branch.{name}.merge", targetRef) : savedRemote;
                if (!savedRemote.IsSuccess || !savedMerge.IsSuccess) return Fail("PULL_TRACKING_SAVE_FAILED");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            if (ex is CommandTerminationUnconfirmedException) return GitTransferFailure.FromException(stage, ex, ct);
            return pullCompleted ? Fail("PULL_TRACKING_SAVE_FAILED") : GitTransferFailure.FromException(stage, ex, ct);
        }
    }

    internal static List<string> BaseArguments() =>
    [
        "-c", $"core.hooksPath={GitCommandPaths.NullDevice}", "-c", "core.fsmonitor=false", "-c", "submodule.recurse=false",
        "-c", "merge.autoStash=false", "-c", "rebase.autoStash=false",
        "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "protocol.ssh.allow=always",
        "-c", "core.askPass=", "-c", "credential.interactive=false", "-c", "http.followRedirects=initial",
    ];

    internal static CommandExecutionOptions BaseOptions(string sshCommand = "", TimeSpan? timeout = null) => new()
    {
        Timeout = timeout ?? TimeSpan.FromMinutes(30),
        Environment = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never",
            ["GIT_ASKPASS"] = "", ["SSH_ASKPASS"] = "", ["SSH_ASKPASS_REQUIRE"] = "never",
            ["GIT_SSH_COMMAND"] = sshCommand, ["GIT_SSH_VARIANT"] = "ssh",
            ["GIT_CONFIG_GLOBAL"] = GitCommandPaths.NullDevice, ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_COUNT"] = "0", ["GIT_CONFIG_PARAMETERS"] = "",
            ["GIT_DIR"] = null, ["GIT_WORK_TREE"] = null, ["GIT_INDEX_FILE"] = null,
            ["GIT_COMMON_DIR"] = null, ["GIT_OBJECT_DIRECTORY"] = null, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null,
            ["GIT_CONFIG"] = null,
        },
    };

    // 传输保留本机 Git 的代理、证书、safe.directory 与 insteadOf，不继承仓库定位或环境配置注入
    // 初始化仍使用完全隔离的 BaseOptions，不能因此套用用户的 init.templateDir 等设置
    internal static CommandExecutionOptions TransferOptions(string sshCommand = "", TimeSpan? timeout = null)
    {
        var isolated = BaseOptions(sshCommand, timeout);
        var environment = isolated.Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        environment.Remove("GIT_CONFIG_GLOBAL");
        environment.Remove("GIT_CONFIG_NOSYSTEM");
        return new CommandExecutionOptions { Timeout = isolated.Timeout, Environment = environment };
    }

    private sealed record Authentication(List<string> Arguments, CommandExecutionOptions Options,
        IDisposable? SshConfiguration = null) : IDisposable
    {
        public void Dispose() => SshConfiguration?.Dispose();
    }

    private async Task<Result<Authentication>> BuildAuthenticationAsync(string gitExecutable, Account account, RemoteProtocol protocol,
        string url, string? workingDirectory, CancellationToken ct)
    {
        if (!account.Enabled) return Result<Authentication>.Failure(new DomainError("TRANSFER_ACCOUNT_INVALID"));
        var args = new List<string> { "-c", "credential.helper=", "-c", "http.extraHeader=" };
        // 认证准备后即使远程配置被并发改写，也不能切换到未准备凭据的另一传输协议
        args.AddRange(["-c", $"protocol.https.allow={(protocol == RemoteProtocol.Https ? "always" : "never")}",
            "-c", $"protocol.ssh.allow={(protocol == RemoteProtocol.Ssh ? "always" : "never")}"]);
        if (protocol == RemoteProtocol.Https)
        {
            if (account.AuthenticationType is not (AuthenticationType.Https or AuthenticationType.Both)
                || string.IsNullOrWhiteSpace(account.HttpsSecretId))
                return Result<Authentication>.Failure(new DomainError("TRANSFER_HTTPS_REQUIRED"));
            var helper = helperPath ?? GitCommandPaths.CredentialHelper;
            if (!File.Exists(helper)) return Result<Authentication>.Failure(new DomainError("TRANSFER_HELPER_MISSING"));
            // 精确 URL 作用域重置，防止仓库内更具体的 helper/extraHeader 覆盖选定账号。
            args.AddRange(["-c", $"credential.{url}.helper=", "-c",
                $"credential.{url}.helper=!{GitCommandPaths.Quote(GitCommandPaths.ToShellPath(helper))} --account-id {account.Id:D}",
                "-c", $"http.{url}.extraHeader="]);
            return Result<Authentication>.Success(new(args, TransferOptions()));
        }
        if (account.AuthenticationType is not (AuthenticationType.Ssh or AuthenticationType.Both)
            || string.IsNullOrWhiteSpace(account.SshPrivateKeyPath) || !File.Exists(account.SshPrivateKeyPath))
            return Result<Authentication>.Failure(new DomainError("TRANSFER_SSH_REQUIRED"));
        var sshExecutable = await GitSshExecutableResolver.ResolveAsync(gitExecutable, ct);
        var configuration = await SshConnectionConfig.CreateAsync(executor, sshExecutable, url, workingDirectory, ct);
        if (!configuration.IsSuccess) return Result<Authentication>.Failure(configuration.Error!);
        // 仅复制 SSH 连接设置，不复制其他 IdentityFile，已解锁的同一密钥可由 Agent 签名
        var keyPath = GitCommandPaths.ToShellPath(Path.GetFullPath(account.SshPrivateKeyPath));
        var configPath = GitCommandPaths.ToShellPath(configuration.Value!.Path);
        var ssh = $"{GitCommandPaths.Quote(GitCommandPaths.ToShellPath(sshExecutable))} -F {GitCommandPaths.Quote(configPath)} -i {GitCommandPaths.Quote(keyPath)} -o IdentitiesOnly=yes"
            + " -o BatchMode=yes -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no -o StrictHostKeyChecking=yes";
        args.AddRange(["-c", $"core.sshCommand={ssh}"]);
        return Result<Authentication>.Success(new(args, TransferOptions(ssh), configuration.Value));
    }

    private static Result Fail(string code, params string[] args) => Result.Failure(new DomainError(code, args));
}
