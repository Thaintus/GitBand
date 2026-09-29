using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitBinder.Application.Common;

namespace GitBinder.Platform.Linux;

/// <summary>Linux 路径大小写敏感，已存在的路径用 realpath 解析符号链接。</summary>
public sealed class LinuxPathNormalizer : IPathNormalizer
{
    public string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    public string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsLinux() && (Directory.Exists(full) || File.Exists(full)))
            full = ResolveExistingPath(full);
        return Normalize(full);
    }

    public bool Equals(string left, string right)
        => string.Equals(ToKey(left), ToKey(right), StringComparison.Ordinal);

    public string ToKey(string path) => Canonicalize(path);

    [SupportedOSPlatform("linux")]
    private static string ResolveExistingPath(string path)
    {
        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return path;
        try { return Marshal.PtrToStringUTF8(resolved) ?? path; }
        finally { Free(resolved); }
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
