using GitBinder.Application.Common;

namespace GitBinder.Platform.Linux;

public sealed class LinuxShellService(ICommandExecutor executor) : IShellService
{
    public void OpenDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        Execute("/usr/bin/xdg-open", [Path.GetFullPath(path)], null,
            "Linux could not open the directory. Install xdg-utils and use an active desktop session.");
    }

    public void OpenTerminal(string path)
    {
        if (!Directory.Exists(path)) return;
        // gnome-terminal 默认把窗口交给终端服务后退出（不加 --wait）。
        // 不猜测任意 x-terminal-emulator 的分叉行为，避免执行器超时误关闭用户终端。
        const string terminal = "/usr/bin/gnome-terminal";
        if (!File.Exists(terminal))
            throw new IOException("Opening a terminal currently requires GNOME Terminal on Linux. Open the repository in your preferred terminal manually.");
        Execute(terminal, ["--working-directory", Path.GetFullPath(path)], path,
            "Linux could not open GNOME Terminal. Check the current desktop session.");
    }

    private void Execute(string executable, IReadOnlyList<string> arguments, string? workingDirectory, string error)
    {
        try
        {
            var result = Task.Run(() => executor.ExecuteWithOptionsAsync(executable, arguments, workingDirectory,
                new CommandExecutionOptions { Timeout = TimeSpan.FromSeconds(10) })).GetAwaiter().GetResult();
            if (!result.IsSuccess) throw new IOException(error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            throw new IOException(error);
        }
    }

    public Task<string?> PickDirectoryAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> PickFileAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}
