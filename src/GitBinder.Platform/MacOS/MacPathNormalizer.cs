using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitBinder.Application.Common;

namespace GitBinder.Platform.MacOS;

/// <summary>保留大小写，避免把大小写敏感卷上的不同仓库误认为同一项目。</summary>
public sealed class MacPathNormalizer : IPathNormalizer
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
        // 仅对已存在的路径解析符号链接；克隆目标目录可能尚未创建。
        if (OperatingSystem.IsMacOS() && (Directory.Exists(full) || File.Exists(full)))
            full = ResolveExistingPath(full);
        return Normalize(full);
    }

    public bool Equals(string left, string right)
        => string.Equals(ToKey(left), ToKey(right), StringComparison.Ordinal);

    public string ToKey(string path) => Canonicalize(path);

    [SupportedOSPlatform("macos")]
    private static string ResolveExistingPath(string path)
    {
        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return path;
        try { return Marshal.PtrToStringUTF8(resolved) ?? path; }
        finally { Free(resolved); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
