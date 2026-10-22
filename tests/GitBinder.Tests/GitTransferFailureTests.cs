using GitBinder.Application.Common;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

public sealed class GitTransferFailureTests
{
    [Theory]
    [InlineData("Can't open user config file NUL: No such file or directory", "TRANSFER_SSH_CONFIG_FAILED")]
    [InlineData("Host key verification failed.", "TRANSFER_HOST_KEY_FAILED")]
    [InlineData("Permission denied (publickey).", "TRANSFER_AUTH_FAILED")]
    [InlineData("Could not resolve hostname host", "TRANSFER_DNS_FAILED")]
    [InlineData("Connection refused", "TRANSFER_CONNECT_FAILED")]
    [InlineData("Command timed out.", "TRANSFER_TIMEOUT")]
    [InlineData("SSL certificate problem: certificate expired", "TRANSFER_CERTIFICATE_FAILED")]
    [InlineData("detected dubious ownership", "TRANSFER_OWNERSHIP_FAILED")]
    [InlineData("Not possible to fast-forward, aborting.", "TRANSFER_DIVERGED")]
    [InlineData("fatal: couldn't find remote ref refs/heads/main", "PULL_REMOTE_BRANCH_MISSING")]
    [InlineData("error: Your local changes to the following files would be overwritten by merge:\n\ttracked.txt\nPlease commit your changes or stash them before you merge.\nAborting", "PULL_DIRTY")]
    [InlineData("error: The following untracked working tree files would be overwritten by merge:\n\tlocal.txt\nPlease move or remove them before you merge.\nAborting", "PULL_DIRTY")]
    [InlineData("cannot lock ref", "TRANSFER_LOCK_FAILED")]
    public void CommandFailure_ReportsSpecificReasonAndStage(string error, string code)
    {
        var result = GitTransferFailure.FromCommand("git pull --ff-only", new CommandResult { ExitCode = 128, StdErr = error });
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(new[] { "git pull --ff-only", "128" }, result.Error.Arguments);
    }

    [Theory]
    [InlineData("WARNING: UNPROTECTED PRIVATE KEY FILE!", "TRANSFER_SSH_KEY_PERMISSIONS_FAILED")]
    [InlineData("Load key \"private-path\": bad permissions", "TRANSFER_SSH_KEY_PERMISSIONS_FAILED")]
    [InlineData("Permissions for private key are too open.", "TRANSFER_SSH_KEY_PERMISSIONS_FAILED")]
    [InlineData("Load key \"private-path\": invalid format", "TRANSFER_SSH_KEY_INVALID")]
    [InlineData("Load key \"private-path\": error in libcrypto", "TRANSFER_SSH_KEY_INVALID")]
    [InlineData("Load key \"private-path\": incorrect passphrase supplied to decrypt private key", "TRANSFER_SSH_PASSPHRASE_FAILED")]
    [InlineData("sign_and_send_pubkey: signing failed for ED25519 from agent: agent refused operation", "TRANSFER_SSH_AGENT_FAILED")]
    public void LocalSshFailure_TakesPriorityOverTrailingPublicKeyRejection(string diagnostic, string code)
    {
        var result = GitTransferFailure.FromCommand("git pull --ff-only", new CommandResult
        {
            ExitCode = 1,
            StdErr = diagnostic + "\nPermission denied (publickey).",
        });
        Assert.Equal(code, result.Error!.Code);
        Assert.Null(result.Error.TechnicalDetails);
        Assert.Equal(new[] { "git pull --ff-only", "1" }, result.Error.Arguments);
    }

    [Theory]
    [InlineData("remote: bad permissions for this repository")]
    [InlineData("remote: repository access is too open")]
    [InlineData("remote: invalid format for repository name")]
    [InlineData("remote: private key has invalid format")]
    [InlineData("remote: agent refused operation")]
    [InlineData("remote: private key authentication required\nremote: bad permissions for this repository")]
    public void RemoteDiagnostics_WithoutLocalKeyContext_AreNotReportedAsLocalKeyFailure(string diagnostic)
    {
        var result = GitTransferFailure.FromCommand("git pull --ff-only", new CommandResult
        {
            ExitCode = 1,
            StdErr = diagnostic + "\nPermission denied (publickey).",
        });
        Assert.Equal("TRANSFER_AUTH_FAILED", result.Error!.Code);
    }

    [Fact]
    public void UnknownFailure_DoesNotExposeRawCredentialsOrServerOutput()
    {
        var result = GitTransferFailure.FromCommand("git clone", new CommandResult
        {
            ExitCode = 128,
            StdErr = "unknown https://user:dummy-password@example.org/repo?token=dummy-token Authorization: Bearer dummy-token",
        });
        Assert.Equal("TRANSFER_COMMAND_FAILED", result.Error!.Code);
        Assert.Null(result.Error.TechnicalDetails);
        Assert.Equal(new[] { "git clone", "128" }, result.Error.Arguments);
    }

    [Fact]
    public void Cancellation_IsNotReportedAsAuthenticationFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = GitTransferFailure.FromCommand("git pull", new CommandResult
            { ExitCode = -1, StdErr = "Permission denied (publickey)." }, cancellation.Token);
        Assert.Equal("TRANSFER_CANCELLED", result.Error!.Code);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    public void UnconfirmedTermination_TakesPriorityOverCancellationAndCommandDiagnostics(bool cancel, int exitCode)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var command = new CommandResult
        {
            ExitCode = exitCode,
            TerminationUnconfirmed = true,
            StdErr = "Permission denied (publickey). dummy-secret",
        };

        var result = GitTransferFailure.FromCommand("git pull", command, cancellation.Token);

        Assert.False(command.IsSuccess);
        Assert.Equal("TRANSFER_TERMINATION_UNCONFIRMED", result.Error!.Code);
        Assert.Equal(new[] { "git pull", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) }, result.Error.Arguments);
        Assert.Null(result.Error.TechnicalDetails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconfirmedTerminationException_TakesPriorityOverCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var command = new CommandResult { ExitCode = 1, TerminationUnconfirmed = true };
        var exception = Assert.Throws<CommandTerminationUnconfirmedException>(command.ThrowIfTerminationUnconfirmed);

        var result = GitTransferFailure.FromException("git config", exception, cancellation.Token);

        Assert.Equal("TRANSFER_TERMINATION_UNCONFIRMED", result.Error!.Code);
        Assert.Equal(new[] { "git config", "1" }, result.Error.Arguments);
        Assert.Null(result.Error.TechnicalDetails);
    }
}
