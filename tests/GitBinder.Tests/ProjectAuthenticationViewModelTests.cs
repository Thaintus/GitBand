using System.Globalization;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Projects;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>协议确认流程仅使用内存后端和专属临时目录，不启动窗口或访问真实 Git。</summary>
public sealed class ProjectAuthenticationViewModelTests
{
    private const string HttpsUrl = "https://gitee.com/team/demo.git";
    private const string SshUrl = "git@gitee.com:team/demo.git";

    [Fact]
    public async Task SwitchAccount_CancelProtocolChangeRetainsNewBindingWithoutChangingOrigin()
    {
        using var f = await Fixture.CreateAsync();
        var originalItem = Assert.Single(f.ViewModel.Items);

        await f.ViewModel.SwitchBindingAccountAsync(originalItem, f.SshAccount);

        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(f.SshAccount.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        var item = Assert.Single(f.ViewModel.Items);
        Assert.Equal(f.SshAccount.Id, item.BoundAccount!.Id);
        Assert.Equal(f.SshAccount.Id, item.SelectedBindingAccount!.Id);
        Assert.Equal("HTTPS", item.ProtocolText);
        Assert.Equal(HttpsUrl, f.Git.OriginUrl);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.True(f.ViewModel.CanManageProjects);
        Assert.False(originalItem.IsSwitchingBinding);
    }

    [Fact]
    public async Task SwitchAccount_ConfirmChangesOriginAndRefreshesCardWithoutConnecting()
    {
        using var f = await Fixture.CreateAsync();
        f.Confirm = () => Task.FromResult(true);

        await f.ViewModel.SwitchBindingAccountAsync(Assert.Single(f.ViewModel.Items), f.SshAccount);

        Assert.Equal(1, f.ConfirmationCount);
        Assert.Contains(f.SshAccount.DisplayAlias, f.LastConfirmation);
        Assert.Contains(HttpsUrl, f.LastConfirmation);
        Assert.Contains(SshUrl, f.LastConfirmation);
        Assert.Equal(SshUrl, f.Git.LastSetOriginUrl);
        var item = Assert.Single(f.ViewModel.Items);
        Assert.Equal("SSH", item.ProtocolText);
        Assert.Equal(f.SshAccount.Id, item.BoundAccount!.Id);
        Assert.Contains(f.SshAccount.SshPrivateKeyPath, f.Config.AppliedSshCommand);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
    }

    [Fact]
    public async Task Test_CancelProtocolChangeDoesNotConnect()
    {
        using var f = await Fixture.CreateAsync(boundToSsh: true);

        await f.RunOperationAsync(pull: false);

        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(HttpsUrl, f.Git.OriginUrl);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.True(f.ViewModel.CanManageProjects);
        Assert.Empty(f.ViewModel.OperationProgress);
    }

    [Fact]
    public async Task Test_ConfirmationUpdatesOriginAndCardBeforeConnecting()
    {
        using var f = await Fixture.CreateAsync(boundToSsh: true);
        f.Confirm = () => Task.FromResult(true);
        string? protocolAtConnection = null;
        string? originAtConnection = null;
        Task<Result> OnConnection(string path, Account account, CancellationToken ct)
        {
            protocolAtConnection = Assert.Single(f.ViewModel.Items).ProtocolText;
            originAtConnection = f.Git.OriginUrl;
            return Task.FromResult(Result.Success());
        }
        f.Transfer.OnTest = OnConnection;
        f.Transfer.OnPull = OnConnection;

        await f.RunOperationAsync(pull: false);

        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(SshUrl, originAtConnection);
        Assert.Equal("SSH", protocolAtConnection);
        Assert.Equal(f.SshAccount.Id, f.Transfer.UsedAccountId);
        Assert.Equal(1, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.True(f.ViewModel.CanManageProjects);
        Assert.Contains("Test.Success", f.ViewModel.Feedback);
    }

    [Fact]
    public async Task Test_GlobalAccountDeterminesConfirmationAndAuthenticationNotBoundAccount()
    {
        using var f = await Fixture.CreateAsync();
        f.Global.IsEnabled = true;
        f.Global.GlobalAccountId = f.SshAccount.Id;
        f.Confirm = () => Task.FromResult(true);
        await f.ViewModel.LoadAsync();

        await f.RunOperationAsync(pull: false);

        Assert.Equal(1, f.ConfirmationCount);
        Assert.Contains(f.SshAccount.DisplayAlias, f.LastConfirmation);
        Assert.Equal(SshUrl, f.Git.OriginUrl);
        Assert.Equal(f.SshAccount.Id, f.Transfer.UsedAccountId);
        Assert.Equal(f.HttpsAccount.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        var item = Assert.Single(f.ViewModel.Items);
        Assert.Equal(f.HttpsAccount.Id, item.BoundAccount!.Id);
        Assert.Equal(f.SshAccount.Id, item.EffectiveAccount!.Id);
        Assert.Equal(1, f.Transfer.TestCount);
    }

    [Fact]
    public async Task Test_AccountWithBothCredentialsKeepsCurrentProtocolWithoutConfirmation()
    {
        using var f = await Fixture.CreateAsync(boundToSsh: true);
        f.SshAccount.AuthenticationType = AuthenticationType.Both;
        f.SshAccount.HttpsSecretId = "fake-b-https-secret-id";

        await f.RunOperationAsync(pull: false);

        Assert.Equal(0, f.ConfirmationCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Equal(HttpsUrl, f.Git.OriginUrl);
        Assert.Equal("HTTPS", Assert.Single(f.ViewModel.Items).ProtocolText);
        Assert.Equal(f.SshAccount.Id, f.Transfer.UsedAccountId);
        Assert.Equal(1, f.Transfer.TestCount);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
    }

    [Fact]
    public async Task Test_OriginChangesDuringConfirmationRejectsStalePreviewWithoutConnection()
    {
        using var f = await Fixture.CreateAsync(boundToSsh: true);
        const string externallyChangedUrl = "https://gitee.com/team/another.git";
        f.Confirm = () =>
        {
            f.Git.OriginUrl = externallyChangedUrl;
            return Task.FromResult(true);
        };

        await f.RunOperationAsync(pull: false);

        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(externallyChangedUrl, f.Git.OriginUrl);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Equal("PROJECT_PROTOCOL_SWITCH_STALE", f.ViewModel.Feedback);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.True(f.ViewModel.CanManageProjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pull_LeavesOriginUnchangedAndUsesEffectiveAccountWithoutProtocolConfirmation(bool globalMode)
    {
        using var f = await Fixture.CreateAsync(boundToSsh: !globalMode);
        f.Global.IsEnabled = globalMode;
        f.Global.GlobalAccountId = globalMode ? f.SshAccount.Id : null;
        f.Confirm = () => throw new InvalidOperationException("Pull must not convert origin before resolving its tracking remote");
        f.Transfer.OnPull = (_, _, _) => Task.FromResult(Result.Failure(new DomainError("TRANSFER_HTTPS_REQUIRED")));

        await f.RunOperationAsync(pull: true);

        Assert.Equal(0, f.ConfirmationCount);
        Assert.Equal(HttpsUrl, f.Git.OriginUrl);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Equal(1, f.Transfer.PullCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(f.SshAccount.Id, f.Transfer.UsedAccountId);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.Contains("TRANSFER_HTTPS_REQUIRED", f.ViewModel.Feedback);
        Assert.False(f.ViewModel.HasPendingOperations);
    }

    [Fact]
    public async Task SwitchAccount_RepeatedSelectionRestoresAcceptedChoiceUntilBindingFinishes()
    {
        using var f = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Confirm = () => { entered.TrySetResult(); return release.Task; };
        var item = Assert.Single(f.ViewModel.Items);
        var task = f.ViewModel.SwitchBindingAccountAsync(item, f.SshAccount);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            item.SelectedBindingAccount = f.HttpsAccount;
            await f.ViewModel.SwitchBindingAccountAsync(item, f.HttpsAccount);
            Assert.Equal(f.SshAccount.Id, item.SelectedBindingAccount!.Id);
            Assert.True(item.IsBusy);
            Assert.True(item.IsSwitchingBinding);
            Assert.Equal(1, f.ConfirmationCount);
            release.TrySetResult(false);
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(f.SshAccount.Id, item.BoundAccount!.Id);
            Assert.Equal(f.SshAccount.Id, item.SelectedBindingAccount!.Id);
            Assert.False(item.IsBusy);
        }
        finally
        {
            release.TrySetResult(false);
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-auth-ui-" + Guid.NewGuid().ToString("N"));
        public FakeAccountRepository Accounts { get; } = new();
        public FakeProjectRepository Projects { get; } = new();
        public FakeBindingRepository Bindings { get; } = new();
        public FakeGitService Git { get; } = new() { OriginUrl = HttpsUrl };
        public FakeGitConfigApplier Config { get; } = new();
        public FakeGitTransfer Transfer { get; } = new();
        public FakeGlobalModeState Global { get; } = new();
        public Account HttpsAccount { get; } = new()
        {
            Alias = "A HTTPS", GitName = "A", GitEmail = "a@example.com",
            AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fake-a-secret-id",
        };
        public Account SshAccount { get; } = new()
        {
            Alias = "B SSH", GitName = "B", GitEmail = "b@example.com", AuthenticationType = AuthenticationType.Ssh,
        };
        public Project Project { get; } = new()
        {
            Name = "demo", OriginUrl = HttpsUrl, RemoteProtocol = RemoteProtocol.Https, RemoteHost = "gitee.com",
        };
        public ProjectsViewModel ViewModel { get; private set; } = null!;
        public int ConfirmationCount { get; private set; }
        public string LastConfirmation { get; private set; } = string.Empty;
        public Func<Task<bool>> Confirm { get; set; } = () => Task.FromResult(false);

        public static async Task<Fixture> CreateAsync(bool boundToSsh = false)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f._root);
            f.Project.RepositoryPath = f.Project.CanonicalPath = f._root;
            f.Git.RepositoryRoot = f._root;
            f.SshAccount.SshPrivateKeyPath = Path.Combine(f._root, "not-a-real-key");
            await f.Accounts.AddAsync(f.HttpsAccount);
            await f.Accounts.AddAsync(f.SshAccount);
            await f.Projects.AddAsync(f.Project);
            await f.Bindings.AddAsync(new Binding
            {
                ProjectId = f.Project.Id, AccountId = boundToSsh ? f.SshAccount.Id : f.HttpsAccount.Id,
            });
            var resolver = new EffectiveAccountResolver(f.Global, f.Bindings, f.Accounts);
            var bindingService = new BindingService(f.Bindings, f.Accounts, f.Projects, new FakeSecretStore(),
                f.Git, f.Config, new FakeSnapshotRepository(), f.Transfer, resolver);
            var paths = new IdentityPathNormalizer();
            var projectService = new ProjectService(f.Projects, f.Git, paths, f.Accounts, bindingService);
            var groups = new FakeProjectGroupRepository();
            var transferService = new ProjectTransferService(f.Transfer, projectService, f.Projects,
                f.Accounts, bindingService, resolver, paths, groups);
            f.ViewModel = new ProjectsViewModel(f.Projects, projectService, f.Bindings, f.Accounts, bindingService,
                resolver, new TestLocalization(), () => throw new InvalidOperationException("No picker in authentication tests."),
                transferService, new ProjectGroupService(groups), (_, message, _) =>
                {
                    f.ConfirmationCount++;
                    f.LastConfirmation = message;
                    return f.Confirm();
                });
            await f.ViewModel.LoadAsync();
            return f;
        }

        public Task RunOperationAsync(bool pull)
            => pull ? ViewModel.PullCommand.ExecuteAsync(Assert.Single(ViewModel.Items))
                : ViewModel.TestBindingCommand.ExecuteAsync(Assert.Single(ViewModel.Items));

        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class IdentityPathNormalizer : IPathNormalizer
    {
        public string Normalize(string path) => path;
        public string Canonicalize(string path) => path;
        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        public string ToKey(string path) => path.ToUpperInvariant();
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.InvariantCulture;
        public IReadOnlyList<CultureInfo> SupportedCultures { get; } = [CultureInfo.InvariantCulture];
        public event EventHandler? CultureChanged;
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args)
            => args.Length == 0 ? key : key + " | " + string.Join(" | ", args);
        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
