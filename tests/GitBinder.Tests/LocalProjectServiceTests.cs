using GitBinder.Application.Bindings;
using GitBinder.Application.Projects;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Platform.Windows;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>仅用临时目录和 Fake 验证登记与显式初始化边界，不执行 git init 或修改真实 Git 配置</summary>
public sealed class LocalProjectServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder-local-project-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProjectRepository _projects = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeBindingRepository _bindings = new();
    private readonly FakeSnapshotRepository _snapshots = new();
    private readonly FakeProjectGroupRepository _groups = new();
    private readonly FakeGitService _git = new() { ValidateResult = false, OriginUrl = string.Empty, CurrentBranch = string.Empty };
    private readonly FakeGitConfigApplier _config = new();
    private readonly FakeGitTransfer _transfer = new();
    private readonly FakeGlobalModeState _globalMode = new();
    private readonly BindingService _bindingService;
    private readonly ProjectService _service;
    private readonly ProjectTransferService _transferService;

    public LocalProjectServiceTests()
    {
        Directory.CreateDirectory(_root);
        _git.RepositoryRoot = _root;
        var paths = new WindowsPathNormalizer();
        var resolver = new EffectiveAccountResolver(_globalMode, _bindings, _accounts);
        _bindingService = new BindingService(_bindings, _accounts, _projects, new FakeSecretStore(),
            _git, _config, _snapshots, _transfer, resolver);
        _service = new ProjectService(_projects, _git, paths, _accounts, _bindingService);
        _transferService = new ProjectTransferService(_transfer, _service, _projects, _accounts,
            _bindingService, resolver, paths, _groups);
    }

    [Fact]
    public async Task Add_PlainDirectoryWithDefaultAccount_RegistersWithoutInitializingOrBinding()
    {
        await _accounts.AddAsync(new Account { IsDefault = true });

        var result = await _service.AddAsync(_root);

        Assert.True(result.IsSuccess);
        var project = Assert.Single(await _projects.GetAllAsync());
        Assert.True(project.IsLocalDirectory);
        Assert.Equal(_root, project.RepositoryPath);
        Assert.Equal(Path.GetFileName(_root), project.Name);
        Assert.Empty(project.GitDir);
        Assert.Empty(project.OriginUrl);
        Assert.Empty(project.CurrentBranch);
        Assert.Empty(await _bindings.GetAllAsync());
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        AssertNoGitWrites();
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
        Assert.False(File.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public async Task Add_GitRepositoryWithoutOrigin_RemainsBindable()
    {
        _git.ValidateResult = true;
        _git.CurrentBranch = "main";
        var account = new Account { IsDefault = true };
        await _accounts.AddAsync(account);

        var result = await _service.AddAsync(_root);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsLocalDirectory);
        Assert.Empty(result.Value.OriginUrl);
        Assert.Equal(account.Id, (await _bindings.GetByProjectIdAsync(result.Value.Id))!.AccountId);
        Assert.Equal(1, _config.ApplyIdentityCount);
    }

    [Fact]
    public async Task Add_RepositorySubdirectoryCannotBypassRootDuplicateCheck()
    {
        _git.ValidateResult = true;
        Assert.True((await _service.AddAsync(_root)).IsSuccess);

        var duplicate = await _service.AddAsync(Path.Combine(_root, "src"));

        Assert.False(duplicate.IsSuccess);
        Assert.Equal("PROJECT_ALREADY_EXISTS", duplicate.Error!.Code);
        Assert.Single(await _projects.GetAllAsync());
    }

    [Fact]
    public async Task Add_PlainDirectoryDuplicateIsRejected()
    {
        Assert.True((await _service.AddAsync(_root)).IsSuccess);

        var duplicate = await _service.AddAsync(_root);

        Assert.Equal("PROJECT_ALREADY_EXISTS", duplicate.Error!.Code);
        Assert.Single(await _projects.GetAllAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_MissingDirectoryOrFileIsRejected(bool existingFile)
    {
        var path = Path.Combine(_root, "not-a-directory");
        if (existingFile) File.WriteAllText(path, "keep");

        var result = await _service.AddAsync(path);

        Assert.Equal("PROJECT_DIRECTORY_NOT_FOUND", result.Error!.Code);
        Assert.Equal(path, Assert.Single(result.Error.Arguments));
        Assert.Empty(await _projects.GetAllAsync());
    }

    [Fact]
    public async Task Refresh_PlainDirectoryIsNormalState_NotAnError()
    {
        var added = (await _service.AddAsync(_root)).Value!;

        var refreshed = await _service.RefreshMetadataAsync(added.Id);

        Assert.True(refreshed.IsSuccess);
        Assert.Same(added, refreshed.Value);
        Assert.True(refreshed.Value!.IsLocalDirectory);
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Refresh_ExternallyInitializedDirectoryActivatesWithoutAutomaticBinding()
    {
        await _accounts.AddAsync(new Account { IsDefault = true });
        var added = (await _service.AddAsync(_root)).Value!;
        _git.ValidateResult = true;
        _git.CurrentBranch = "main";

        var refreshed = await _service.RefreshMetadataAsync(added.Id);

        Assert.True(refreshed.IsSuccess);
        Assert.False(refreshed.Value!.IsLocalDirectory);
        Assert.Equal(added.Id, refreshed.Value.Id);
        Assert.Equal(added.CreatedAt, refreshed.Value.CreatedAt);
        Assert.Equal(_root, refreshed.Value.RepositoryPath);
        Assert.Equal(Path.Combine(_root, ".git"), refreshed.Value.GitDir);
        Assert.Equal("main", refreshed.Value.CurrentBranch);
        Assert.Empty(refreshed.Value.OriginUrl);
        Assert.Empty(await _bindings.GetAllAsync());
        Assert.Null(await _snapshots.GetByProjectIdAsync(added.Id));
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Refresh_DoesNotAdoptParentRepository()
    {
        var added = (await _service.AddAsync(_root)).Value!;
        _git.ValidateResult = true;
        _git.RepositoryRoot = Path.GetDirectoryName(_root)!;
        _git.OriginUrl = "https://example.invalid/parent.git";

        var refreshed = await _service.RefreshMetadataAsync(added.Id);

        Assert.True(refreshed.IsSuccess);
        Assert.Same(added, refreshed.Value);
        Assert.True(refreshed.Value!.IsLocalDirectory);
        Assert.Equal(_root, refreshed.Value.RepositoryPath);
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Refresh_ActivationFailurePreservesPlainDirectory()
    {
        var added = (await _service.AddAsync(_root)).Value!;
        _git.ValidateResult = true;
        _git.OriginReadFails = true;

        var refreshed = await _service.RefreshMetadataAsync(added.Id);

        Assert.Equal("PROJECT_METADATA_READ_FAILED", refreshed.Error!.Code);
        Assert.Same(added, await _projects.GetByIdAsync(added.Id));
        Assert.True(added.IsLocalDirectory);
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Initialize_PlainDirectoryPreservesIdentityAndGroup_WithoutDefaultBinding()
    {
        await _accounts.AddAsync(new Account { IsDefault = true });
        var project = (await _service.AddAsync(_root)).Value!;
        var groupId = Guid.NewGuid();
        _groups.Memberships[project.Id] = groupId;

        var result = await _service.InitializeRepositoryAsync(project.Id);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsLocalDirectory);
        Assert.Equal(project.Id, result.Value.Id);
        Assert.Equal(project.Name, result.Value.Name);
        Assert.Equal(project.CreatedAt, result.Value.CreatedAt);
        Assert.Equal(_root, result.Value.RepositoryPath);
        Assert.Equal(Path.Combine(_root, ".git"), result.Value.GitDir);
        Assert.Equal(groupId, _groups.Memberships[project.Id]);
        Assert.Equal(1, _git.InitializeCount);
        Assert.Equal(_root, _git.LastInitializedPath);
        Assert.Empty(await _bindings.GetAllAsync());
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        Assert.Equal(0, _config.ApplyIdentityCount);
        Assert.Null(_git.LastSetOriginUrl);
    }

    [Fact]
    public async Task Initialize_ThenExplicitBindUsesSelectedAccountAndCapturesSnapshot()
    {
        var project = (await _service.AddAsync(_root)).Value!;
        var selected = new Account { GitName = "selected", GitEmail = "selected@example.invalid" };
        var defaultAccount = new Account { IsDefault = true, GitName = "default" };
        await _accounts.AddAsync(selected);
        await _accounts.AddAsync(defaultAccount);

        Assert.True((await _service.InitializeRepositoryAsync(project.Id)).IsSuccess);
        var bound = await _bindingService.BindAsync(project.Id, selected.Id);

        Assert.True(bound.IsSuccess);
        Assert.Equal(selected.Id, bound.Value!.AccountId);
        Assert.Equal((selected.GitName, selected.GitEmail), _config.AppliedIdentity);
        Assert.NotNull(await _snapshots.GetByProjectIdAsync(project.Id));
        Assert.Equal(1, _git.InitializeCount);
        Assert.Equal(1, _config.ApplyIdentityCount);
        Assert.Equal(0, _transfer.TestCount);
        Assert.Equal(0, _transfer.PullCount);
    }

    [Fact]
    public async Task Initialize_MissingProjectOrDirectoryNeverCallsGitInit()
    {
        Assert.Equal("PROJECT_NOT_FOUND", (await _service.InitializeRepositoryAsync(Guid.NewGuid())).Error!.Code);
        var project = new Project { RepositoryPath = Path.Combine(_root, "missing") };
        await _projects.AddAsync(project);

        var result = await _service.InitializeRepositoryAsync(project.Id);

        Assert.Equal("PROJECT_DIRECTORY_NOT_FOUND", result.Error!.Code);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Initialize_GitFailureKeepsPlainRegistrationWithoutBinding()
    {
        var project = (await _service.AddAsync(_root)).Value!;
        _git.InitializeSucceeds = false;

        var result = await _service.InitializeRepositoryAsync(project.Id);

        Assert.Equal("PROJECT_GIT_INITIALIZATION_FAILED", result.Error!.Code);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        Assert.True(project.IsLocalDirectory);
        Assert.Equal(1, _git.InitializeCount);
        Assert.Empty(await _bindings.GetAllAsync());
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        Assert.Null(_git.LastSetOriginUrl);
    }

    [Theory]
    [InlineData("PROJECT_METADATA_READ_FAILED", false)]
    [InlineData("PROJECT_REPOSITORY_ROOT_MISMATCH", true)]
    public async Task Initialize_MetadataFailureDoesNotAdoptParentOrRemoveCreatedGitData(string error, bool parentRoot)
    {
        var project = (await _service.AddAsync(_root)).Value!;
        var createdGitDir = Path.Combine(_root, ".git");
        _git.OnInitialize = (_, _) =>
        {
            Directory.CreateDirectory(createdGitDir);
            _git.ValidateResult = true;
            _git.RepositoryRoot = parentRoot ? Path.GetDirectoryName(_root)! : _root;
            _git.OriginReadFails = !parentRoot;
            return Task.FromResult(Result.Success());
        };

        var result = await _service.InitializeRepositoryAsync(project.Id);

        Assert.Equal(error, result.Error!.Code);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        Assert.True(project.IsLocalDirectory);
        Assert.True(Directory.Exists(createdGitDir));
        Assert.Equal(1, _git.InitializeCount);
        Assert.Empty(await _bindings.GetAllAsync());
        Assert.Null(_git.LastSetOriginUrl);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, false, "PROJECT_REPOSITORY_NOT_FOUND")]
    [InlineData(true, true, "PROJECT_REPOSITORY_ROOT_MISMATCH")]
    public async Task Initialize_ExistingRegistrationValidatesActualRepositoryWithoutReinitializing(
        bool exists, bool parentRoot, string? error)
    {
        var project = new Project { RepositoryPath = _root, GitDir = Path.Combine(_root, ".git") };
        await _projects.AddAsync(project);
        _git.ValidateResult = exists;
        _git.RepositoryRoot = parentRoot ? Path.GetDirectoryName(_root)! : _root;

        var result = await _service.InitializeRepositoryAsync(project.Id);

        Assert.Equal(error, result.Error?.Code);
        if (error is null) Assert.Same(project, result.Value);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Refresh_PreviouslyKnownGitRepositoryIsNotDowngradedToPlainDirectory()
    {
        var project = new Project { RepositoryPath = _root, GitDir = Path.Combine(_root, ".git") };
        await _projects.AddAsync(project);

        var refreshed = await _service.RefreshMetadataAsync(project.Id);

        Assert.Equal("PROJECT_REPOSITORY_NOT_FOUND", refreshed.Error!.Code);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        Assert.False(project.IsLocalDirectory);
    }

    [Fact]
    public async Task UpdatePath_PlainDirectoryCanMove_WithoutChangingIdentityOrGroup()
    {
        var added = (await _service.AddAsync(_root)).Value!;
        var groupId = Guid.NewGuid();
        _groups.Memberships[added.Id] = groupId;
        var target = Path.Combine(_root, "moved");
        Directory.CreateDirectory(target);

        var result = await _service.UpdatePathAsync(added.Id, target);

        Assert.True(result.IsSuccess);
        Assert.Equal(added.Id, result.Value!.Id);
        Assert.Equal(added.Name, result.Value.Name);
        Assert.Equal(target, result.Value.RepositoryPath);
        Assert.True(result.Value.IsLocalDirectory);
        Assert.Equal(groupId, _groups.Memberships[added.Id]);
        AssertNoGitWrites();
    }

    [Fact]
    public async Task UpdatePath_InitializedProjectCannotMoveToPlainDirectory()
    {
        var project = new Project { RepositoryPath = "old", GitDir = "old/.git" };
        await _projects.AddAsync(project);

        var result = await _service.UpdatePathAsync(project.Id, _root);

        Assert.Equal("PROJECT_REPOSITORY_NOT_FOUND", result.Error!.Code);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        Assert.Equal("old", project.RepositoryPath);
    }

    [Fact]
    public async Task UpdatePath_PlainDirectoryCanBeRelocatedToInitializedRepository()
    {
        var added = (await _service.AddAsync(_root)).Value!;
        var target = Path.Combine(_root, "moved");
        _git.ValidateResult = true;
        _git.RepositoryRoot = target;
        _git.OriginUrl = "https://example.invalid/repo.git";

        var result = await _service.UpdatePathAsync(added.Id, target);

        Assert.True(result.IsSuccess);
        Assert.Equal(added.Id, result.Value!.Id);
        Assert.Equal(target, result.Value.RepositoryPath);
        Assert.False(result.Value.IsLocalDirectory);
        Assert.Equal(RemoteProtocol.Https, result.Value.RemoteProtocol);
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Bind_PlainDirectoryDoesNotCreateBindingOrSnapshot()
    {
        var project = (await _service.AddAsync(_root)).Value!;
        var account = new Account();
        await _accounts.AddAsync(account);

        var result = await _bindingService.BindAsync(project.Id, account.Id);

        Assert.Equal("PROJECT_GIT_NOT_INITIALIZED", result.Error!.Code);
        Assert.Empty(await _bindings.GetAllAsync());
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        AssertNoGitWrites();
    }

    [Fact]
    public async Task PlainDirectoryWithStaleBindingIsNeverAppliedOrTested()
    {
        var project = (await _service.AddAsync(_root)).Value!;
        var account = new Account();
        var binding = new Binding { ProjectId = project.Id, AccountId = account.Id };
        await _accounts.AddAsync(account);
        await _bindings.AddAsync(binding);

        Assert.True((await _bindingService.ApplyGlobalAccountAsync(account)).IsSuccess);
        Assert.True((await _bindingService.RestoreBoundAccountsAsync()).IsSuccess);
        Assert.True((await _bindingService.ReapplyAccountIdentityAsync(account.Id)).IsSuccess);
        Assert.Equal(BindingStatus.RepositoryMissing, await _bindingService.ValidateAsync(binding));
        Assert.Equal("PROJECT_GIT_NOT_INITIALIZED", (await _bindingService.TestAsync(binding)).Error!.Code);
        Assert.Equal(0, _transfer.TestCount);
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        AssertNoGitWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolPreviewAndConfirmation_PlainDirectoryNeverReadOrWriteParentRepository(bool confirm)
    {
        var project = (await _service.AddAsync(_root)).Value!;
        var account = new Account
        {
            IsDefault = true, AuthenticationType = AuthenticationType.Ssh, SshPrivateKeyPath = "not-read-private-key",
        };
        await _accounts.AddAsync(account);
        var repositoryProbeCount = 0;
        _git.ValidateRepository = _ => { repositoryProbeCount++; return true; };
        _git.RepositoryRoot = Path.GetDirectoryName(_root)!;
        _git.OriginUrl = "https://example.invalid/parent.git";
        var change = new BindingProtocolChange(project.Id, account.Id, account.DisplayAlias, _root,
            _git.OriginUrl, "git@example.invalid:parent.git", RemoteProtocol.Ssh);

        var error = confirm
            ? (await _bindingService.ChangeProtocolAsync(change)).Error
            : (await _bindingService.GetProtocolChangeAsync(project.Id)).Error;

        Assert.NotNull(error);
        Assert.Equal("PROJECT_GIT_NOT_INITIALIZED", error.Code);
        Assert.Equal(0, repositoryProbeCount);
        Assert.Equal("https://example.invalid/parent.git", _git.OriginUrl);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        Assert.True(project.IsLocalDirectory);
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        Assert.Equal(0, _transfer.TestCount);
        Assert.Equal(0, _transfer.PullCount);
        AssertNoGitWrites();
    }

    [Theory]
    [InlineData("https://example.invalid/repo.git", RemoteProtocol.Https)]
    [InlineData("git@example.invalid:repo.git", RemoteProtocol.Ssh)]
    public async Task UpdateRemote_PlainDirectoryInitializesAndSavesOriginWithoutDefaultBinding(
        string url, RemoteProtocol protocol)
    {
        await _accounts.AddAsync(new Account { IsDefault = true });
        var project = (await _service.AddAsync(_root)).Value!;

        var result = await _service.UpdateOriginUrlAsync(project.Id, url);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsLocalDirectory);
        Assert.Equal(project.Id, result.Value.Id);
        Assert.Equal(url, result.Value.OriginUrl);
        Assert.Equal(protocol, result.Value.RemoteProtocol);
        Assert.Equal("example.invalid", result.Value.RemoteHost);
        Assert.Equal(result.Value.OriginUrl, _git.LastSetOriginUrl);
        Assert.Equal(1, _git.InitializeCount);
        Assert.Empty(await _bindings.GetAllAsync());
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        Assert.Equal(0, _config.ApplyIdentityCount);
        Assert.Equal(0, _transfer.TestCount);
        Assert.Equal(0, _transfer.PullCount);
    }

    [Theory]
    [InlineData("", "PROJECT_REMOTE_URL_REQUIRED")]
    [InlineData("not a remote", "PROJECT_REMOTE_URL_INVALID")]
    [InlineData("http://example.invalid/repo.git", "PROJECT_REMOTE_URL_INVALID")]
    [InlineData("file:///repo", "PROJECT_REMOTE_URL_INVALID")]
    [InlineData("https://example.invalid", "PROJECT_REMOTE_URL_INVALID")]
    [InlineData("https://example.invalid/repo.git?credential=value", "PROJECT_REMOTE_URL_INVALID")]
    [InlineData("https://user:placeholder@example.invalid/repo.git", "PROJECT_REMOTE_CREDENTIAL_FORBIDDEN")]
    [InlineData("ssh://user:placeholder@example.invalid/repo.git", "PROJECT_REMOTE_URL_INVALID")]
    public async Task UpdateRemote_InvalidPlainDirectoryUrlDoesNotInitialize(string url, string error)
    {
        var project = (await _service.AddAsync(_root)).Value!;

        var result = await _service.UpdateOriginUrlAsync(project.Id, url);

        Assert.Equal(error, result.Error!.Code);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        AssertNoGitWrites();
    }

    [Fact]
    public async Task UpdateRemote_OriginWriteFailureKeepsInitializedProjectAndSource()
    {
        var file = Path.Combine(_root, "keep.txt");
        File.WriteAllText(file, "keep");
        var project = (await _service.AddAsync(_root)).Value!;
        _git.SetOriginUrlSucceeds = false;

        var result = await _service.UpdateOriginUrlAsync(project.Id, "https://example.invalid/repo.git");

        Assert.Equal("PROJECT_REMOTE_UPDATE_FAILED", result.Error!.Code);
        var stored = await _projects.GetByIdAsync(project.Id);
        Assert.False(stored!.IsLocalDirectory);
        Assert.Equal(project.Id, stored.Id);
        Assert.Empty(stored.OriginUrl);
        Assert.Equal("keep", File.ReadAllText(file));
        Assert.Equal(1, _git.InitializeCount);
        Assert.Empty(await _bindings.GetAllAsync());
    }

    [Theory]
    [InlineData(true, "PROJECT_REPOSITORY_ROOT_MISMATCH")]
    [InlineData(false, "PROJECT_METADATA_READ_FAILED")]
    public async Task UpdateRemote_ExistingProjectCannotWriteParentRepositoryOrUnreadableRoot(bool parentRoot, string error)
    {
        var project = new Project
        {
            RepositoryPath = _root, GitDir = Path.Combine(_root, ".git"),
            OriginUrl = "https://example.invalid/project.git", RemoteProtocol = RemoteProtocol.Https,
        };
        var binding = new Binding { ProjectId = project.Id, AccountId = Guid.NewGuid() };
        await _projects.AddAsync(project);
        await _bindings.AddAsync(binding);
        _git.ValidateResult = true;
        _git.RepositoryRoot = parentRoot ? Path.GetDirectoryName(_root)! : string.Empty;
        _git.OriginUrl = "https://example.invalid/parent.git";

        var result = await _service.UpdateOriginUrlAsync(project.Id, "https://example.invalid/new.git");

        Assert.Equal(error, result.Error!.Code);
        Assert.Same(project, await _projects.GetByIdAsync(project.Id));
        Assert.Equal("https://example.invalid/project.git", project.OriginUrl);
        Assert.Equal("https://example.invalid/parent.git", _git.OriginUrl);
        Assert.Same(binding, await _bindings.GetByProjectIdAsync(project.Id));
        Assert.Null(await _snapshots.GetByProjectIdAsync(project.Id));
        AssertNoGitWrites();
        Assert.Equal(0, _transfer.TestCount);
        Assert.Equal(0, _transfer.PullCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateRemote_ReappliesOriginalBindingForNewProtocol_WithGlobalModePriority(bool globalMode)
    {
        _git.ValidateResult = true;
        var project = (await _service.AddAsync(_root, bindDefaultAccount: false)).Value!;
        var boundAccount = new Account
        {
            GitName = "bound", GitEmail = "bound@example.invalid",
            AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fake-bound-secret",
        };
        var globalAccount = new Account
        {
            GitName = "global", GitEmail = "global@example.invalid", IsDefault = true,
            AuthenticationType = AuthenticationType.Https, HttpsSecretId = "fake-global-secret",
        };
        await _accounts.AddAsync(boundAccount);
        await _accounts.AddAsync(globalAccount);
        Assert.True((await _bindingService.BindAsync(project.Id, boundAccount.Id)).IsSuccess);
        var binding = await _bindings.GetByProjectIdAsync(project.Id);
        _globalMode.IsEnabled = globalMode;
        _globalMode.GlobalAccountId = globalAccount.Id;

        var result = await _service.UpdateOriginUrlAsync(project.Id, "https://example.invalid/repo.git");

        Assert.True(result.IsSuccess);
        var effective = globalMode ? globalAccount : boundAccount;
        Assert.Equal((effective.GitName, effective.GitEmail), _config.AppliedIdentity);
        Assert.Equal(effective.Id, _config.AppliedCredentialAccountId);
        var savedBinding = await _bindings.GetByProjectIdAsync(project.Id);
        Assert.NotNull(savedBinding);
        Assert.NotSame(binding, savedBinding);
        Assert.Equal(binding!.Id, savedBinding.Id);
        Assert.Equal(boundAccount.Id, savedBinding.AccountId);
        Assert.Equal(project.Id, savedBinding.ProjectId);
        Assert.Equal(BindingStatus.Active, savedBinding.Status);
        Assert.Equal(boundAccount.Id, binding!.AccountId);
        Assert.Equal(2, _config.ApplyIdentityCount);
        Assert.Equal(1, _config.ApplyCredentialHelperCount);
        Assert.Equal(0, _git.InitializeCount);
    }

    [Fact]
    public async Task UpdateRemote_ReapplyFailureReportsSavedAddressAndRetainsOriginalBinding()
    {
        _git.ValidateResult = true;
        var project = (await _service.AddAsync(_root, bindDefaultAccount: false)).Value!;
        var account = new Account { GitName = "bound" };
        await _accounts.AddAsync(account);
        Assert.True((await _bindingService.BindAsync(project.Id, account.Id)).IsSuccess);
        var binding = await _bindings.GetByProjectIdAsync(project.Id);
        _config.ThrowOnApply = true;

        var result = await _service.UpdateOriginUrlAsync(project.Id, "https://example.invalid/repo.git");

        Assert.Equal("PROJECT_REMOTE_SAVED_BINDING_APPLY_FAILED", result.Error!.Code);
        Assert.Equal("https://example.invalid/repo.git", (await _projects.GetByIdAsync(project.Id))!.OriginUrl);
        Assert.Equal("https://example.invalid/repo.git", _git.LastSetOriginUrl);
        Assert.Same(binding, await _bindings.GetByProjectIdAsync(project.Id));
        Assert.Equal(account.Id, binding!.AccountId);
        Assert.Equal(0, _git.InitializeCount);
        Assert.Equal(0, _transfer.TestCount);
        Assert.Equal(0, _transfer.PullCount);
    }

    [Fact]
    public async Task GroupPull_PlainDirectoryStopsButGitWithoutCachedOriginDelegatesToTransfer()
    {
        var local = (await _service.AddAsync(_root)).Value!;
        var repositoryPath = Path.Combine(_root, "repository");
        var repository = new Project { RepositoryPath = repositoryPath, GitDir = Path.Combine(repositoryPath, ".git") };
        await _projects.AddAsync(repository);
        _git.ValidateRepository = path => path == repositoryPath;
        await _accounts.AddAsync(new Account { IsDefault = true });
        _transfer.PullResult = Result.Failure(new DomainError("TRANSFER_REMOTE_MISSING", new[] { "git remote", "2" }));

        var result = await _transferService.PullGroupAsync(null, true);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Failed);
        Assert.Equal("PROJECT_GIT_NOT_INITIALIZED", result.Value.Outcomes.Single(x => x.ProjectId == local.Id).Error!.Code);
        Assert.Equal("TRANSFER_REMOTE_MISSING", result.Value.Outcomes.Single(x => x.ProjectId == repository.Id).Error!.Code);
        Assert.Equal(1, _transfer.PullCount);
        AssertNoGitWrites();
    }

    [Fact]
    public async Task Remove_PlainDirectoryOnlyRemovesRegistration()
    {
        var file = Path.Combine(_root, "keep.txt");
        File.WriteAllText(file, "keep");
        var project = (await _service.AddAsync(_root)).Value!;

        var result = await _service.RemoveAsync(project.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(await _projects.GetByIdAsync(project.Id));
        Assert.True(Directory.Exists(_root));
        Assert.Equal("keep", File.ReadAllText(file));
        AssertNoGitWrites();
    }

    private void AssertNoGitWrites()
    {
        Assert.Equal(0, _git.InitializeCount);
        Assert.Null(_git.LastSetOriginUrl);
        Assert.Equal(0, _config.ApplyIdentityCount);
        Assert.Equal(0, _config.ApplySshCommandCount);
        Assert.Equal(0, _config.ApplyCredentialHelperCount);
        Assert.Equal(0, _config.RestoreCount);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
