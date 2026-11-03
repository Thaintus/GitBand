using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitBinder.Desktop;
using GitBinder.Desktop.Views;
using GitBinder.Desktop.Controls;

namespace GitBinder.UiSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        SmokeApp.ScreenshotsOnly = args.Contains("--screenshots-only");
        SmokeApp.ResourceProfile = args.Contains("--resource-profile");
        SmokeApp.DragOnly = args.Contains("--drag-only");
        SmokeApp.SuppressTestAutomation = args.Contains("--suppress-test-uia");
        if (SmokeApp.SuppressTestAutomation && !SmokeApp.ResourceProfile)
            throw new ArgumentException("--suppress-test-uia 仅用于隔离资源诊断，不能作为普通界面验收。");
        SmokeApp.OutputDirectory = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? Path.Combine("tests", "GitBinder.UiSmoke", "artifacts"));
        Directory.CreateDirectory(SmokeApp.OutputDirectory);
        Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(SmokeApp.OutputDirectory, "avalonia.log")));
        Trace.AutoFlush = true;
        var builder = AppBuilder.Configure<SmokeApp>().UsePlatformDetect().WithInterFont().LogToTrace();
        if (args.Contains("--direct-composition"))
            builder.With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface],
            });
        return builder.StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }
}

// 继承真实 App.Initialize 加载已编译 XAML、资源、控件模板；绝不调用 App 的启动/组合根。
public sealed partial class SmokeApp : App
{
    public static string OutputDirectory { get; set; } = string.Empty;
    public static bool ScreenshotsOnly { get; set; }
    public static bool ResourceProfile { get; set; }
    public static bool DragOnly { get; set; }
    public static bool SuppressTestAutomation { get; set; }
    private readonly List<string> _checks = [];
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private MainWindow _window = null!;
    private SmokeFixture _fixture = null!;

    public override void OnFrameworkInitializationCompleted()
    {
        // 故意不调用 base：base 会初始化真实数据库、托盘和真实 Git 配置。
        _desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        Dispatcher.UIThread.Post(async () => await RunAsync());
    }

