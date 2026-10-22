using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.Settings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Infrastructure.Common;

namespace GitBinder.Infrastructure.Git;

/// <summary>
/// Git 定位器，支持用户设置、系统 PATH 及 Windows / macOS 常见安装目录。
/// </summary>
public sealed class GitLocator : IGitLocator
{
    private readonly ISettingsRepository _settings;

    public GitLocator(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task<string?> LocateAsync(CancellationToken ct = default)
    {
        // 1. 用户设置。
        var configured = await _settings.GetAsync("git.executable", ct);
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(configured) && ExecutableDiscovery.IsExecutable(configured))
        {
            return configured;
        }

        // 2. PATH。
        var onPath = ExecutableDiscovery.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");
        if (onPath is not null)
        {
            return onPath;
        }

        // 3. 常见安装目录。
        foreach (var common in CommonLocations())
        {
            if (ExecutableDiscovery.IsExecutable(common))
            {
                return common;
            }
        }

        return null;
    }

    private static IEnumerable<string> CommonLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Finder 启动不会读取 zsh/bash 登录配置，显式包含两种架构的 Homebrew 与系统 Git。
            yield return "/opt/homebrew/bin/git";
            yield return "/usr/local/bin/git";
            yield return "/usr/bin/git";
            yield break;
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var programFiles = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
            }
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");
        }
    }
}

/// <summary>
/// IGitService 实现，封装 Git CLI 行为。
/// </summary>
public sealed class GitService : IGitService
{
    private readonly ICommandExecutor _executor;
    private readonly IGitLocator _locator;

    public GitService(ICommandExecutor executor, IGitLocator locator)
    {
        _executor = executor;
        _locator = locator;
    }

    // 无论定位结果如何都回退到 PATH 中的 git，因此调用方始终可获得非空可执行文件名。
    private async Task<string> ResolveGitAsync(CancellationToken ct)
        => await _locator.LocateAsync(ct) ?? "git";

