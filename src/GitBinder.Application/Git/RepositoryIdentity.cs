namespace GitBinder.Application.Git;

/// <summary>Git 配置解析后的当前提交身份，不代表远程认证账号。null 表示该配置未设置。</summary>
public sealed record RepositoryIdentity(string? UserName, string? UserEmail);
