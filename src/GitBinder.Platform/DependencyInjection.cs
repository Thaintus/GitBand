using GitBinder.Application.Common;
using GitBinder.Application.Security;
using GitBinder.Platform.Windows;
using GitBinder.Platform.MacOS;
using GitBinder.Platform.Linux;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace GitBinder.Platform;

/// <summary>
/// Platform 层依赖注入注册（Windows / macOS / Linux 按运行时选择）。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            AddWindowsPlatformServices(services);
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddMacPlatformServices(services);
        }
        else if (OperatingSystem.IsLinux())
        {
            AddLinuxPlatformServices(services);
        }
        else
        {
            throw new PlatformNotSupportedException("GitBinder currently supports Windows, macOS and Linux.");
        }

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IPathNormalizer, WindowsPathNormalizer>();
        services.AddSingleton<IApplicationDataPath, WindowsApplicationDataPath>();
        services.AddSingleton<IShellService, WindowsShellService>();
        services.AddSingleton<IPrivateKeyImporter, WindowsPrivateKeyImporter>();

        // Secret Store 依赖 IApplicationDataPath 提供存储目录。
        services.AddSingleton<ISecretStore>(sp =>
        {
            var dataPath = sp.GetRequiredService<IApplicationDataPath>();
            return new WindowsDpapiSecretStore(Path.Combine(dataPath.DataDirectory, "secrets"));
        });
    }

    [SupportedOSPlatform("macos")]
    private static void AddMacPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IPathNormalizer, MacPathNormalizer>();
        services.AddSingleton<IApplicationDataPath, MacApplicationDataPath>();
        services.AddSingleton<IShellService, MacShellService>();
        services.AddSingleton<IPrivateKeyImporter, MacPrivateKeyImporter>();
        services.AddSingleton<ISecretStore, MacKeychainSecretStore>();
    }

    [SupportedOSPlatform("linux")]
    private static void AddLinuxPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IPathNormalizer, LinuxPathNormalizer>();
        services.AddSingleton<IApplicationDataPath, LinuxApplicationDataPath>();
        services.AddSingleton<IShellService, LinuxShellService>();
        services.AddSingleton<IPrivateKeyImporter, LinuxPrivateKeyImporter>();
        services.AddSingleton<ISecretStore, LinuxSecretServiceStore>();
    }
}
