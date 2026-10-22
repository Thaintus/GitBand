using System.Diagnostics;
using System.Text;
using GitBinder.Application.Common;

namespace GitBinder.Infrastructure.Common;

/// <summary>
/// 命令执行器实现，使用参数数组避免 Shell 注入，统一处理 stdout/stderr/exit code/超时。
/// </summary>
public sealed class CommandExecutor : ICommandExecutor
{
    private readonly TimeSpan _defaultTimeout;

    public CommandExecutor(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
    }

    public Task<CommandResult> ExecuteAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(executable, arguments, workingDirectory, null, cancellationToken);

    public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(executable, arguments, workingDirectory, options, cancellationToken);

    private async Task<CommandResult> ExecuteCoreAsync(string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, CommandExecutionOptions? options, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }
        if (options is not null)
        {
            foreach (var pair in options.Environment)
            {
                if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
                else startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var started = false;
        Task<string>? stdout = null;
        Task<string>? stderr = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StdErr = "Failed to start process.",
                };
            }
            started = true;

            // 并发读取原始文本，不按行重写换行符，保留 Git 配置的 NUL 分隔和多行值
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options?.Timeout ?? _defaultTimeout);

            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).WaitAsync(timeoutCts.Token);

            return new CommandResult
            {
                ExitCode = process.ExitCode,
                StdOut = await stdout,
                StdErr = await stderr,
            };
        }
        catch (OperationCanceledException)
        {
            var stopped = !started || await StopAndWaitAsync(process, stdout, stderr);
            return new CommandResult
            {
                ExitCode = -1,
                TerminationUnconfirmed = !stopped,
                StdErr = !stopped ? "Command termination could not be confirmed."
                    : cancellationToken.IsCancellationRequested ? "Command cancelled." : "Command timed out.",
            };
        }
        catch (Exception ex)
        {
            // 启动后的读写异常同样要结束本次进程，不能把仍在写仓库的任务遗留在后台
            var stopped = !started || await StopAndWaitAsync(process, stdout, stderr);
            return new CommandResult
            {
                ExitCode = -1,
                TerminationUnconfirmed = !stopped,
                StdErr = stopped ? ex.Message : "Command termination could not be confirmed.",
            };
        }
    }

    private static async Task<bool> StopAndWaitAsync(Process process, Task<string>? stdout, Task<string>? stderr)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            // Kill 是异步终止请求，等待退出和输出收尾后调用方才能释放仓库占用与临时配置
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(process.WaitForExitAsync(), stdout ?? Task.FromResult(string.Empty),
                stderr ?? Task.FromResult(string.Empty)).WaitAsync(cleanupTimeout.Token);
            return true;
        }
        catch
        {
            // 清理异常不能覆盖原异常，但必须明确标记未确认退出，避免误报已正常取消
            return false;
        }
    }
}
