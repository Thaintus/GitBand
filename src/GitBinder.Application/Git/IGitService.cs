        using GitBinder.Domain.Projects;
using GitBinder.Domain.Common;

namespace GitBinder.Application.Git;

/// <summary>
/// Git 仓库定位器。
/// </summary>
public interface IGitLocator
{
    Task<string?> LocateAsync(CancellationToken ct = default);
}

/// <summary>
/// Git 服务，封装所有 Git CLI 行为。
/// </summary>
public interface IGitService
{
    /// <summary>显式初始化现存目录自身的 Git 仓库，不添加文件、提交或联网</summary>
    Task<Result> InitializeRepositoryAsync(string path, CancellationToken ct = default);

    /// <summary>校验目录是否为有效 Git 仓库。</summary>
    Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default);

    /// <summary>获取仓库根路径。</summary>
    Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default);

    /// <summary>获取 .git 目录真实路径。</summary>
    Task<string?> GetGitDirAsync(string path, CancellationToken ct = default);

    /// <summary>读取 origin URL；空字符串表示未配置 origin，null 表示读取失败。</summary>
    Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default);

    /// <summary>在与认证测试/传输相同的隔离上下文读取 origin；空字符串表示不存在，null 表示读取失败。</summary>
    Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default);

    /// <summary>在传输隔离上下文中仅更新已有 origin；不新增远程、不改写 pushurl。</summary>
    Task<Result> SetTransferOriginUrlAsync(string path, string originUrl, CancellationToken ct = default);

    /// <summary>新增或更新仓库的 origin URL。</summary>
    Task<Result> SetOriginUrlAsync(string path, string originUrl, CancellationToken ct = default);

    /// <summary>获取当前分支。</summary>
    Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default);

    /// <summary>解析 Remote URL 为协议与主机。</summary>
    (RemoteProtocol Protocol, string Host) ParseRemote(string url);

    /// <summary>获取 Git 版本。</summary>
    Task<string?> GetGitVersionAsync(CancellationToken ct = default);
}

/// <summary>Remote 连接测试结果。</summary>
public sealed class TestRemoteResult
{
    public bool Success { get; init; }

    /// <summary>安全、本地化错误；不包含远程命令原始输出或凭据。</summary>
    public DomainError? Error { get; init; }

    public TimeSpan Duration { get; init; }
}
