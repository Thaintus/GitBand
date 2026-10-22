using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Projects;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

public sealed class GitTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-transfer-" + Guid.NewGuid().ToString("N"));

    public GitTransferTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("https://example.org/team/repo.git", true)]
    [InlineData("git@example.org:team/repo.git", true)]
    [InlineData("ssh://git@example.org:2222/team/repo.git", true)]
    [InlineData("http://example.org/repo.git", false)]
    [InlineData("https://user:dummy@example.org/repo.git", false)]
    [InlineData("ssh://user:dummy@example.org/repo.git", false)]
    [InlineData("https://example.org/repo.git?token=dummy", false)]
    [InlineData("ext::arbitrary", false)]
    [InlineData("../local", false)]
    public void RemoteValidation_RestrictsProtocolsAndSecrets(string url, bool valid)
        => Assert.Equal(valid, GitTransferRemote.TryParse(url, out _));

    [Fact]
    public async Task Clone_HttpsPinsHelperAndDisablesPromptFallback()
    {
        var helper = Path.Combine(_root, "helper with space.exe");
        File.WriteAllText(helper, "fake executable; never launched");
        var executor = new RecordingExecutor(_root);
        var account = new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "dummy-secret-id" };
        var sut = new GitTransfer(executor, new Locator(), helper);
        var result = await sut.CloneAsync("https://example.org/team/repo.git", Path.Combine(_root, "new"), account);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(executor.Calls, c => c.Args.Contains("clone"));
        Assert.Contains("credential.helper=", call.Args);
        Assert.Contains(call.Args, arg => arg.Contains("--account-id " + account.Id.ToString("D")));
        Assert.DoesNotContain(call.Args, arg => arg.Contains(account.HttpsSecretId));
        Assert.Contains("--no-recurse-submodules", call.Args);
        Assert.Equal("0", call.Options.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.False(call.Options.Environment.ContainsKey("GIT_CONFIG_GLOBAL"));
        Assert.False(call.Options.Environment.ContainsKey("GIT_CONFIG_NOSYSTEM"));
        Assert.Null(call.Options.Environment["GIT_DIR"]);
        Assert.Equal(TimeSpan.FromMinutes(30), call.Options.Timeout);
    }

    [Fact]
    public async Task Clone_ExistingDirectoryDoesNotInvokeGit()
    {
        var executor = new RecordingExecutor(_root);
        var result = await new GitTransfer(executor, new Locator()).CloneAsync(
            "https://example.org/repo.git", _root, new Account());
        Assert.False(result.IsSuccess);
        Assert.Equal("CLONE_TARGET_EXISTS", result.Error!.Code);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Clone_SshUsesOnlySelectedKeyWithShellSafeQuoting()
    {
        var key = Path.Combine(_root, "test'key");
        File.WriteAllText(key, "fake key; never read by SSH");
        var executor = new RecordingExecutor(_root);
        var result = await new GitTransfer(executor, new Locator()).CloneAsync(
            "git@example.org:repo.git", Path.Combine(_root, "new"),
            new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = key });
        Assert.True(result.IsSuccess);
        var ssh = Assert.Single(executor.Calls, c => c.Args.Contains("clone")).Options.Environment["GIT_SSH_COMMAND"];
        Assert.Contains("IdentitiesOnly=yes", ssh);
        Assert.Contains("StrictHostKeyChecking=yes", ssh);
        Assert.Contains("'\\''", ssh);
    }

    [Fact]
    public async Task Pull_LocalChangesStillAttemptFastForwardWithoutChangingWorktree()
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "fake executable; never launched");
        var executor = new RecordingExecutor(_root) { StatusOutput = " M local.txt" };
        var result = await new GitTransfer(executor, new Locator(), helper).PullAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "dummy-secret-id" });

        Assert.True(result.IsSuccess);
        var pull = Assert.Single(executor.Calls, c => c.Args.Contains("pull"));
        Assert.Contains("--ff-only", pull.Args);
        Assert.Contains("--no-rebase", pull.Args);
        Assert.Contains("merge.autoStash=false", pull.Args);
        Assert.Contains("rebase.autoStash=false", pull.Args);
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("stash") || c.Args.Contains("reset")
            || c.Args.Contains("rebase") || c.Args.Contains("--autostash") || c.Args.Contains("--rebase"));
    }

    [Fact]
    public async Task Pull_GitWouldOverwriteUntrackedFileReportsDirtyAfterAttemptingPull()
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "fake executable; never launched");
        var executor = new RecordingExecutor(_root)
        {
            StatusOutput = "?? local.txt",
            TransferFailure = new CommandResult
            {
                ExitCode = 1,
                StdErr = "error: The following untracked working tree files would be overwritten by merge:\n\tlocal.txt\nPlease move or remove them before you merge.\nAborting",
            },
        };

        var result = await new GitTransfer(executor, new Locator(), helper).PullAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "dummy-secret-id" });

        Assert.Equal("PULL_DIRTY", result.Error!.Code);
        Assert.Equal(new[] { "git pull --ff-only", "1" }, result.Error.Arguments);
        Assert.Single(executor.Calls, c => c.Args.Contains("pull"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pull_UsesTrackingOrSameNameFallbackAndForcesFastForward(bool upstream)
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "");
        var executor = new RecordingExecutor(_root) { HasUpstream = upstream };
        var result = await new GitTransfer(executor, new Locator(), helper).PullAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "dummy" });
        Assert.True(result.IsSuccess);
        var pull = Assert.Single(executor.Calls, c => c.Args.Contains("pull"));
        Assert.Contains("--ff-only", pull.Args);
        Assert.Contains("--no-rebase", pull.Args);
        Assert.Contains("refs/heads/main", pull.Args);
    }

    [Fact]
    public async Task Pull_UsesConfiguredNonOriginRemoteAndDoesNotChangeItsUrl()
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "fixture only");
        var executor = new RecordingExecutor(_root) { UpstreamRemote = "company" };

        var result = await new GitTransfer(executor, new Locator(), helper).PullAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fixture-id" });

        Assert.True(result.IsSuccess);
        var pull = Assert.Single(executor.Calls, c => c.Args.Contains("pull"));
        Assert.Equal(new[] { "company", "refs/heads/main" }, pull.Args.TakeLast(2));
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("set-url"));
    }

    [Fact]
    public async Task Pull_UnbornRepositoryDiscoversDefaultBranchAndSavesTrackingAfterSuccess()
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "fixture only");
        var executor = new RecordingExecutor(_root) { HasUpstream = false, HasCommits = false, DefaultBranch = "trunk" };

        var result = await new GitTransfer(executor, new Locator(), helper).PullAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fixture-id" });

        Assert.True(result.IsSuccess);
        var pull = Assert.Single(executor.Calls, c => c.Args.Contains("pull"));
        Assert.Equal("refs/heads/trunk", pull.Args[^1]);
        var writes = executor.Calls.Where(c => c.Args.Contains("--replace-all")).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Contains(writes, c => c.Args.Contains("branch.main.remote") && c.Args[^1] == "origin");
        Assert.Contains(writes, c => c.Args.Contains("branch.main.merge") && c.Args[^1] == "refs/heads/trunk");
        Assert.True(executor.Calls.IndexOf(pull) < executor.Calls.IndexOf(writes[0]));
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("reset") || c.Args.Contains("stash"));
    }

    [Fact]
    public async Task Pull_FailedFirstPullDoesNotSaveGuessedTracking()
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "fixture only");
        var executor = new RecordingExecutor(_root)
        {
            HasUpstream = false,
            TransferFailure = new CommandResult { ExitCode = 1, StdErr = "would be overwritten" },
        };
        var result = await new GitTransfer(executor, new Locator(), helper).PullAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fixture-id" });

        Assert.Equal("PULL_DIRTY", result.Error!.Code);
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("--replace-all"));
    }

    [Fact]
    public async Task Pull_DetachedHeadStopsBeforeConnecting()
    {
        var executor = new RecordingExecutor(_root) { Detached = true };
        var result = await new GitTransfer(executor, new Locator()).PullAsync(_root, new Account());

        Assert.Equal("PULL_DETACHED_HEAD", result.Error!.Code);
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("pull") || c.Args.Contains("ls-remote"));
    }

    [Fact]
    public async Task Pull_TrackingWriteFailureReportsCodeAlreadyPulled()
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "fixture only");
        var executor = new RecordingExecutor(_root) { HasUpstream = false, TrackingWriteFails = true };
        var result = await new GitTransfer(executor, new Locator(), helper).PullAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fixture-id" });

        Assert.Equal("PULL_TRACKING_SAVE_FAILED", result.Error!.Code);
        Assert.Single(executor.Calls, c => c.Args.Contains("pull"));
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("reset") || c.Args.Contains("stash"));
    }

    [Fact]
    public async Task Test_SshConfigurationFailureStopsBeforeConnecting()
    {
        var key = Path.Combine(_root, "selected-key");
        File.WriteAllText(key, "fixture only");
        var executor = new RecordingExecutor(_root) { OriginUrl = "git@example.org:repo.git", SshConfigurationFails = true };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = key });

        Assert.Equal("TRANSFER_SSH_CONFIG_FAILED", result.Error!.Code);
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("ls-remote"));
    }

    [Fact]
    public async Task Test_SshPreservesConnectionSettingsWithoutCopyingAnotherIdentity()
    {
        var key = Path.Combine(_root, "selected-key");
        File.WriteAllText(key, "fixture only");
        var executor = new RecordingExecutor(_root)
        {
            OriginUrl = "git@work-alias:team/repo.git",
            SshConfiguration = "hostname git.example.org\nuser git\nport 2222\nproxyjump bastion\nidentityfile unwanted-key\nstricthostkeychecking no\n",
            OnCall = (args, options) =>
            {
                if (!args.Contains("ls-remote")) return;
                var ssh = options.Environment["GIT_SSH_COMMAND"]!;
                var quoted = ssh[(ssh.IndexOf(" -F ", StringComparison.Ordinal) + 4)..ssh.IndexOf(" -i ", StringComparison.Ordinal)];
                var profile = File.ReadAllText(quoted[1..^1].Replace("'\\''", "'"));
                Assert.Contains("Host work-alias", profile);
                Assert.Contains("hostname git.example.org", profile);
                Assert.Contains("port 2222", profile);
                Assert.Contains("proxyjump bastion", profile);
                Assert.Contains("Host * !work-alias", profile);
                Assert.DoesNotContain("unwanted-key", profile);
                Assert.DoesNotContain("stricthostkeychecking no", profile);
                Assert.Contains("StrictHostKeyChecking=yes", ssh);
            },
        };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = key });

        Assert.True(result.IsSuccess);
        Assert.Single(executor.Calls, c => c.Args.Contains("-G"));
    }

    [Fact]
    public async Task Clone_AddressRewriteIsValidatedBeforePreparingCredentials()
    {
        var executor = new RecordingExecutor(_root) { ResolvedCloneUrl = "ext::untrusted-command" };
        var result = await new GitTransfer(executor, new Locator()).CloneAsync("https://example.org/repo.git",
            Path.Combine(_root, "new"), new Account());

        Assert.Equal("TRANSFER_URL_INVALID", result.Error!.Code);
        Assert.Single(executor.Calls);
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("clone"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Test_HttpsPinsSelectedHelperAndUsesReadOnlyNonInteractiveCommands()
    {
        var helper = Path.Combine(_root, "helper with space.exe");
        File.WriteAllText(helper, "fake executable; never launched");
        var executor = new RecordingExecutor(_root);
        var account = new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "dummy-secret-id" };
        var result = await new GitTransfer(executor, new Locator(), helper).TestAsync(_root, account);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, executor.Calls.Count);
        Assert.Contains("remote", executor.Calls[0].Args);
        Assert.Contains("get-url", executor.Calls[0].Args);
        var test = executor.Calls[1];
        Assert.Equal(new[] { "ls-remote", "--", "origin" }, test.Args.TakeLast(3));
        Assert.Contains("credential.helper=", test.Args);
        Assert.Contains("credential." + executor.OriginUrl + ".helper=", test.Args);
        Assert.Contains(test.Args, arg => arg.Contains("--account-id " + account.Id.ToString("D")));
        Assert.All(executor.Calls, call =>
        {
            Assert.DoesNotContain(call.Args, arg => arg.Contains(account.HttpsSecretId));
            Assert.DoesNotContain("config", call.Args);
            Assert.DoesNotContain("set-url", call.Args);
            Assert.DoesNotContain("pull", call.Args);
            Assert.DoesNotContain("fetch", call.Args);
            Assert.Contains("core.askPass=", call.Args);
            Assert.Contains("credential.interactive=false", call.Args);
            Assert.Equal("0", call.Options.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("Never", call.Options.Environment["GCM_INTERACTIVE"]);
            Assert.Equal("", call.Options.Environment["GIT_ASKPASS"]);
            Assert.Equal("", call.Options.Environment["SSH_ASKPASS"]);
            Assert.False(call.Options.Environment.ContainsKey("GIT_CONFIG_GLOBAL"));
            Assert.False(call.Options.Environment.ContainsKey("GIT_CONFIG_NOSYSTEM"));
            Assert.Equal("0", call.Options.Environment["GIT_CONFIG_COUNT"]);
            Assert.Equal("", call.Options.Environment["GIT_CONFIG_PARAMETERS"]);
            Assert.Null(call.Options.Environment["GIT_DIR"]);
            Assert.Null(call.Options.Environment["GIT_WORK_TREE"]);
            Assert.Null(call.Options.Environment["GIT_COMMON_DIR"]);
            Assert.Equal(TimeSpan.FromSeconds(60), call.Options.Timeout);
        });
    }

    [Fact]
    public async Task Test_HttpsOriginWithSshOnlyAccountReturnsProtocolMismatchBeforeConnection()
    {
        var key = Path.Combine(_root, "key");
        File.WriteAllText(key, "fake key; never read by SSH");
        var executor = new RecordingExecutor(_root);
        var account = new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = key };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root, account);

        Assert.Equal("TRANSFER_HTTPS_REQUIRED", result.Error!.Code);
        Assert.Single(executor.Calls);
        Assert.DoesNotContain(executor.Calls, c => c.Args.Contains("ls-remote"));
    }

    [Fact]
    public async Task Test_SshOriginWithHttpsOnlyAccountReturnsProtocolMismatchBeforeConnection()
    {
        var executor = new RecordingExecutor(_root) { OriginUrl = "git@example.org:team/repo.git" };
        var account = new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "dummy-secret-id" };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root, account);

        Assert.Equal("TRANSFER_SSH_REQUIRED", result.Error!.Code);
        Assert.Single(executor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_UsesActualSshOriginAndCleansConfigAfterSuccessOrFailure(bool fail)
    {
        var key = Path.Combine(_root, "test'key");
        File.WriteAllText(key, "fake key; never read by SSH");
        string? config = null;
        var executor = new RecordingExecutor(_root)
        {
            OriginUrl = "git@example.org:team/repo.git",
            TransferFailure = fail ? new CommandResult { ExitCode = 128, StdErr = "Permission denied (publickey)." } : null,
            OnCall = (args, options) =>
            {
                if (!args.Contains("ls-remote")) return;
                var ssh = options.Environment["GIT_SSH_COMMAND"]!;
                var quoted = ssh[(ssh.IndexOf(" -F ", StringComparison.Ordinal) + 4)..ssh.IndexOf(" -i ", StringComparison.Ordinal)];
                config = quoted[1..^1].Replace("'\\''", "'");
                Assert.True(File.Exists(config));
                Assert.Contains("hostname example.org", File.ReadAllText(config));
                Assert.DoesNotContain("identityfile", File.ReadAllText(config));
                Assert.Contains("IdentitiesOnly=yes", ssh);
                Assert.Contains("BatchMode=yes", ssh);
                Assert.Contains("StrictHostKeyChecking=yes", ssh);
                Assert.Contains("'\\''", ssh);
                Assert.Contains("core.sshCommand=" + ssh, args);
                Assert.DoesNotContain(args, arg => arg.Contains("--account-id"));
            },
        };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = key });

        Assert.Equal(!fail, result.IsSuccess);
        Assert.NotNull(config);
        Assert.False(File.Exists(config));
        var test = Assert.Single(executor.Calls, c => c.Args.Contains("ls-remote"));
        Assert.Equal("origin", test.Args[^1]);
        if (fail) Assert.Equal("TRANSFER_AUTH_FAILED", result.Error!.Code);
    }

    [Fact]
    public async Task Test_CancellationDuringSshCleansTemporaryConfig()
    {
        var key = Path.Combine(_root, "key");
        File.WriteAllText(key, "fake key; never read by SSH");
        using var cancellation = new CancellationTokenSource();
        string? config = null;
        var executor = new RecordingExecutor(_root)
        {
            OriginUrl = "git@example.org:team/repo.git",
            OnCall = (args, options) =>
            {
                if (!args.Contains("ls-remote")) return;
                var ssh = options.Environment["GIT_SSH_COMMAND"]!;
                var quoted = ssh[(ssh.IndexOf(" -F ", StringComparison.Ordinal) + 4)..ssh.IndexOf(" -i ", StringComparison.Ordinal)];
                config = quoted[1..^1].Replace("'\\''", "'");
                Assert.True(File.Exists(config));
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            },
        };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = key }, cancellation.Token);

        Assert.Equal("TRANSFER_CANCELLED", result.Error!.Code);
        Assert.NotNull(config);
        Assert.False(File.Exists(config));
    }

    [Fact]
    public async Task Test_PreCancelledDoesNotInvokeGit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executor = new RecordingExecutor(_root);
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root, new Account(), cancellation.Token);
        Assert.Equal("TRANSFER_CANCELLED", result.Error!.Code);
        Assert.Empty(executor.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.org/team/repo.git")]
    [InlineData("https://user:dummy-secret@example.org/team/repo.git")]
    [InlineData("ext::arbitrary")]
    public async Task Test_RejectsInvalidOrCredentialBearingOriginWithoutConnection(string origin)
    {
        var executor = new RecordingExecutor(_root) { OriginUrl = origin };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root, new Account());
        Assert.Equal("TRANSFER_URL_INVALID", result.Error!.Code);
        Assert.Empty(result.Error.Arguments);
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task Test_RemoteReadFailureDoesNotLeakRawOutputOrInvokeConnection()
    {
        var executor = new RecordingExecutor(_root)
        {
            Failure = new CommandResult { ExitCode = 128, StdErr = "fatal: dummy-secret in unknown output" },
        };
        var result = await new GitTransfer(executor, new Locator()).TestAsync(_root, new Account());
        Assert.Equal("TRANSFER_COMMAND_FAILED", result.Error!.Code);
        Assert.DoesNotContain("dummy-secret", string.Join(" ", result.Error.Arguments));
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task Test_MissingDirectoryDoesNotInvokeGit()
    {
        var executor = new RecordingExecutor(_root);
        var result = await new GitTransfer(executor, new Locator()).TestAsync(Path.Combine(_root, "missing"), new Account());
        Assert.Equal("PROJECT_REPOSITORY_NOT_FOUND", result.Error!.Code);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Test_ConnectionFailureIsClassifiedWithoutRawCredentials()
    {
        var helper = Path.Combine(_root, "helper.exe");
        File.WriteAllText(helper, "fake executable; never launched");
        var executor = new RecordingExecutor(_root)
        {
            TransferFailure = new CommandResult { ExitCode = 128, StdErr = "Authentication failed for https://user:dummy-secret@example.org" },
        };
        var result = await new GitTransfer(executor, new Locator(), helper).TestAsync(_root,
            new Account { AuthenticationType = AuthenticationType.Https, HttpsSecretId = "dummy-secret-id" });
        Assert.Equal("TRANSFER_AUTH_FAILED", result.Error!.Code);
        Assert.Equal(new[] { "git ls-remote", "128" }, result.Error.Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clone_SshUsesConnectionOnlyConfig_AndCleansUpAfterSuccessOrFailure(bool fail)
    {
        var key = Path.Combine(_root, "key");
        File.WriteAllText(key, "fake key; never read by SSH");
        string? config = null;
        var executor = new RecordingExecutor(_root)
        {
            TransferFailure = fail ? new CommandResult { ExitCode = 128, StdErr = "Permission denied (publickey)." } : null,
            OnCall = (args, options) =>
            {
                if (!args.Contains("clone")) return;
                var ssh = options.Environment["GIT_SSH_COMMAND"]!;
                var quoted = ssh[(ssh.IndexOf(" -F ", StringComparison.Ordinal) + 4)..ssh.IndexOf(" -i ", StringComparison.Ordinal)];
                config = quoted[1..^1].Replace("'\\''", "'");
                Assert.True(Path.IsPathFullyQualified(config));
                Assert.True(File.Exists(config));
                Assert.Contains("hostname example.org", File.ReadAllText(config));
                Assert.DoesNotContain("identityfile", File.ReadAllText(config));
                Assert.Contains("IdentitiesOnly=yes", ssh);
                Assert.Contains("StrictHostKeyChecking=yes", ssh);
            },
        };
        var result = await new GitTransfer(executor, new Locator()).CloneAsync(
            "git@example.org:repo.git", Path.Combine(_root, "new"),
            new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = key });
        Assert.Equal(!fail, result.IsSuccess);
        Assert.NotNull(config);
        Assert.False(File.Exists(config));
        if (fail) Assert.Equal("TRANSFER_AUTH_FAILED", result.Error!.Code);
    }

    private sealed class Locator : IGitLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => Task.FromResult<string?>("fake-git");
    }

    private sealed class RecordingExecutor(string root) : ICommandExecutor
    {
        public List<(string[] Args, CommandExecutionOptions Options)> Calls { get; } = [];
        public string StatusOutput { get; init; } = "";
        public bool HasUpstream { get; init; } = true;
        public string UpstreamRemote { get; init; } = "origin";
        public bool HasCommits { get; init; } = true;
        public bool Detached { get; init; }
        public bool TrackingWriteFails { get; init; }
        public bool SshConfigurationFails { get; init; }
        public string? ResolvedCloneUrl { get; init; }
        public string DefaultBranch { get; init; } = "main";
        public string? SshConfiguration { get; init; }
        public string OriginUrl { get; init; } = "https://example.org/team/repo.git";
        public Action<string[], CommandExecutionOptions>? OnCall { get; init; }
        public CommandResult? Failure { get; init; }
        public CommandResult? TransferFailure { get; init; }
        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Transfer must use isolated options.");

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add((arguments.ToArray(), options));
            OnCall?.Invoke(arguments.ToArray(), options);
            if (Failure is not null) return Task.FromResult(Failure);
            if (arguments[0] == "-G")
                return Task.FromResult(new CommandResult { ExitCode = SshConfigurationFails ? 255 : 0,
                    StdOut = SshConfiguration ?? "hostname example.org\nuser git\nport 22\nidentityfile unwanted-key\n" });
            var index = 0;
            while (arguments[index] == "-c") index += 2;
            var args = arguments.Skip(index).ToArray();
            if (args[0] == "ls-remote" && args.Contains("--get-url"))
                return Task.FromResult(new CommandResult { ExitCode = 0, StdOut = ResolvedCloneUrl ?? args[^1] });
            if ((args[0] is "ls-remote" or "clone" or "pull") && TransferFailure is not null)
                return Task.FromResult(TransferFailure);
            if (args[0] == "config" && args.Contains("--get") && !HasUpstream)
                return Task.FromResult(new CommandResult { ExitCode = 1 });
            if (args[0] == "config" && args.Contains("--replace-all") && TrackingWriteFails)
                return Task.FromResult(new CommandResult { ExitCode = 1 });
            if (args[0] == "rev-parse" && args.Contains("--verify") && !HasCommits
                || args[0] == "symbolic-ref" && Detached)
                return Task.FromResult(new CommandResult { ExitCode = 1 });
            var output = args[0] switch
            {
                "status" => StatusOutput,
                "rev-parse" when args.Contains("--show-toplevel") => root,
                "rev-parse" => Path.Combine(root, ".git", args[^1]),
                "symbolic-ref" => "main",
                "config" => args[^1].EndsWith(".remote") ? UpstreamRemote : "refs/heads/main",
                "remote" => OriginUrl,
                "ls-remote" when args.Contains("--symref") => $"ref: refs/heads/{DefaultBranch}\tHEAD\n",
                _ => "",
            };
            return Task.FromResult(new CommandResult { ExitCode = 0, StdOut = output });
        }
    }
}
