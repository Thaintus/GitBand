using System.Globalization;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>只验证内存列表过滤，不启动桌面程序、Git、数据库或真实凭据服务。</summary>
public sealed class ListFilteringViewModelTests
{
    [Theory]
    [InlineData("  TEAM  ")]
    [InlineData("研发")]
    [InlineData("COMMITTER")]
    [InlineData("ALICE@EXAMPLE")]
    [InlineData("云效")]
    [InlineData("CODEUP.COMPANY")]
    public async Task Accounts_SearchMatchesAnyPublicField(string query)
    {
        var fixture = new Fixture();
        var expected = new Account
        {
            Alias = "研发 Team",
            GitName = "Committer Alice",
            GitEmail = "alice@example.net",
            PlatformName = "阿里云云效",
            Host = "codeup.company.test",
        };
        await fixture.Accounts.AddAsync(expected);
        await fixture.Accounts.AddAsync(new Account { Alias = "Other" });
        var viewModel = fixture.CreateAccounts();
        await viewModel.LoadAsync();

        viewModel.SearchText = query;

        Assert.Same(expected, Assert.Single(viewModel.FilteredItems).Account);
        Assert.Equal(2, viewModel.Items.Count);
        Assert.True(viewModel.HasItems);
        Assert.True(viewModel.HasSearchText);
        Assert.False(viewModel.HasNoMatches);
    }

    [Fact]
    public async Task Accounts_NoDataDiffersFromNoMatch_AndWhitespaceOrClearRestoresAll()
    {
        var fixture = new Fixture();
        var viewModel = fixture.CreateAccounts();
        viewModel.SearchText = "missing";
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasItems);
        Assert.False(viewModel.HasNoMatches);

        await fixture.Accounts.AddAsync(new Account { Alias = "Alpha" });
        await fixture.Accounts.AddAsync(new Account { Alias = "Beta" });
        await viewModel.LoadAsync();
        Assert.True(viewModel.HasItems);
        Assert.True(viewModel.HasNoMatches);
        Assert.Empty(viewModel.FilteredItems);

        viewModel.SearchText = " \t ";
        Assert.Equal(2, viewModel.FilteredItems.Count);
        Assert.False(viewModel.HasNoMatches);

