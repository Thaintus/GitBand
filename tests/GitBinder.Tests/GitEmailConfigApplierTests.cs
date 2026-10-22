using GitBinder.Application.Common;
using GitBinder.Application.GlobalMode;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.GlobalMode;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

/// <summary>仅通过内存命令执行器检查参数与恢复语义，不执行 Git 或启动进程。</summary>
public sealed class GitEmailConfigApplierTests
{
    private const string RepositoryPath = @"C:\repositories\email-config";
    private static readonly string[] EmailKeys = ["user.email", "author.email", "committer.email"];

    [Theory]
    [InlineData(false, null, "")]
    [InlineData(false, "", "")]
    [InlineData(false, " \t ", "")]
    [InlineData(false, " selected@example.com ", "selected@example.com")]
    [InlineData(true, null, "")]
    [InlineData(true, "", "")]
    [InlineData(true, " \t ", "")]
    [InlineData(true, " selected@example.com ", "selected@example.com")]
    public async Task Apply_ReplacesAllEmailKeysIncludingEmptyValues(bool global, string? input, string expected)
    {
        var executor = new RecordingExecutor();
        var scope = Scope(global);
        foreach (var key in EmailKeys)
        {
            executor.Values[(scope, key)] = "previous@example.com";
            if (!global)
            {
                executor.Values[("--global", key)] = "inherited@example.com";
            }
        }

        await ApplyAsync(new GitConfigApplier(executor), global, input);

        var calls = EmailCalls(executor).ToArray();
        Assert.Equal(3, calls.Length);
        foreach (var key in EmailKeys)
        {
            var call = Assert.Single(calls, call => call.Arguments[3] == key);
            Assert.Equal(new[] { "config", scope, "--replace-all", key, expected }, call.Arguments);
            Assert.Equal(global ? null : RepositoryPath, call.WorkingDirectory);
            AssertProcessScope(call, global);
            Assert.Equal(expected, executor.Values[(scope, key)]);
            if (!global)
            {
                Assert.Equal("inherited@example.com", executor.Values[("--global", key)]);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_SwitchesFromEmptyEmailToSelectedAccount(bool global)
    {
        var executor = new RecordingExecutor();
        var applier = new GitConfigApplier(executor);
        await ApplyAsync(applier, global, "");
        executor.Calls.Clear();

        await ApplyAsync(applier, global, " next@example.com ");

        Assert.Equal(3, EmailCalls(executor).Count());
        foreach (var key in EmailKeys)
        {
            Assert.Equal("next@example.com", executor.Values[(Scope(global), key)]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_PreservesMissingEmptyAndWhitespaceSeparately(bool global)
    {
        var executor = new RecordingExecutor();
        var scope = Scope(global);
        executor.Values[(scope, "user.email")] = "";
        executor.Values[(scope, "author.email")] = " \t ";
        var applier = new GitConfigApplier(executor);

        var snapshot = await CaptureAsync(applier, global);

        Assert.Equal(new GitEmailConfigSnapshot("", " \t ", null), snapshot);
        var calls = EmailCalls(executor).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.All(calls, call =>
        {
            Assert.Equal(scope, call.Arguments[1]);
            Assert.Equal("--get", call.Arguments[2]);
            Assert.Equal(global ? null : RepositoryPath, call.WorkingDirectory);
            AssertProcessScope(call, global);
        });
    }

    [Theory]
    [InlineData(false, "user.email", 128)]
    [InlineData(false, "author.email", 2)]
    [InlineData(false, "committer.email", -1)]
    [InlineData(true, "user.email", 128)]
    [InlineData(true, "author.email", 2)]
    [InlineData(true, "committer.email", -1)]
    public async Task Capture_ReadFailureNeverBecomesMissingSnapshot(bool global, string key, int exitCode)
    {
        var executor = new RecordingExecutor
        {
            Failure = call => call.Arguments[2] == "--get" && call.Arguments[3] == key
                ? new CommandResult { ExitCode = exitCode, StdErr = "private diagnostic" }
                : null,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CaptureAsync(new GitConfigApplier(executor), global));

        Assert.Contains(key, error.Message);
        Assert.Contains(exitCode.ToString(), error.Message);
        Assert.DoesNotContain("private diagnostic", error.Message);
        Assert.DoesNotContain(executor.Calls, call => call.Arguments[2] is "--replace-all" or "--unset-all");
    }

    [Theory]
    [InlineData(false, null, "", " \t ")]
    [InlineData(false, "", " old-author@example.com ", null)]
    [InlineData(false, " \t ", null, "")]
    [InlineData(true, null, "", " \t ")]
    [InlineData(true, "", " old-author@example.com ", null)]
    [InlineData(true, " \t ", null, "")]
    public async Task Restore_PreservesExactEmailSnapshot(bool global, string? user, string? author, string? committer)
    {
        var executor = new RecordingExecutor();
        var applier = new GitConfigApplier(executor);
        await ApplyAsync(applier, global, "selected@example.com");
        executor.Calls.Clear();
        var snapshot = new GitEmailConfigSnapshot(user, author, committer);

        await RestoreAsync(applier, global, snapshot);

        var expected = new[] { user, author, committer };
        for (var index = 0; index < EmailKeys.Length; index++)
        {
            var key = EmailKeys[index];
            var value = expected[index];
            var call = Assert.Single(EmailCalls(executor), call => call.Arguments[3] == key);
            var args = value is null
                ? new[] { "config", Scope(global), "--unset-all", key }
                : new[] { "config", Scope(global), "--replace-all", key, value };
            Assert.Equal(args, call.Arguments);
            AssertProcessScope(call, global);
            Assert.Equal(value is not null, executor.Values.ContainsKey((Scope(global), key)));
            if (value is not null)
            {
                Assert.Equal(value, executor.Values[(Scope(global), key)]);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_MissingEmailKeysAcceptsUnsetExitCodeFive(bool global)
    {
        var executor = new RecordingExecutor();

        await RestoreAsync(new GitConfigApplier(executor), global, new GitEmailConfigSnapshot(null, null, null));

        Assert.Equal(3, EmailCalls(executor).Count());
        Assert.All(EmailCalls(executor), call => Assert.Equal("--unset-all", call.Arguments[2]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("original@example.com")]
    public async Task Restore_LegacyGlobalSnapshotPreservesUserEmailAndLeavesOverrides(string? email)
    {
        var executor = new RecordingExecutor();
        executor.Values[("--global", "author.email")] = "author@example.com";
        executor.Values[("--global", "committer.email")] = "committer@example.com";
        var applier = new GitConfigApplier(executor);

        var result = await applier.RestoreAsync(new GlobalGitConfigSnapshot("Original", email, null));

        Assert.True(result.IsSuccess);
        var call = Assert.Single(EmailCalls(executor));
        Assert.Equal("user.email", call.Arguments[3]);
        Assert.Equal(email is null ? "--unset-all" : "--replace-all", call.Arguments[2]);
        if (email is not null)
        {
            Assert.Equal(email, executor.Values[("--global", "user.email")]);
        }
        Assert.Equal("author@example.com", executor.Values[("--global", "author.email")]);
        Assert.Equal("committer@example.com", executor.Values[("--global", "committer.email")]);
    }

    [Fact]
    public async Task Restore_LegacyLocalSnapshotKeepsExistingRestoreContract()
    {
        var executor = new RecordingExecutor();
        executor.Values[("--local", "author.email")] = "author@example.com";
        executor.Values[("--local", "committer.email")] = "committer@example.com";

        await new GitConfigApplier(executor).RestoreAsync(RepositoryPath, "Original", "", null, null);

        var call = Assert.Single(EmailCalls(executor));
        Assert.Equal(new[] { "config", "--local", "--unset-all", "user.email" }, call.Arguments);
        Assert.Equal("author@example.com", executor.Values[("--local", "author.email")]);
        Assert.Equal("committer@example.com", executor.Values[("--local", "committer.email")]);
    }

    [Theory]
    [InlineData(false, "user.email", 128)]
    [InlineData(false, "author.email", 128)]
    [InlineData(false, "committer.email", 128)]
    [InlineData(false, "user.email", 5)]
    [InlineData(true, "user.email", 128)]
    [InlineData(true, "author.email", 128)]
    [InlineData(true, "committer.email", 128)]
    [InlineData(true, "user.email", 5)]
    public async Task Apply_EmailWriteFailureIsReported(bool global, string key, int exitCode)
    {
        var executor = FailingWriteExecutor(key, exitCode);
        var applier = new GitConfigApplier(executor);

        if (global)
        {
            var result = await applier.ApplyAsync(new Account { GitName = "Selected", GitEmail = "" });
            Assert.False(result.IsSuccess);
            Assert.Equal("GLOBALMODE_GLOBAL_CONFIG_FAILED", result.Error?.Code);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => applier.ApplyIdentityAsync(RepositoryPath, "Selected", ""));
            Assert.Contains(key, error.Message);
        }

        Assert.Equal(key, executor.Calls[^1].Arguments[3]);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(true, null)]
    [InlineData(true, "")]
    public async Task Restore_EmailWriteFailureIsReported(bool global, string? authorEmail)
    {
        var executor = FailingWriteExecutor("author.email");
        var applier = new GitConfigApplier(executor);
        var snapshot = new GitEmailConfigSnapshot(null, authorEmail, "");

        if (global)
        {
            var result = await applier.RestoreAsync(new GlobalGitConfigSnapshot(null, null, null, EmailConfig: snapshot));
            Assert.False(result.IsSuccess);
            Assert.Equal("GLOBALMODE_GLOBAL_CONFIG_FAILED", result.Error?.Code);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => applier.RestoreAsync(
                RepositoryPath, null, null, null, null, emailConfig: snapshot));
        }

        Assert.Equal("author.email", executor.Calls[^1].Arguments[3]);
    }

    [Fact]
    public async Task LocalIdentityAndAuthentication_UseIsolatedRepositoryForCaptureApplyAndRestore()
    {
        var executor = new RecordingExecutor();
        executor.Values[("--local", "user.name")] = "Original";
        executor.Values[("--local", "user.email")] = "original@example.com";
        executor.Values[("--local", "core.sshCommand")] = "original-ssh";
        executor.Values[("--local", "credential.helper")] = "original-helper";
        var applier = new GitConfigApplier(executor);

        var identity = await applier.ReadIdentityAsync(RepositoryPath);
        var emailConfig = await applier.ReadEmailConfigAsync(RepositoryPath);
        var sshCommand = await applier.ReadSshCommandAsync(RepositoryPath);
        var credentialHelper = await applier.ReadCredentialHelperAsync(RepositoryPath);
        await applier.ApplyIdentityAsync(RepositoryPath, "Selected", "selected@example.com");
        await applier.ApplySshCommandAsync(RepositoryPath, "selected-ssh");
        await applier.ApplyCredentialHelperAsync(RepositoryPath, null);
        await applier.RestoreAsync(RepositoryPath, identity.Name, identity.Email, sshCommand,
            credentialHelper, emailConfig: emailConfig);

        Assert.Equal(("Original", "original@example.com"), identity);
        Assert.Equal("Original", executor.Values[("--local", "user.name")]);
        Assert.Equal("original@example.com", executor.Values[("--local", "user.email")]);
        Assert.Equal("original-ssh", executor.Values[("--local", "core.sshCommand")]);
        Assert.Equal("original-helper", executor.Values[("--local", "credential.helper")]);
        Assert.All(executor.Calls, call => AssertProcessScope(call, global: false));
    }

    private static void AssertProcessScope(Invocation call, bool global)
    {
        if (global)
        {
            Assert.Null(call.Options);
            return;
        }

        Assert.Equal(RepositoryPath, call.WorkingDirectory);
        Assert.NotNull(call.Options);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Options.Timeout);
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG" })
        {
            Assert.True(call.Options.Environment.TryGetValue(variable, out var value), variable);
            Assert.Null(value);
        }
        Assert.Equal("0", call.Options.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal(string.Empty, call.Options.Environment["GIT_CONFIG_PARAMETERS"]);
        Assert.False(call.Options.Environment.ContainsKey("GIT_CONFIG_GLOBAL"));
        Assert.False(call.Options.Environment.ContainsKey("GIT_CONFIG_NOSYSTEM"));
    }

    private static RecordingExecutor FailingWriteExecutor(string key, int exitCode = 128) => new()
    {
        Failure = call => call.Arguments[3] == key
            ? new CommandResult { ExitCode = exitCode }
            : null,
    };

    private static string Scope(bool global) => global ? "--global" : "--local";

    private static IEnumerable<Invocation> EmailCalls(RecordingExecutor executor)
        => executor.Calls.Where(call => EmailKeys.Contains(call.Arguments[3]));

    private static async Task ApplyAsync(GitConfigApplier applier, bool global, string? email)
    {
        if (global)
        {
            Assert.True((await applier.ApplyAsync(new Account { GitName = "Selected", GitEmail = email! })).IsSuccess);
        }
        else
        {
            await applier.ApplyIdentityAsync(RepositoryPath, "Selected", email!);
        }
    }

    private static async Task<GitEmailConfigSnapshot> CaptureAsync(GitConfigApplier applier, bool global)
    {
        if (!global)
        {
            return await applier.ReadEmailConfigAsync(RepositoryPath);
        }

        var snapshot = await applier.CaptureAsync();
        Assert.NotNull(snapshot.EmailConfig);
        Assert.Equal(snapshot.UserEmail, snapshot.EmailConfig.UserEmail);
        return snapshot.EmailConfig;
    }

    private static async Task RestoreAsync(GitConfigApplier applier, bool global, GitEmailConfigSnapshot snapshot)
    {
        if (global)
        {
            Assert.True((await applier.RestoreAsync(
                new GlobalGitConfigSnapshot(null, "ignored@example.com", null, EmailConfig: snapshot))).IsSuccess);
        }
        else
        {
            await applier.RestoreAsync(RepositoryPath, null, "ignored@example.com", null, null, emailConfig: snapshot);
        }
    }

    private sealed record Invocation(string[] Arguments, string? WorkingDirectory, CommandExecutionOptions? Options);

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public Dictionary<(string Scope, string Key), string> Values { get; } = [];
        public List<Invocation> Calls { get; } = [];
        public Func<Invocation, CommandResult?>? Failure { get; init; }

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
            => ExecuteCore(arguments, workingDirectory, null);

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
            => ExecuteCore(arguments, workingDirectory, options);

        private Task<CommandResult> ExecuteCore(IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions? options)
        {
            var call = new Invocation(arguments.ToArray(), workingDirectory, options);
            Calls.Add(call);
            var failure = Failure?.Invoke(call);
            if (failure is not null)
            {
                return Task.FromResult(failure);
            }

            var key = (arguments[1], arguments[3]);
            CommandResult result;
            switch (arguments[2])
            {
                case "--get":
                case "--get-all":
                    result = Values.TryGetValue(key, out var value)
                        ? new CommandResult { ExitCode = 0, StdOut = value + "\r\n" }
                        : new CommandResult { ExitCode = 1 };
                    break;
                case "--replace-all":
                    Values[key] = arguments[4];
                    result = new CommandResult();
                    break;
                case "--unset-all":
                    result = new CommandResult { ExitCode = Values.Remove(key) ? 0 : 5 };
                    break;
                default:
                    // 保留现有全局 user.name 等配置不带 --replace-all 的写法。
                    Values[(arguments[1], arguments[2])] = arguments[3];
                    result = new CommandResult();
                    break;
            }

            return Task.FromResult(result);
        }
    }
}
