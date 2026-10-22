using System.Globalization;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.Projects;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>本地目录状态只使用内存 Git 和独立空目录，不启动 Git、窗口或网络请求</summary>
public sealed class LocalProjectViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_LocalDirectoryShowsLocalStateWithoutAnEffectiveAccount(bool globalMode)
    {
        using var f = await Fixture.CreateAsync(globalMode: globalMode);

        var item = Assert.Single(f.ViewModel.Items);

        Assert.Same(item, Assert.Single(f.ViewModel.FilteredItems));
        Assert.True(f.ViewModel.HasItems);
        Assert.True(item.IsLocalDirectory);
        Assert.False(item.HasGitRepository);
        Assert.False(item.HasRemote);
        Assert.True(item.CanBindAccount);
        Assert.False(item.CanTest);
        Assert.False(item.CanPull);
        Assert.False(item.HasBinding);
        Assert.Null(item.EffectiveAccount);
        Assert.Equal("Projects.LocalDirectory", item.ProtocolLabel);
        Assert.Equal("Projects.LocalDirectory", item.EffectiveReasonLabel);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
    }

    [Fact]
    public async Task Load_NewlyInitializedGitEnablesBindingAndPullPreflightWithoutOrigin()
    {
        using var f = await Fixture.CreateAsync();
        Assert.True(Assert.Single(f.ViewModel.Items).IsLocalDirectory);
        f.Git.ValidateResult = true;

        await f.ViewModel.LoadAsync();

        var item = Assert.Single(f.ViewModel.Items);
        Assert.False(item.IsLocalDirectory);
        Assert.True(item.HasGitRepository);
        Assert.True(item.CanBindAccount);
        Assert.False(item.HasRemote);
        Assert.False(item.CanTest);
        Assert.True(item.CanPull);
        Assert.False(item.HasBinding);
        Assert.Null(item.SelectedBindingAccount);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.ConfirmationCount);
        Assert.Equal(0, f.Git.InitializeCount);
    }

    [Fact]
    public async Task Load_NewlyConfiguredRemoteEnablesPullAndEnablesTestOnlyAfterBinding()
    {
        using var f = await Fixture.CreateAsync();
        f.Git.ValidateResult = true;
        f.Git.OriginUrl = "git@github.com:team/local-only.git";

        await f.ViewModel.LoadAsync();

        var item = Assert.Single(f.ViewModel.Items);
        Assert.True(item.HasGitRepository);
        Assert.True(item.HasRemote);
        Assert.True(item.CanBindAccount);
        Assert.True(item.CanPull);
        Assert.False(item.CanTest);
        Assert.Equal("SSH", item.ProtocolText);

        await f.ViewModel.SwitchBindingAccountAsync(item, f.Account);

        var boundItem = Assert.Single(f.ViewModel.Items);
        Assert.True(boundItem.HasBinding);
        Assert.True(boundItem.CanTest);
        Assert.True(boundItem.CanPull);
        Assert.Equal(f.Account.Id, boundItem.BoundAccount!.Id);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Transfer.TestCount);
    }

    [Fact]
    public async Task SwitchBinding_CancelInitializationRestoresSelectionWithoutWritingGitOrBinding()
    {
        using var f = await Fixture.CreateAsync();
        var item = Assert.Single(f.ViewModel.Items);
        await f.ViewModel.SwitchBindingAccountAsync(item, f.Account);

        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(item.SelectedBindingAccount);
        Assert.True(Assert.Single(f.ViewModel.Items).IsLocalDirectory);
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Equal(0, f.CountingGit.TransferOriginReadCount);
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.Equal(0, f.Config.ApplySshCommandCount);
        Assert.Equal(0, f.Config.ApplyCredentialHelperCount);
        Assert.Equal(0, f.Config.RestoreCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal("Projects.InitializeGitConfirmTitle", f.LastConfirmationTitle);
        Assert.Contains("Projects.InitializeGitConfirmMessage", f.LastConfirmationMessage);
        Assert.Contains(f.Project.RepositoryPath, f.LastConfirmationMessage);
        Assert.Equal("Projects.InitializeGitConfirm", f.LastConfirmationAction);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.False(item.IsSwitchingBinding);
        Assert.True(f.ViewModel.CanManageProjects);
    }

    [Fact]
    public async Task SwitchBinding_InitializationFailureKeepsLocalDirectoryWithoutBindingOrSnapshot()
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        f.Git.InitializeSucceeds = false;
        var originalItem = Assert.Single(f.ViewModel.Items);

        await f.ViewModel.SwitchBindingAccountAsync(originalItem, f.Account);

        var item = Assert.Single(f.ViewModel.Items);
        Assert.StartsWith("PROJECT_GIT_INITIALIZATION_FAILED", f.ViewModel.Feedback);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.True(item.IsLocalDirectory);
        Assert.Null(item.SelectedBindingAccount);
        Assert.Null(item.EffectiveAccount);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.Equal(0, f.Config.ApplySshCommandCount);
        Assert.Equal(0, f.Config.ApplyCredentialHelperCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.True(Directory.Exists(f.Project.RepositoryPath));
        Assert.False(originalItem.IsSwitchingBinding);
        Assert.True(f.ViewModel.CanManageProjects);
    }

    [Fact]
    public async Task SwitchBinding_ConfirmInitializationSavesIdentityAndKeepsRemoteOperationsDisabled()
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        var originalItem = Assert.Single(f.ViewModel.Items);

        await f.ViewModel.SwitchBindingAccountAsync(originalItem, f.Account);

        var item = Assert.Single(f.ViewModel.Items);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.Equal(f.Project.RepositoryPath, f.Git.LastInitializedPath);
        Assert.Equal(f.Project.Id, item.Project.Id);
        Assert.Equal(f.Project.Name, item.Name);
        Assert.True(item.HasGitRepository);
        Assert.False(item.IsLocalDirectory);
        Assert.True(item.HasBinding);
        Assert.Equal(f.Account.Id, item.SelectedBindingAccount!.Id);
        Assert.Equal(f.Account.Id, item.EffectiveAccount!.Id);
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        Assert.NotNull(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(1, f.Config.ApplyIdentityCount);
        Assert.False(item.HasRemote);
        Assert.False(item.CanTest);
        Assert.True(item.CanPull);
        Assert.Equal(0, f.CountingGit.TransferOriginReadCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Empty(f.ViewModel.Feedback);
        Assert.False(originalItem.IsSwitchingBinding);
        Assert.True(f.ViewModel.CanManageProjects);
    }

    [Fact]
    public async Task SwitchBinding_BindingPersistenceExceptionRefreshesInitializedGitAndKeepsBindingError()
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        f.BindingRepository.ThrowOnAdd = true;
        var originalItem = Assert.Single(f.ViewModel.Items);

        await f.ViewModel.SwitchBindingAccountAsync(originalItem, f.Account);

        var item = Assert.Single(f.ViewModel.Items);
        Assert.Same(originalItem, item);
        Assert.StartsWith("BINDING_SAVE_FAILED", f.ViewModel.Feedback);
        Assert.True(item.HasGitRepository);
        Assert.False(item.IsLocalDirectory);
        Assert.False(item.HasBinding);
        Assert.Null(item.SelectedBindingAccount);
        Assert.False((await f.Projects.GetByIdAsync(f.Project.Id))!.IsLocalDirectory);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.NotNull(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(1, f.BindingRepository.AddFailureCount);
        Assert.Equal(1, f.Config.RestoreCount);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.Equal(1, f.Config.ApplyIdentityCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.False(originalItem.IsSwitchingBinding);
        Assert.True(f.ViewModel.CanManageProjects);
    }

    [Fact]
    public async Task RemoteEditor_OpenAndCancelDoNotInitializeLocalDirectory()
    {
        using var f = await Fixture.CreateAsync();
        var item = Assert.Single(f.ViewModel.Items);

        f.ViewModel.StartEditRemoteCommand.Execute(item);
        Assert.True(item.IsRemoteEditorOpen);
        item.EditingOriginUrl = "https://example.org/team/local-only.git";
        f.ViewModel.CancelEditRemoteCommand.Execute(item);

        Assert.False(item.IsRemoteEditorOpen);
        Assert.Empty(item.EditingOriginUrl);
        Assert.True(item.IsLocalDirectory);
        Assert.Equal(0, f.ConfirmationCount);
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Null(f.Git.LastSetOriginUrl);
    }

    [Theory]
    [InlineData("", "PROJECT_REMOTE_URL_REQUIRED")]
    [InlineData("   ", "PROJECT_REMOTE_URL_REQUIRED")]
    [InlineData("not-a-repository-url", "PROJECT_REMOTE_URL_INVALID")]
    [InlineData("file:///some/local/repository", "PROJECT_REMOTE_URL_INVALID")]
    [InlineData("https://placeholder:placeholder@example.org/team/local-only.git", "PROJECT_REMOTE_CREDENTIAL_FORBIDDEN")]
    public async Task SaveRemote_InvalidAddressDoesNotInitializeOrSaveBinding(string origin, string error)
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        var item = Assert.Single(f.ViewModel.Items);
        f.ViewModel.StartEditRemoteCommand.Execute(item);
        item.EditingOriginUrl = origin;

        await f.ViewModel.SaveRemoteCommand.ExecuteAsync(item);

        Assert.Equal(error, f.ViewModel.Feedback);
        Assert.True(Assert.Single(f.ViewModel.Items).IsLocalDirectory);
        Assert.True(item.IsRemoteEditorOpen);
        Assert.Equal(origin, item.EditingOriginUrl);
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Equal(0, f.ConfirmationCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Config.ApplyIdentityCount);
    }

    [Fact]
    public async Task SaveRemote_AfterInitializingThroughBindingRetainsBindingAndReappliesIdentity()
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        await f.ViewModel.SwitchBindingAccountAsync(Assert.Single(f.ViewModel.Items), f.Account);
        var binding = await f.Bindings.GetByProjectIdAsync(f.Project.Id);
        var snapshot = await f.Snapshots.GetByProjectIdAsync(f.Project.Id);
        var item = Assert.Single(f.ViewModel.Items);
        const string origin = "git@example.org:team/local-only.git";
        f.ViewModel.StartEditRemoteCommand.Execute(item);
        item.EditingOriginUrl = origin;

        await f.ViewModel.SaveRemoteCommand.ExecuteAsync(item);

        var refreshed = Assert.Single(f.ViewModel.Items);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(origin, refreshed.Project.OriginUrl);
        Assert.Equal(binding!.Id, refreshed.Binding!.Id);
        Assert.Equal(f.Account.Id, refreshed.SelectedBindingAccount!.Id);
        Assert.Equal(snapshot!.Id, (await f.Snapshots.GetByProjectIdAsync(f.Project.Id))!.Id);
        Assert.Equal(2, f.Config.ApplyIdentityCount);
        Assert.True(refreshed.CanTest);
        Assert.True(refreshed.CanPull);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Empty(f.ViewModel.Feedback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveRemote_AccountApplyFailureKeepsSavedAddressWarningWithTargetedRefresh(bool failTargetRefresh)
    {
        using var f = await Fixture.CreateAsync(isRepository: true);
        await f.ViewModel.SwitchBindingAccountAsync(Assert.Single(f.ViewModel.Items), f.Account);
        var binding = await f.Bindings.GetByProjectIdAsync(f.Project.Id);
        var item = Assert.Single(f.ViewModel.Items);
        const string origin = "https://example.org/team/local-only.git";
        f.ViewModel.StartEditRemoteCommand.Execute(item);
        item.EditingOriginUrl = origin;
        f.Config.ThrowOnApply = true;
        f.ProjectRepository.ThrowOnGetAll = true;
        f.AccountRepository.ThrowOnGetAll = failTargetRefresh;

        await f.ViewModel.SaveRemoteCommand.ExecuteAsync(item);

        Assert.StartsWith("PROJECT_REMOTE_SAVED_BINDING_APPLY_FAILED", f.ViewModel.Feedback);
        Assert.DoesNotContain("PROJECT_REMOTE_SAVE_FAILED", f.ViewModel.Feedback);
        Assert.DoesNotContain("Projects.RemoteReloadFailed", f.ViewModel.Feedback);
        Assert.Equal(0, f.ProjectRepository.GetAllFailureCount);
        Assert.Equal(failTargetRefresh ? 1 : 0, f.AccountRepository.GetAllFailureCount);
        Assert.Same(item, Assert.Single(f.ViewModel.Items));
        Assert.True(item.IsRemoteEditorOpen);
        Assert.Equal(origin, item.EditingOriginUrl);
        Assert.Equal(origin, f.Git.LastSetOriginUrl);
        Assert.Equal(origin, (await f.Projects.GetByIdAsync(f.Project.Id))!.OriginUrl);
        Assert.Same(binding, await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(f.Account.Id, item.BoundAccount!.Id);
        Assert.Equal(f.Account.Id, item.SelectedBindingAccount!.Id);
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Equal(0, f.ConfirmationCount);
        Assert.Equal(1, f.Config.ApplyIdentityCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.True(f.ViewModel.CanManageProjects);
        Assert.False(item.IsBusy);
        Assert.False(f.ViewModel.HasPendingOperations);
    }

    [Fact]
    public async Task SaveRemote_CancelInitializationKeepsEditorInputWithoutWritingGit()
    {
        using var f = await Fixture.CreateAsync();
        var item = Assert.Single(f.ViewModel.Items);
        const string origin = "https://example.org/team/local-only.git";
        f.ViewModel.StartEditRemoteCommand.Execute(item);
        item.EditingOriginUrl = origin;

        await f.ViewModel.SaveRemoteCommand.ExecuteAsync(item);

        Assert.True(item.IsRemoteEditorOpen);
        Assert.Equal(origin, item.EditingOriginUrl);
        Assert.True(Assert.Single(f.ViewModel.Items).IsLocalDirectory);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Config.ApplyIdentityCount);
    }

    [Theory]
    [InlineData("https://example.org/team/local-only.git", "HTTPS")]
    [InlineData("git@example.org:team/local-only.git", "SSH")]
    public async Task SaveRemote_ConfirmInitializationEnablesRemoteOperationsAfterExplicitBinding(string origin, string protocol)
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        f.Account.AuthenticationType = protocol == "HTTPS" ? AuthenticationType.Https : AuthenticationType.Ssh;
        f.Account.HttpsSecretId = "fake-https-secret-id";
        var originalItem = Assert.Single(f.ViewModel.Items);
        f.ViewModel.StartEditRemoteCommand.Execute(originalItem);
        originalItem.EditingOriginUrl = origin;

        await f.ViewModel.SaveRemoteCommand.ExecuteAsync(originalItem);

        var item = Assert.Single(f.ViewModel.Items);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.Equal(origin, f.Git.LastSetOriginUrl);
        Assert.Equal(origin, item.Project.OriginUrl);
        Assert.Equal(protocol, item.ProtocolText);
        Assert.True(item.HasGitRepository);
        Assert.True(item.HasRemote);
        Assert.True(item.CanPull);
        Assert.False(item.CanTest);
        Assert.False(item.HasBinding);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Config.ApplyIdentityCount);

        await f.ViewModel.SwitchBindingAccountAsync(item, f.Account);

        var boundItem = Assert.Single(f.ViewModel.Items);
        Assert.True(boundItem.HasBinding);
        Assert.True(boundItem.CanTest);
        Assert.True(boundItem.CanPull);
        Assert.Equal(f.Account.Id, boundItem.BoundAccount!.Id);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Empty(f.ViewModel.Feedback);
    }

    [Fact]
    public async Task SaveRemote_InitializationFailureDoesNotSetOriginOrBindAccount()
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        f.Git.InitializeSucceeds = false;
        var item = Assert.Single(f.ViewModel.Items);
        f.ViewModel.StartEditRemoteCommand.Execute(item);
        const string origin = "https://example.org/team/local-only.git";
        item.EditingOriginUrl = origin;

        await f.ViewModel.SaveRemoteCommand.ExecuteAsync(item);

        Assert.StartsWith("PROJECT_GIT_INITIALIZATION_FAILED", f.ViewModel.Feedback);
        var refreshed = Assert.Single(f.ViewModel.Items);
        Assert.True(refreshed.IsLocalDirectory);
        Assert.True(refreshed.IsRemoteEditorOpen);
        Assert.Equal(origin, refreshed.EditingOriginUrl);
        Assert.Equal(1, f.ConfirmationCount);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.True(Directory.Exists(f.Project.RepositoryPath));
    }

    [Fact]
    public async Task SaveRemote_OriginWriteFailureRefreshesSuccessfullyInitializedRepository()
    {
        using var f = await Fixture.CreateAsync(confirmInitialization: true);
        f.Git.SetOriginUrlSucceeds = false;
        var originalItem = Assert.Single(f.ViewModel.Items);
        f.ViewModel.StartEditRemoteCommand.Execute(originalItem);
        const string origin = "https://example.org/team/local-only.git";
        originalItem.EditingOriginUrl = origin;

        await f.ViewModel.SaveRemoteCommand.ExecuteAsync(originalItem);

        var item = Assert.Single(f.ViewModel.Items);
        Assert.StartsWith("PROJECT_REMOTE_UPDATE_FAILED", f.ViewModel.Feedback);
        Assert.Equal(1, f.Git.InitializeCount);
        Assert.True(item.HasGitRepository);
        Assert.True(item.IsRemoteEditorOpen);
        Assert.Equal(origin, item.EditingOriginUrl);
        Assert.False(item.HasRemote);
        Assert.False(item.CanTest);
        Assert.True(item.CanPull);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Null(await f.Bindings.GetByProjectIdAsync(f.Project.Id));
        Assert.Null(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(0, f.Config.ApplyIdentityCount);
        Assert.True(Directory.Exists(item.PathText));
    }

    [Fact]
    public async Task SwitchBinding_GitWithoutOriginSavesIdentityWithoutProtocolPreviewOrConfirmation()
    {
        using var f = await Fixture.CreateAsync(isRepository: true);
        var originalItem = Assert.Single(f.ViewModel.Items);

        await f.ViewModel.SwitchBindingAccountAsync(originalItem, f.Account);

        var item = Assert.Single(f.ViewModel.Items);
        Assert.Equal(f.Account.Id, (await f.Bindings.GetByProjectIdAsync(f.Project.Id))!.AccountId);
        Assert.NotNull(await f.Snapshots.GetByProjectIdAsync(f.Project.Id));
        Assert.Equal(f.Account.Id, item.SelectedBindingAccount!.Id);
        Assert.Equal(1, f.Config.ApplyIdentityCount);
        Assert.Equal(0, f.CountingGit.TransferOriginReadCount);
        Assert.Equal(0, f.ConfirmationCount);
        Assert.Equal(0, f.Transfer.PullCount);
        Assert.Equal(0, f.Transfer.TestCount);
        Assert.Null(f.Git.LastSetOriginUrl);
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Empty(f.ViewModel.Feedback);
        Assert.True(item.CanBindAccount);
        Assert.True(item.HasBinding);
        Assert.False(item.HasRemote);
        Assert.False(item.CanTest);
        Assert.True(item.CanPull);
        Assert.False(originalItem.IsSwitchingBinding);
        Assert.True(f.ViewModel.CanManageProjects);
    }

    [Fact]
    public async Task BindingBusyStateUpdatesAccountSelectionAvailability()
    {
        using var f = await Fixture.CreateAsync(isRepository: true);
        var item = Assert.Single(f.ViewModel.Items);
        var notifications = new List<string?>();
        item.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        item.IsSwitchingBinding = true;
        Assert.False(item.CanBindAccount);
        item.IsSwitchingBinding = false;

        Assert.True(item.CanBindAccount);
        Assert.Equal(2, notifications.Count(name => name == nameof(ProjectItemViewModel.CanBindAccount)));
    }

    [Fact]
    public async Task OpenDirectory_LocalProjectUsesRegisteredPathWithoutInitializingGit()
    {
        using var f = await Fixture.CreateAsync();
        f.ViewModel.OpenDirectoryCommand.Execute(Assert.Single(f.ViewModel.Items));

        Assert.Equal(Path.GetFullPath(f.Project.RepositoryPath), f.Shell.LastOpenedPath);
        Assert.Equal(0, f.Git.InitializeCount);
        Assert.Empty(f.ViewModel.Feedback);
    }

    [Fact]
    public async Task OpenDirectory_MissingPathAndShellFailuresAreSafe()
    {
        using var f = await Fixture.CreateAsync();
        var item = Assert.Single(f.ViewModel.Items);
        f.Shell.ThrowOnOpen = true;
        f.ViewModel.OpenDirectoryCommand.Execute(item);
        Assert.Equal("PROJECT_OPEN_DIRECTORY_FAILED", f.ViewModel.Feedback);

        item.Project.RepositoryPath = Path.Combine(item.PathText, "missing-folder");
        f.ViewModel.OpenDirectoryCommand.Execute(item);
        Assert.StartsWith("PROJECT_DIRECTORY_NOT_FOUND", f.ViewModel.Feedback);
        Assert.Equal(1, f.Shell.OpenCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-local-project-ui-" + Guid.NewGuid().ToString("N"));
        public FakeAccountRepository Accounts { get; } = new();
        public FaultingAccountRepository AccountRepository { get; }
        public FakeProjectRepository Projects { get; } = new();
        public FakeBindingRepository Bindings { get; } = new();
        public FaultingProjectRepository ProjectRepository { get; }
        public FaultingBindingRepository BindingRepository { get; }
        public FakeSnapshotRepository Snapshots { get; } = new();
        public CountingGitService CountingGit { get; } = new();
        public FakeGitService Git => CountingGit.Inner;
        public FakeGitConfigApplier Config { get; } = new();
        public FakeGitTransfer Transfer { get; } = new();
        public FakeGlobalModeState Global { get; } = new();
        public RecordingShell Shell { get; } = new();
        public Account Account { get; } = new()
        {
            Alias = "default account", GitName = "example", GitEmail = "example@example.com",
            IsDefault = true, Enabled = true, AuthenticationType = AuthenticationType.Ssh,
        };
        public Project Project { get; } = new() { Name = "local-only" };
        public ProjectsViewModel ViewModel { get; private set; } = null!;
        public int ConfirmationCount { get; private set; }
        public string LastConfirmationTitle { get; private set; } = string.Empty;
        public string LastConfirmationMessage { get; private set; } = string.Empty;
        public string LastConfirmationAction { get; private set; } = string.Empty;

        private Fixture()
        {
            AccountRepository = new FaultingAccountRepository(Accounts);
            ProjectRepository = new FaultingProjectRepository(Projects);
            BindingRepository = new FaultingBindingRepository(Bindings);
        }

        public static async Task<Fixture> CreateAsync(bool isRepository = false, bool globalMode = false, bool confirmInitialization = false)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f._root);
            f.Project.RepositoryPath = f.Project.CanonicalPath = f._root;
            f.Git.RepositoryRoot = f._root;
            f.Git.ValidateResult = isRepository;
            f.Git.OriginUrl = string.Empty;
            if (isRepository)
            {
                f.Project.GitDir = Path.Combine(f._root, ".git");
                f.Project.CurrentBranch = "main";
            }
            f.Account.SshPrivateKeyPath = Path.Combine(f._root, "not-a-real-key");
            f.Global.IsEnabled = globalMode;
            f.Global.GlobalAccountId = globalMode ? f.Account.Id : null;
            await f.Accounts.AddAsync(f.Account);
            await f.Projects.AddAsync(f.Project);
            var resolver = new EffectiveAccountResolver(f.Global, f.Bindings, f.Accounts);
            var bindings = new BindingService(f.BindingRepository, f.Accounts, f.ProjectRepository, new FakeSecretStore(),
                f.CountingGit, f.Config, f.Snapshots, f.Transfer, resolver);
            var paths = new IdentityPathNormalizer();
            var projects = new ProjectService(f.ProjectRepository, f.CountingGit, paths, f.Accounts, bindings);
            var groups = new FakeProjectGroupRepository();
            var transfers = new ProjectTransferService(f.Transfer, projects, f.ProjectRepository,
                f.Accounts, bindings, resolver, paths, groups);
            f.ViewModel = new ProjectsViewModel(f.ProjectRepository, projects, f.BindingRepository, f.AccountRepository, bindings,
                resolver, new TestLocalization(), () => throw new InvalidOperationException("No directory picker expected."),
                transfers, new ProjectGroupService(groups), (title, message, action) =>
                {
                    f.ConfirmationCount++;
                    f.LastConfirmationTitle = title;
                    f.LastConfirmationMessage = message;
                    f.LastConfirmationAction = action;
                    return Task.FromResult(confirmInitialization);
                }, shellService: f.Shell);
            await f.ViewModel.LoadAsync();
            return f;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingShell : IShellService
    {
        public string? LastOpenedPath { get; private set; }
        public int OpenCount { get; private set; }
        public bool ThrowOnOpen { get; set; }
        public void OpenDirectory(string path)
        {
            OpenCount++;
            if (ThrowOnOpen) throw new IOException("Fixture error");
            LastOpenedPath = path;
        }
        public void OpenTerminal(string path) => throw new InvalidOperationException("Not expected");
        public Task<string?> PickDirectoryAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FaultingProjectRepository(FakeProjectRepository inner) : IProjectRepository
    {
        public bool ThrowOnGetAll { get; set; }
        public int GetAllFailureCount { get; private set; }

        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default)
        {
            if (!ThrowOnGetAll) return inner.GetAllAsync(ct);
            GetAllFailureCount++;
            return Task.FromException<IReadOnlyList<Project>>(new InvalidOperationException("Fake project reload failure"));
        }

        public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<Project?> FindByPathAsync(string canonicalPath, CancellationToken ct = default)
            => inner.FindByPathAsync(canonicalPath, ct);
        public Task AddAsync(Project project, CancellationToken ct = default) => inner.AddAsync(project, ct);
        public Task UpdateAsync(Project project, CancellationToken ct = default) => inner.UpdateAsync(project, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }

    private sealed class FaultingAccountRepository(FakeAccountRepository inner) : IAccountRepository
    {
        public bool ThrowOnGetAll { get; set; }
        public int GetAllFailureCount { get; private set; }

        public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
        {
            if (!ThrowOnGetAll) return inner.GetAllAsync(ct);
            GetAllFailureCount++;
            return Task.FromException<IReadOnlyList<Account>>(new InvalidOperationException("Fake account reload failure"));
        }

        public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<Account?> GetDefaultAsync(CancellationToken ct = default) => inner.GetDefaultAsync(ct);
        public Task AddAsync(Account account, CancellationToken ct = default) => inner.AddAsync(account, ct);
        public Task UpdateAsync(Account account, CancellationToken ct = default) => inner.UpdateAsync(account, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }

    private sealed class FaultingBindingRepository(FakeBindingRepository inner) : IBindingRepository
    {
        public bool ThrowOnAdd { get; set; }
        public int AddFailureCount { get; private set; }

        public Task AddAsync(Binding binding, CancellationToken ct = default)
        {
            if (!ThrowOnAdd) return inner.AddAsync(binding, ct);
            AddFailureCount++;
            return Task.FromException(new InvalidOperationException("Fake binding persistence failure"));
        }

        public Task<Binding?> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
            => inner.GetByProjectIdAsync(projectId, ct);
        public Task<IReadOnlyList<Binding>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
        public Task<IReadOnlyList<Binding>> GetByAccountIdAsync(Guid accountId, CancellationToken ct = default)
            => inner.GetByAccountIdAsync(accountId, ct);
        public Task<int> CountByAccountIdAsync(Guid accountId, CancellationToken ct = default)
            => inner.CountByAccountIdAsync(accountId, ct);
        public Task UpdateAsync(Binding binding, CancellationToken ct = default) => inner.UpdateAsync(binding, ct);
        public Task DeleteByProjectIdAsync(Guid projectId, CancellationToken ct = default)
            => inner.DeleteByProjectIdAsync(projectId, ct);
    }

    private sealed class CountingGitService : IGitService
    {
        public FakeGitService Inner { get; } = new();
        public int ValidationCount { get; private set; }
        public int TransferOriginReadCount { get; private set; }

        public Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default)
        {
            ValidationCount++;
            return Inner.ValidateRepositoryAsync(path, ct);
        }

        public Task<Result> InitializeRepositoryAsync(string path, CancellationToken ct = default)
            => Inner.InitializeRepositoryAsync(path, ct);

        public Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default)
        {
            TransferOriginReadCount++;
            return Inner.GetTransferOriginUrlAsync(path, ct);
        }

        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
            => Inner.GetRepositoryRootAsync(path, ct);
        public Task<string?> GetGitDirAsync(string path, CancellationToken ct = default)
            => Inner.GetGitDirAsync(path, ct);
        public Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default)
            => Inner.GetOriginUrlAsync(path, ct);
        public Task<Result> SetTransferOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
            => Inner.SetTransferOriginUrlAsync(path, originUrl, ct);
        public Task<Result> SetOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
            => Inner.SetOriginUrlAsync(path, originUrl, ct);
        public Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default)
            => Inner.GetCurrentBranchAsync(path, ct);
        public (RemoteProtocol Protocol, string Host) ParseRemote(string url) => Inner.ParseRemote(url);
        public Task<string?> GetGitVersionAsync(CancellationToken ct = default) => Inner.GetGitVersionAsync(ct);
    }

    private sealed class IdentityPathNormalizer : IPathNormalizer
    {
        public string Normalize(string path) => path;
        public string Canonicalize(string path) => path;
        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);
        public string ToKey(string path) => path;
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
