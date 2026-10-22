using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Domain.GlobalMode;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

/// <summary>仅使用内存执行器验证本地补偿快照，不运行 Git，不访问真实全局配置</summary>
public sealed class GitLocalConfigRollbackTests
{
    private const string RepositoryPath = @"C:\repositories\local-rollback";
    private const string HelperKey = "credential.helper";
    private static readonly string[] ScalarKeys =
        ["user.name", "user.email", "author.email", "committer.email", "core.sshCommand"];

    public static IEnumerable<object[]> HelperChains()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { "" }];
        yield return [new[] { "", "!managed-helper --account-id fixture" }];
        yield return [new[] { "first", "", "second", "second" }];
        yield return [new[] { "", "!f() {\n  printf 'fixture';\r\n}; f\n", " trailing space " }];
    }

    [Theory]
    [MemberData(nameof(HelperChains))]
    public async Task CaptureAndRestore_PreserveCompleteHelperChainIncludingEmptyReset(string[] helpers)
    {
        var executor = new MemoryExecutor();
        executor.Set("user.name", "");
        executor.Set("user.email", " user@example.com\n");
        executor.Set("author.email", " \t ");
        executor.Set("core.sshCommand", "ssh \r\ncommand\n");
        if (helpers.Length > 0) executor.Values[HelperKey] = helpers.ToList();
        executor.GlobalValues[HelperKey] = ["must-never-be-written"];
        var applier = new GitConfigApplier(executor);

        var snapshot = await applier.CaptureLocalConfigAsync(RepositoryPath);
        foreach (var key in ScalarKeys.Append(HelperKey)) executor.Set(key, "changed");
        await applier.RestoreLocalConfigAsync(RepositoryPath, snapshot);

        Assert.Equal("", snapshot.UserName);
        Assert.Equal(new GitEmailConfigSnapshot(" user@example.com\n", " \t ", null), snapshot.EmailConfig);
        Assert.Equal("ssh \r\ncommand\n", snapshot.SshCommand);
        Assert.Equal(helpers, snapshot.CredentialHelpers);
        Assert.Equal(helpers.Length > 0, executor.Values.ContainsKey(HelperKey));
        if (helpers.Length > 0) Assert.Equal(helpers, executor.Values[HelperKey]);
        Assert.Equal(new[] { "must-never-be-written" }, executor.GlobalValues[HelperKey]);
        Assert.Equal("", Assert.Single(executor.Values["user.name"]));
        Assert.Equal(" user@example.com\n", Assert.Single(executor.Values["user.email"]));
        Assert.Equal(" \t ", Assert.Single(executor.Values["author.email"]));
        Assert.False(executor.Values.ContainsKey("committer.email"));
        Assert.Equal("ssh \r\ncommand\n", Assert.Single(executor.Values["core.sshCommand"]));
        Assert.All(executor.Calls, AssertLocalScope);

        var helperReads = executor.Calls.Where(call => call.Key == HelperKey && call.IsRead).ToArray();
        Assert.Equal(new[] { "config", "--local", "--null", "--get-all", HelperKey },
            Assert.Single(helperReads).Arguments);
        var helperWrites = executor.Calls.Where(call => call.Key == HelperKey && !call.IsRead).ToArray();
        if (helpers.Length == 0)
        {
            Assert.Equal("--unset-all", Assert.Single(helperWrites).Operation);
        }
        else
        {
            Assert.Equal(helpers.Length, helperWrites.Length);
            Assert.Equal("--replace-all", helperWrites[0].Operation);
            Assert.All(helperWrites.Skip(1), call => Assert.Equal("--add", call.Operation));
            Assert.Equal(helpers, helperWrites.Select(call => call.Arguments[^1]));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("first\r\nsecond\n")]
    public async Task CaptureAndRestore_DistinguishMissingAndExactScalarValues(string? value)
    {
        var executor = new MemoryExecutor();
        if (value is not null)
        {
            foreach (var key in ScalarKeys) executor.Set(key, value);
        }
        var applier = new GitConfigApplier(executor);

        var snapshot = await applier.CaptureLocalConfigAsync(RepositoryPath);
        foreach (var key in ScalarKeys) executor.Set(key, "changed");
        await applier.RestoreLocalConfigAsync(RepositoryPath, snapshot);

        Assert.Equal(value, snapshot.UserName);
        Assert.Equal(new GitEmailConfigSnapshot(value, value, value), snapshot.EmailConfig);
        Assert.Equal(value, snapshot.SshCommand);
        foreach (var key in ScalarKeys)
        {
            Assert.Equal(value is not null, executor.Values.ContainsKey(key));
            if (value is not null) Assert.Equal(value, Assert.Single(executor.Values[key]));
            var read = Assert.Single(executor.Calls, call => call.Key == key && call.IsRead);
            Assert.Equal(new[] { "config", "--local", "--null", "--get", key }, read.Arguments);
        }
    }

    public static IEnumerable<object[]> ReadFailures()
    {
        foreach (var key in ScalarKeys.Append(HelperKey))
        {
            foreach (var exitCode in new[] { -1, 1, 2, 128 }) yield return [key, exitCode];
        }
    }

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task Capture_ReadFailureNeverBecomesMissingSnapshot(string key, int exitCode)
    {
        var executor = new MemoryExecutor
        {
            Failure = call => call.Key == key
                ? new CommandResult { ExitCode = exitCode, StdErr = "private diagnostic" }
                : null,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GitConfigApplier(executor).CaptureLocalConfigAsync(RepositoryPath));

        Assert.Contains(key, error.Message);
        Assert.Contains(exitCode.ToString(), error.Message);
        Assert.DoesNotContain("private diagnostic", error.Message);
        Assert.Equal(key, executor.Calls[^1].Key);
        Assert.All(executor.Calls, call => Assert.True(call.IsRead));
    }

    [Theory]
    [InlineData("")]
    [InlineData("helper\n")]
    [InlineData("helper\0\n")]
    public async Task Capture_MalformedNullOutputFailsWithoutGuessing(string output)
    {
        var executor = new MemoryExecutor
        {
            Failure = call => call.Key == HelperKey ? new CommandResult { StdOut = output } : null,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GitConfigApplier(executor).CaptureLocalConfigAsync(RepositoryPath));

        Assert.Contains(HelperKey, error.Message);
        Assert.All(executor.Calls, call => Assert.True(call.IsRead));
    }

    [Theory]
    [InlineData("user.name", "--replace-all")]
    [InlineData("user.email", "--replace-all")]
    [InlineData("author.email", "--unset-all")]
    [InlineData("committer.email", "--replace-all")]
    [InlineData("core.sshCommand", "--replace-all")]
    [InlineData(HelperKey, "--replace-all")]
    [InlineData(HelperKey, "--add")]
    [InlineData(HelperKey, "--unset-all")]
    public async Task Restore_WriteFailureStopsAndIsReported(string key, string operation)
    {
        var executor = new MemoryExecutor
        {
            Failure = call => call.Key == key && call.Operation == operation
                ? new CommandResult { ExitCode = 128, StdErr = "private diagnostic" }
                : null,
        };
        var snapshot = Snapshot(operation == "--unset-all" && key == HelperKey ? [] : ["", "managed"]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GitConfigApplier(executor).RestoreLocalConfigAsync(RepositoryPath, snapshot));

        Assert.Contains(key, error.Message);
        Assert.DoesNotContain("private diagnostic", error.Message);
        Assert.Equal(key, executor.Calls[^1].Key);
        Assert.Equal(operation, executor.Calls[^1].Operation);
        Assert.All(executor.Calls, AssertLocalScope);
    }

    public static IEnumerable<object[]> UnconfirmedTerminations()
    {
        foreach (var key in ScalarKeys.Append(HelperKey))
        {
            foreach (var cancel in new[] { false, true })
            {
                yield return [false, key, "--get", cancel];
                yield return [true, key, key == "author.email" ? "--unset-all" : "--replace-all", cancel];
            }
        }
        yield return [true, HelperKey, "--add", false];
        yield return [true, HelperKey, "--add", true];
        yield return [true, HelperKey, "--unset-all", false];
        yield return [true, HelperKey, "--unset-all", true];
    }

    [Theory]
    [MemberData(nameof(UnconfirmedTerminations))]
    public async Task CaptureAndRestore_PreserveUnconfirmedTerminationBeforeCancellation(
        bool restore, string key, string operation, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new MemoryExecutor
        {
            Failure = call =>
            {
                if (call.Key != key || restore && call.Operation != operation) return null;
                if (cancel) cancellation.Cancel();
                return new CommandResult { ExitCode = 1, TerminationUnconfirmed = true };
            },
        };
        var applier = new GitConfigApplier(executor);
        var snapshot = Snapshot(key == HelperKey && operation == "--unset-all" ? [] : ["", "managed"]);

        await Assert.ThrowsAsync<CommandTerminationUnconfirmedException>(async () =>
        {
            if (restore) await applier.RestoreLocalConfigAsync(RepositoryPath, snapshot, cancellation.Token);
            else await applier.CaptureLocalConfigAsync(RepositoryPath, cancellation.Token);
        });

        Assert.Equal(key, executor.Calls[^1].Key);
        Assert.Equal(cancel, cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Restore_MissingKeysAcceptUnsetExitCodeFive()
    {
        var executor = new MemoryExecutor();
        var snapshot = new GitLocalConfigSnapshot(null, new GitEmailConfigSnapshot(null, null, null), null, []);

        await new GitConfigApplier(executor).RestoreLocalConfigAsync(RepositoryPath, snapshot);

        Assert.Equal(6, executor.Calls.Count);
        Assert.All(executor.Calls, call => Assert.Equal("--unset-all", call.Operation));
        Assert.Empty(executor.Values);
    }

    private static GitLocalConfigSnapshot Snapshot(IReadOnlyList<string> helpers)
        => new("", new GitEmailConfigSnapshot("", null, " \t "), "", helpers);

    private static void AssertLocalScope(Invocation call)
    {
        Assert.Equal("config", call.Arguments[0]);
        Assert.Equal("--local", call.Arguments[1]);
        Assert.DoesNotContain("--global", call.Arguments);
        Assert.Equal(RepositoryPath, call.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Options.Timeout);
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                     "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG" })
        {
            Assert.True(call.Options.Environment.TryGetValue(variable, out var value), variable);
            Assert.Null(value);
        }
        Assert.Equal("0", call.Options.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal(string.Empty, call.Options.Environment["GIT_CONFIG_PARAMETERS"]);
    }

    private sealed record Invocation(string[] Arguments, string? WorkingDirectory, CommandExecutionOptions Options)
    {
        public bool IsRead => Arguments[2] == "--null";
        public string Operation => Arguments[IsRead ? 3 : 2];
        public string Key => Arguments[IsRead ? 4 : 3];
    }

    private sealed class MemoryExecutor : ICommandExecutor
    {
        public Dictionary<string, List<string>> Values { get; } = [];
        public Dictionary<string, List<string>> GlobalValues { get; } = [];
        public List<Invocation> Calls { get; } = [];
        public Func<Invocation, CommandResult?>? Failure { get; init; }

        public void Set(string key, string value) => Values[key] = [value];

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Local config must use isolated process options");

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            var call = new Invocation(arguments.ToArray(), workingDirectory, options);
            Calls.Add(call);
            AssertLocalScope(call);
            var failure = Failure?.Invoke(call);
            if (failure is not null) return Task.FromResult(failure);

            CommandResult result;
            switch (call.Operation)
            {
                case "--get":
                case "--get-all":
                    result = Values.TryGetValue(call.Key, out var values) && values.Count > 0
                        ? new CommandResult
                        {
                            StdOut = call.Operation == "--get"
                                ? values[^1] + '\0'
                                : string.Join("\0", values) + '\0',
                        }
                        : new CommandResult { ExitCode = 1 };
                    break;
                case "--replace-all":
                    Set(call.Key, arguments[^1]);
                    result = new CommandResult();
                    break;
                case "--add":
                    if (!Values.TryGetValue(call.Key, out var existing)) Values[call.Key] = existing = [];
                    existing.Add(arguments[^1]);
                    result = new CommandResult();
                    break;
                case "--unset-all":
                    result = new CommandResult { ExitCode = Values.Remove(call.Key) ? 0 : 5 };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected in-memory config operation");
            }
            return Task.FromResult(result);
        }
    }
}
