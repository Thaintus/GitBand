using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.GlobalMode;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public sealed class BindingPersistenceRollbackTests
{
    [Fact]
    public async Task InitialBind_SaveFails_RestoresOperationConfigAndKeepsOriginalSnapshot()
    {
        using var fixture = await Fixture.CreateAsync();
        var original = fixture.Config.State;
        fixture.Bindings.OnWrite = (_, _) => throw new IOException("save failed");

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("BINDING_SAVE_FAILED", result.Error?.Code);
        Assert.Null(fixture.Bindings.Stored);
        Assert.Equal(original, fixture.Config.State);
        Assert.Equal(1, fixture.Config.RestoreCount);
        var snapshot = await fixture.Snapshots.GetByProjectIdAsync(fixture.Project.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(original.Name, snapshot.UserName);
        Assert.Equal(original.Emails, snapshot.EmailConfig);
    }

    [Fact]
    public async Task Rebind_SaveFails_RestoresLatestConfigWithoutMutatingStoredBindingOrEarliestSnapshot()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Service.BindAsync(fixture.Project.Id, fixture.First.Id)).IsSuccess);
        var stored = fixture.Bindings.Stored!;
        stored.Status = BindingStatus.CredentialMissing;
        stored.VerifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
        stored.LastTestResult = "previous validation";
        var previousAppliedAt = stored.AppliedAt;
        var original = await fixture.Snapshots.GetByProjectIdAsync(fixture.Project.Id);
        fixture.Config.State = new("manual-name", new("current@example.test", "", null), "manual-ssh", "manual-helper");
        var operationConfig = fixture.Config.State;
        Binding? attempted = null;
        fixture.Bindings.OnWrite = (candidate, _) =>
        {
            attempted = candidate;
            throw new IOException("update failed");
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("BINDING_SAVE_FAILED", result.Error?.Code);
        Assert.NotNull(attempted);
        Assert.NotSame(stored, attempted);
        Assert.Equal(stored.Id, attempted.Id);
        Assert.Equal(stored.VerifiedAt, attempted.VerifiedAt);
        Assert.Equal(stored.LastTestResult, attempted.LastTestResult);
        Assert.Same(stored, fixture.Bindings.Stored);
        Assert.Equal(fixture.First.Id, stored.AccountId);
        Assert.Equal(BindingStatus.CredentialMissing, stored.Status);
        Assert.Equal(previousAppliedAt, stored.AppliedAt);
        Assert.Equal(operationConfig, fixture.Config.State);
        Assert.Same(original, await fixture.Snapshots.GetByProjectIdAsync(fixture.Project.Id));
        Assert.Equal("original-name", original!.UserName);
    }

    [Theory]
    [InlineData("helper")]
    [InlineData("ssh")]
    [InlineData("identity")]
    public async Task ApplyFailsAfterPartialWrite_RestoresAllCapturedFieldsWithoutSaving(string stage)
    {
        using var fixture = await Fixture.CreateAsync();
        var original = fixture.Config.State;
        fixture.Config.AfterWrite = current =>
        {
            if (current == stage)
                throw new IOException("partial config write");
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("BINDING_APPLY_FAILED", result.Error?.Code);
        Assert.Equal(original, fixture.Config.State);
        Assert.Equal(1, fixture.Config.RestoreCount);
        Assert.Equal(0, fixture.Bindings.WriteCount);
    }

    [Fact]
    public async Task ApplyTerminationUnconfirmed_DoesNotStartCompensationOrSave()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Config.AfterWrite = _ => throw new CommandTerminationUnconfirmedException();

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("TRANSFER_TERMINATION_UNCONFIRMED", result.Error?.Code);
        Assert.Equal(0, fixture.Config.RestoreCount);
        Assert.Equal(0, fixture.Bindings.WriteCount);
        Assert.Null(fixture.Bindings.Stored);
    }

    [Fact]
    public async Task RestoreTerminationUnconfirmed_PreservesSpecificFailure()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Config.RestoreError = new CommandTerminationUnconfirmedException();
        fixture.Bindings.OnWrite = (_, _) => throw new IOException("save failed");

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("TRANSFER_TERMINATION_UNCONFIRMED", result.Error?.Code);
        Assert.Equal(1, fixture.Config.RestoreCount);
        Assert.Null(fixture.Bindings.Stored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreFails_ReturnsDistinctErrorForApplyOrSaveFailure(bool failDuringApply)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Config.ThrowOnRestore = true;
        if (failDuringApply)
            fixture.Config.AfterWrite = _ => throw new IOException("apply failed");
        else
            fixture.Bindings.OnWrite = (_, _) => throw new IOException("save failed");

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("BINDING_ROLLBACK_FAILED", result.Error?.Code);
        Assert.Equal(1, fixture.Config.RestoreCount);
        Assert.Null(fixture.Bindings.Stored);
    }

    [Fact]
    public async Task CancellationDuringApply_RestoresWithUncancelledToken()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        var original = fixture.Config.State;
        fixture.Config.AfterWrite = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id, cts.Token);

        Assert.Equal("BINDING_APPLY_FAILED", result.Error?.Code);
        Assert.Equal(original, fixture.Config.State);
        Assert.False(fixture.Config.RestoreToken.CanBeCanceled);
        Assert.Equal(0, fixture.Bindings.WriteCount);
    }

    [Fact]
    public async Task CancellationAfterApplyBeforeSave_RestoresWithoutCallingRepositoryWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        var original = fixture.Config.State;
        fixture.Config.AfterWrite = stage =>
        {
            if (stage == "identity")
                cts.Cancel();
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id, cts.Token);

        Assert.Equal("BINDING_SAVE_FAILED", result.Error?.Code);
        Assert.Equal(original, fixture.Config.State);
        Assert.Equal(0, fixture.Bindings.WriteCount);
        Assert.False(fixture.Config.RestoreToken.CanBeCanceled);
        Assert.False(fixture.Bindings.ReadTokens.Last().CanBeCanceled);
    }

    [Fact]
    public async Task CancellationDuringSaveBeforeCommit_RestoresWithUncancelledToken()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        var original = fixture.Config.State;
        fixture.Bindings.OnWrite = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id, cts.Token);

        Assert.Equal("BINDING_SAVE_FAILED", result.Error?.Code);
        Assert.Equal(original, fixture.Config.State);
        Assert.False(fixture.Config.RestoreToken.CanBeCanceled);
        Assert.False(fixture.Bindings.ReadTokens.Last().CanBeCanceled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveCommitsThenThrowsOrCancels_ReturnsSavedBindingWithoutRollback(bool rebind, bool cancel)
    {
        using var fixture = await Fixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        if (rebind)
            Assert.True((await fixture.Service.BindAsync(fixture.Project.Id, fixture.First.Id)).IsSuccess);
        fixture.Bindings.OnWrite = (binding, token) =>
        {
            fixture.Bindings.Stored = binding;
            if (cancel)
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new IOException("exception after commit");
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id, cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(fixture.Second.Id, result.Value!.AccountId);
        Assert.Same(fixture.Bindings.Stored, result.Value);
        Assert.Equal(fixture.Second.GitName, fixture.Config.State.Name);
        Assert.Equal(0, fixture.Config.RestoreCount);
        Assert.False(fixture.Bindings.ReadTokens.Last().CanBeCanceled);
    }

    [Fact]
    public async Task SaveFailsAndReadbackFails_DoesNotAssumeCommitFailedOrOverwriteConfig()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Bindings.OnWrite = (binding, _) =>
        {
            fixture.Bindings.Stored = binding;
            fixture.Bindings.ThrowOnRead = true;
            throw new IOException("post-commit failure");
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("BINDING_ROLLBACK_FAILED", result.Error?.Code);
        Assert.Equal(fixture.Second.Id, fixture.Bindings.Stored!.AccountId);
        Assert.Equal(fixture.Second.GitName, fixture.Config.State.Name);
        Assert.Equal(0, fixture.Config.RestoreCount);
    }

    [Fact]
    public async Task InitialBindingReadFails_DoesNotReadOrWriteConfig()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Bindings.ThrowOnRead = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id));

        Assert.Equal(0, fixture.Config.ReadCount);
        Assert.Equal(0, fixture.Config.WriteCount);
        Assert.Equal(0, fixture.Config.RestoreCount);
        Assert.Null(await fixture.Snapshots.GetByProjectIdAsync(fixture.Project.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRepository_SaveSuccessOrFailureNeverTouchesConfig(bool failSave)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Project.RepositoryPath = Path.Combine(fixture.RepositoryPath, "missing");
        if (failSave)
            fixture.Bindings.OnWrite = (_, _) => throw new IOException("save failed");

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal(!failSave, result.IsSuccess);
        Assert.Equal(failSave ? "BINDING_SAVE_FAILED" : null, result.Error?.Code);
        Assert.Equal(0, fixture.Config.ReadCount);
        Assert.Equal(0, fixture.Config.WriteCount);
        Assert.Equal(0, fixture.Config.RestoreCount);
        Assert.Null(await fixture.Snapshots.GetByProjectIdAsync(fixture.Project.Id));
    }

    [Fact]
    public async Task GlobalMode_SaveFailureRestoresActualPreOperationConfigNotRequestedAccount()
    {
        using var fixture = await Fixture.CreateAsync(globalMode: true);
        fixture.Config.State = new("active-global-before", new("before@example.test", null, ""), "global-ssh", "global-helper");
        var original = fixture.Config.State;
        ConfigState? appliedConfig = null;
        fixture.Bindings.OnWrite = (_, _) =>
        {
            appliedConfig = fixture.Config.State;
            throw new IOException("save failed");
        };

        var result = await fixture.Service.BindAsync(fixture.Project.Id, fixture.Second.Id);

        Assert.Equal("BINDING_SAVE_FAILED", result.Error?.Code);
        Assert.NotNull(appliedConfig);
        Assert.Equal(fixture.Global.GitName, appliedConfig.Name);
        Assert.Equal(fixture.Global.Id.ToString(), appliedConfig.Helper);
        Assert.Equal(original, fixture.Config.State);
        Assert.Null(fixture.Bindings.Stored);
    }

    private sealed class Fixture : IDisposable
    {
        public string RepositoryPath { get; } = Path.Combine(Path.GetTempPath(), "gitbinder-binding-rollback-" + Guid.NewGuid().ToString("N"));
        public FakeAccountRepository Accounts { get; } = new();
        public FakeProjectRepository Projects { get; } = new();
        public ControlledBindingRepository Bindings { get; } = new();
        public FakeSnapshotRepository Snapshots { get; } = new();
        public StatefulConfigApplier Config { get; } = new();
        public Account First { get; } = MakeAccount("first");
        public Account Second { get; } = MakeAccount("second");
        public Account Global { get; } = MakeAccount("global");
        public Project Project { get; }
        public BindingService Service { get; }

        private Fixture(bool globalMode)
        {
            Directory.CreateDirectory(RepositoryPath);
            Project = new Project
            {
                RepositoryPath = RepositoryPath,
                GitDir = Path.Combine(RepositoryPath, ".git"),
                RemoteProtocol = RemoteProtocol.Https,
            };
            var resolver = new EffectiveAccountResolver(
                new FakeGlobalModeState { IsEnabled = globalMode, GlobalAccountId = Global.Id },
                Bindings, Accounts);
            Service = new BindingService(Bindings, Accounts, Projects, new FakeSecretStore(),
                new FakeGitService(), Config, Snapshots, new FakeGitTransfer(), resolver);
        }

        public static async Task<Fixture> CreateAsync(bool globalMode = false)
        {
            var fixture = new Fixture(globalMode);
            await fixture.Accounts.AddAsync(fixture.First);
            await fixture.Accounts.AddAsync(fixture.Second);
            await fixture.Accounts.AddAsync(fixture.Global);
            await fixture.Projects.AddAsync(fixture.Project);
            return fixture;
        }

        private static Account MakeAccount(string name) => new()
        {
            Alias = name,
            Username = name,
            GitName = name + "-identity",
            GitEmail = name + "@example.test",
            AuthenticationType = AuthenticationType.Https,
            HttpsSecretId = "fake-secret-" + name,
            Enabled = true,
        };

        public void Dispose() => Directory.Delete(RepositoryPath, recursive: true);
    }

    private sealed class ControlledBindingRepository : IBindingRepository, IBindingReader
    {
        public Binding? Stored { get; set; }
        public Action<Binding, CancellationToken>? OnWrite { get; set; }
        public bool ThrowOnRead { get; set; }
        public int WriteCount { get; private set; }
        public List<CancellationToken> ReadTokens { get; } = [];

        public Task<Binding?> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        {
            ReadTokens.Add(ct);
            ct.ThrowIfCancellationRequested();
            if (ThrowOnRead)
                throw new IOException("binding read failed");
            return Task.FromResult(Stored?.ProjectId == projectId ? Stored : null);
        }

        public Task<IReadOnlyList<Binding>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Binding>>(Stored is null ? [] : [Stored]);

        public Task<IReadOnlyList<Binding>> GetByAccountIdAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Binding>>(Stored is { } binding && binding.AccountId == accountId ? [binding] : []);

        public Task<int> CountByAccountIdAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult(Stored?.AccountId == accountId ? 1 : 0);

        public Task AddAsync(Binding binding, CancellationToken ct = default) => WriteAsync(binding, ct);
        public Task UpdateAsync(Binding binding, CancellationToken ct = default) => WriteAsync(binding, ct);

        private Task WriteAsync(Binding binding, CancellationToken ct)
        {
            WriteCount++;
            ct.ThrowIfCancellationRequested();
            if (OnWrite is not null)
                OnWrite(binding, ct);
            else
                Stored = binding;
            return Task.CompletedTask;
        }

        public Task DeleteByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        {
            if (Stored?.ProjectId == projectId)
                Stored = null;
            return Task.CompletedTask;
        }

        public Guid? GetAccountIdByProject(Guid projectId) => Stored?.ProjectId == projectId ? Stored.AccountId : null;
    }

    private sealed record ConfigState(string Name, GitEmailConfigSnapshot Emails, string Ssh, string Helper);

    private sealed class StatefulConfigApplier : IGitConfigApplier
    {
        public ConfigState State { get; set; } = new("original-name", new("original@example.test", "", null), "original-ssh", "original-helper");
        public Action<string>? AfterWrite { get; set; }
        public bool ThrowOnRestore { get; set; }
        public Exception? RestoreError { get; set; }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int RestoreCount { get; private set; }
        public CancellationToken RestoreToken { get; private set; }

        public string BuildSshCommand(Account account) => "ssh-" + account.Id;

        public Task<(string Name, string Email)> ReadIdentityAsync(string repositoryPath, CancellationToken ct = default)
        {
            ReadCount++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult((State.Name, State.Emails.UserEmail ?? string.Empty));
        }

        public Task<GitEmailConfigSnapshot> ReadEmailConfigAsync(string repositoryPath, CancellationToken ct = default)
        {
            ReadCount++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(State.Emails);
        }

        public Task<string> ReadSshCommandAsync(string repositoryPath, CancellationToken ct = default)
        {
            ReadCount++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(State.Ssh);
        }

        public Task<string> ReadCredentialHelperAsync(string repositoryPath, CancellationToken ct = default)
        {
            ReadCount++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(State.Helper);
        }

        public Task ApplyIdentityAsync(string repositoryPath, string gitName, string gitEmail, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            WriteCount++;
            State = State with { Name = gitName, Emails = new(gitEmail, gitEmail, gitEmail) };
            AfterWrite?.Invoke("identity");
            return Task.CompletedTask;
        }

        public Task ApplySshCommandAsync(string repositoryPath, string sshCommand, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            WriteCount++;
            State = State with { Ssh = sshCommand };
            AfterWrite?.Invoke("ssh");
            return Task.CompletedTask;
        }

        public Task ApplyCredentialHelperAsync(string repositoryPath, Guid? accountId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            WriteCount++;
            State = State with { Helper = accountId?.ToString() ?? string.Empty };
            AfterWrite?.Invoke("helper");
            return Task.CompletedTask;
        }

        public Task RestoreAsync(string repositoryPath, string? name, string? email, string? sshCommand,
            string? credentialHelper, CancellationToken ct = default, GitEmailConfigSnapshot? emailConfig = null)
        {
            RestoreCount++;
            RestoreToken = ct;
            ct.ThrowIfCancellationRequested();
            if (RestoreError is not null)
                throw RestoreError;
            if (ThrowOnRestore)
                throw new IOException("config restoration failed");
            State = new(name ?? string.Empty, emailConfig ?? new(email, null, null),
                sshCommand ?? string.Empty, credentialHelper ?? string.Empty);
            return Task.CompletedTask;
        }
    }
}
