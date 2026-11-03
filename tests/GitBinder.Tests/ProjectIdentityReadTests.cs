using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

public sealed class ProjectIdentityReadTests
{
    [Fact]
    public async Task IdentityRead_IsReadOnlyRetainsWhitespaceAndAllowsGlobalInheritance()
    {
        var executor = new Executor();
        var identity = await new GitService(executor, new Locator()).GetCurrentIdentityAsync("X:/fake-project");
        Assert.NotNull(identity);
        Assert.Equal(" name with spaces ", identity.UserName);
        Assert.Equal("user@example.org", identity.UserEmail);
        Assert.Equal(2, executor.Calls.Count);
        Assert.Equal(new[] { "user.name", "user.email" }, executor.Calls.Select(call => call.Args[^1]));
        foreach (var call in executor.Calls)
        {
            Assert.Equal("X:/fake-project", call.Path);
            Assert.Equal(new[] { "config", "--null", "--get", call.Args[^1] }, call.Args.TakeLast(4));
            Assert.DoesNotContain("--local", call.Args);
            Assert.Null(call.Options.Environment["GIT_DIR"]);
            Assert.Null(call.Options.Environment["GIT_WORK_TREE"]);
            Assert.False(call.Options.Environment.ContainsKey("GIT_CONFIG_GLOBAL"));
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(128, false)]
    public async Task IdentityRead_DistinguishesUnsetValueFromFailure(int emailExitCode, bool succeeds)
    {
        var executor = new Executor { EmailExitCode = emailExitCode };
        var identity = await new GitService(executor, new Locator()).GetCurrentIdentityAsync("X:/fake-project");
        Assert.Equal(succeeds, identity is not null);
        if (identity is not null) Assert.Null(identity.UserEmail);
    }

    private sealed class Locator : IGitLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => Task.FromResult<string?>("git");
    }

    private sealed class Executor : ICommandExecutor
    {
        public int EmailExitCode { get; init; }
        public List<(string[] Args, string? Path, CommandExecutionOptions Options)> Calls { get; } = [];
        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Expected repository environment isolation");
        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add((arguments.ToArray(), workingDirectory, options));
            var isEmail = arguments[^1] == "user.email";
            return Task.FromResult(new CommandResult
            {
                ExitCode = isEmail ? EmailExitCode : 0,
                StdOut = isEmail ? "user@example.org\0" : " name with spaces \0",
            });
        }
    }
}
