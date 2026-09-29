using GitBinder.Application.Bindings;
using GitBinder.Application.Git;
using GitBinder.Application.Projects;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.GlobalMode;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>纯内存用例：不执行 Git、不读取真实凭据、不访问远程仓库。</summary>
public class BindingProtocolSwitchTests
{
    private const string HttpsUrl = "https://gitee.com/example/repository.git";
    private const string SshUrl = "git@gitee.com:example/repository.git";

    [Fact]
    public async Task Preview_HttpsToSsh_AfterRebinding_DoesNotWriteAnything()
    {
        var f = await Fixture.CreateAsync();
        var result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.True(result.IsSuccess);
        var change = Assert.IsType<BindingProtocolChange>(result.Value);
        Assert.Equal(f.Account.Id, change.EffectiveAccountId);
        Assert.Equal(HttpsUrl, change.CurrentUrl);
        Assert.Equal(SshUrl, change.TargetUrl);
        Assert.Equal(RemoteProtocol.Ssh, change.TargetProtocol);
        Assert.Equal(f.Project.RepositoryPath, change.RepositoryPath);
        Assert.Equal(0, f.Git.SetCount);
        Assert.Equal(0, f.Projects.UpdateCount);
        Assert.Equal(0, f.Config.Inner.ApplyIdentityCount);
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        // 取消弹窗就是不调用 Change；新账号绑定必须保留，地址保持不变。
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        Assert.Equal(HttpsUrl, f.Git.Inner.OriginUrl);
    }

    [Fact]
    public async Task Confirm_HttpsToSsh_UpdatesMetadataAndReappliesAlreadyBoundAccount()
    {
        var f = await Fixture.CreateAsync();
        f.Project.LastTestResult = "old failure";
        f.Project.LastTestAt = DateTimeOffset.UtcNow;
        var preview = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        var result = await f.Service.ChangeProtocolAsync(preview.Value!);
        Assert.True(result.IsSuccess);
        Assert.Equal(SshUrl, f.Git.Inner.OriginUrl);
        var stored = (await f.Projects.GetByIdAsync(f.Project.Id))!;
        Assert.Equal(SshUrl, stored.OriginUrl);
        Assert.Equal(RemoteProtocol.Ssh, stored.RemoteProtocol);
        Assert.Equal("gitee.com", stored.RemoteHost);
        Assert.Empty(stored.LastTestResult);
        Assert.Null(stored.LastTestAt);
        Assert.Contains(f.Account.SshPrivateKeyPath, f.Config.Inner.AppliedSshCommand);
        Assert.Equal((f.Account.GitName, f.Account.GitEmail), f.Config.Inner.AppliedIdentity);
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        Assert.NotNull(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
    }

    [Fact]
    public async Task Confirm_SshToHttps_UsesOnlyTheEffectiveAccountsHelper()
    {
        var f = await Fixture.CreateAsync(AuthenticationType.Https, SshUrl);
        var preview = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.Equal(HttpsUrl, preview.Value!.TargetUrl);
        Assert.True((await f.Service.ChangeProtocolAsync(preview.Value)).IsSuccess);
        Assert.Equal(f.Account.Id, f.Config.Inner.AppliedCredentialAccountId);
        Assert.Empty(f.Config.Inner.AppliedSshCommand);
        Assert.Equal(RemoteProtocol.Https, (await f.Projects.GetByIdAsync(f.Project.Id))!.RemoteProtocol);
    }

    [Theory]
    [InlineData(HttpsUrl)]
    [InlineData(SshUrl)]
    public async Task Preview_BothCredentials_KeepsCurrentProtocol(string origin)
    {
        var f = await Fixture.CreateAsync(AuthenticationType.Both, origin);
        var result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(0, f.Git.SetCount);
    }

    [Fact]
    public async Task Preview_UsesRealOrigin_NotCachedProtocol()
    {
        var f = await Fixture.CreateAsync();
        f.Project.OriginUrl = SshUrl;
        f.Project.RemoteProtocol = RemoteProtocol.Ssh;
        var result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.Equal(HttpsUrl, result.Value!.CurrentUrl);
        Assert.Equal(SshUrl, f.Project.OriginUrl); // 预览不偷偷刷新缓存。
    }

    [Fact]
    public async Task Preview_GlobalModeTakesPrecedenceOverProjectBinding()
    {
        var f = await Fixture.CreateAsync();
        var global = new Account
        {
            Alias = "global", AuthenticationType = AuthenticationType.Https,
            HttpsSecretId = "test-global-secret", GitName = "Global",
        };
        await f.Accounts.AddAsync(global);
        f.Global.IsEnabled = true;
        f.Global.GlobalAccountId = global.Id;
        var result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value); // 绑定是 SSH，但全局 HTTPS 账号已匹配当前地址。

        global.AuthenticationType = AuthenticationType.Ssh;
        global.SshPrivateKeyPath = "X:/protocol-fixture/global-key";
        result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.Equal(global.Id, result.Value!.EffectiveAccountId);
        Assert.True((await f.Service.ChangeProtocolAsync(result.Value)).IsSuccess);
        Assert.Contains("global-key", f.Config.Inner.AppliedSshCommand);
    }

