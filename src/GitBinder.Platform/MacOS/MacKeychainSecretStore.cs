using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GitBinder.Application.Security;

namespace GitBinder.Platform.MacOS;

/// <summary>
/// 使用登录钥匙串保存机密。只经内存调用 Security.framework；不经命令行或明文文件中转。
/// 新建条目仅信任同一应用包内的桌面程序与 Credential Helper，不授予任意程序访问权限。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore : ISecretStore
{
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private const string ServiceName = "io.github.gitbinder.credentials";
    private readonly bool _isCredentialHelper = string.Equals(
        Path.GetFileName(Environment.ProcessPath), "gitbinder-credential", StringComparison.Ordinal);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            using var scope = new NativeScope();
            var query = CreateQuery(scope, key);
            var data = Encoding.UTF8.GetBytes(value);
            try
            {
                var changes = scope.Dictionary();
                var nativeData = scope.Own(Native.CFDataCreate(IntPtr.Zero, data, data.Length));
                Native.CFDictionarySetValue(changes, Native.SecurityValue("kSecValueData"), nativeData);
                var status = Native.SecItemUpdate(query, changes);
                if (status == ItemNotFound)
                {
                    Native.CFDictionarySetValue(query, Native.SecurityValue("kSecValueData"), nativeData);
                    Native.CFDictionarySetValue(query, Native.SecurityValue("kSecAttrAccess"), CreateAccess(scope));
                    status = Native.SecItemAdd(query, IntPtr.Zero);
                    // 并发保存创建了同一个条目时，只更新数据，不改动已有 ACL。
                    if (status == DuplicateItem)
                        status = Native.SecItemUpdate(CreateQuery(scope, key), changes);
                }
                EnsureSuccess(status, "write");
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }, ct);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            using var scope = new NativeScope();
            var query = CreateQuery(scope, key);
            Native.CFDictionarySetValue(query, Native.SecurityValue("kSecReturnData"), Native.CoreValue("kCFBooleanTrue"));
            Native.CFDictionarySetValue(query, Native.SecurityValue("kSecMatchLimit"), Native.SecurityValue("kSecMatchLimitOne"));
            var status = Native.SecItemCopyMatching(query, out var result);
            if (result != IntPtr.Zero) scope.Own(result);
            if (status == ItemNotFound) return null;
            EnsureSuccess(status, "read");
            if (result == IntPtr.Zero || Native.CFGetTypeID(result) != Native.CFDataGetTypeID())
                throw new InvalidOperationException("Keychain returned an unexpected data type.");
            var bytes = new byte[checked((int)Native.CFDataGetLength(result))];
            try
            {
                if (bytes.Length > 0) Marshal.Copy(Native.CFDataGetBytePtr(result), bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, ct);

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            using var scope = new NativeScope();
            var status = Native.SecItemDelete(CreateQuery(scope, key));
            if (status != ItemNotFound) EnsureSuccess(status, "delete");
        }, ct);

    private IntPtr CreateQuery(NativeScope scope, string key)
    {
        var query = scope.Dictionary();
        Native.CFDictionarySetValue(query, Native.SecurityValue("kSecClass"), Native.SecurityValue("kSecClassGenericPassword"));
        Native.CFDictionarySetValue(query, Native.SecurityValue("kSecAttrService"), scope.String(ServiceName));
        Native.CFDictionarySetValue(query, Native.SecurityValue("kSecAttrAccount"), scope.String(key));
        // Helper 在 Git 子进程中必须非交互：钥匙串锁定或需要重新授权时失败，绝不回退到其他凭据。
        if (_isCredentialHelper)
            Native.CFDictionarySetValue(query, Native.SecurityValue("kSecUseAuthenticationUI"),
                Native.SecurityValue("kSecUseAuthenticationUIFail"));
        return query;
    }

    private static IntPtr CreateAccess(NativeScope scope)
    {
        var apps = new List<IntPtr>();
        foreach (var name in new[] { "GitBinder.Desktop", "gitbinder-credential" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path))
                throw new InvalidOperationException("The macOS application bundle must include GitBinder.Desktop and gitbinder-credential.");
            var status = Native.SecTrustedApplicationCreateFromPath(path, out var application);
            if (application != IntPtr.Zero) scope.Own(application);
            EnsureSuccess(status, "create application access");
            if (application == IntPtr.Zero) throw new InvalidOperationException("Keychain could not identify the bundled application.");
            apps.Add(application);
        }
        var trusted = scope.Own(Native.CFArrayCreate(IntPtr.Zero, apps.ToArray(), apps.Count,
            Native.CoreSymbol("kCFTypeArrayCallBacks")));
        var result = Native.SecAccessCreate(scope.String("GitBinder credentials"), trusted, out var access);
        if (access != IntPtr.Zero) scope.Own(access);
        EnsureSuccess(result, "create access control");
        if (access == IntPtr.Zero) throw new InvalidOperationException("Keychain could not create application access control.");
        return access;
    }

    private static void EnsureSuccess(int status, string operation)
    {
        if (status != 0)
            // 错误只保留系统错误码，不输出 key、用户名或机密内容。
            throw new InvalidOperationException($"Keychain {operation} failed (OSStatus {status}). Unlock the login keychain and authorize GitBinder if requested.");
    }

    private sealed class NativeScope : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr Own(IntPtr value)
        {
            if (value == IntPtr.Zero) throw new InvalidOperationException("Could not allocate a Keychain object.");
            _owned.Add(value);
            return value;
        }

        public IntPtr Dictionary() => Own(Native.CFDictionaryCreateMutable(IntPtr.Zero, 0,
            Native.CoreSymbol("kCFTypeDictionaryKeyCallBacks"), Native.CoreSymbol("kCFTypeDictionaryValueCallBacks")));

        public IntPtr String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Own(Native.CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, 0x08000100, 0));
        }

        public void Dispose()
        {
            for (var index = _owned.Count - 1; index >= 0; index--) Native.CFRelease(_owned[index]);
            _owned.Clear();
        }
    }

    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        // 框架句柄保留到进程退出；全局 CF 常量由框架所有，不可 CFRelease。
        private static readonly IntPtr SecurityLibrary = NativeLibrary.Load(Security);
        private static readonly IntPtr CoreLibrary = NativeLibrary.Load(CoreFoundation);

        internal static IntPtr SecurityValue(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityLibrary, name));
        internal static IntPtr CoreValue(string name) => Marshal.ReadIntPtr(CoreSymbol(name));
        internal static IntPtr CoreSymbol(string name) => NativeLibrary.GetExport(CoreLibrary, name);

        [DllImport(Security)] internal static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(Security)] internal static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
        [DllImport(Security)] internal static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(Security)] internal static extern int SecItemDelete(IntPtr query);
        [DllImport(Security)] internal static extern int SecTrustedApplicationCreateFromPath(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr application);
        [DllImport(Security)] internal static extern int SecAccessCreate(IntPtr description, IntPtr trusted, out IntPtr access);
        [DllImport(CoreFoundation)] internal static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport(CoreFoundation)] internal static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
        [DllImport(CoreFoundation)] internal static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint count, uint encoding, byte externalRepresentation);
        [DllImport(CoreFoundation)] internal static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint count);
        [DllImport(CoreFoundation)] internal static extern nint CFDataGetLength(IntPtr data);
        [DllImport(CoreFoundation)] internal static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundation)] internal static extern nuint CFDataGetTypeID();
        [DllImport(CoreFoundation)] internal static extern nuint CFGetTypeID(IntPtr value);
        [DllImport(CoreFoundation)] internal static extern IntPtr CFArrayCreate(IntPtr allocator, [In] IntPtr[] values, nint count, IntPtr callbacks);
        [DllImport(CoreFoundation)] internal static extern void CFRelease(IntPtr value);
    }
}