        viewModel.SearchText = "Alpha";
        Assert.Single(viewModel.FilteredItems);
        viewModel.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.False(viewModel.HasSearchText);
        Assert.Equal(2, viewModel.FilteredItems.Count);
    }

    [Fact]
    public async Task Accounts_ReloadKeepsQueryAndFiltersFreshRecords()
    {
        var fixture = new Fixture();
        var first = new Account { Alias = "Work first" };
        await fixture.Accounts.AddAsync(first);
        var viewModel = fixture.CreateAccounts();
        await viewModel.LoadAsync();
        viewModel.SearchText = "work";
        await fixture.Accounts.DeleteAsync(first.Id);
        var replacement = new Account { Alias = "Work replacement" };
        await fixture.Accounts.AddAsync(replacement);
        await fixture.Accounts.AddAsync(new Account { Alias = "Personal" });

        await viewModel.LoadAsync();

        Assert.Equal("work", viewModel.SearchText);
        Assert.Same(replacement, Assert.Single(viewModel.FilteredItems).Account);
        Assert.Equal(2, viewModel.Items.Count);
    }

    [Theory]
    [InlineData("支付")]
    [InlineData("  LEDGER-SOURCE  ")]
    [InlineData("COMMERCE.GIT")]
    [InlineData("MIRROR.EXAMPLE")]
    [InlineData("https")]
    [InlineData("cloud build")]
    [InlineData("GLOBAL RUN")]
    public async Task Projects_SearchMatchesProjectMetadataAndBothAccountAliases(string query)
    {
        var fixture = new Fixture();
        var bound = new Account { Alias = "Cloud Builder" };
        var effective = new Account { Alias = "Global Runner" };
        await fixture.Accounts.AddAsync(bound);
        await fixture.Accounts.AddAsync(effective);
        fixture.GlobalMode.IsEnabled = true;
        fixture.GlobalMode.GlobalAccountId = effective.Id;
        var project = new Project
        {
            Name = "支付 Gateway",
            RepositoryPath = @"D:\work\ledger-source",
            OriginUrl = "https://git.example.org/team/commerce.git",
            RemoteHost = "mirror.example.net",
            RemoteProtocol = RemoteProtocol.Https,
        };
        await fixture.Projects.AddAsync(project);
        await fixture.Bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = bound.Id });
        var viewModel = fixture.CreateProjects();
        await viewModel.LoadAsync();

        viewModel.SearchText = query;

        Assert.Same(project, Assert.Single(viewModel.FilteredItems).Project);
        Assert.False(viewModel.HasNoMatches);
    }

    [Fact]
    public async Task Projects_FilterPreservesItemReferenceRemoteDraftAndFullAccountOptions()
    {
        var fixture = new Fixture();
        var firstAccount = new Account { Alias = "First" };
        var secondAccount = new Account { Alias = "Second" };
        await fixture.Accounts.AddAsync(firstAccount);
        await fixture.Accounts.AddAsync(secondAccount);
        await fixture.Accounts.AddAsync(new Account { Alias = "Disabled", Enabled = false });
        var project = new Project { Name = "Alpha", OriginUrl = "git@example.org:team/alpha.git" };
        await fixture.Projects.AddAsync(project);
        await fixture.Projects.AddAsync(new Project { Name = "Beta" });
        await fixture.Bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = firstAccount.Id });
        var viewModel = fixture.CreateProjects();
        await viewModel.LoadAsync();
        var item = viewModel.Items.Single(candidate => candidate.Project.Id == project.Id);
        item.BeginRemoteEdit();
        item.EditingOriginUrl = "https://example.org/team/new-alpha.git";
        item.SelectedBindingAccount = secondAccount;
        item.IsSwitchingBinding = true;

        viewModel.SearchText = "Alpha";
        Assert.Same(item, Assert.Single(viewModel.FilteredItems));
        Assert.Equal(2, item.AvailableAccounts.Count);
        Assert.Contains(firstAccount, item.AvailableAccounts);
        Assert.Contains(secondAccount, item.AvailableAccounts);
        viewModel.SearchText = "missing";
        Assert.Empty(viewModel.FilteredItems);
        Assert.True(viewModel.HasNoMatches);
        viewModel.ClearSearchCommand.Execute(null);

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Equal(2, viewModel.FilteredItems.Count);
        Assert.Same(item, viewModel.FilteredItems.Single(candidate => candidate.Project.Id == project.Id));
        Assert.True(item.IsRemoteEditorOpen);
        Assert.Equal("https://example.org/team/new-alpha.git", item.EditingOriginUrl);
        Assert.Same(secondAccount, item.SelectedBindingAccount);
        Assert.True(item.IsSwitchingBinding);
        Assert.False(viewModel.HasSearchText);
        Assert.False(viewModel.HasNoMatches);
        Assert.Equal(0, fixture.GitConfig.ApplyIdentityCount);
        Assert.Equal(0, fixture.GitConfig.ApplyCredentialHelperCount);
    }

    [Fact]
    public async Task Projects_BusyBindingWithoutAcceptedChoiceRestoresBoundAccountWithoutWritingBinding()
    {
        var fixture = new Fixture();
        var bound = new Account { Alias = "Bound" };
        var pending = new Account { Alias = "Pending" };
        var repeated = new Account { Alias = "Repeated" };
        foreach (var account in new[] { bound, pending, repeated })
        {
            await fixture.Accounts.AddAsync(account);
        }

        var project = new Project { Name = "Alpha", GitDir = Path.Combine("Alpha", ".git") };
        await fixture.Projects.AddAsync(project);
        await fixture.Bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = bound.Id });
        var viewModel = fixture.CreateProjects();
        await viewModel.LoadAsync();
        var item = Assert.Single(viewModel.Items);
        item.SelectedBindingAccount = pending;
        item.IsSwitchingBinding = true;

        await viewModel.SwitchBindingAccountAsync(item, repeated);

        Assert.Same(bound, item.SelectedBindingAccount);
        Assert.True(item.IsSwitchingBinding);
        Assert.Same(item, Assert.Single(viewModel.Items));
        Assert.Equal(bound.Id, (await fixture.Bindings.GetByProjectIdAsync(project.Id))!.AccountId);
        Assert.Equal(0, fixture.GitConfig.ApplyIdentityCount);
    }

    [Theory]
    [InlineData("dummy-userinfo")]
    [InlineData("dummy-password")]
    public async Task Projects_SearchDoesNotMatchHiddenHttpsUserInfo(string hiddenQuery)
    {
        var fixture = new Fixture();
        // 仅用于覆盖旧数据脱敏边界的虚构用户信息，不使用任何真实凭据。
        await fixture.Projects.AddAsync(new Project
        {
            Name = "Service",
            OriginUrl = "https://dummy-userinfo:dummy-password@example.org/team/service.git",
            RemoteHost = "example.org",
            RemoteProtocol = RemoteProtocol.Https,
        });
        var viewModel = fixture.CreateProjects();
        await viewModel.LoadAsync();

        viewModel.SearchText = hiddenQuery;
        Assert.Empty(viewModel.FilteredItems);
        Assert.True(viewModel.HasNoMatches);
        viewModel.SearchText = "TEAM/SERVICE";
        Assert.Equal("https://example.org/team/service.git", Assert.Single(viewModel.FilteredItems).OriginUrlDisplay);
    }

    [Fact]
    public async Task Projects_ReloadKeepsQuery_AndEmptySourceIsNotNoMatch()
    {
        var fixture = new Fixture();
        var viewModel = fixture.CreateProjects();
        viewModel.SearchText = "alpha";
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasItems);
        Assert.False(viewModel.HasNoMatches);
        await fixture.Projects.AddAsync(new Project { Name = "Alpha" });
        await fixture.Projects.AddAsync(new Project { Name = "Beta" });

        await viewModel.LoadAsync();

        Assert.Equal("alpha", viewModel.SearchText);
        Assert.Equal("Alpha", Assert.Single(viewModel.FilteredItems).Name);
        viewModel.SearchText = " \t ";
        Assert.Equal(2, viewModel.FilteredItems.Count);
    }

    [Theory]
    [InlineData("云效")]
    [InlineData("  CODEUP.EXAMPLE  ")]
    public async Task Platforms_SearchMatchesNameOrHost(string query)
    {
        var fixture = new Fixture();
        var platform = new GitPlatform { Name = "阿里云云效", Host = "codeup.example.org" };
        await fixture.Platforms.AddAsync(platform);
        await fixture.Platforms.AddAsync(new GitPlatform { Name = "GitHub", Host = "github.com" });
        var viewModel = fixture.CreatePlatforms();
        await viewModel.LoadAsync();

        viewModel.SearchText = query;

        Assert.Same(platform, Assert.Single(viewModel.FilteredItems).Platform);
        Assert.Equal(2, viewModel.Items.Count);
    }

    [Fact]
    public async Task Platforms_FilterKeepsEnabledFirstAndExistingSecondaryOrder()
    {
        var fixture = new Fixture();
        var disabled = new GitPlatform { Name = "GitLab", Enabled = false, SortOrder = 0 };
        var later = new GitPlatform { Name = "GitHub", SortOrder = 2 };
        var earlier = new GitPlatform { Name = "Git priority", SortOrder = 1 };
        var alphabetic = new GitPlatform { Name = "Gitee", SortOrder = 2 };
        foreach (var platform in new[] { disabled, later, earlier, alphabetic })
        {
            await fixture.Platforms.AddAsync(platform);
        }

        var viewModel = fixture.CreatePlatforms();
        await viewModel.LoadAsync();
        viewModel.SearchText = "GIT";

        Assert.Equal(new[] { earlier.Id, alphabetic.Id, later.Id, disabled.Id },
            viewModel.FilteredItems.Select(item => item.Platform.Id));
    }

    [Fact]
    public async Task Platforms_RefreshKeepsQuery()
    {
        var fixture = new Fixture();
        var viewModel = fixture.CreatePlatforms();
        viewModel.SearchText = "hub";
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasItems);
        Assert.False(viewModel.HasNoMatches);
        await fixture.Platforms.AddAsync(new GitPlatform { Name = "GitHub" });
        await fixture.Platforms.AddAsync(new GitPlatform { Name = "GitLab" });
        await viewModel.LoadAsync();
        Assert.Equal("hub", viewModel.SearchText);
        Assert.Equal("GitHub", Assert.Single(viewModel.FilteredItems).Name);

        viewModel.SearchText = "missing";
        Assert.True(viewModel.HasItems);
        Assert.True(viewModel.HasNoMatches);
        viewModel.SearchText = " \t ";
        Assert.Equal(2, viewModel.FilteredItems.Count);
        viewModel.ClearSearchCommand.Execute(null);

        Assert.False(viewModel.HasSearchText);
        Assert.False(viewModel.HasNoMatches);
    }

    [Fact]
    public async Task Projects_GroupFilterCombinesWithSearch_AndAssignmentPersists()
    {
        var fixture = new Fixture();
        var group = new ProjectGroup(Guid.NewGuid(), "Work");
        fixture.Groups.Groups.Add(group.Id, group);
        var project = new Project { Name = "alpha" };
        await fixture.Projects.AddAsync(project);
        await fixture.Projects.AddAsync(new Project { Name = "beta" });
        var vm = fixture.CreateProjects();
        await vm.LoadAsync();
        var item = vm.Items.Single(i => i.Project.Id == project.Id);
        await vm.SwitchProjectGroupAsync(item, vm.Groups.Single(g => g.Id == group.Id));
        vm.SelectedGroup = vm.Groups.Single(g => g.Id == group.Id);
        Assert.Equal(project.Id, Assert.Single(vm.FilteredItems).Project.Id);
        vm.SearchText = "beta";
        Assert.Empty(vm.FilteredItems);
        Assert.Equal(1, vm.SelectedGroup.Count);
        vm.SearchText = "alpha";
        await vm.LoadAsync();
        Assert.Equal(group.Id, vm.SelectedGroup!.Id);
        Assert.Equal(project.Id, Assert.Single(vm.FilteredItems).Project.Id);
        Assert.Equal(group.Id, fixture.Groups.Memberships[project.Id]);
    }

    private sealed class Fixture
    {
        public FakeAccountRepository Accounts { get; } = new();
        public FakeProjectRepository Projects { get; } = new();
        public FakeProjectGroupRepository Groups { get; } = new();
        public FakeBindingRepository Bindings { get; } = new();
        public FakeGlobalModeState GlobalMode { get; } = new();
        public FakeGitConfigApplier GitConfig { get; } = new();
        public MemoryPlatformRepository Platforms { get; } = new();
        private TestLocalization Localization { get; } = new();

        public AccountsViewModel CreateAccounts()
            => new(Accounts,
                new AccountService(Accounts, new DeletionGuard(Bindings),
                    new SecretService(new FakeSecretStore()), new FakePrivateKeyImporter()),
                Localization, new NoUiServiceProvider());

        public ProjectsViewModel CreateProjects(FakeGitService? git = null)
        {
            // 默认模拟离线仓库：过滤用例应使用缓存数据，不被 Fake 的固定 origin 替换。
            git ??= new FakeGitService { ValidateResult = false };
            var resolver = new EffectiveAccountResolver(GlobalMode, Bindings, Accounts);
            var transfer = new FakeGitTransfer();
            var bindingService = new BindingService(Bindings, Accounts, Projects,
                new FakeSecretStore(), git, GitConfig, new FakeSnapshotRepository(), transfer, resolver);
            var projectService = new ProjectService(Projects, git, new IdentityPathNormalizer(), Accounts, bindingService);
            var transferService = new ProjectTransferService(transfer, projectService, Projects,
                Accounts, bindingService, resolver, new IdentityPathNormalizer(), Groups);
            return new ProjectsViewModel(Projects, projectService, Bindings, Accounts,
                bindingService, resolver, Localization,
                () => throw new InvalidOperationException("列表过滤不能打开目录选择器。"), transferService, new ProjectGroupService(Groups));
        }

        public PlatformsViewModel CreatePlatforms()
            => new(new PlatformService(Platforms, new FakeSettingsRepository()), Localization);
    }

    [Fact]
    public async Task Projects_LoadRefreshesUnknownProtocolFromLocalOrigin()
    {
        var fixture = new Fixture();
        var project = new Project
        {
            Name = "Moved", RepositoryPath = @"D:\moved", GitDir = Path.Combine(@"D:\moved", ".git"),
        };
        await fixture.Projects.AddAsync(project);
        var viewModel = fixture.CreateProjects(new FakeGitService { OriginUrl = "https://gitee.com/team/moved.git" });

        await viewModel.LoadAsync();

        var item = Assert.Single(viewModel.Items);
        Assert.Equal("HTTPS", item.ProtocolText);
        Assert.Equal("gitee.com", item.RemoteHost);
        Assert.False(item.HasMetadataWarning);
        Assert.Equal(0, fixture.GitConfig.ApplyIdentityCount);
        Assert.Equal(0, fixture.GitConfig.ApplyCredentialHelperCount);
    }

    private sealed class MemoryPlatformRepository : IPlatformRepository
    {
        private readonly Dictionary<Guid, GitPlatform> _items = [];

        public Task<GitPlatform?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_items.GetValueOrDefault(id));

        public Task<GitPlatform?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_items.Values.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<GitPlatform>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitPlatform>>(_items.Values.ToList());

        public Task AddAsync(GitPlatform platform, CancellationToken ct = default)
        {
            _items[platform.Id] = platform;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(GitPlatform platform, CancellationToken ct = default) => AddAsync(platform, ct);

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.InvariantCulture;
        public IReadOnlyList<CultureInfo> SupportedCultures { get; } = [CultureInfo.InvariantCulture];
        public event EventHandler? CultureChanged;
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => key;

        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class DeletionGuard(FakeBindingRepository bindings) : IAccountDeletionGuard
    {
        public Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default)
            => bindings.CountByAccountIdAsync(accountId, ct);
    }

    private sealed class TestDataPath : IApplicationDataPath
    {
        public string DataDirectory => Path.Combine(Path.GetTempPath(), "gitbinder-list-filter-not-created");
        public string DatabasePath => Path.Combine(DataDirectory, "gitbinder.db");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
        public string KeysDirectory => Path.Combine(DataDirectory, "keys");
    }

    private sealed class NoUiServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => throw new InvalidOperationException("列表过滤不能解析或打开窗口服务。");
    }

    private sealed class IdentityPathNormalizer : IPathNormalizer
    {
        public string Normalize(string path) => path;
        public string Canonicalize(string path) => path;
        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        public string ToKey(string path) => path.ToUpperInvariant();
    }
}
