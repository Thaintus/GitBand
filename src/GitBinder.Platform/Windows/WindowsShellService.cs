using System.Diagnostics;
using GitBinder.Application.Common;

namespace GitBinder.Platform.Windows;

/// <summary>
/// Windows Shell 服务实现。
/// </summary>
public sealed class WindowsShellService : IShellService
{
    public void OpenDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(startInfo);
        }
    }

    public void OpenTerminal(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = Directory.Exists(path) ? path : string.Empty,
            UseShellExecute = true,
        };
        Process.Start(startInfo);
    }

    public async Task<string?> PickDirectoryAsync(CancellationToken ct = default)
    {
        // V1 使用 FolderBrowserDialog（需要 STA）。为保持简单，此处基于 Win32 的简单实现，
        // 具体由 GUI 层通过 Avalonia 的对话框完成。此方法作为回退返回 null。
        await Task.CompletedTask;
        return null;
    }

    public async Task<string?> PickFileAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return null;
    }
}