    [Fact]
    public async Task Preview_UnboundProject_UsesDefaultAccount()
    {
        var f = await Fixture.CreateAsync();
        await f.Bindings.DeleteByProjectIdAsync(f.Project.Id);
        f.Account.IsDefault = true;
        Assert.Equal(f.Account.Id, (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!.EffectiveAccountId);
    }

    [Theory]
    [InlineData(HttpsUrl, "TRANSFER_HTTPS_REQUIRED")]
    [InlineData(SshUrl, "TRANSFER_SSH_REQUIRED")]
    public async Task Preview_NoConfiguredCredential_ReturnsReadableProtocolError(string origin, string error)
    {
        var f = await Fixture.CreateAsync(AuthenticationType.None, origin);
        var result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.Equal(error, result.Error!.Code);
        Assert.Equal(0, f.Git.SetCount);
    }

    [Theory]
    [InlineData("https://user:password@gitee.com/example/repository.git")]
    [InlineData("https://gitee.com:8443/example/repository.git")]
    [InlineData("https://gitee.com/example/repository.git?token=private")]
    [InlineData("http://gitee.com/example/repository.git")]
    [InlineData("ext::unexpected-command")]
    public async Task Preview_NonstandardOrSensitiveOrigin_RejectsWithoutEchoingUrl(string origin)
    {
        var f = await Fixture.CreateAsync(AuthenticationType.Ssh, origin);
        var result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.Equal("PROJECT_PROTOCOL_SWITCH_UNSUPPORTED", result.Error!.Code);
        Assert.Null(result.Value);
        Assert.Equal(0, f.Git.SetCount);
    }

    [Fact]
    public async Task Preview_OriginReadFailure_PreservesCachedMetadata()
    {
        var f = await Fixture.CreateAsync();
        f.Git.Inner.OriginReadFails = true;
        var result = await f.Service.GetProtocolChangeAsync(f.Project.Id);
        Assert.Equal("PROJECT_METADATA_READ_FAILED", result.Error!.Code);
        Assert.Equal(HttpsUrl, f.Project.OriginUrl);
        Assert.Equal(0, f.Projects.UpdateCount);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("path")]
    [InlineData("account")]
    [InlineData("target")]
    [InlineData("already-switched")]
    public async Task Confirm_StaleOrAlteredPreview_DoesNotWrite(string changed)
    {
        var f = await Fixture.CreateAsync();
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        if (changed == "origin") f.Git.Inner.OriginUrl = "https://gitee.com/other/project.git";
        if (changed == "path") f.Project.RepositoryPath = "X:/protocol-fixture/moved";
        if (changed == "target") preview = preview with { TargetUrl = "git@evil.example:other/repo.git" };
        if (changed == "already-switched") f.Git.Inner.OriginUrl = SshUrl;
        if (changed == "account")
        {
            var other = new Account { AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = "X:/other-key" };
            await f.Accounts.AddAsync(other);
            (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId = other.Id;
        }
        var result = await f.Service.ChangeProtocolAsync(preview);
        Assert.Equal("PROJECT_PROTOCOL_SWITCH_STALE", result.Error!.Code);
        Assert.Equal(0, f.Git.SetCount);
        Assert.Equal(0, f.Config.Inner.ApplyIdentityCount);
    }

    [Fact]
    public async Task Confirm_ConfigFailure_RestoresOriginCacheAndKeepsNewAccountWithoutFallback()
    {
        var f = await Fixture.CreateAsync();
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        f.Config.FailNextSshApply = true;
        var result = await f.Service.ChangeProtocolAsync(preview);
        Assert.Equal("PROJECT_PROTOCOL_SWITCH_FAILED", result.Error!.Code);
        Assert.Equal(HttpsUrl, f.Git.Inner.OriginUrl);
        Assert.Equal(HttpsUrl, (await f.Projects.GetByIdAsync(f.Project.Id))!.OriginUrl);
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        Assert.Equal(1, f.Config.Inner.ApplyCredentialHelperCount);
        Assert.Null(f.Config.Inner.AppliedCredentialAccountId); // SSH-only B 不会恢复 A 的 HTTPS 凭据。
        Assert.Equal(f.Account.GitName, f.Config.Inner.AppliedIdentity.Name);
    }

    [Fact]
    public async Task Confirm_MetadataWriteFailure_RollsBackOriginAndOriginalCache()
    {
        var f = await Fixture.CreateAsync();
        f.Project.RemoteProtocol = RemoteProtocol.Unknown;
        f.Project.RemoteHost = string.Empty;
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        f.Projects.FailNextUpdate = true;
        var result = await f.Service.ChangeProtocolAsync(preview);
        Assert.Equal("PROJECT_PROTOCOL_SWITCH_FAILED", result.Error!.Code);
        Assert.Equal(HttpsUrl, f.Git.Inner.OriginUrl);
        var restored = (await f.Projects.GetByIdAsync(f.Project.Id))!;
        Assert.Equal(RemoteProtocol.Unknown, restored.RemoteProtocol);
        Assert.Empty(restored.RemoteHost);
        Assert.Equal(1, f.Config.Inner.ApplyCredentialHelperCount); // 仍按真实 HTTPS 恢复配置。
    }

    [Fact]
    public async Task Confirm_RollbackFailure_IsReportedExplicitly()
    {
        var f = await Fixture.CreateAsync();
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        f.Config.FailNextSshApply = true;
        f.Git.FailSetCall = 2;
        var result = await f.Service.ChangeProtocolAsync(preview);
        Assert.Equal("PROJECT_PROTOCOL_SWITCH_ROLLBACK_FAILED", result.Error!.Code);
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        Assert.Equal(SshUrl, f.Git.Inner.OriginUrl);
        Assert.Contains(f.Account.SshPrivateKeyPath, f.Config.Inner.AppliedSshCommand);
    }

    [Fact]
    public async Task Confirm_PreservesOriginalUnbindSnapshot()
    {
        var f = await Fixture.CreateAsync();
        var originalSnapshot = new RepositorySnapshot { ProjectId = f.Project.Id, UserName = "before-first-bind" };
        await f.Snapshots.SaveAsync(originalSnapshot);
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        Assert.True((await f.Service.ChangeProtocolAsync(preview)).IsSuccess);
        Assert.Same(originalSnapshot, await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
    }

    [Fact]
    public async Task Confirm_CancelledAfterOriginWrite_StillRollsBack()
    {
        var f = await Fixture.CreateAsync();
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        using var cancellation = new CancellationTokenSource();
        f.Git.AfterSet = count => { if (count == 1) cancellation.Cancel(); };
        var result = await f.Service.ChangeProtocolAsync(preview, cancellation.Token);
        Assert.Equal("PROJECT_PROTOCOL_SWITCH_FAILED", result.Error!.Code);
        Assert.Equal(HttpsUrl, f.Git.Inner.OriginUrl);
        Assert.Equal(HttpsUrl, (await f.Projects.GetByIdAsync(f.Project.Id))!.OriginUrl);
        Assert.Equal(2, f.Git.SetCount);
    }

    [Theory]
    [InlineData("https://gitee.com/changed/by-ide.git")]
    [InlineData("git@gitee.com:changed/by-ide.git")]
    public async Task Confirm_ConcurrentOriginChange_IsNotOverwrittenDuringRollback(string concurrentUrl)
    {
        var f = await Fixture.CreateAsync();
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        f.Git.AfterSet = count => { if (count == 1) f.Git.Inner.OriginUrl = concurrentUrl; };

        var result = await f.Service.ChangeProtocolAsync(preview);

        Assert.Equal("PROJECT_PROTOCOL_SWITCH_ROLLBACK_FAILED", result.Error!.Code);
        Assert.Equal(concurrentUrl, f.Git.Inner.OriginUrl);
        Assert.Equal(1, f.Git.SetCount);
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        if (concurrentUrl.StartsWith("git@", StringComparison.Ordinal))
            Assert.Contains(f.Account.SshPrivateKeyPath, f.Config.Inner.AppliedSshCommand);
        else
        {
            Assert.Equal(1, f.Config.Inner.ApplyCredentialHelperCount);
            Assert.Null(f.Config.Inner.AppliedCredentialAccountId);
        }
    }

    [Fact]
    public async Task Confirm_OriginBecomesUnreadable_DoesNotBlindlyOverwriteIt()
    {
        var f = await Fixture.CreateAsync();
        var preview = (await f.Service.GetProtocolChangeAsync(f.Project.Id)).Value!;
        f.Git.AfterSet = count => { if (count == 1) f.Git.Inner.OriginReadFails = true; };

        var result = await f.Service.ChangeProtocolAsync(preview);

        Assert.Equal("PROJECT_PROTOCOL_SWITCH_ROLLBACK_FAILED", result.Error!.Code);
        Assert.Equal(1, f.Git.SetCount);
        Assert.Equal(SshUrl, f.Git.Inner.OriginUrl);
        Assert.Equal(1, f.Config.Inner.ApplyCredentialHelperCount);
        Assert.Null(f.Config.Inner.AppliedCredentialAccountId);
        Assert.Contains("IdentityAgent=none", f.Config.Inner.AppliedSshCommand);
        Assert.Contains("BatchMode=yes", f.Config.Inner.AppliedSshCommand);
        Assert.DoesNotContain(f.Account.SshPrivateKeyPath, f.Config.Inner.AppliedSshCommand);
    }

    private sealed class Fixture
    {
        public FakeAccountRepository Accounts { get; } = new();
        public FailingProjectRepository Projects { get; } = new();
        public FakeBindingRepository Bindings { get; } = new();
        public FakeSnapshotRepository Snapshots { get; } = new();
        public TrackingGitService Git { get; } = new();
        public FailingConfigApplier Config { get; } = new();
        public FakeGlobalModeState Global { get; } = new();
        public Account Account { get; } = new()
        {
            Alias = "B", GitName = "Account B", GitEmail = "b@example.com",
            SshPrivateKeyPath = "X:/protocol-fixture/b-key", HttpsSecretId = "test-b-secret",
        };
        public Project Project { get; } = new() { RepositoryPath = "X:/protocol-fixture/repository" };
        public BindingService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(AuthenticationType type = AuthenticationType.Ssh, string origin = HttpsUrl)
        {
            var f = new Fixture();
            f.Account.AuthenticationType = type;
            f.Project.OriginUrl = origin;
            f.Git.Inner.OriginUrl = origin;
            (f.Project.RemoteProtocol, f.Project.RemoteHost) = f.Git.ParseRemote(origin);
            await f.Accounts.AddAsync(f.Account);
            await f.Projects.AddAsync(f.Project);
            await f.Bindings.AddAsync(new Binding { ProjectId = f.Project.Id, AccountId = f.Account.Id });
            f.Service = new BindingService(f.Bindings, f.Accounts, f.Projects, new FakeSecretStore(),
                f.Git, f.Config, f.Snapshots, new FakeGitTransfer(),
                new EffectiveAccountResolver(f.Global, f.Bindings, f.Accounts));
            return f;
        }
    }

    private sealed class TrackingGitService : IGitService
    {
        public FakeGitService Inner { get; } = new();
        public int SetCount { get; private set; }
        public int FailSetCall { get; set; }
        public Action<int>? AfterSet { get; set; }
        public Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default) => Inner.ValidateRepositoryAsync(path, ct);
        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => Inner.GetRepositoryRootAsync(path, ct);
        public Task<string?> GetGitDirAsync(string path, CancellationToken ct = default) => Inner.GetGitDirAsync(path, ct);
        public Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Inner.GetOriginUrlAsync(path, ct);
        }
        public Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default) => Inner.GetCurrentBranchAsync(path, ct);
        public Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default) => GetOriginUrlAsync(path, ct);
        public Task<Result> SetTransferOriginUrlAsync(string path, string origin, CancellationToken ct = default)
            => SetOriginUrlAsync(path, origin, ct);
        public (RemoteProtocol Protocol, string Host) ParseRemote(string url) => Inner.ParseRemote(url);
        public Task<string?> GetGitVersionAsync(CancellationToken ct = default) => Inner.GetGitVersionAsync(ct);
        public async Task<Result> SetOriginUrlAsync(string path, string origin, CancellationToken ct = default)
        {
            SetCount++;
            if (SetCount == FailSetCall)
                return Result.Failure(new DomainError("PROJECT_REMOTE_UPDATE_FAILED"));
            var result = await Inner.SetOriginUrlAsync(path, origin, ct);
            AfterSet?.Invoke(SetCount);
            return result;
        }
    }

    private sealed class FailingConfigApplier : IGitConfigApplier
    {
        public FakeGitConfigApplier Inner { get; } = new();
        public bool FailNextSshApply { get; set; }
        public string BuildSshCommand(Account account) => Inner.BuildSshCommand(account);
        public Task<(string Name, string Email)> ReadIdentityAsync(string path, CancellationToken ct = default) => Inner.ReadIdentityAsync(path, ct);
        public Task<GitEmailConfigSnapshot> ReadEmailConfigAsync(string path, CancellationToken ct = default) => Inner.ReadEmailConfigAsync(path, ct);
        public Task<string> ReadSshCommandAsync(string path, CancellationToken ct = default) => Inner.ReadSshCommandAsync(path, ct);
        public Task<string> ReadCredentialHelperAsync(string path, CancellationToken ct = default) => Inner.ReadCredentialHelperAsync(path, ct);
        public Task ApplyIdentityAsync(string path, string name, string email, CancellationToken ct = default) => Inner.ApplyIdentityAsync(path, name, email, ct);
        public Task ApplyCredentialHelperAsync(string path, Guid? accountId, CancellationToken ct = default) => Inner.ApplyCredentialHelperAsync(path, accountId, ct);
        public Task RestoreAsync(string path, string? name, string? email, string? ssh, string? helper, CancellationToken ct = default, GitEmailConfigSnapshot? emailConfig = null)
            => Inner.RestoreAsync(path, name, email, ssh, helper, ct, emailConfig);
        public Task ApplySshCommandAsync(string path, string sshCommand, CancellationToken ct = default)
        {
            if (FailNextSshApply)
            {
                FailNextSshApply = false;
                throw new InvalidOperationException("Simulated local config failure.");
            }
            return Inner.ApplySshCommandAsync(path, sshCommand, ct);
        }
    }

    private sealed class FailingProjectRepository : IProjectRepository
    {
        private readonly FakeProjectRepository _inner = new();
        public int UpdateCount { get; private set; }
        public bool FailNextUpdate { get; set; }
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default) => _inner.GetAllAsync(ct);
        public Task<Project?> FindByPathAsync(string path, CancellationToken ct = default) => _inner.FindByPathAsync(path, ct);
        public Task AddAsync(Project project, CancellationToken ct = default) => _inner.AddAsync(project, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
        public Task UpdateAsync(Project project, CancellationToken ct = default)
        {
            UpdateCount++;
            if (FailNextUpdate)
            {
                FailNextUpdate = false;
                throw new InvalidOperationException("Simulated metadata write failure.");
            }
            return _inner.UpdateAsync(project, ct);
        }
    }
}
