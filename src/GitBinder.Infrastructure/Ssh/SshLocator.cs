using GitBinder.Application.Security;
using GitBinder.Infrastructure.Common;

namespace GitBinder.Infrastructure.Ssh;

/// <summary>
/// SSH 定位器，支持 Windows OpenSSH、macOS 系统及 Homebrew 安装。
/// </summary>
public sealed class SshLocator : ISshLocator
{
    public async Task<string?> LocateAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();

        var onPath = ExecutableDiscovery.FindOnPath(OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");
        if (onPath is not null) return onPath;

        if (!OperatingSystem.IsWindows())
        {
            foreach (var candidate in new[] { "/usr/bin/ssh", "/opt/homebrew/bin/ssh", "/usr/local/bin/ssh" })
            {
                if (ExecutableDiscovery.IsExecutable(candidate)) return candidate;
            }
            return null;
        }

        // 常见 OpenSSH 安装目录。
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(systemRoot))
        {
            candidates.Add(Path.Combine(systemRoot, "System32", "OpenSSH", "ssh.exe"));
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Git", "usr", "bin", "ssh.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (ExecutableDiscovery.IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
