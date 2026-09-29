using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using GitBinder.Application.Common;
using GitBinder.Application.Settings;
using GitBinder.Desktop.ViewModels;
using GitBinder.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GitBinder.Desktop;

public partial class App : Avalonia.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _showWindowMenuItem;
    private NativeMenuItem? _exitMenuItem;
    private ISettingsRepository? _settings;
    private ILocalizationService? _localization;
    private bool _exitRequested;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 构建 DI 容器。
        Services = CompositionRoot.Build();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _settings = Services.GetRequiredService<ISettingsRepository>();
            _localization = Services.GetRequiredService<ILocalizationService>();
            _localization.CultureChanged += OnCultureChanged;

            _mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
            _mainWindow.Closing += OnMainWindowClosing;
            desktop.MainWindow = _mainWindow;

            var trayIcon = CreateTrayIcon();
            _trayIcon = trayIcon;
            UpdateTrayTexts();
            TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon CreateTrayIcon()
    {
        _showWindowMenuItem = new NativeMenuItem();
        _showWindowMenuItem.Click += OnShowWindowClicked;
        _exitMenuItem = new NativeMenuItem();
        _exitMenuItem.Click += OnExitClicked;

        var menu = new NativeMenu();
        menu.Add(_showWindowMenuItem);
        menu.Add(_exitMenuItem);

        var trayIcon = new TrayIcon
        {
            Icon = CreateTrayWindowIcon(),
            Menu = menu,
            IsVisible = true,
        };
        trayIcon.Clicked += OnTrayIconClicked;
        return trayIcon;
    }

    private static WindowIcon CreateTrayWindowIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://GitBinder.Desktop/Assets/avalonia-logo.ico"));
        return new WindowIcon(iconStream);
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitRequested || e.CloseReason is not WindowCloseReason.WindowClosing || !CloseToTrayOnClose())
        {
            return;
        }

        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnShowWindowClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _exitRequested = true;
        _trayIcon?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState is WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private bool CloseToTrayOnClose()
    {
        try
        {
            var value = _settings?.GetAsync(DesktopSettingKeys.CloseToTrayOnClose).GetAwaiter().GetResult();
            return bool.TryParse(value, out var enabled) ? enabled : DesktopSettingKeys.DefaultCloseToTrayOnClose;
        }
        catch
        {
            // 保持平台默认行为；Linux 不能假设有可用托盘入口。
            return DesktopSettingKeys.DefaultCloseToTrayOnClose;
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e) => UpdateTrayTexts();

    private void UpdateTrayTexts()
    {
        if (_localization is null)
        {
            return;
        }

        if (_showWindowMenuItem is not null)
        {
            _showWindowMenuItem.Header = _localization.GetString("Tray.ShowWindow");
        }

        if (_exitMenuItem is not null)
        {
            _exitMenuItem.Header = _localization.GetString("Tray.Exit");
        }

        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = _localization.GetString("Tray.Tooltip");
        }
    }
}
