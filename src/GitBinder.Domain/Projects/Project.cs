using GitBinder.Domain.Projects;

namespace GitBinder.Domain.Projects;

/// <summary>
/// 项目聚合，表示一个本机项目目录；可以尚未初始化 Git
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>项目名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>仓库根路径。</summary>
    public string RepositoryPath { get; set; } = string.Empty;

    /// <summary>规范化后的绝对路径，用于比较。</summary>
    public string CanonicalPath { get; set; } = string.Empty;

    /// <summary>.git 目录真实路径。</summary>
    public string GitDir { get; set; } = string.Empty;

    /// <summary>origin remote URL。</summary>
    public string OriginUrl { get; set; } = string.Empty;

    /// <summary>解析出的 Remote Host。</summary>
    public string RemoteHost { get; set; } = string.Empty;

    /// <summary>解析出的协议。</summary>
    public RemoteProtocol RemoteProtocol { get; set; } = RemoteProtocol.Unknown;

    /// <summary>当前分支。</summary>
    public string CurrentBranch { get; set; } = string.Empty;

    /// <summary>尚无 Git 元数据的本地目录；仅由登记信息判断，不进行磁盘或 Git 操作</summary>
    public bool IsLocalDirectory => string.IsNullOrWhiteSpace(GitDir)
        && string.IsNullOrWhiteSpace(OriginUrl)
        && string.IsNullOrWhiteSpace(RemoteHost)
        && RemoteProtocol == RemoteProtocol.Unknown
        && string.IsNullOrWhiteSpace(CurrentBranch);

    /// <summary>最近一次连接测试结果。</summary>
    public string LastTestResult { get; set; } = string.Empty;

    /// <summary>最近一次连接测试时间。</summary>
    public DateTimeOffset? LastTestAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
