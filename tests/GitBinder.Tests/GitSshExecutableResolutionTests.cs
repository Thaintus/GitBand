using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Domain.Accounts;
using GitBinder.Infrastructure.Git;
using GitBinder.Infrastructure.Ssh;

namespace GitBinder.Tests;

/// <summary>用临时程序占位文件和命令 Fake 检查 SSH 选择，不执行真实 Git、SSH 或认证</summary>
public sealed class GitSshExecutableResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder ssh's path-" + Guid.NewGuid().ToString("N"));

    public GitSshExecutableResolutionTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("cmd", "test")]
    [InlineData("bin", "test")]
    [InlineData("mingw64/bin", "test")]
    [InlineData("mingw32/bin", "test")]
    [InlineData("cmd", "clone")]
    [InlineData("cmd", "pull")]
    public async Task Windows_UsesSshFromSelectedGitInstallationForConfigurationAndTransfer(string layout, string operation)
    {
        if (!OperatingSystem.IsWindows()) return;
        var git = CreateFile(Path.Combine("selected-git", layout.Replace('/', Path.DirectorySeparatorChar), "git.exe"));
        var ssh = CreateFile(Path.Combine("selected-git", "usr", "bin", "ssh.exe"));
        CreateFile(Path.Combine("other-git", "usr", "bin", "ssh.exe"));
        var executor = new RecordingExecutor(_root);

        var result = await ExecuteAsync(git, executor, operation);

        Assert.True(result.IsSuccess);
        Assert.Equal(ssh, Assert.Single(executor.Calls, call => call.Args[0] == "-G").Executable);
        var command = operation == "test" ? "ls-remote" : operation;
        var transfer = Assert.Single(executor.Calls,
            call => call.Args.Contains(command) && !call.Args.Contains("--get-url"));
        var sshCommand = transfer.Options.Environment["GIT_SSH_COMMAND"]!;
        Assert.StartsWith(QuotePath(ssh) + " -F ", sshCommand);
        Assert.Contains("core.sshCommand=" + sshCommand, transfer.Args);
        Assert.Contains(" -i " + QuotePath(Path.Combine(_root, "selected-key")), sshCommand);
        Assert.Contains("IdentitiesOnly=yes", sshCommand);
        Assert.Contains("StrictHostKeyChecking=yes", sshCommand);
    }

    [Theory]
    [InlineData("missing-bundled")]
    [InlineData("missing-git")]
    [InlineData("unrecognized-layout")]
    [InlineData("relative-git")]
    public async Task Windows_InvalidOrMissingBundledInstallationUsesExistingSshLocator(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var git = scenario switch
        {
            "missing-git" => Path.Combine(_root, "selected-git", "cmd", "git.exe"),
            "unrecognized-layout" => CreateFile(Path.Combine("selected-git", "tools", "git.exe")),
            "relative-git" => "git.exe",
            _ => CreateFile(Path.Combine("selected-git", "cmd", "git.exe")),
        };
        if (scenario != "missing-bundled") CreateFile(Path.Combine("selected-git", "usr", "bin", "ssh.exe"));
        // 即使工作目录内存在同名 SSH，也不能把它当作安装配套程序
        CreateFile(Path.Combine("usr", "bin", "ssh.exe"));
        var expected = await new SshLocator().LocateAsync() ?? "ssh";
        var executor = new RecordingExecutor(_root);

        var result = await ExecuteAsync(git, executor, "test");

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, Assert.Single(executor.Calls, call => call.Args[0] == "-G").Executable);
    }

    [Fact]
    public async Task NonWindows_KeepsExistingSshSelectionEvenWithWindowsLayout()
    {
        if (OperatingSystem.IsWindows()) return;
        var git = CreateFile(Path.Combine("selected-git", "cmd", "git.exe"));
        CreateFile(Path.Combine("selected-git", "usr", "bin", "ssh.exe"));
        var expected = await new SshLocator().LocateAsync() ?? (OperatingSystem.IsMacOS() ? "/usr/bin/ssh" : "ssh");
        var executor = new RecordingExecutor(_root);

        var result = await ExecuteAsync(git, executor, "test");

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, Assert.Single(executor.Calls, call => call.Args[0] == "-G").Executable);
    }

    private Task<GitBinder.Domain.Common.Result> ExecuteAsync(string git, RecordingExecutor executor, string operation)
    {
        var account = new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = CreateFile("selected-key") };
        var transfer = new GitTransfer(executor, new Locator(git));
        return operation switch
        {
            "clone" => transfer.CloneAsync("git@example.org:repo.git", Path.Combine(_root, "clone"), account),
            "pull" => transfer.PullAsync(_root, account),
            _ => transfer.TestAsync(_root, account),
        };
    }

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture only; never executed or read by SSH");
        return path;
    }

    private static string QuotePath(string path)
    {
        var shellPath = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        return "'" + shellPath.Replace("'", "'\\''") + "'";
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Locator(string executable) : IGitLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => Task.FromResult<string?>(executable);
    }

    private sealed class RecordingExecutor(string root) : ICommandExecutor
    {
        public List<(string Executable, string[] Args, CommandExecutionOptions Options)> Calls { get; } = [];

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Transfer must use explicit command options");

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add((executable, arguments.ToArray(), options));
            if (arguments[0] == "-G")
                return Task.FromResult(new CommandResult { ExitCode = 0, StdOut = "hostname example.org\nuser git\nport 22\n" });
            var index = 0;
            while (arguments[index] == "-c") index += 2;
            var args = arguments.Skip(index).ToArray();
            var output = args[0] switch
            {
                "remote" => "git@example.org:repo.git",
                "ls-remote" when args.Contains("--get-url") => args[^1],
                "rev-parse" when args.Contains("--show-toplevel") => root,
                "rev-parse" => Path.Combine(root, ".git", args[^1]),
                "symbolic-ref" => "main",
                "config" => args[^1].EndsWith(".remote") ? "origin" : "refs/heads/main",
                _ => string.Empty,
            };
            return Task.FromResult(new CommandResult { ExitCode = 0, StdOut = output });
        }
    }
}
