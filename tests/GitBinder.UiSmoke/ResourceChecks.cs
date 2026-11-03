using System.Diagnostics;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using GitBinder.Application.Settings;
using GitBinder.Desktop;
using GitBinder.Desktop.Controls;
using GitBinder.Desktop.ViewModels;
using GitBinder.Desktop.Views;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Projects;

namespace GitBinder.UiSmoke;

public sealed partial class SmokeApp
{
    private async Task VerifyResourceUsageAsync()
    {
        const int additionalRows = 300;
        for (var index = 0; index < additionalRows; index++)
        {
            await _fixture.AccountRepository.AddAsync(new Account
            {
                Alias = $"Profile account {index:D3}", GitName = "Profile Developer",
                GitEmail = $"profile{index:D3}@example.test", Host = "profile.example.test",
                PlatformName = "Profile platform",
            });
            await _fixture.PlatformRepository.AddAsync(new GitPlatform
            {
                Name = $"Profile platform {index:D3}", Host = $"platform{index:D3}.example.test", SortOrder = index + 10,
            });
            var path = @"X:\UI-Smoke-Demo\profile-project-" + index.ToString("D3");
            await _fixture.ProjectRepository.AddAsync(new Project
            {
                Name = $"profile-project-{index:D3}", RepositoryPath = path, CanonicalPath = path,
                GitDir = path + @"\.git", OriginUrl = "https://github.com/example/profile.git",
                RemoteHost = "github.com", RemoteProtocol = RemoteProtocol.Https, CurrentBranch = "main",
            });
        }
        foreach (var name in new[] { "Profile group A", "Profile group B" })
        {
            var group = new ProjectGroup(Guid.NewGuid(), name);
            _fixture.Groups.Groups.Add(group.Id, group);
        }
        await _fixture.Accounts.LoadAsync();
        await _fixture.Platforms.LoadAsync();
        await _fixture.Projects.LoadStoredAsync();
        var accountRows = _fixture.Accounts.Items.ToDictionary(item => item.Account.Id);
        var platformRows = _fixture.Platforms.Items.ToDictionary(item => item.Platform.Id);
        var projectRows = _fixture.Projects.Items.ToDictionary(item => item.Project.Id);
        var accountItems = _fixture.Accounts.Items;
        var platformItems = _fixture.Platforms.Items;
        var projectItems = _fixture.Projects.Items;
        var virtualLists = new List<object>();

        foreach (var scope in new[] { ListOrderScope.Accounts, ListOrderScope.Platforms, ListOrderScope.Projects })
            virtualLists.Add(await InspectVirtualListAsync(scope));

        // 反复切页触发真实导航读取；只使用 Fake，不执行任何外部仓库命令。
        var navigationTimer = Stopwatch.StartNew();
        for (var cycle = 0; cycle < 3; cycle++)
            foreach (var scope in new[] { ListOrderScope.Accounts, ListOrderScope.Platforms, ListOrderScope.Projects })
                await NavigateResourcePageAsync(scope);
        navigationTimer.Stop();
        Check(ReferenceEquals(accountItems, _fixture.Accounts.Items)
            && _fixture.Accounts.Items.All(item => ReferenceEquals(accountRows[item.Account.Id], item)),
            "300新增账号：反复导航后复用集合和全部行 VM");
        Check(ReferenceEquals(platformItems, _fixture.Platforms.Items)
            && _fixture.Platforms.Items.All(item => ReferenceEquals(platformRows[item.Platform.Id], item)),
            "300新增平台：反复导航后复用集合和全部行 VM");
        Check(ReferenceEquals(projectItems, _fixture.Projects.Items)
            && _fixture.Projects.Items.All(item => ReferenceEquals(projectRows[item.Project.Id], item)),
            "300新增项目：反复导航后复用集合和全部行 VM");

        foreach (var scope in new[] { ListOrderScope.Accounts, ListOrderScope.Platforms, ListOrderScope.Projects, ListOrderScope.Groups })
            await VerifyPublicPreviewAsync(scope);

        var cachedProjectView = _window.GetVisualDescendants().OfType<ProjectsView>().Single();
        // 同一内存设置仓储中验证一次保存+重载；这不是指针长按/捕获的自动化。
        await NavigateResourcePageAsync(ListOrderScope.Accounts);
        var first = _fixture.Accounts.FilteredItems[0];
        var second = _fixture.Accounts.FilteredItems[1];
        var saved = ((IListOrderHost)_fixture.Accounts).BeginReorderPreview(ListOrderScope.Accounts, first);
        Check(saved is not null && saved.Move(second, after: true), "账号预览在保存前先移动展示集合");
        var expectedOrder = _fixture.Accounts.FilteredItems.Select(item => item.Account.Id).ToArray();
        await saved!.CommitAsync();
        await _fixture.Accounts.LoadAsync();
        Check(expectedOrder.SequenceEqual(_fixture.Accounts.FilteredItems.Select(item => item.Account.Id)),
            "账号预览提交后重载内存仓储仍保留最终顺序");
        Capture(_window, "resource-accounts-saved");

        // 完成截图及导航后再等待，观测时不强制 GC、不清空工作集。
        await Task.Delay(1000);
        var cachedProgressBars = cachedProjectView.GetVisualDescendants().OfType<ProgressBar>().ToArray();
        var originalIndeterminate = cachedProgressBars.Select(bar => bar.IsIndeterminate).ToArray();
        var progressStates = cachedProgressBars.Select((bar, index) => new
        {
            Index = index, bar.IsIndeterminate, bar.IsVisible, bar.IsEffectivelyVisible,
            IsAttached = TopLevel.GetTopLevel(bar) is not null, Width = bar.Bounds.Width, Height = bar.Bounds.Height,
        }).ToArray();
        var accountsView = _window.GetVisualDescendants().OfType<AccountsView>().Single();
        var accountsList = ResourceList(ListOrderScope.Accounts);
        var visibleActivity = await ObserveLayoutActivityAsync("visible", accountsView, accountsList);
        var visibleIdle = visibleActivity.Observation;
        object accountsHiddenIdle;
        var originalViewVisible = accountsView.IsVisible;
        try
        {
            accountsView.IsVisible = false;
            await SettleAsync();
            accountsHiddenIdle = await ObserveIdleAsync("visible-window-accounts-view-hidden", TimeSpan.FromSeconds(5));
        }
        finally
        {
            accountsView.IsVisible = originalViewVisible;
            await SettleAsync();
        }
        Check(accountsView.IsVisible == originalViewVisible, "布局诊断后恢复账号视图可见状态");
        object? listHiddenIdle = null;
        if (accountsHiddenIdle.GetType().GetProperty("CpuSeconds")?.GetValue(accountsHiddenIdle) is double hiddenCpu && hiddenCpu == 0)
        {
            var originalListVisible = accountsList.IsVisible;
            try
            {
                accountsList.IsVisible = false;
                await SettleAsync();
                listHiddenIdle = await ObserveIdleAsync("visible-window-only-accounts-list-hidden", TimeSpan.FromSeconds(5));
            }
            finally
            {
                accountsList.IsVisible = originalListVisible;
                await SettleAsync();
            }
            Check(accountsList.IsVisible == originalListVisible, "布局诊断后恢复账号列表可见状态");
        }
        object progressDisabledIdle;
        try
        {
            foreach (var bar in cachedProgressBars) bar.IsIndeterminate = false;
            await SettleAsync();
            progressDisabledIdle = await ObserveIdleAsync("visible-cached-project-progress-disabled", TimeSpan.FromSeconds(5));
        }
        finally
        {
            for (var index = 0; index < cachedProgressBars.Length; index++)
                cachedProgressBars[index].IsIndeterminate = originalIndeterminate[index];
        }
        Check(cachedProgressBars.Select(bar => bar.IsIndeterminate).SequenceEqual(originalIndeterminate),
            "动画归因对照只修改缓存项目视图并在观察后恢复原值");
        _window.Hide();
        await SettleAsync();
        var hiddenIdle = await ObserveIdleAsync("hidden", TimeSpan.FromSeconds(5));
        _window.Show();
        await SettleAsync();
        Check(_window.IsVisible && App.Services is null, "资源观察只隐藏/显示本隔离宿主窗口");

        var report = new
        {
            Mode = "isolated-ui-resource-profile",
            ApplicationVersion = typeof(App).Assembly.GetName().Version?.ToString(),
            TestWindowAutomationSuppressed = SuppressTestAutomation,
            Dataset = new { Accounts = _fixture.Accounts.Items.Count, Platforms = _fixture.Platforms.Items.Count, Projects = _fixture.Projects.Items.Count },
            VirtualLists = virtualLists,
            Navigation = new { Cycles = 3, PageChanges = 9, ElapsedSeconds = navigationTimer.Elapsed.TotalSeconds, RowsAndCollectionsReused = true },
            Preview = new { PublicVmScopesChecked = 4, CancelRestored = true, AccountSaveReloadPreserved = true, NativePointerGesturesChecked = false },
            IdleObservations = new[] { visibleIdle, hiddenIdle },
            LayoutActivityDiagnostic = new
            {
                Visible = visibleActivity.Activity,
                AccountsViewHiddenObservation = accountsHiddenIdle,
                AccountsListHiddenObservation = listHiddenIdle,
                StatesRestored = true,
                CountInterval = "CPU observation interval only; callbacks only increment counters",
            },
            CachedProjectProgressDiagnostic = new
            {
                BarCount = cachedProgressBars.Length,
                OriginallyIndeterminateCount = originalIndeterminate.Count(value => value),
                Before = progressStates,
                DisabledObservation = progressDisabledIdle,
                OriginalValuesRestored = true,
            },
            Isolation = new { RealCompositionRootCreated = false, RealDatabaseAccessed = false, ExternalGitInvoked = false, SystemInputInjected = false, ExistingApplicationControlled = false },
            Limits = new[]
            {
                "Current isolated harness only; no before/after A/B baseline.",
                "CPU percentage uses one logical core as 100%; it is not measured electrical power or watts.",
                "Memory includes this test harness, Fake data and renderer, not a production process measurement.",
                "CPU observation uses only endpoint CPU reads around one async delay; process memory probes run outside that interval.",
                "Memory contains only before/after endpoints, not a continuous peak measurement; no forced GC or working-set trimming.",
                "Preview checks use the public VM contract, not native pointer/long-press/capture automation.",
                "Navigation elapsed time includes deliberate layout settling waits and is not an application latency benchmark.",
                "The diagnostic comparison only disables cached ProjectsView progress animations in this harness, then restores their original values.",
                "Layout diagnostics count events only during the visible CPU interval, then temporarily hide the AccountsView and optionally its ListBox; all handlers and visibility values are restored.",
            },
        };
        await File.WriteAllTextAsync(Path.Combine(OutputDirectory, "resource-profile.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task NavigateResourcePageAsync(ListOrderScope scope)
    {
        var key = scope switch
        {
            ListOrderScope.Accounts => "Nav.Accounts",
            ListOrderScope.Platforms => "Nav.Platforms",
            _ => "Nav.Projects",
        };
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(item => item.Key == key);
        await SettleAsync();
        if (key == "Nav.Projects")
        {
            var timeout = Stopwatch.StartNew();
            while (_fixture.Projects.HasPendingOperations && timeout.Elapsed < TimeSpan.FromSeconds(10)) await SettleAsync();
            Check(!_fixture.Projects.HasPendingOperations, "项目 Fake 元数据刷新完成，无残留操作");
        }
        await SettleAsync();
    }

    private ListBox ResourceList(ListOrderScope scope) => _window.GetVisualDescendants().OfType<ListBox>()
        .Single(list => ListReorder.GetScope(list) == scope);

    private async Task<object> InspectVirtualListAsync(ListOrderScope scope)
    {
        await NavigateResourcePageAsync(scope);
        var list = ResourceList(scope);
        var count = list.Items.Count;
        var topRealized = list.GetRealizedContainers().Count();
        Check(count >= 300 && topRealized > 0 && topRealized < count / 4,
            $"{scope} 顶部虚拟化：{count}条数据，仅实现{topRealized}个容器");
        Check(list.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any(), $"{scope} 实际使用虚拟化面板");
        Capture(_window, "resource-" + scope.ToString().ToLowerInvariant() + "-top");
        list.ScrollIntoView(count - 1);
        await SettleAsync();
        await SettleAsync();
        var bottomRealized = list.GetRealizedContainers().Count();
        Check(list.ContainerFromIndex(count - 1) is not null && bottomRealized > 0 && bottomRealized < count / 4,
            $"{scope} 底部虚拟化：末项已实现，当前{bottomRealized}个容器");
        Capture(_window, "resource-" + scope.ToString().ToLowerInvariant() + "-bottom");
        list.ScrollIntoView(0);
        await SettleAsync();
        return new { Scope = scope.ToString(), DataCount = count, TopRealized = topRealized, BottomRealized = bottomRealized,
            ViewportWidth = list.Bounds.Width, ViewportHeight = list.Bounds.Height };
    }

    private async Task VerifyPublicPreviewAsync(ListOrderScope scope)
    {
        await NavigateResourcePageAsync(scope);
        IListOrderHost host;
        object[] original;
        object source;
        object target;
        if (scope == ListOrderScope.Accounts)
        { host = _fixture.Accounts; original = _fixture.Accounts.FilteredItems.Cast<object>().ToArray(); source = original[0]; target = original[1]; }
        else if (scope == ListOrderScope.Platforms)
        { host = _fixture.Platforms; original = _fixture.Platforms.FilteredItems.Cast<object>().ToArray(); source = original[0]; target = original[1]; }
        else if (scope == ListOrderScope.Groups)
        {
            host = _fixture.Projects;
            original = _fixture.Projects.Groups.Cast<object>().ToArray();
            var custom = _fixture.Projects.Groups.Where(group => group.Id.HasValue).ToArray();
            source = custom[0]; target = custom[1];
        }
        else
        { host = _fixture.Projects; original = _fixture.Projects.FilteredItems.Cast<object>().ToArray(); source = original[0]; target = original[1]; }
        var preview = host.BeginReorderPreview(scope, source);
        Check(preview is not null && preview.Move(target, after: true) && preview.HasChanges,
            $"{scope} 公开 VM 预览立即显示新顺序");
        await SettleAsync();
        var list = ResourceList(scope);
        Check(list.GetVisualDescendants().OfType<Control>().Any(control => ListReorder.GetIsItem(control)
            && ReferenceEquals(control.DataContext, source)), $"{scope} 移动后的源条目实际在控件树中显示");
        Capture(_window, "resource-" + scope.ToString().ToLowerInvariant() + "-preview");
        preview!.Cancel();
        await SettleAsync();
        var restored = list.Items.Cast<object>().ToArray();
        Check(original.SequenceEqual(restored, ReferenceEqualityComparer.Instance), $"{scope} 取消预览后恢复原顺序和条目引用");
    }

    private async Task<(object Observation, object Activity)> ObserveLayoutActivityAsync(string state, AccountsView view, ListBox list)
    {
        var collecting = false;
        long layouts = 0, prepared = 0, cleared = 0, filteredChanges = 0, itemChanges = 0;
        var viewProperties = new Dictionary<string, long>(StringComparer.Ordinal);
        var listProperties = new Dictionary<string, long>(StringComparer.Ordinal);
        var filtered = (INotifyCollectionChanged)_fixture.Accounts.FilteredItems;
        var items = (INotifyCollectionChanged)list.Items;
        static void Increment(Dictionary<string, long> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        void LayoutChanged(object? sender, EventArgs args) { if (collecting) layouts++; }
        void ViewChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        { if (collecting) Increment(viewProperties, args.Property.Name); }
        void ListChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        { if (collecting) Increment(listProperties, args.Property.Name); }
        void ContainerPrepared(object? sender, ContainerPreparedEventArgs args) { if (collecting) prepared++; }
        void ContainerCleared(object? sender, ContainerClearingEventArgs args) { if (collecting) cleared++; }
        void FilteredChanged(object? sender, NotifyCollectionChangedEventArgs args) { if (collecting) filteredChanges++; }
        void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) { if (collecting) itemChanges++; }
        _window.LayoutUpdated += LayoutChanged;
        view.PropertyChanged += ViewChanged;
        list.PropertyChanged += ListChanged;
        list.ContainerPrepared += ContainerPrepared;
        list.ContainerClearing += ContainerCleared;
        filtered.CollectionChanged += FilteredChanged;
        items.CollectionChanged += ItemsChanged;
        try
        {
            var observation = await ObserveIdleAsync(state, TimeSpan.FromSeconds(5), active => collecting = active);
            return (observation, new
            {
                WindowLayoutUpdated = layouts,
                AccountsViewProperties = viewProperties.OrderByDescending(pair => pair.Value).ToDictionary(),
                AccountsListProperties = listProperties.OrderByDescending(pair => pair.Value).ToDictionary(),
                ContainersPrepared = prepared, ContainersCleared = cleared,
                FilteredItemsCollectionChanged = filteredChanges, ListItemsCollectionChanged = itemChanges,
            });
        }
        finally
        {
            collecting = false;
            _window.LayoutUpdated -= LayoutChanged;
            view.PropertyChanged -= ViewChanged;
            list.PropertyChanged -= ListChanged;
            list.ContainerPrepared -= ContainerPrepared;
            list.ContainerClearing -= ContainerCleared;
            filtered.CollectionChanged -= FilteredChanged;
            items.CollectionChanged -= ItemsChanged;
        }
    }

    private static async Task<object> ObserveIdleAsync(string state, TimeSpan duration, Action<bool>? countInterval = null)
    {
        // Process.Refresh + WorkingSet/Private 可能走全系统进程信息查询并分配大缓冲区。
        // 将这些探测放到 CPU 计时区间之外，避免把采样器自身开销当作应用空闲占用。
        await Task.Delay(TimeSpan.FromSeconds(5));
        using var process = Process.GetCurrentProcess();
        var beforeProbe = Stopwatch.StartNew();
        process.Refresh();
        var workingBefore = process.WorkingSet64;
        var privateBefore = process.PrivateMemorySize64;
        beforeProbe.Stop();
        var cpuStart = process.TotalProcessorTime;
        var gcStart = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var clock = Stopwatch.StartNew();
        countInterval?.Invoke(true);
        try { await Task.Delay(duration); }
        finally { countInterval?.Invoke(false); }
        var elapsed = clock.Elapsed.TotalSeconds;
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        var gc = new[] { GC.CollectionCount(0) - gcStart[0], GC.CollectionCount(1) - gcStart[1], GC.CollectionCount(2) - gcStart[2] };
        var managed = GC.GetTotalMemory(forceFullCollection: false);
        var afterProbe = Stopwatch.StartNew();
        process.Refresh();
        var workingAfter = process.WorkingSet64;
        var privateAfter = process.PrivateMemorySize64;
        afterProbe.Stop();
        return new
        {
            State = state, WallSeconds = elapsed, CpuSeconds = cpu, AverageOneCoreCpuPercent = cpu / elapsed * 100,
            LogicalProcessorCount = Environment.ProcessorCount,
            CpuSampleStrategy = "single-async-delay-without-memory-polling",
            StabilityWaitSeconds = 5,
            MemorySampleStrategy = "two-endpoints-outside-cpu-interval",
            WorkingSetMinBytes = Math.Min(workingBefore, workingAfter), WorkingSetMaxBytes = Math.Max(workingBefore, workingAfter),
            PrivateMinBytes = Math.Min(privateBefore, privateAfter), PrivateMaxBytes = Math.Max(privateBefore, privateAfter),
            ManagedHeapBytes = managed, GcCollections = gc,
            MemoryProbeWallSeconds = new[] { beforeProbe.Elapsed.TotalSeconds, afterProbe.Elapsed.TotalSeconds },
            Samples = new[]
            {
                new { Position = "before", WorkingSetBytes = workingBefore, PrivateBytes = privateBefore },
                new { Position = "after", WorkingSetBytes = workingAfter, PrivateBytes = privateAfter },
            },
        };
    }
}
