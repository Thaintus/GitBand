using GitBinder.Application.Common;
using GitBinder.Domain.Accounts;
using GitBinder.Infrastructure.Git;
using GitBinder.Infrastructure.Ssh;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>只检查配置参数与临时程序路径，不执行 Git/SSH 或改写真实 Git 配置。</summary>
public sealed class GitPlatformCompatibilityTests
{
    [Fact]
    public void SshCommand_QuotesPathWithoutShellExpansion()
    {
        var account = new Account
        {
            AuthenticationType = AuthenticationType.Ssh,
            SshPrivateKeyPath = "/Users/example/keys/user's $(printf unsafe) `key`",
        };
        var command = new GitConfigApplier(new RecordingExecutor()).BuildSshCommand(account);

        var executable = OperatingSystem.IsMacOS() ? "/usr/bin/ssh" : "ssh";
        Assert.Equal(executable + " -i '/Users/example/keys/user'\\''s $(printf unsafe) `key`' -o IdentitiesOnly=yes", command);
    }

    [Theory]
    [InlineData(AuthenticationType.Https, "/Users/example/keys/ignored")]
    [InlineData(AuthenticationType.Ssh, "")]
    [InlineData(AuthenticationType.Both, " ")]
    public void SshCommand_MissingAccountKeyUsesPlatformNullDevice(AuthenticationType type, string key)
    {
        var command = new GitConfigApplier(new RecordingExecutor()).BuildSshCommand(
            new Account { AuthenticationType = type, SshPrivateKeyPath = key });
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        Assert.Contains($"-F {nullDevice} -i {nullDevice}", command);
        Assert.Contains("IdentityAgent=none", command);
        Assert.Contains("BatchMode=yes", command);
        Assert.DoesNotContain("ignored", command);
    }

    [Fact]
    public async Task GlobalSshConfigurationUsesSameBuilderAsLocalBinding()
    {
        var executor = new RecordingExecutor();
        var applier = new GitConfigApplier(executor);
        var account = new Account
        {
            GitName = "Fixture",
            AuthenticationType = AuthenticationType.Ssh,
            SshPrivateKeyPath = "/Users/example/a key's file",
        };

        Assert.True((await applier.ApplyAsync(account)).IsSuccess);
        var call = Assert.Single(executor.Calls, args => args.Contains("core.sshCommand"));
        Assert.Equal(applier.BuildSshCommand(account), call[^1]);
    }

    [Fact]
    public async Task GitLocator_PrefersConfiguredExecutableRegardlessOfFileExtension()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitbinder-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "custom-git");
            File.WriteAllText(executable, "fixture only; never executed");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var settings = new FakeSettingsRepository();
            await settings.SetAsync("git.executable", executable);

            Assert.Equal(executable, await new GitLocator(settings).LocateAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Locators_HonorCancellationBeforePathLookup()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GitLocator(new FakeSettingsRepository()).LocateAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SshLocator().LocateAsync(cancellation.Token));
    }

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public List<string[]> Calls { get; } = [];

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }
    }
}
