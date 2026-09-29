using GitBinder.Application.Common;

namespace GitBinder.Platform.Linux;

/// <summary>遵循 XDG 数据目录约定；桌面端和 Credential Helper 共用此实现。</summary>
public sealed class LinuxApplicationDataPath : IApplicationDataPath
{
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "gitbinder.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    public LinuxApplicationDataPath() : this(Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    public LinuxApplicationDataPath(string? xdgDataHome, string userProfile)
    {
        // XDG 规定相对路径应忽略；不把它相对当前仓库展开，避免 Helper 与桌面端读到不同数据库。
        if (!string.IsNullOrWhiteSpace(xdgDataHome) && xdgDataHome.StartsWith('/') && !xdgDataHome.Contains('\0'))
            DataDirectory = Path.Combine(xdgDataHome, "GitBinder");
        else
        {
            if (string.IsNullOrWhiteSpace(userProfile) || !userProfile.StartsWith('/') || userProfile.Contains('\0'))
                throw new InvalidOperationException("The current Linux user profile directory is unavailable.");
            DataDirectory = Path.Combine(userProfile, ".local", "share", "GitBinder");
        }
    }
}
