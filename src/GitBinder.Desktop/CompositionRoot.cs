using GitBinder.Application;
using GitBinder.Application.Common;
using GitBinder.Desktop.Localization;
using GitBinder.Desktop.ViewModels;
using GitBinder.Infrastructure;
using GitBinder.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace GitBinder.Desktop;

/// <summary>
/// 组合根：构建 DI 容器并初始化数据库。
/// </summary>
public static class CompositionRoot
{
    public static IServiceProvider Build()
    {
        // 与 Credential Helper 使用同一平台数据根，避免桌面端与凭据端读取不同数据库。
        var dataPath = PlatformPaths.CreateApplicationDataPath();

        var services = new ServiceCollection();

        // 本地化。
        var localization = new LocalizationService();
        services.AddSingleton<ILocalizationService>(localization);

        // Application / Infrastructure / Platform。
        services.AddApplication();
        services.AddInfrastructure(dataPath.DatabasePath);
        services.AddPlatform();

        // ViewModels。
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<PlatformsViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // 文件选择委托（Avalonia 实现）。
        services.AddSingleton<FilePickerDelegate>(DialogHelper.PickFileAsync);

        // 目录选择委托（Avalonia 实现）。
        services.AddSingleton<DirectoryPickerDelegate>(DialogHelper.PickDirectoryAsync);

        // 账号编辑窗口每次新建实例（状态独立）。
        services.AddTransient<AccountEditViewModel>();

        var provider = services.BuildServiceProvider();

        // 初始化数据库。
        var initializer = provider.GetRequiredService<DatabaseInitializer>();
        initializer.Initialize();

        // 初始化默认平台目录（首次运行）。
        var platformService = provider.GetRequiredService<GitBinder.Application.Accounts.PlatformService>();
        platformService.SeedDefaultsAsync().GetAwaiter().GetResult();

        // 应用持久化的语言设置。
        var settings = provider.GetRequiredService<GitBinder.Application.Settings.ISettingsRepository>();
        var savedLanguage = settings.GetAsync("settings.language").GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(savedLanguage))
        {
            try
            {
                localization.SetCultureAsync(new System.Globalization.CultureInfo(savedLanguage))
                    .GetAwaiter().GetResult();
            }
            catch (System.Globalization.CultureNotFoundException)
            {
                // 无效语言回退默认。
            }
        }

        return provider;
    }
}
