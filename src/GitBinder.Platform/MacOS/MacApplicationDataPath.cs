using GitBinder.Application.Common;

namespace GitBinder.Platform.MacOS;

public sealed class MacApplicationDataPath : IApplicationDataPath
{
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "gitbinder.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    public MacApplicationDataPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("The current user profile directory is unavailable.");
        DataDirectory = Path.Combine(profile, "Library", "Application Support", "GitBinder");
    }
}