    private async Task RunAsync()
    {
        var exitCode = 0;
        try
        {
            Check(App.Services is null, "启动未调用真实 CompositionRoot");
            _fixture = new SmokeFixture(OutputDirectory);
            await _fixture.LoadAsync();
            _window = SuppressTestAutomation ? new ResourceDiagnosticMainWindow() : new MainWindow();
            _window.DataContext = _fixture.Main;
            _window.Width = 1180;
            _window.Height = 780;
            _window.ShowInTaskbar = false;
            _desktop.MainWindow = _window;
            _window.Show();
            await SettleAsync();
            if (DragOnly)
            {
                await VerifyDragPreviewAsync();
                Check(App.Services is null, "拖动专项验证未建立真实服务容器");
                return;
            }
            if (ResourceProfile)
            {
                await VerifyResourceUsageAsync();
                Check(App.Services is null, "资源验证结束未建立真实 CompositionRoot");
                Check(_fixture.Transfer.CloneCount == 0 && _fixture.Transfer.PullCount == 0 && _fixture.Transfer.TestCount == 0,
                    "资源验证未触发克隆、拉取或认证（包括 Fake）");
                return;
            }
            var tagline = _window.GetVisualDescendants().OfType<TextBlock>().Single(text =>
                text.Text == _fixture.Localization.GetString("Ui.Navigation.Tagline"));
            ProjectsView? retainedProjectsView = null;
            foreach (var culture in new[] { "zh-CN", "en-US" })
            {
                await _fixture.Localization.SetCultureAsync(new CultureInfo(culture));
                await SettleAsync();
                Check(ReferenceEquals(_fixture.Localization, GitBinder.Desktop.Localization.LocalizationService.Instance),
                    $"语言服务使用唯一相同实例 {culture}");
                Check(tagline.Text == _fixture.Localization.GetString("Ui.Navigation.Tagline"),
                    $"原有主窗口副标题实时切换语言 {culture}；actual={tagline.Text}");
                foreach (var width in new[] { 1180, 960 })
                {
                    _window.Width = width;
                    _window.Height = width == 960 ? 640 : 780;
                    foreach (var nav in _fixture.Main.NavigationItems)
                    {
                        _fixture.Main.SelectedNav = nav;
                        await SettleAsync();
                        Capture(_window, $"{culture}-{width}-{nav.Key[4..]}");
                        Check(_window.GetVisualDescendants().OfType<UserControl>().Any(), $"页面渲染 {culture} {width} {nav.Key}");
                        if (nav.Key == "Nav.Projects")
                        {
                            var view = _window.GetVisualDescendants().OfType<ProjectsView>().Single();
                            var list = view.GetVisualDescendants().OfType<ProjectListBox>().Single();
                            Check(list.StyleKey == typeof(ListBox) && list.Template is not null,
                                "项目列表继承标准主题且已应用模板");
                            Check(list.Bounds.Width > 0 && list.Bounds.Height > 0,
                                "项目列表具有可见视口");
                            Check(list.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                                _fixture.Projects.FilteredItems.Any(item => item.Name == text.Text)),
                                "项目卡片名称实际生成，不能只有项目计数");
                            Check(retainedProjectsView is null || ReferenceEquals(retainedProjectsView, view),
                                "往返导航复用项目视图");
                            retainedProjectsView = view;
                        }
                    }
                    await _fixture.Main.OpenSettingsCommand.ExecuteAsync(null);
                    await SettleAsync();
                    Capture(_window, $"{culture}-{width}-Settings");
                    Check(_window.GetVisualDescendants().OfType<SettingsView>().Any(), $"页面渲染 {culture} {width} Nav.Settings");
                    await CaptureModalPreviewsAsync(culture, width);
                }
            }
            await _fixture.Localization.SetCultureAsync(new CultureInfo("zh-CN"));
            await SettleAsync();
            Check(tagline.Text == _fixture.Localization.GetString("Ui.Navigation.Tagline"),
                "原有主窗口副标题从英文切回中文实时刷新");
            _window.Width = 1180;
            _window.Height = 780;
            if (!ScreenshotsOnly)
            {
                await VerifySearchAsync();
                await VerifyGroupsAsync();
                await VerifyNoticesAsync();
                await VerifyAccountEditorAsync();
                await VerifyPlatformEditorAsync();
                await VerifyCloneEditorAsync();
                await VerifyProjectConcurrencyAsync();
                await VerifyDragPreviewAsync();
                await VerifyPageEscapeAsync();
            }
            Check(App.Services is null, "验证结束仍未建立真实服务容器");
            Check(_fixture.Transfer.CloneCount == (ScreenshotsOnly ? 0 : 3)
                && _fixture.Transfer.PullCount == (ScreenshotsOnly ? 0 : 2) && _fixture.Transfer.TestCount == 0,
                "传输调用仅为受控内存 Fake：截图模式零次，完整验证三次克隆、两次拉取、零次认证测试");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            _checks.Add("FAIL " + exception);
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(OutputDirectory, "results.txt"), _checks);
            foreach (var check in _checks) Console.WriteLine(check);
            Console.WriteLine("Artifacts: " + OutputDirectory);
            _desktop.Shutdown(exitCode);
        }
    }

    private async Task VerifySearchAsync()
    {
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(item => item.Key == "Nav.Accounts");
        await SettleAsync();
        SearchBox().Text = "  workspace  ";
        await SettleAsync();
        Check(_fixture.Accounts.FilteredItems.Count == 2, "账号搜索 TextBox 输入更新过滤结果");
        SearchBox().Text = "not-found";
        await SettleAsync();
        Check(_fixture.Accounts.HasNoMatches, "账号无匹配空态");
        SearchBox().Text = "";
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(item => item.Key == "Nav.Projects");
        await SettleAsync();
        SearchBox().Text = "SERVICE";
        await SettleAsync();
        Check(_fixture.Projects.FilteredItems.Count == 1, "项目搜索 TextBox 输入大小写无关过滤");
        Capture(_window, "projects-filtered");
        SearchBox().Text = "";
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(item => item.Key == "Nav.Platforms");
        await SettleAsync();
        SearchBox().Text = "gitlab";
        await SettleAsync();
        Check(_fixture.Platforms.FilteredItems.Count == 1, "平台搜索 TextBox 输入过滤");
        SearchBox().Text = "";
    }

    private TextBox SearchBox() => _window.GetVisualDescendants().OfType<TextBox>()
        .First(box => box.IsVisible && box.PlaceholderText?.Contains("搜索") == true);

    private async Task VerifyGroupsAsync()
    {
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(item => item.Key == "Nav.Projects");
        await SettleAsync();
        var originalCount = _fixture.Groups.Groups.Count;
        var task = _fixture.Projects.NewGroupCommand.ExecuteAsync(null);
        await SettleAsync();
        var dialog = _desktop.Windows.OfType<GroupEditDialog>().Single();
        CheckDialogChrome(dialog, "分组编辑");
        Check(dialog.IsVisible, "新增分组命令打开真实模态弹窗");
        Click(dialog.FindControl<Button>("SaveButton")!);
        await SettleAsync();
        Check(dialog.FindControl<Border>("ErrorPanel")!.IsVisible, "空分组名校验失败仍留在弹窗");
        Check(_fixture.Groups.Groups.Count == originalCount, "校验失败没有保存");
        Capture(dialog, "group-validation");
        dialog.FindControl<TextBox>("NameInput")!.Text = "界面验证分组";
        Click(dialog.FindControl<Button>("SaveButton")!);
        await task;
        await DismissNoticesAsync();
        Check(_fixture.Groups.Groups.Values.Any(group => group.Name == "界面验证分组"), "新增分组已写入内存 Fake 并刷新侧栏");
        var groupItem = _fixture.Projects.Groups.Single(group => group.Name == "界面验证分组");
        task = _fixture.Projects.RenameGroupCommand.ExecuteAsync(groupItem);
        await SettleAsync();
        dialog = _desktop.Windows.OfType<GroupEditDialog>().Single();
        Check(dialog.FindControl<TextBox>("NameInput")!.Text == groupItem.Name, "重命名弹窗回填名称");
        dialog.FindControl<TextBox>("NameInput")!.Text = "验证分组已重命名";
        Capture(dialog, "group-rename");
        Click(dialog.FindControl<Button>("SaveButton")!);
        await task;
        await DismissNoticesAsync();
        Check(_fixture.Groups.Groups.Values.Any(group => group.Name == "验证分组已重命名"), "分组重命名成功");
        task = _fixture.Projects.NewGroupCommand.ExecuteAsync(null);
        await SettleAsync();
        dialog = _desktop.Windows.OfType<GroupEditDialog>().Single();
        dialog.FindControl<TextBox>("NameInput")!.Text = "取消不能保存";
        PressEscape(dialog.FindControl<TextBox>("NameInput")!);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(_fixture.Groups.Groups.Count == originalCount + 1, "分组输入框 Escape 关闭且不保存");
        Check(_window.IsVisible, "分组 Escape 不关闭父窗口");
    }

    private async Task VerifyNoticesAsync()
    {
        var message = "认证成功（演示结果）\n此窗口使用可选择的正文，不使用只读输入框。\n" + string.Join('\n', Enumerable.Range(1, 24).Select(n => $"第 {n} 项：这是隔离界面验证中的长消息，用来确认滚动与排版。"));
        var dialog = new MessageDialog(message);
        var close = dialog.ShowDialog(_window);
        await SettleAsync();
        CheckDialogChrome(dialog, "操作结果");
        var text = dialog.FindControl<SelectableTextBlock>("MessageText")!;
        text.SelectAll();
        Check(text.SelectionEnd - text.SelectionStart == message.Length, "消息正文可全选（未写入系统剪贴板）");
        Check(!dialog.GetVisualDescendants().OfType<TextBox>().Any(), "结果弹窗没有只读 TextBox");
        Capture(dialog, "message-long-selected");
        PressEscape(text);
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!dialog.IsVisible && _window.IsVisible, "结果正文 Escape 只关闭当前弹窗");
        var confirm = new ConfirmationDialog("删除确认", "这是虚构条目，只验证取消，不执行删除。", "删除");
        var confirmation = confirm.ShowDialog<bool?>(_window);
        await SettleAsync();
        CheckDialogChrome(confirm, "二次确认");
        var cancel = confirm.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消"));
        Check(cancel.IsDefault, "确认弹窗默认按钮为取消");
        Capture(confirm, "confirmation");
        PressEscape(cancel);
        Check(await confirmation.WaitAsync(TimeSpan.FromSeconds(5)) is false, "确认弹窗 Escape 返回 false，不执行确认");
        Check(_window.IsVisible, "确认 Escape 不关闭父窗口");
    }

    private async Task VerifyAccountEditorAsync()
    {
        _fixture.Main.SelectedNav = _fixture.Main.NavigationItems.Single(item => item.Key == "Nav.Accounts");
        await SettleAsync();
        var count = (await _fixture.AccountRepository.GetAllAsync()).Count;
        var task = _fixture.Accounts.AddCommand.ExecuteAsync(null);
        await SettleAsync();
        var editor = _desktop.Windows.OfType<AccountEditWindow>().Single();
        Check(editor.IsVisible, "新增账号命令打开真实账号编辑窗");
        CheckDialogChrome(editor, "账号编辑");
        _fixture.AccountEditor.Alias = "Escape 不应保存此账号";
        Capture(editor, "account-editor");
        var nested = new ConfirmationDialog("嵌套确认", "只检查 Escape 路由，不执行任何删除。", "确认");
        var nestedResult = nested.ShowDialog<bool?>(editor);
        await SettleAsync();
        PressEscape(nested);
        Check(await nestedResult.WaitAsync(TimeSpan.FromSeconds(5)) is false && editor.IsVisible && _window.IsVisible,
            "嵌套确认 Escape 只取消最上层弹窗，保留账号编辑及主窗");
        PressEscape(editor.GetVisualDescendants().OfType<TextBox>().First());
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Check((await _fixture.AccountRepository.GetAllAsync()).Count == count, "账号输入框 Escape 未写入账号仓储");
        Check(!editor.IsVisible && _window.IsVisible, "账号 Escape 只关闭编辑窗");
    }

    private async Task DismissNoticesAsync()
    {
        await SettleAsync();
        foreach (var notice in _desktop.Windows.OfType<MessageDialog>().ToArray()) notice.Close();
        await SettleAsync();
    }

    private static void Click(Button button)
    {
        if (!button.IsEnabled) throw new InvalidOperationException("不能点击禁用按钮");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task SettleAsync()
    {
        await Task.Delay(180);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(OutputDirectory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks.Add("PASS " + message);
    }
}
