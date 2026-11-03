using System.Globalization;
using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Application.Security;
using GitBinder.Desktop.ViewModels;
using GitBinder.Domain.Accounts;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

/// <summary>仅验证内存快照回填，不启动 UI、数据库或真实 Git。</summary>
public sealed class ListSnapshotReuseTests
{
    [Fact]
    public async Task Accounts_ReloadReusesRowsAndCollectionsButReplacesEntitySnapshot()
    {
        var repository = new FakeAccountRepository();
        var account = new Account { Alias = "Work", GitEmail = "old@example.test" };
        await repository.AddAsync(account);
        var vm = CreateAccounts(repository);
        await vm.LoadAsync();
        var row = Assert.Single(vm.Items);
        var items = vm.Items;
        var visible = vm.FilteredItems;
        var resets = 0;
        vm.Items.CollectionChanged += (_, _) => resets++;
        var replacement = new Account { Id = account.Id, Alias = "Work renamed", GitEmail = "new@example.test", IsDefault = true };
        await repository.UpdateAsync(replacement);

        await vm.LoadAsync();

        Assert.Same(items, vm.Items);
        Assert.Same(visible, vm.FilteredItems);
        Assert.Same(row, Assert.Single(vm.Items));
        Assert.Same(replacement, row.Account);
        Assert.Equal("Work renamed", row.DisplayAlias);
        Assert.Equal("new@example.test", row.EmailText);
        Assert.True(row.IsDefault);
        Assert.Equal(0, resets);
        vm.SearchText = "new@example";
        Assert.Same(row, Assert.Single(vm.FilteredItems));
    }

    [Fact]
    public async Task Accounts_InPlaceMutationNotifiesChangedFieldsWithoutRecreatingRow()
    {
        var repository = new FakeAccountRepository();
        var account = new Account { Alias = "First", GitEmail = "same@example.test" };
        await repository.AddAsync(account);
        var vm = CreateAccounts(repository);
        await vm.LoadAsync();
        var row = Assert.Single(vm.Items);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        account.Alias = "Changed";
        account.Enabled = false;

        await vm.LoadAsync();

        Assert.Same(row, Assert.Single(vm.Items));
        Assert.Equal("Changed", row.DisplayAlias);
        Assert.False(row.Enabled);
        Assert.Contains(nameof(AccountItemViewModel.DisplayAlias), changed);
        Assert.Contains(nameof(AccountItemViewModel.Enabled), changed);
        Assert.DoesNotContain(nameof(AccountItemViewModel.EmailText), changed);
        Assert.DoesNotContain(string.Empty, changed);
        changed.Clear();
        await vm.LoadAsync();
        Assert.Empty(changed);
    }

    [Fact]
    public async Task Platforms_ReloadRetainsRowAndShowsFreshNameHostAndEnabledState()
    {
        var repository = new MemoryPlatforms();
        var platform = new GitPlatform { Name = "Initial", Host = "old.test" };
        await repository.AddAsync(platform);
        var localization = new TestLocalization();
        var vm = new PlatformsViewModel(new PlatformService(repository, new FakeSettingsRepository()), localization);
        await vm.LoadAsync();
        var row = Assert.Single(vm.Items);
        var visible = vm.FilteredItems;
        var replacement = new GitPlatform { Id = platform.Id, Name = "Renamed", Host = "new.test", Enabled = false };
        await repository.UpdateAsync(replacement);

        await vm.LoadAsync();

        Assert.Same(row, Assert.Single(vm.Items));
        Assert.Same(visible, vm.FilteredItems);
        Assert.Same(replacement, row.Platform);
        Assert.Equal("Renamed", row.Name);
        Assert.Equal("new.test", row.Host);
        Assert.False(row.Enabled);
        Assert.EndsWith("Platforms.Status.Disabled", row.StatusText);
        Assert.EndsWith("Platforms.Enable", row.ToggleText);
        vm.SearchText = "new.test";
        Assert.Same(row, Assert.Single(vm.FilteredItems));
    }

    [Fact]
    public async Task Platforms_CultureChangeRefreshesExistingRowWithoutReload()
    {
        var repository = new MemoryPlatforms();
        await repository.AddAsync(new GitPlatform { Name = "Example" });
        var localization = new TestLocalization();
        var vm = new PlatformsViewModel(new PlatformService(repository, new FakeSettingsRepository()), localization);
        await vm.LoadAsync();
        var row = Assert.Single(vm.Items);
        var oldStatus = row.StatusText;

        await localization.SetCultureAsync(CultureInfo.GetCultureInfo("en-US"));

        Assert.Same(row, Assert.Single(vm.Items));
        Assert.NotEqual(oldStatus, row.StatusText);
        Assert.StartsWith("en-US:", row.StatusText);
        Assert.StartsWith("en-US:", row.ToggleText);
    }

    private static AccountsViewModel CreateAccounts(FakeAccountRepository repository) => new(repository,
        new AccountService(repository, new NoBindings(), new SecretService(new FakeSecretStore()), new FakePrivateKeyImporter()),
        new TestLocalization(), new NoUiServices());

    private sealed class NoBindings : IAccountDeletionGuard
    {
        public Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoUiServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("No UI in snapshot tests.");
    }

    private sealed class MemoryPlatforms : IPlatformRepository
    {
        private readonly Dictionary<Guid, GitPlatform> _items = [];
        public Task<GitPlatform?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(id));
        public Task<GitPlatform?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_items.Values.FirstOrDefault(item => item.Name == name));
        public Task<IReadOnlyList<GitPlatform>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitPlatform>>(_items.Values.ToArray());
        public Task AddAsync(GitPlatform platform, CancellationToken ct = default) { _items[platform.Id] = platform; return Task.CompletedTask; }
        public Task UpdateAsync(GitPlatform platform, CancellationToken ct = default) => AddAsync(platform, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) { _items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("zh-CN");
        public IReadOnlyList<CultureInfo> SupportedCultures { get; } = [CultureInfo.GetCultureInfo("zh-CN"), CultureInfo.GetCultureInfo("en-US")];
        public event EventHandler? CultureChanged;
        public string GetString(string key) => CurrentCulture.Name + ":" + key;
        public string GetString(string key, params object[] args) => GetString(key);
        public Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
