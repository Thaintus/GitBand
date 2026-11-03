using Avalonia.Controls;
using Avalonia.Interactivity;
using GitBinder.Desktop.ViewModels;

namespace GitBinder.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnProjectLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null && await launcher.LaunchUriAsync(viewModel.ProjectUri)) return;
        }
        catch (Exception)
        {
            // 系统未配置浏览器或启动失败时给出可读提示，不传播平台异常。
        }
        viewModel.ReportProjectOpenFailed();
    }
}
