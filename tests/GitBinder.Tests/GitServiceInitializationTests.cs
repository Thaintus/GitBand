using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Infrastructure.Git;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>初始化保护只记录命令参数，测试源码不调用真实 Git</summary>
public sealed class GitServiceInitializationTests
{
    [Fact]
    public async Task Initialize_UsesExactDirectoryEmptyTemplateAndIsolatedProcess()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.InitializeRepositoryAsync(fixture.DirectoryPath);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.Executor.OrdinaryCount);
        Assert.Equal(3, fixture.Executor.Calls.Count);
        Assert.Equal(new[] { "init", "--template=", "--", fixture.DirectoryPath },
            Command(fixture.Executor.Calls[1].Arguments));
        Assert.All(fixture.Executor.Calls, call => AssertIsolation(call, fixture.DirectoryPath));
        Assert.All(fixture.Executor.Calls.Where(call => Command(call.Arguments)[0] != "init"),
            call => Assert.Equal(new[] { "rev-parse", "--show-toplevel" }, Command(call.Arguments)));
        Assert.Equal("keep local content", File.ReadAllText(fixture.LocalFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_ParentRepositoryNeverSubstitutesForOwnGitDirectory(bool parentReportsChildAsWorktree)
    {
        using var fixture = new Fixture();
        fixture.Executor.ExistingRoot = parentReportsChildAsWorktree ? fixture.DirectoryPath : fixture.ParentPath;

        var result = await fixture.Service.InitializeRepositoryAsync(fixture.DirectoryPath);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Executor.InitializeCount);
        Assert.Equal(fixture.DirectoryPath, Command(fixture.Executor.Calls[1].Arguments)[3]);
        Assert.True(Directory.Exists(Path.Combine(fixture.DirectoryPath, ".git")));
        Assert.False(Directory.Exists(Path.Combine(fixture.ParentPath, ".git")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_ExistingSameRootRepositoryOrWorktreeIsNotReinitialized(bool gitFile)
    {
        using var fixture = new Fixture();
        fixture.CreateMarker(gitFile);
        fixture.Executor.ExistingRoot = fixture.DirectoryPath;

        var result = await fixture.Service.InitializeRepositoryAsync(fixture.DirectoryPath);

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Executor.Calls);
        Assert.Equal(0, fixture.Executor.InitializeCount);
        Assert.Equal("keep local content", File.ReadAllText(fixture.LocalFile));
        if (gitFile)
            Assert.Equal("gitdir: unchanged-worktree-metadata", File.ReadAllText(Path.Combine(fixture.DirectoryPath, ".git")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_ExistingGitMarkerWithoutExactRootFailsWithoutWrite(bool gitFile)
    {
        using var fixture = new Fixture();
        fixture.CreateMarker(gitFile);
        fixture.Executor.ExistingRoot = fixture.ParentPath;

        var result = await fixture.Service.InitializeRepositoryAsync(fixture.DirectoryPath);

        Assert.False(result.IsSuccess);
        Assert.Equal("PROJECT_GIT_INITIALIZATION_FAILED", result.Error!.Code);
        Assert.Single(fixture.Executor.Calls);
        Assert.Equal(0, fixture.Executor.InitializeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_VerifiesBothOwnMarkerAndExactRootAfterSuccess(bool markerMissing)
    {
        using var fixture = new Fixture();
        fixture.Executor.CreateMarker = !markerMissing;
        fixture.Executor.VerifiedRoot = markerMissing ? fixture.DirectoryPath : fixture.ParentPath;

        var result = await fixture.Service.InitializeRepositoryAsync(fixture.DirectoryPath);

        Assert.False(result.IsSuccess);
        Assert.Equal("PROJECT_GIT_INITIALIZATION_FAILED", result.Error!.Code);
        Assert.Equal(1, fixture.Executor.InitializeCount);
        Assert.Equal(3, fixture.Executor.Calls.Count);
        Assert.Equal("keep local content", File.ReadAllText(fixture.LocalFile));
        if (!markerMissing)
            Assert.True(Directory.Exists(Path.Combine(fixture.DirectoryPath, ".git")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_CommandFailureOrExceptionReturnsSafeError(bool throws)
    {
        using var fixture = new Fixture();
        fixture.Executor.InitializeSucceeds = false;
        fixture.Executor.ThrowOnInitialize = throws;

        var result = await fixture.Service.InitializeRepositoryAsync(fixture.DirectoryPath);

        Assert.False(result.IsSuccess);
        Assert.Equal("PROJECT_GIT_INITIALIZATION_FAILED", result.Error!.Code);
        Assert.Empty(result.Error.Arguments);
        Assert.Null(result.Error.TechnicalDetails);
        Assert.Equal(2, fixture.Executor.Calls.Count);
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, ".git")));
    }

    [Fact]
    public async Task Initialize_InvalidMissingAndFilesystemRootTargetsDoNotInvokeGit()
    {
        using var fixture = new Fixture();
        var targets = new[]
        {
            "", "relative-project", Path.Combine(fixture.ParentPath, "not-created"),
            Path.GetPathRoot(fixture.DirectoryPath)!, fixture.DirectoryPath + "\0",
        };
        foreach (var target in targets)
        {
            var result = await fixture.Service.InitializeRepositoryAsync(target);
            Assert.False(result.IsSuccess);
            Assert.Equal("PROJECT_GIT_INITIALIZATION_FAILED", result.Error!.Code);
        }
        Assert.Empty(fixture.Executor.Calls);
        Assert.Equal(0, fixture.Executor.OrdinaryCount);
    }

    [Fact]
    public async Task Initialize_PreCancelledDoesNotInvokeGit()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.InitializeRepositoryAsync(fixture.DirectoryPath, cancellation.Token));

        Assert.Empty(fixture.Executor.Calls);
    }

    [Fact]
    public async Task FakeInitialization_ChangesOrdinaryDirectoryOnlyAfterExplicitCall()
    {
        using var fixture = new Fixture();
        var fake = new FakeGitService
        {
            ValidateResult = false, RepositoryRoot = fixture.ParentPath, OriginUrl = "", ValidateRepository = _ => false,
        };
        Assert.False(await fake.ValidateRepositoryAsync(fixture.DirectoryPath));
        Assert.Equal(fixture.ParentPath, await fake.GetRepositoryRootAsync(fixture.DirectoryPath));
        Assert.Equal(0, fake.InitializeCount);

        Assert.True((await fake.InitializeRepositoryAsync(fixture.DirectoryPath)).IsSuccess);

        Assert.Equal(1, fake.InitializeCount);
        Assert.Equal(fixture.DirectoryPath, fake.LastInitializedPath);
        Assert.True(await fake.ValidateRepositoryAsync(fixture.DirectoryPath));
        Assert.Equal(fixture.DirectoryPath, await fake.GetRepositoryRootAsync(fixture.DirectoryPath));
        Assert.Equal("", await fake.GetOriginUrlAsync(fixture.DirectoryPath));
    }

    [Fact]
    public async Task FakeInitialization_PreservesExistingRepositoryOriginAndSupportsFailure()
    {
        using var fixture = new Fixture();
        var fake = new FakeGitService { RepositoryRoot = fixture.DirectoryPath };
        var originalOrigin = fake.OriginUrl;
        Assert.True((await fake.InitializeRepositoryAsync(fixture.DirectoryPath)).IsSuccess);
        Assert.Equal(originalOrigin, fake.OriginUrl);

        fake.InitializeSucceeds = false;
        var result = await fake.InitializeRepositoryAsync(fixture.ParentPath);
        Assert.False(result.IsSuccess);
        Assert.Equal("PROJECT_GIT_INITIALIZATION_FAILED", result.Error!.Code);
        Assert.Equal(fixture.DirectoryPath, fake.RepositoryRoot);
        Assert.Equal(originalOrigin, fake.OriginUrl);
    }

    [Fact]
    public async Task RepositoryMetadataAndOriginWrites_IsolateDirectoryWithoutSuppressingUserConfiguration()
    {
        using var fixture = new Fixture();
        var executor = new MetadataExecutor(fixture.DirectoryPath);
        var service = new GitService(executor, new Locator());

        Assert.True(await service.ValidateRepositoryAsync(fixture.DirectoryPath));
        Assert.Equal(fixture.DirectoryPath, await service.GetRepositoryRootAsync(fixture.DirectoryPath));
        Assert.Equal(Path.Combine(fixture.DirectoryPath, ".git"), await service.GetGitDirAsync(fixture.DirectoryPath));
        Assert.Equal(executor.OriginUrl, await service.GetOriginUrlAsync(fixture.DirectoryPath));
        Assert.Equal("main", await service.GetCurrentBranchAsync(fixture.DirectoryPath));
        Assert.True((await service.SetOriginUrlAsync(fixture.DirectoryPath, "git@example.invalid:team/project.git")).IsSuccess);

        Assert.Equal(0, executor.OrdinaryCount);
        Assert.Equal(1, executor.RemoteWriteCount);
        Assert.All(executor.Calls, call => AssertRepositoryIsolation(call, fixture.DirectoryPath));
        var write = Assert.Single(executor.Calls, call => Command(call.Arguments)[1] == "set-url");
        Assert.Equal(new[] { "remote", "set-url", "origin", "git@example.invalid:team/project.git" }, Command(write.Arguments));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(128, false)]
    public async Task SetOrigin_OnlyAddsAfterConfirmedMissingOriginAndUsesSameIsolatedContext(int configExit, bool succeeds)
    {
        using var fixture = new Fixture();
        var executor = new MetadataExecutor(fixture.DirectoryPath) { OriginReadFails = true, ConfigExitCode = configExit };
        var service = new GitService(executor, new Locator());

        var result = await service.SetOriginUrlAsync(fixture.DirectoryPath, "https://example.invalid/team/project.git");

        Assert.Equal(succeeds, result.IsSuccess);
        Assert.Equal(succeeds ? 1 : 0, executor.RemoteWriteCount);
        Assert.All(executor.Calls, call => AssertRepositoryIsolation(call, fixture.DirectoryPath));
        Assert.Equal(new[] { "config", "--get", "remote.origin.url" }, Command(executor.Calls[1].Arguments));
        if (succeeds)
            Assert.Equal(new[] { "remote", "add", "origin", "https://example.invalid/team/project.git" },
                Command(executor.Calls[2].Arguments));
        else
            Assert.Equal("PROJECT_REMOTE_UPDATE_FAILED", result.Error!.Code);
    }

    [Fact]
    public async Task RepositoryCommands_PreCancelledNeverInvokeExecutor()
    {
        using var fixture = new Fixture();
        var executor = new MetadataExecutor(fixture.DirectoryPath);
        var service = new GitService(executor, new Locator());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<CancellationToken, Task>[] operations =
        [
            ct => service.ValidateRepositoryAsync(fixture.DirectoryPath, ct),
            ct => service.GetRepositoryRootAsync(fixture.DirectoryPath, ct),
            ct => service.GetGitDirAsync(fixture.DirectoryPath, ct),
            ct => service.GetOriginUrlAsync(fixture.DirectoryPath, ct),
            ct => service.GetCurrentBranchAsync(fixture.DirectoryPath, ct),
            ct => service.SetOriginUrlAsync(fixture.DirectoryPath, "https://example.invalid/team/project.git", ct),
        ];
        foreach (var operation in operations)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(cancellation.Token));

        Assert.Empty(executor.Calls);
        Assert.Equal(0, executor.OrdinaryCount);
    }

    private static void AssertRepositoryIsolation(Invocation call, string directory)
    {
        Assert.Equal(directory, call.Directory);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Options.Timeout);
        foreach (var key in new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG",
        })
            Assert.Null(call.Options.Environment[key]);
        Assert.Equal("0", call.Options.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("", call.Options.Environment["GIT_CONFIG_PARAMETERS"]);
        foreach (var key in new[] { "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_SYSTEM", "GIT_SSH_COMMAND", "GIT_SSH_VARIANT" })
            Assert.False(call.Options.Environment.ContainsKey(key));
    }

    private static string[] Command(string[] arguments)
    {
        var offset = 0;
        while (offset < arguments.Length && arguments[offset] == "-c") offset += 2;
        return arguments.Skip(offset).ToArray();
    }

    private static void AssertIsolation(Invocation call, string directory)
    {
        Assert.Equal(directory, call.Directory);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Options.Timeout);
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        Assert.Contains($"core.hooksPath={nullDevice}", call.Arguments);
        Assert.Contains("core.fsmonitor=false", call.Arguments);
        Assert.Equal(nullDevice, call.Options.Environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("1", call.Options.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", call.Options.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("", call.Options.Environment["GIT_CONFIG_PARAMETERS"]);
        Assert.Null(call.Options.Environment["GIT_DIR"]);
        Assert.Null(call.Options.Environment["GIT_WORK_TREE"]);
        Assert.Null(call.Options.Environment["GIT_COMMON_DIR"]);
        Assert.Null(call.Options.Environment["GIT_INDEX_FILE"]);
        Assert.Null(call.Options.Environment["GIT_OBJECT_DIRECTORY"]);
        Assert.Null(call.Options.Environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"]);
    }

    private sealed record Invocation(string[] Arguments, string? Directory, CommandExecutionOptions Options);

    private sealed class Locator : IGitLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => Task.FromResult<string?>("fake-git");
    }

    private sealed class MetadataExecutor(string directory) : ICommandExecutor
    {
        public List<Invocation> Calls { get; } = [];
        public int OrdinaryCount { get; private set; }
        public int RemoteWriteCount { get; private set; }
        public string OriginUrl { get; } = "https://example.invalid/team/project.git";
        public bool OriginReadFails { get; init; }
        public int ConfigExitCode { get; init; } = 1;

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            OrdinaryCount++;
            throw new InvalidOperationException("Repository operations must isolate inherited Git directory variables.");
        }

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            var call = new Invocation(arguments.ToArray(), workingDirectory, options);
            Calls.Add(call);
            var command = Command(call.Arguments);
            if (command[0] == "rev-parse")
                return Task.FromResult(new CommandResult
                {
                    ExitCode = 0, StdOut = command[1] == "--git-dir" ? ".git" : directory,
                });
            if (command[0] == "branch")
                return Task.FromResult(new CommandResult { ExitCode = 0, StdOut = "main" });
            if (command[0] == "config")
                return Task.FromResult(new CommandResult { ExitCode = ConfigExitCode });
            if (command[0] == "remote" && command[1] == "get-url")
                return Task.FromResult(new CommandResult { ExitCode = OriginReadFails ? 128 : 0, StdOut = OriginUrl });
            RemoteWriteCount++;
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }
    }

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public List<Invocation> Calls { get; } = [];
        public int OrdinaryCount { get; private set; }
        public int InitializeCount { get; private set; }
        public bool InitializeSucceeds { get; set; } = true;
        public bool ThrowOnInitialize { get; set; }
        public bool CreateMarker { get; set; } = true;
        public string? ExistingRoot { get; set; }
        public string? VerifiedRoot { get; set; }

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            OrdinaryCount++;
            throw new InvalidOperationException("Initialization must use isolated options.");
        }

        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            Calls.Add(new Invocation(args, workingDirectory, options));
            var command = Command(args);
            if (command[0] == "init")
            {
                InitializeCount++;
                if (ThrowOnInitialize)
                    throw new IOException("dummy-secret process exception");
                if (InitializeSucceeds && CreateMarker)
                    System.IO.Directory.CreateDirectory(Path.Combine(command[3], ".git"));
                return Task.FromResult(new CommandResult
                {
                    ExitCode = InitializeSucceeds ? 0 : 128,
                    StdOut = "dummy-secret stdout", StdErr = "dummy-secret stderr",
                });
            }

            var root = InitializeCount == 0 ? ExistingRoot : VerifiedRoot ?? workingDirectory;
            return Task.FromResult(new CommandResult { ExitCode = root is null ? 128 : 0, StdOut = root ?? "" });
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string ParentPath { get; } = Directory.CreateTempSubdirectory("gitbinder-init-").FullName;
        public string DirectoryPath { get; }
        public string LocalFile { get; }
        public RecordingExecutor Executor { get; } = new();
        public GitService Service { get; }

        public Fixture()
        {
            DirectoryPath = Path.Combine(ParentPath, "--local project");
            Directory.CreateDirectory(DirectoryPath);
            LocalFile = Path.Combine(DirectoryPath, "keep.txt");
            File.WriteAllText(LocalFile, "keep local content");
            Service = new GitService(Executor, new Locator());
        }

        public void CreateMarker(bool gitFile)
        {
            var marker = Path.Combine(DirectoryPath, ".git");
            if (gitFile) File.WriteAllText(marker, "gitdir: unchanged-worktree-metadata");
            else Directory.CreateDirectory(marker);
        }

        public void Dispose() => Directory.Delete(ParentPath, recursive: true);
    }
}
