using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Domain.Accounts;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

public sealed class GitTransferTerminationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-termination-" + Guid.NewGuid().ToString("N"));

    public GitTransferTerminationTests() => Directory.CreateDirectory(_root);

    public static IEnumerable<object[]> UnconfirmedTerminationCases()
    {
        (string Operation, string Step)[] cases =
        [
            ("test", "origin"),
            ("test", "ssh-config"),
            ("test", "test"),
            ("clone", "resolve"),
            ("clone", "ssh-config"),
            ("clone", "clone"),
            ("pull", "root"),
            ("pull", "state"),
            ("pull", "branch"),
            ("pull", "remote-config"),
            ("pull", "merge-config"),
            ("pull", "origin"),
            ("pull", "ssh-config"),
            ("pull", "head"),
            ("pull", "discovery"),
            ("pull", "ref-check"),
            ("pull", "pull"),
            ("pull", "tracking-remote"),
            ("pull", "tracking-merge"),
        ];
        foreach (var (operation, step) in cases)
        {
            yield return [operation, step, false];
            yield return [operation, step, true];
        }
    }

    [Theory]
    [MemberData(nameof(UnconfirmedTerminationCases))]
    public async Task UnconfirmedTermination_IsPreservedAtEveryCommandBoundary(string operation, string step, bool cancel)
    {
        var fixturePath = Path.Combine(_root, "fixture-file");
        File.WriteAllText(fixturePath, "fixture only; no process is launched");
        using var cancellation = new CancellationTokenSource();
        var useSsh = step == "ssh-config";
        var url = useSsh ? "git@example.org:team/repo.git" : "https://example.org/team/repo.git";
        var account = new Account
        {
            AuthenticationType = useSsh ? AuthenticationType.Ssh : AuthenticationType.Https,
            SshPrivateKeyPath = fixturePath,
            HttpsSecretId = "fixture-secret-id",
        };
        var executor = new UnconfirmedExecutor(_root, url, step, cancel ? cancellation.Cancel : null);
        var sut = new GitTransfer(executor, new Locator(), fixturePath);

        var result = operation switch
        {
            "test" => await sut.TestAsync(_root, account, cancellation.Token),
            "clone" => await sut.CloneAsync(url, Path.Combine(_root, "destination"), account, cancellation.Token),
            "pull" => await sut.PullAsync(_root, account, cancellation.Token),
            _ => throw new InvalidOperationException("Unknown fixture operation"),
        };

        Assert.False(result.IsSuccess);
        Assert.Equal("TRANSFER_TERMINATION_UNCONFIRMED", result.Error!.Code);
        Assert.Equal("1", result.Error.Arguments[1]);
        Assert.Null(result.Error.TechnicalDetails);
        Assert.Equal(cancel, cancellation.IsCancellationRequested);
        Assert.Equal(1, executor.FailureCount);
        Assert.Equal(step, executor.Steps[^1]);
        if (step.StartsWith("tracking-", StringComparison.Ordinal))
        {
            Assert.Contains("pull", executor.Steps);
            Assert.True(executor.Steps.IndexOf("pull") < executor.Steps.IndexOf(step));
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Locator : IGitLocator
    {
        public Task<string?> LocateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-git");
    }

    private sealed class UnconfirmedExecutor(string root, string url, string failureStep, Action? cancel) : ICommandExecutor
    {
        public List<string> Steps { get; } = [];
        public int FailureCount { get; private set; }

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Transfer must use isolated options");

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            var index = 0;
            while (arguments[index] == "-c") index += 2;
            var args = arguments.Skip(index).ToArray();
            var step = args[0] switch
            {
                "-G" => "ssh-config",
                "remote" => "origin",
                "ls-remote" when args.Contains("--get-url") => "resolve",
                "ls-remote" when args.Contains("--symref") => "discovery",
                "ls-remote" => "test",
                "clone" => "clone",
                "pull" => "pull",
                "rev-parse" when args.Contains("--show-toplevel") => "root",
                "rev-parse" when args.Contains("--git-path") => "state",
                "rev-parse" => "head",
                "symbolic-ref" => "branch",
                "check-ref-format" => "ref-check",
                "config" when args.Contains("--replace-all") => args[^2].EndsWith(".remote", StringComparison.Ordinal)
                    ? "tracking-remote" : "tracking-merge",
                "config" => args[^1].EndsWith(".remote", StringComparison.Ordinal) ? "remote-config" : "merge-config",
                _ => throw new InvalidOperationException("Unexpected fixture command"),
            };
            Steps.Add(step);
            if (step == failureStep)
            {
                FailureCount++;
                cancel?.Invoke();
                return Task.FromResult(new CommandResult
                {
                    ExitCode = 1,
                    TerminationUnconfirmed = true,
                    StdErr = "Permission denied (publickey). fixture secret",
                });
            }

            var missing = step is "remote-config" or "merge-config" or "head";
            var output = step switch
            {
                "ssh-config" => "hostname example.org\nuser git\nport 22\n",
                "origin" or "resolve" => url,
                "root" => root,
                "state" => Path.Combine(root, ".git", args[^1]),
                "branch" => "main",
                "discovery" => "ref: refs/heads/main\tHEAD\n",
                _ => "",
            };
            return Task.FromResult(new CommandResult { ExitCode = missing ? 1 : 0, StdOut = output });
        }
    }
}
