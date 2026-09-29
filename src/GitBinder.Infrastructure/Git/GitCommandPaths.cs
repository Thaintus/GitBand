namespace GitBinder.Infrastructure.Git;

/// <summary>Git 执行层的平台路径约定；不会把平台判断泄漏到 Application。</summary>
internal static class GitCommandPaths
{
    public static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    public static string SshExecutable => OperatingSystem.IsMacOS() ? "/usr/bin/ssh" : "ssh";
    public static string CredentialHelper => Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "gitbinder-credential.exe" : "gitbinder-credential");

    // macOS 的反斜杠是合法文件名字符，不能按 Windows 分隔符替换。
    public static string ToShellPath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    // credential.helper / core.sshCommand 由 Git 的 POSIX Shell 解释，单引号阻止 $、` 等展开。
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
