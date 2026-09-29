using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GitBinder.Application.Security;

namespace GitBinder.Platform.Linux;

/// <summary>
/// 通过 libsecret 原生 API 使用当前桌面会话的 Secret Service。
/// 不把秘密交给命令行、不写明文文件；构造时不连接 D-Bus，未配置 HTTPS 也能启动。
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string SchemaName = "io.github.gitbinder.credentials";
    private const string LockedMessage = "The Linux keyring is locked. Unlock it in the desktop keyring manager and retry; GitBinder will not unlock it in a background Git operation.";
    private const string ServiceMessage = "Linux Secret Service is unavailable or denied the request. Use a desktop session with a running, unlocked GNOME Keyring or compatible Secret Service; credentials are not saved to plaintext files.";

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("The credential value contains an unsupported null character.", nameof(value));
        return ExecuteAsync(key, call =>
        {
            var service = call.OpenService();
            var (schema, attributes) = call.CreateQuery(key);
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                // secret_value_new 会复制到由 libsecret 管理的安全内存。
                // 为零长度值保留一个有效的缓冲区地址，但长度仍传 0。
                var nativeValue = call.Own(Native.secret_value_new(bytes.Length == 0 ? [(byte)0] : bytes,
                    bytes.Length, "text/plain"), Native.secret_value_unref);
                var success = Native.secret_service_store_sync(service, schema, attributes, null,
                    "GitBinder credential", nativeValue, call.Cancellable, out var error);
                call.CheckError(error);
                if (success == 0) throw new InvalidOperationException(ServiceMessage);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, ct);
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => ExecuteAsync<string?>(key, call =>
        {
            var service = call.OpenService();
            var (schema, attributes) = call.CreateQuery(key);
            // LOAD_SECRETS = 8，绝不使用 UNLOCK = 4；Credential Helper 不触发解锁提示。
            // 未指定 ALL 时，libsecret 优先选择最近保存的已解锁条目。
            var items = call.Search(service, schema, attributes, flags: 8);
            if (items == IntPtr.Zero) return null;
            var item = Marshal.ReadIntPtr(items);
            if (Native.secret_item_get_locked(item) != 0) throw new InvalidOperationException(LockedMessage);
            var value = Native.secret_item_get_secret(item);
            if (value == IntPtr.Zero) throw new InvalidOperationException(ServiceMessage);
            call.Own(value, Native.secret_value_unref);
            var text = Native.secret_value_get_text(value);
            if (text == IntPtr.Zero) throw new InvalidOperationException("The Linux keyring returned an unsupported credential format.");
            return Marshal.PtrToStringUTF8(text);
        }, ct);

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => ExecuteAsync(key, call =>
        {
            var service = call.OpenService();
            var (schema, attributes) = call.CreateQuery(key);
            var items = call.Search(service, schema, attributes, flags: 2); // ALL，不解锁也不读取机密。
            // 先检查全部条目，避免 clear_sync 静默跳过锁定条目后误报删除成功。
            for (var node = items; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                if (Native.secret_item_get_locked(Marshal.ReadIntPtr(node)) != 0)
                    throw new InvalidOperationException(LockedMessage);
            for (var node = items; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var success = Native.secret_item_delete_sync(Marshal.ReadIntPtr(node), call.Cancellable, out var error);
                call.CheckError(error);
                if (success == 0) throw new InvalidOperationException(ServiceMessage);
            }
            return true;
        }, ct);

    private static Task<T> ExecuteAsync<T>(string key, Func<NativeCall, T> operation, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('\0')) throw new ArgumentException("The credential identifier is invalid.", nameof(key));
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                using var call = new NativeCall(linked.Token, ct);
                return operation(call);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // 不直接输出动态加载器/服务端的原始异常，防止未来包含机密参数。
                throw new InvalidOperationException("Linux credential storage requires libsecret-1 and GLib/GIO. Install the system libsecret runtime and enable a desktop Secret Service; no plaintext fallback is available.");
            }
        }, ct);
    }

    private sealed class NativeCall : IDisposable
    {
        private readonly List<Action> _release = [];
        private readonly CancellationToken _operationToken;
        private readonly CancellationToken _callerToken;
        private readonly CancellationTokenRegistration _registration;
        public IntPtr Cancellable { get; }

        public NativeCall(CancellationToken operationToken, CancellationToken callerToken)
        {
            _operationToken = operationToken;
            _callerToken = callerToken;
            Cancellable = Native.g_cancellable_new();
            if (Cancellable == IntPtr.Zero) throw new InvalidOperationException(ServiceMessage);
            _registration = operationToken.Register(() => Native.g_cancellable_cancel(Cancellable));
        }

        public IntPtr Own(IntPtr pointer, Action<IntPtr> release)
        {
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Could not allocate a Linux keyring object.");
            _release.Add(() => release(pointer));
            return pointer;
        }

        public IntPtr OpenService()
        {
            // OPEN_SESSION = 2：初始化加密传输会话，不解锁任何集合。
            var service = Native.secret_service_get_sync(2, Cancellable, out var error);
            if (service != IntPtr.Zero) Own(service, Native.g_object_unref);
            CheckError(error);
            if (service == IntPtr.Zero) throw new InvalidOperationException(ServiceMessage);
            return service;
        }

        public (IntPtr Schema, IntPtr Attributes) CreateQuery(string key)
        {
            // 使用 newv 避免跨架构的 C 变参 ABI 或 SecretSchema 结构体布局假设。
            using var types = new NativeTable();
            types.AddType("account", 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
            var schema = Own(Native.secret_schema_newv(SchemaName, 0, types.Handle), Native.secret_schema_unref);
            var attributes = new NativeTable();
            _release.Add(attributes.Dispose);
            attributes.AddString("account", key);
            return (schema, attributes.Handle);
        }

        public IntPtr Search(IntPtr service, IntPtr schema, IntPtr attributes, int flags)
        {
            var items = Native.secret_service_search_sync(service, schema, attributes, flags, Cancellable, out var error);
            if (items != IntPtr.Zero) Own(items, FreeItems);
            CheckError(error);
            return items;
        }

        public void CheckError(IntPtr error)
        {
            try
            {
                _callerToken.ThrowIfCancellationRequested();
                if (_operationToken.IsCancellationRequested)
                    throw new TimeoutException("Linux keyring operation timed out. Check the desktop Secret Service and unlock the keyring before retrying.");
                if (error == IntPtr.Zero) return;
                if (Native.g_error_matches(error, Native.secret_error_get_quark(), 2) != 0)
                    throw new InvalidOperationException(LockedMessage);
                // 不包含 GError.message：错误来自外部服务，可能包含敏感信息。
                throw new InvalidOperationException(ServiceMessage);
            }
            finally { if (error != IntPtr.Zero) Native.g_error_free(error); }
        }

        private static void FreeItems(IntPtr items)
        {
            for (var node = items; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                Native.g_object_unref(Marshal.ReadIntPtr(node));
            Native.g_list_free(items);
        }

        public void Dispose()
        {
            // 等待取消回调结束后再释放 GCancellable，避免 native use-after-free。
            _registration.Dispose();
            for (var index = _release.Count - 1; index >= 0; index--) _release[index]();
            Native.g_object_unref(Cancellable);
        }
    }

    private sealed class NativeTable : IDisposable
    {
        private readonly List<IntPtr> _strings = [];
        public IntPtr Handle { get; }

        public NativeTable()
        {
            Handle = Native.g_hash_table_new(Native.GLibSymbol("g_str_hash"), Native.GLibSymbol("g_str_equal"));
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Could not allocate a Linux keyring query.");
        }

        private IntPtr String(string value)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(value);
            _strings.Add(pointer);
            return pointer;
        }

        public void AddType(string name, int type) => Native.g_hash_table_insert(Handle, String(name), (IntPtr)type);
        public void AddString(string name, string value) => Native.g_hash_table_insert(Handle, String(name), String(value));

        public void Dispose()
        {
            Native.g_hash_table_unref(Handle);
            foreach (var pointer in _strings) Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static class Native
    {
        private const string Secret = "libsecret-1.so.0";
        private const string GLib = "libglib-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        private const string GIO = "libgio-2.0.so.0";
        // 动态符号仅在第一次凭据操作时加载；句柄保留至进程结束。
        private static readonly Lazy<IntPtr> GLibLibrary = new(() => NativeLibrary.Load(GLib));
        internal static IntPtr GLibSymbol(string name) => NativeLibrary.GetExport(GLibLibrary.Value, name);

        [DllImport(Secret)] internal static extern IntPtr secret_service_get_sync(int flags, IntPtr cancellable, out IntPtr error);
        [DllImport(Secret)] internal static extern IntPtr secret_schema_newv(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, IntPtr types);
        [DllImport(Secret)] internal static extern void secret_schema_unref(IntPtr schema);
        [DllImport(Secret)] internal static extern IntPtr secret_service_search_sync(
            IntPtr service, IntPtr schema, IntPtr attributes, int flags, IntPtr cancellable, out IntPtr error);
        [DllImport(Secret)] internal static extern IntPtr secret_value_new(
            byte[] secret, nint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType);
        [DllImport(Secret)] internal static extern int secret_service_store_sync(
            IntPtr service, IntPtr schema, IntPtr attributes, [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr value, IntPtr cancellable, out IntPtr error);
        [DllImport(Secret)] internal static extern int secret_item_get_locked(IntPtr item);
        [DllImport(Secret)] internal static extern IntPtr secret_item_get_secret(IntPtr item);
        [DllImport(Secret)] internal static extern IntPtr secret_value_get_text(IntPtr value);
        [DllImport(Secret)] internal static extern void secret_value_unref(IntPtr value);
        [DllImport(Secret)] internal static extern int secret_item_delete_sync(IntPtr item, IntPtr cancellable, out IntPtr error);
        [DllImport(Secret)] internal static extern uint secret_error_get_quark();
        [DllImport(GIO)] internal static extern IntPtr g_cancellable_new();
        [DllImport(GIO)] internal static extern void g_cancellable_cancel(IntPtr cancellable);
        [DllImport(GObject)] internal static extern void g_object_unref(IntPtr instance);
        [DllImport(GLib)] internal static extern int g_error_matches(IntPtr error, uint domain, int code);
        [DllImport(GLib)] internal static extern void g_error_free(IntPtr error);
        [DllImport(GLib)] internal static extern void g_list_free(IntPtr list);
        [DllImport(GLib)] internal static extern IntPtr g_hash_table_new(IntPtr hashFunction, IntPtr keyEqualFunction);
        [DllImport(GLib)] internal static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
        [DllImport(GLib)] internal static extern void g_hash_table_unref(IntPtr table);
    }
}
