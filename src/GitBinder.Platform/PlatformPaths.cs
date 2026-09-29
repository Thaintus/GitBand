using GitBinder.Application.Common;
using GitBinder.Platform.MacOS;
using GitBinder.Platform.Windows;

namespace GitBinder.Platform;

/// <summary>桌面程序和独立 Credential Helper 使用相同的数据根目录。</summary>
public static class PlatformPaths
{
    public static IApplicationDataPath CreateApplicationDataPath()
    {
        if (OperatingSystem.IsWindows()) return new WindowsApplicationDataPath();
        if (OperatingSystem.IsMacOS()) return new MacApplicationDataPath();
        throw new PlatformNotSupportedException("GitBinder currently supports Windows and macOS.");
    }
}
