using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

/// <summary>协议切换的 origin 读写必须与传输使用同一隔离环境；仅记录参数，不执行 Git。</summary>
public sealed class GitServiceProtocolTests
{
    private const string RepositoryPath = "X:/gitbinder-protocol-reader/not-created";
    private const string HttpsUrl = "https://example.org/team/demo.git";
    private const string SshUrl = "git@example.org:team/demo.git";

    [Fact]
    public async Task GetTransferOrigin_UsesReadOnlyCommandAndIsolatedEnvironment()
    {
        var executor = new RecordingExecutor();
        var service = new GitService(executor, new Locator());

        var origin = await service.GetTransferOriginUrlAsync(RepositoryPath);

        Assert.Equal(HttpsUrl, origin);
        Assert.Equal(0, executor.OrdinaryCount);
        var call = Assert.Single(executor.Calls);
        Assert.Equal(new[] { "remote", "get-url", "origin" }, call.Args.TakeLast(3));
        AssertIsolation(call.Args, call.Options, call.Path);
    }

    [Fact]
    public async Task GetTransferOrigin_DoesNotUseOrdinaryEnvironmentInsteadOfRewrite()
    {
        var executor = new RecordingExecutor { AllowOrdinaryRead = true };
        var service = new GitService(executor, new Locator());

        var ideLikeOrigin = await service.GetOriginUrlAsync(RepositoryPath);
        var transferOrigin = await service.GetTransferOriginUrlAsync(RepositoryPath);

        Assert.Equal(SshUrl, ideLikeOrigin);
        Assert.Equal(HttpsUrl, transferOrigin);
        Assert.Equal(1, executor.OrdinaryCount);
        Assert.Single(executor.Calls);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, null)]
    [InlineData(128, null)]
    public async Task GetTransferOrigin_DistinguishesMissingOriginFromReadFailure(int configExitCode, string? expected)
    {
        var executor = new RecordingExecutor { ReadFails = true, ConfigExitCode = configExitCode };
        var origin = await new GitService(executor, new Locator()).GetTransferOriginUrlAsync(RepositoryPath);

        Assert.Equal(expected, origin);
        Assert.Equal(2, executor.Calls.Count);
        Assert.Equal(new[] { "config", "--get", "remote.origin.url" }, executor.Calls[1].Args.TakeLast(3));
        Assert.All(executor.Calls, call => AssertIsolation(call.Args, call.Options, call.Path));
        Assert.Equal(0, executor.OrdinaryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetTransferOrigin_OnlyUpdatesExistingOriginAndNeverAddsOrChangesPushUrl(bool fail)
    {
        var executor = new RecordingExecutor { SetFails = fail };
        var result = await new GitService(executor, new Locator()).SetTransferOriginUrlAsync(RepositoryPath, SshUrl);

        Assert.Equal(!fail, result.IsSuccess);
        var call = Assert.Single(executor.Calls);
        Assert.Equal(new[] { "remote", "set-url", "origin", SshUrl }, call.Args.TakeLast(4));
        Assert.DoesNotContain("add", call.Args);
        Assert.DoesNotContain("--push", call.Args);
        Assert.DoesNotContain(call.Args, arg => arg.Contains("pushurl", StringComparison.OrdinalIgnoreCase));
        AssertIsolation(call.Args, call.Options, call.Path);
        Assert.Equal(0, executor.OrdinaryCount);
        if (fail)
        {
            Assert.Equal("PROJECT_REMOTE_UPDATE_FAILED", result.Error!.Code);
            Assert.DoesNotContain("dummy-secret", result.Error.TechnicalDetails ?? string.Empty);
            Assert.DoesNotContain("dummy-secret", string.Join(" ", result.Error.Arguments));
        }
    }

    [Fact]
    public async Task TransferOrigin_PreCancelledDoesNotInvokeGit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executor = new RecordingExecutor();
        var service = new GitService(executor, new Locator());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetTransferOriginUrlAsync(RepositoryPath, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SetTransferOriginUrlAsync(RepositoryPath, SshUrl, cancellation.Token));
        Assert.Empty(executor.Calls);
        Assert.Equal(0, executor.OrdinaryCount);
    }

    private static void AssertIsolation(string[] args, CommandExecutionOptions options, string? path)
    {
        Assert.Equal(RepositoryPath, path);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Timeout);
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        Assert.Contains($"core.hooksPath={nullDevice}", args);
        Assert.Contains("protocol.allow=never", args);
        Assert.Contains("core.askPass=", args);
        Assert.Equal(nullDevice, options.Environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("1", options.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", options.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("", options.Environment["GIT_CONFIG_PARAMETERS"]);
        Assert.Equal("0", options.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Null(options.Environment["GIT_DIR"]);
        Assert.Null(options.Environment["GIT_WORK_TREE"]);
        Assert.Null(options.Environment["GIT_COMMON_DIR"]);
        Assert.Null(options.Environment["GIT_INDEX_FILE"]);
    }

    private sealed class Locator : IGitLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => Task.FromResult<string?>("fake-git");
    }

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public List<(string[] Args, CommandExecutionOptions Options, string? Path)> Calls { get; } = [];
        public bool AllowOrdinaryRead { get; init; }
        public int OrdinaryCount { get; private set; }
        public bool ReadFails { get; init; }
        public int ConfigExitCode { get; init; } = 1;
        public bool SetFails { get; init; }

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            OrdinaryCount++;
            if (!AllowOrdinaryRead) throw new InvalidOperationException("Transfer origin operations must use isolated options.");
            // 模拟普通环境的全局 url.*.insteadOf 将 HTTPS 重写为 SSH。
            return Task.FromResult(new CommandResult { ExitCode = 0, StdOut = SshUrl });
        }

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add((arguments.ToArray(), options, workingDirectory));
            var index = 0;
            while (arguments[index] == "-c") index += 2;
            var args = arguments.Skip(index).ToArray();
            if (args[0] == "config")
                return Task.FromResult(new CommandResult { ExitCode = ConfigExitCode, StdOut = HttpsUrl });
            if (args[1] == "set-url")
                return Task.FromResult(new CommandResult { ExitCode = SetFails ? 128 : 0, StdErr = "dummy-secret" });
            return Task.FromResult(new CommandResult { ExitCode = ReadFails ? 128 : 0, StdOut = HttpsUrl + "\n" });
        }
    }
}
