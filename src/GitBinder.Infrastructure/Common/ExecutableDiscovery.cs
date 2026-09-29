namespace GitBinder.Infrastructure.Common;

/// <summary>定位可直接执行的程序，不启动进程，也不依赖登录 Shell 的初始化脚本。</summary>
internal static class ExecutableDiscovery
{
    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static string? FindOnPath(string executable)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var entry in paths)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var directory = OperatingSystem.IsWindows() ? entry.Trim().Trim('"') : entry;
            // 空条目不应退回当前工作目录，避免加载仓库内同名程序。
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }
}
