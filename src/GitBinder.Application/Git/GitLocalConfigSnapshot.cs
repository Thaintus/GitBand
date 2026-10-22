using GitBinder.Domain.GlobalMode;

namespace GitBinder.Application.Git;

/// <summary>操作补偿使用的瞬时快照，null 表示配置项缺失，Helper 列表保留空 reset 与顺序</summary>
public sealed record GitLocalConfigSnapshot(
    string? UserName,
    GitEmailConfigSnapshot EmailConfig,
    string? SshCommand,
    IReadOnlyList<string> CredentialHelpers);
