namespace GitBinder.Application.Common;

/// <summary>
/// 命令执行结果。
/// </summary>
public sealed class CommandResult
{
    public int ExitCode { get; init; }

    public string StdOut { get; init; } = string.Empty;

    public string StdErr { get; init; } = string.Empty;

    public bool TerminationUnconfirmed { get; init; }

    public bool IsSuccess => ExitCode == 0 && !TerminationUnconfirmed;

    public void ThrowIfTerminationUnconfirmed()
    {
        if (TerminationUnconfirmed) throw new CommandTerminationUnconfirmedException(ExitCode);
    }
}

public sealed class CommandTerminationUnconfirmedException(int exitCode = -1)
    : Exception("External command termination could not be confirmed.")
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// 命令执行器，统一处理 Git / SSH 等外部命令调用，参数使用数组避免 Shell 注入。
/// </summary>
public interface ICommandExecutor
{
    /// <summary>带子进程专用环境和超时的执行；不支持时失败，不能静默丢弃认证隔离选项。</summary>
    Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Process options are not supported.");

    Task<CommandResult> ExecuteAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}

public sealed class CommandExecutionOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}
