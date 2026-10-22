using System.Diagnostics;
using GitBinder.Application.Common;
using GitBinder.Infrastructure.Common;

namespace GitBinder.Tests;

/// <summary>只启动专属的无窗口等待进程，不运行 Git、网络请求或用户程序</summary>
public sealed class CommandExecutorCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder process-" + Guid.NewGuid().ToString("N"));
    private readonly string _shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    public CommandExecutorCancellationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancellationAndTimeout_DoNotReturnUntilTheStartedProcessTreeHasExited(bool timeout, bool descendant)
    {
        if (!OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(_root, "process-id.txt");
        var quotedMarker = marker.Replace("'", "''");
        var script = descendant
            ? "$fixtureChild = Start-Process -FilePath '" + _shell.Replace("'", "''")
                + "' -WindowStyle Hidden -PassThru -ArgumentList '-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30'; "
                + "[IO.File]::WriteAllText('" + quotedMarker + ".tmp', ([string]$PID + ',' + [string]$fixtureChild.Id)); "
            : "[IO.File]::WriteAllText('" + quotedMarker + ".tmp', [string]$PID); ";
        script += "[IO.File]::Move('" + quotedMarker + ".tmp', '" + quotedMarker + "'); Start-Sleep -Seconds 30";
        using var cancellation = new CancellationTokenSource();
        var task = new CommandExecutor().ExecuteWithOptionsAsync(_shell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], _root,
            new CommandExecutionOptions { Timeout = timeout ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(30) },
            cancellation.Token);
        var processes = new List<Process>();
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker) && !task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20);
            Assert.True(File.Exists(marker), "The isolated process must start before cancellation is tested");
            var pids = (await File.ReadAllTextAsync(marker)).Split(',').Select(int.Parse).ToArray();
            Assert.Equal(descendant ? 2 : 1, pids.Length);
            foreach (var pid in pids)
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    processes.Add(process);
                    _ = process.Handle;
                }
                catch (ArgumentException) when (timeout && task.IsCompleted)
                {
                    // 超时可能先于测试读取句柄；该 PID 已退出也是预期终态
                }
            }
            if (!timeout) cancellation.Cancel();

            var result = await task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(result.IsSuccess);
            Assert.False(result.TerminationUnconfirmed);
            Assert.Equal(timeout ? "Command timed out." : "Command cancelled.", result.StdErr);
            foreach (var process in processes)
                Assert.True(process.HasExited, "A completed command must not release its caller while an owned process is still running");
        }
        finally
        {
            cancellation.Cancel();
            try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        }
                    }
                    finally { process.Dispose(); }
                }
            }
        }
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotLaunchTheProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(_root, "must-not-exist.txt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new CommandExecutor().ExecuteAsync(_shell,
            ["-NoProfile", "-NonInteractive", "-Command", "[IO.File]::WriteAllText('" + marker + "', 'started')"],
            _root, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("Command cancelled.", result.StdErr);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task NormalCompletion_DrainsOutputBeforeReturning()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await new CommandExecutor().ExecuteAsync(_shell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.WriteLine('fixture-output'); [Console]::Error.WriteLine('fixture-error')"], _root);
        Assert.True(result.IsSuccess);
        Assert.Contains("fixture-output", result.StdOut);
        Assert.Contains("fixture-error", result.StdErr);
    }

    [Fact]
    public async Task NormalCompletion_PreservesNullSeparatorsAndExactLineEndings()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await new CommandExecutor().ExecuteAsync(_shell,
            ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Out.Write('first' + [char]0 + [char]10 + 'second' + [char]13 + [char]10 + [char]0); [Console]::Error.Write('tail')"], _root);

        Assert.True(result.IsSuccess);
        Assert.Equal("first\0\nsecond\r\n\0", result.StdOut);
        Assert.Equal("tail", result.StdErr);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
