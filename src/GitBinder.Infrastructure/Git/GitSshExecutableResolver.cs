using GitBinder.Infrastructure.Common;
using GitBinder.Infrastructure.Ssh;

namespace GitBinder.Infrastructure.Git;

/// <summary>传输优先复用所选 Git for Windows 安装中的 SSH，避免混用 Windows OpenSSH 的密钥与 Agent 规则</summary>
internal static class GitSshExecutableResolver
{
    internal static async Task<string> ResolveAsync(string gitExecutable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            var bundled = FindBundledWindowsSsh(gitExecutable);
            if (bundled is not null) return bundled;
        }

        return await new SshLocator().LocateAsync(ct) ?? GitCommandPaths.SshExecutable;
    }

    private static string? FindBundledWindowsSsh(string gitExecutable)
    {
        // 只从已定位的绝对 Git 路径推导已知安装布局，不在仓库目录或任意父目录搜索程序
        if (!Path.IsPathFullyQualified(gitExecutable) || !File.Exists(gitExecutable)
            || !Path.GetFileName(gitExecutable).Equals("git.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var executableDirectory = new DirectoryInfo(Path.GetDirectoryName(gitExecutable)!);
        DirectoryInfo? installationDirectory;
        if (executableDirectory.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            installationDirectory = executableDirectory.Parent;
        }
        else if (executableDirectory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            var parent = executableDirectory.Parent;
            installationDirectory = parent?.Name.Equals("mingw64", StringComparison.OrdinalIgnoreCase) == true
                || parent?.Name.Equals("mingw32", StringComparison.OrdinalIgnoreCase) == true
                ? parent?.Parent : parent;
        }
        else return null;

        if (installationDirectory is null) return null;
        var candidate = Path.Combine(installationDirectory.FullName, "usr", "bin", "ssh.exe");
        return ExecutableDiscovery.IsExecutable(candidate) ? candidate : null;
    }
}
