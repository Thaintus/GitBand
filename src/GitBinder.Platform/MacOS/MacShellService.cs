using GitBinder.Application.Common;

namespace GitBinder.Platform.MacOS;

public sealed class MacShellService(ICommandExecutor executor) : IShellService
{
    public void OpenDirectory(string path)
    {
        if (Directory.Exists(path)) Open([Path.GetFullPath(path)]);
    }

    public void OpenTerminal(string path)
    {
        if (Directory.Exists(path)) Open(["-a", "Terminal", Path.GetFullPath(path)]);
    }

    private void Open(IReadOnlyList<string> arguments)
    {
        // 接口是同步的，后台执行避免阻塞等待捕获到 UI SynchronizationContext。
        var result = Task.Run(() => executor.ExecuteWithOptionsAsync("/usr/bin/open", arguments, null,
            new CommandExecutionOptions { Timeout = TimeSpan.FromSeconds(10) })).GetAwaiter().GetResult();
        if (!result.IsSuccess) throw new IOException("macOS could not open the requested location.");
    }

    // 文件/目录选择交给桌面层的 Avalonia StorageProvider。
    public Task<string?> PickDirectoryAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> PickFileAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}