    public async Task<Result> InitializeRepositoryAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || path.Any(char.IsControl))
                return InitializationFailed();

            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(directory) || PathsEqual(directory, Path.GetPathRoot(directory)))
                return InitializationFailed();

            // 根目录的符号链接同样不能作为初始化目标
            var linkTarget = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true);
            if (linkTarget is not null && PathsEqual(linkTarget.FullName, Path.GetPathRoot(linkTarget.FullName)))
                return InitializationFailed();

            var git = await ResolveGitAsync(ct);
            var options = GitTransfer.BaseOptions(timeout: TimeSpan.FromSeconds(60));
            async Task<CommandResult> ReadRootAsync()
            {
                var root = await _executor.ExecuteWithOptionsAsync(git,
                    [.. GitTransfer.BaseArguments(), "rev-parse", "--show-toplevel"], directory, options, ct);
                ct.ThrowIfCancellationRequested();
                return root;
            }

            var markerPath = Path.Combine(directory, ".git");
            var markerExists = GitMarkerExists(markerPath);
            var existingRoot = await ReadRootAsync();
            if (markerExists && existingRoot.IsSuccess && PathsEqual(existingRoot.StdOut.Trim(), directory))
                return Result.Success();

            // 本目录已有 .git 却无法精确定位时保持原状，避免改写异常仓库或工作树
            if (markerExists)
                return InitializationFailed();

            ct.ThrowIfCancellationRequested();
            var initialized = await _executor.ExecuteWithOptionsAsync(git,
                [.. GitTransfer.BaseArguments(), "init", "--template=", "--", directory], directory, options, ct);
            ct.ThrowIfCancellationRequested();
            if (!initialized.IsSuccess)
                return InitializationFailed();

            // 父级后来成为仓库也必须在指定目录创建独立仓库，成功后再核对真实根目录
            var actualRoot = await ReadRootAsync();
            return GitMarkerExists(markerPath) && actualRoot.IsSuccess && PathsEqual(actualRoot.StdOut.Trim(), directory)
                ? Result.Success() : InitializationFailed();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InitializationFailed();
        }
    }

    private static Result InitializationFailed()
        => Result.Failure(new DomainError("PROJECT_GIT_INITIALIZATION_FAILED"));

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)
            || !Path.IsPathFullyQualified(first) || !Path.IsPathFullyQualified(second))
            return false;

        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool GitMarkerExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    // 项目读写只隔离仓库定位与环境配置注入，保留用户全局、系统及 SSH 配置的读取语义
    internal static CommandExecutionOptions RepositoryOptions() => new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        Environment = new Dictionary<string, string?>
        {
            ["GIT_DIR"] = null, ["GIT_WORK_TREE"] = null, ["GIT_COMMON_DIR"] = null,
            ["GIT_INDEX_FILE"] = null, ["GIT_OBJECT_DIRECTORY"] = null,
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null, ["GIT_CONFIG"] = null,
            ["GIT_CONFIG_COUNT"] = "0", ["GIT_CONFIG_PARAMETERS"] = "",
        },
    };

    private async Task<CommandResult> ExecuteRepositoryAsync(string git, IReadOnlyList<string> arguments,
        string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await _executor.ExecuteWithOptionsAsync(git,
            [.. GitTransfer.BaseArguments(), .. arguments], path, RepositoryOptions(), ct);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    public async Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteRepositoryAsync(git, ["rev-parse", "--git-dir"], path, ct);
        return result.IsSuccess;
    }

    public async Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteRepositoryAsync(git, ["rev-parse", "--show-toplevel"], path, ct);
        return result.IsSuccess ? result.StdOut.Trim() : null;
    }

    public async Task<string?> GetGitDirAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteRepositoryAsync(git, ["rev-parse", "--git-dir"], path, ct);
        if (!result.IsSuccess)
        {
            return null;
        }

        var gitDir = result.StdOut.Trim();
        // 转换为绝对路径
        if (!Path.IsPathRooted(gitDir))
        {
            var root = await GetRepositoryRootAsync(path, ct) ?? path;
            gitDir = Path.GetFullPath(Path.Combine(root, gitDir));
        }

        return gitDir;
    }

    public async Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteRepositoryAsync(git, ["remote", "get-url", "origin"], path, ct);
        if (result.IsSuccess)
            return result.StdOut.Trim();

        // 区分未配置 origin（空字符串）与 Git 读取失败（null），避免错误覆盖缓存
        var configured = await ExecuteRepositoryAsync(git, ["config", "--get", "remote.origin.url"], path, ct);
        return configured.ExitCode == 1 ? string.Empty : null;
    }

    public async Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        var options = GitTransfer.TransferOptions(timeout: TimeSpan.FromSeconds(60));
        var result = await _executor.ExecuteWithOptionsAsync(git,
            [.. GitTransfer.BaseArguments(), "remote", "get-url", "origin"], path, options, ct);
        ct.ThrowIfCancellationRequested();
        if (result.IsSuccess) return result.StdOut.Trim();

        // 预览与实际传输使用相同的本机 Git 配置，同时隔离继承的仓库定位变量
        var configured = await _executor.ExecuteWithOptionsAsync(git,
            [.. GitTransfer.BaseArguments(), "config", "--get", "remote.origin.url"], path, options, ct);
        ct.ThrowIfCancellationRequested();
        return configured.ExitCode == 1 ? string.Empty : null;
    }

    public async Task<Result> SetTransferOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        // set-url 对缺失 origin 自行失败，禁止把预览期间消失的远程重新新增回来。
        var result = await _executor.ExecuteWithOptionsAsync(git,
            [.. GitTransfer.BaseArguments(), "remote", "set-url", "origin", originUrl], path,
            GitTransfer.TransferOptions(timeout: TimeSpan.FromSeconds(60)), ct);
        ct.ThrowIfCancellationRequested();
        return result.IsSuccess ? Result.Success()
            : Result.Failure(new DomainError("PROJECT_REMOTE_UPDATE_FAILED", path));
    }

    public async Task<Result> SetOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        var existing = await GetOriginUrlAsync(path, ct);
        if (existing is null)
            return Result.Failure(new DomainError("PROJECT_REMOTE_UPDATE_FAILED", path));
        var arguments = string.IsNullOrWhiteSpace(existing)
            ? new[] { "remote", "add", "origin", originUrl }
            : new[] { "remote", "set-url", "origin", originUrl };
        var result = await ExecuteRepositoryAsync(git, arguments, path, ct);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(new DomainError("PROJECT_REMOTE_UPDATE_FAILED", path));
    }

    public async Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var git = await ResolveGitAsync(ct);
        var result = await ExecuteRepositoryAsync(git, ["branch", "--show-current"], path, ct);
        return result.IsSuccess ? result.StdOut.Trim() : null;
    }

    public (RemoteProtocol Protocol, string Host) ParseRemote(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (RemoteProtocol.Unknown, string.Empty);
        }

        var trimmed = url.Trim();

        // SCP-like: git@host:path/repo.git
        if (trimmed.StartsWith("git@", StringComparison.Ordinal))
        {
            var at = trimmed.IndexOf('@');
            var colon = trimmed.IndexOf(':', at + 1);
            if (colon > at)
            {
                var host = trimmed.Substring(at + 1, colon - at - 1);
                return (RemoteProtocol.Ssh, host);
            }
        }

        // ssh://
        if (trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var host = ExtractHostFromUri(trimmed, "ssh://");
            return (RemoteProtocol.Ssh, host);
        }

        // https://
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var host = ExtractHostFromUri(trimmed, "https://");
            return (RemoteProtocol.Https, host);
        }

        // http://
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var host = ExtractHostFromUri(trimmed, "http://");
            return (RemoteProtocol.Http, host);
        }

        // 本地路径。
        if (Directory.Exists(trimmed) || Path.IsPathRooted(trimmed))
        {
            return (RemoteProtocol.File, string.Empty);
        }

        return (RemoteProtocol.Unknown, string.Empty);
    }

    public async Task<string?> GetGitVersionAsync(CancellationToken ct = default)
    {
        var git = await ResolveGitAsync(ct);
        var result = await _executor.ExecuteAsync(git, ["--version"], null, ct);
        return result.IsSuccess ? result.StdOut.Trim() : null;
    }

    private static string ExtractHostFromUri(string uri, string scheme)
    {
        var rest = uri.Substring(scheme.Length);
        var slash = rest.IndexOf('/');
        var hostPort = slash >= 0 ? rest.Substring(0, slash) : rest;

        // 去除 userinfo 与端口。
        var at = hostPort.LastIndexOf('@');
        if (at >= 0)
        {
            hostPort = hostPort.Substring(at + 1);
        }

        var colon = hostPort.LastIndexOf(':');
        if (colon >= 0 && colon > hostPort.LastIndexOf(']'))
        {
            hostPort = hostPort.Substring(0, colon);
        }

        return hostPort;
    }
}
