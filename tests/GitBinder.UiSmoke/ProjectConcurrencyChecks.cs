using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Desktop;
using GitBinder.Desktop.ViewModels;
using GitBinder.Desktop.Views;
using GitBinder.Domain.Common;

namespace GitBinder.UiSmoke;

public sealed partial class SmokeApp
{
    private async Task VerifyProjectConcurrencyAsync()
    {
        var vm = _fixture.Projects;
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(item => item.Key == "Nav.Projects");
        vm.SearchText = string.Empty;
        vm.SelectedGroup = vm.Groups.Single(group => group.IsAll);
        await SettleAsync();
        var view = _window.GetVisualDescendants().OfType<ProjectsView>().Single();
        await vm.LoadStoredAsync();
        await SettleAsync();
        var accountChoices = view.GetVisualDescendants().OfType<ComboBox>()
            .Where(combo => combo.Classes.Contains("project-binding-account")).ToList();
        Check(accountChoices.Count > 0, "账号回填检查找到可见的真实下拉框");
        foreach (var combo in accountChoices)
        {
            if (combo.DataContext is not ProjectItemViewModel { BoundAccount: { } bound } row) continue;
            Check(combo.SelectedItem is GitBinder.Domain.Accounts.Account selected && selected.Id == bound.Id,
                $"刷新后绑定账号下拉框实际选中已登记账号：{row.Name}");
            Check(ReferenceEquals(combo.SelectedItem, row.SelectedBindingAccount),
                $"控件选项与 VM 使用同一账号实例：{row.Name}");
            Check(combo.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == bound.DisplayAlias),
                $"绑定账号实际显示别名：{row.Name}");
        }
        var first = vm.Items.Single(item => item.Name == "gitbinder-ui");
        var second = vm.Items.Single(item => item.Name == "service-api");
        var originalRemote = second.Project.OriginUrl;
        var originalCalls = _fixture.Transfer.PullCount;
        var firstPull = new DeferredSmokePull();
        var secondPull = new DeferredSmokePull();
        _fixture.Transfer.OnPull = (path, _, ct) => path == first.PathText
            ? firstPull.RunAsync(ct) : secondPull.RunAsync(ct);
        Task firstTask = Task.CompletedTask;
        Task secondTask = Task.CompletedTask;
        try
        {
            firstTask = RunBoundCommand(ProjectButton(view, first, vm.PullCommand));
            await firstPull.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SettleAsync();

            Check(first.IsBusy && !second.IsBusy && vm.CanManageProjects && vm.HasPendingOperations,
                "单项拉取只标记当前行忙碌，页面保留独立操作能力");
            Check(!ProjectButton(view, first, vm.PullCommand).IsEffectivelyEnabled
                && !ProjectButton(view, first, vm.RemoveCommand).IsEffectivelyEnabled
                && !ProjectButton(view, first, vm.TestBindingCommand).IsEffectivelyEnabled
                && !ProjectPicker(view, first, group: false).IsEffectivelyEnabled
                && !ProjectPicker(view, first, group: true).IsEffectivelyEnabled,
                "忙碌行实际绑定禁用拉取、移除、认证、账号和分组修改");
            Check(ProjectButton(view, second, vm.PullCommand).IsEffectivelyEnabled
                && ProjectButton(view, second, vm.StartEditRemoteCommand).IsEffectivelyEnabled
                && ProjectButton(view, second, vm.TestBindingCommand).IsEffectivelyEnabled
                && ProjectPicker(view, second, group: false).IsEffectivelyEnabled
                && ProjectPicker(view, second, group: true).IsEffectivelyEnabled,
                "其他项目实际按钮和下拉仍可用，共用拉取命令不全局锁定");
            Check(ProjectButton(view, first, vm.OpenDirectoryCommand).IsEffectivelyEnabled
                && SearchBox().IsEffectivelyEnabled
                && view.GetVisualDescendants().OfType<ListBox>().Single(list => list.Classes.Contains("project-groups")).IsEffectivelyEnabled,
                "忙碌项目目录入口、搜索与分组浏览保持可用（未启动文件管理器）");
            Check(view.FindControl<Button>("CloneRepositoryButton")!.IsEffectivelyEnabled
                && view.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("project-entry")
                    && ReferenceEquals(button.Command, vm.CreateCommand)).IsEffectivelyEnabled,
                "单项操作不禁用页头添加项目和克隆仓库入口");
            var batchButtons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, vm.PullGroupCommand)).ToArray();
            Check(batchButtons.Length > 0 && batchButtons.All(button => !button.IsEffectivelyEnabled)
                && !_fixture.Main.CanCloseWithEscape,
                "任一项目忙碌时批量拉取和主窗口 Escape 关闭仍受保护");

            SearchBox().Text = "service-api";
            await SettleAsync();
            Check(vm.FilteredItems.Count == 1 && ReferenceEquals(vm.FilteredItems[0], second) && first.IsBusy,
                "单行拉取期间真实搜索输入仍过滤列表且不终止隐藏行任务");
            SearchBox().Text = string.Empty;
            await SettleAsync();

            RunBoundAction(ProjectButton(view, second, vm.StartEditRemoteCommand));
            await SettleAsync();
            var remote = InlineInput(view, second, "project-remote-editor");
            remote.Text = "https://example.test/escape-not-saved.git";
            await SettleAsync();
            Check(remote.Focus(), "其他项目地址编辑在首行拉取中可获得焦点");
            PressEscape(remote);
            await SettleAsync();
            Check(!second.IsRemoteEditorOpen && first.IsBusy && _window.IsVisible,
                "其他项目忙碌不妨碍 Escape 取消当前空闲行的编辑");

            RunBoundAction(ProjectButton(view, second, vm.StartEditRemoteCommand));
            await SettleAsync();
            const string draftRemote = "https://example.test/concurrency-draft.git";
            InlineInput(view, second, "project-remote-editor").Text = draftRemote;
            await SettleAsync();
            Check(second.EditingOriginUrl == draftRemote, "真实地址输入绑定保留未保存草稿");
            secondTask = RunBoundCommand(ProjectButton(view, second, vm.PullCommand));
            await secondPull.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SettleAsync();
            Check(first.IsBusy && second.IsBusy && _fixture.Transfer.PullCount == originalCalls + 2,
                "同一拉取命令通过两张卡片绑定可同时进入两个受控 Fake");
            await vm.PullCommand.ExecuteAsync(first).WaitAsync(TimeSpan.FromSeconds(5));
            Check(_fixture.Transfer.PullCount == originalCalls + 2, "同项目重复执行被项目门禁拒绝");
            Check(ProjectButton(view, first, vm.CancelProjectOperationCommand).IsEffectivelyEnabled
                && ProjectButton(view, second, vm.CancelProjectOperationCommand).IsEffectivelyEnabled
                && ProjectButton(view, first, vm.CancelProjectOperationCommand).IsVisible
                && ProjectButton(view, second, vm.CancelProjectOperationCommand).IsVisible
                && first.HasOperationProgress && second.HasOperationProgress
                && !InlineInput(view, second, "project-remote-editor").IsEffectivelyEnabled,
                "两行各自显示可用取消入口和进度，忙碌行草稿暂不可编辑");
            Capture(_window, "projects-operations-running");

            await vm.LoadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await SettleAsync();
            Check(ReferenceEquals(first, vm.Items.Single(item => item.Project.Id == first.Project.Id))
                && ReferenceEquals(second, vm.Items.Single(item => item.Project.Id == second.Project.Id))
                && second.IsRemoteEditorOpen && second.EditingOriginUrl == draftRemote,
                "运行中全量刷新保留项目实例、任务状态和另一行未保存输入");

            RunBoundAction(ProjectButton(view, first, vm.CancelProjectOperationCommand));
            await firstPull.CancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SettleAsync();
            Check(first.IsBusy && second.IsBusy && !firstTask.IsCompleted
                && !secondPull.CancellationRequested.Task.IsCompleted
                && !ProjectButton(view, first, vm.CancelProjectOperationCommand).IsVisible
                && ProjectButton(view, second, vm.CancelProjectOperationCommand).IsEffectivelyEnabled,
                "取消首行只请求自身取消并保留收尾状态，第二行取消入口仍可用");
            firstPull.Release.TrySetResult();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
            await DismissNoticesAsync();
            Check(!first.IsBusy && second.IsBusy && vm.HasPendingOperations
                && second.IsRemoteEditorOpen && second.EditingOriginUrl == draftRemote
                && InlineInput(view, second, "project-remote-editor").Text == draftRemote,
                "首行取消及刷新结束不清理第二行任务、编辑状态或实际输入");
            Check(ProjectButton(view, first, vm.PullCommand).IsEffectivelyEnabled
                && !ProjectButton(view, second, vm.PullCommand).IsEffectivelyEnabled
                && !_fixture.Main.CanCloseWithEscape,
                "首行单独恢复操作，最后一个任务结束前仍保留退出保护");
            Capture(_window, "projects-operations-cancel-one");

            secondPull.Release.TrySetResult();
            await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
            await DismissNoticesAsync();
            Check(!vm.HasPendingOperations && !first.IsBusy && !second.IsBusy && vm.CanPullGroup
                && !first.HasOperationProgress && !second.HasOperationProgress,
                "全部项目任务及刷新结束后释放忙碌、进度和批量门禁");
            Check(second.IsRemoteEditorOpen && second.EditingOriginUrl == draftRemote
                && second.Project.OriginUrl == originalRemote
                && InlineInput(view, second, "project-remote-editor").IsEffectivelyEnabled,
                "目标行自身刷新也保留草稿且恢复实际输入，不擅自保存地址");
            var finalRemote = InlineInput(view, second, "project-remote-editor");
            Check(finalRemote.Focus(), "任务完成后地址输入恢复焦点能力");
            PressEscape(finalRemote);
            await SettleAsync();
            Check(!second.IsRemoteEditorOpen && _window.IsVisible, "任务完成后 Escape 正常取消保留的草稿");
            Capture(_window, "projects-operations-finished");
            Check(App.Services is null && _fixture.Transfer.TestCount == 0,
                "并发 UI 验证未建立真实服务容器、执行认证或触发外部命令");
        }
        finally
        {
            firstPull.Release.TrySetResult();
            secondPull.Release.TrySetResult();
            await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));
            _fixture.Transfer.OnPull = null;
            await DismissNoticesAsync();
        }
    }

    private static Button ProjectButton(ProjectsView view, ProjectItemViewModel item, ICommand command)
        => view.GetVisualDescendants().OfType<Button>().Single(button =>
            ReferenceEquals(button.DataContext, item) && ReferenceEquals(button.Command, command)
            && ReferenceEquals(button.CommandParameter, item));

    private static ComboBox ProjectPicker(ProjectsView view, ProjectItemViewModel item, bool group)
        => view.GetVisualDescendants().OfType<ComboBox>().Single(combo =>
            ReferenceEquals(combo.DataContext, item) && combo.Classes.Contains("project-group-choice") == group);

    // 执行真实按钮绑定的命令，不调用按钮内部逻辑，也不向系统发送鼠标/键盘事件
    private static Task RunBoundCommand(Button button)
    {
        if (!button.IsEffectivelyEnabled || button.Command is not IAsyncRelayCommand command)
            throw new InvalidOperationException("Expected an enabled asynchronous command binding");
        return command.ExecuteAsync(button.CommandParameter);
    }

    private static void RunBoundAction(Button button)
    {
        if (!button.IsEffectivelyEnabled || button.Command is null)
            throw new InvalidOperationException("Expected an enabled command binding");
        button.Command.Execute(button.CommandParameter);
    }

    private sealed class DeferredSmokePull
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result> RunAsync(CancellationToken ct)
        {
            using var registration = ct.Register(() => CancellationRequested.TrySetResult());
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();
            return Result.Success();
        }
    }
}
