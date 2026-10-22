namespace GitBinder.Domain.Common;

/// <summary>
/// 统一的领域错误模型，用 Code + Arguments 描述，由 GUI 层做本地化。
/// </summary>
public sealed class DomainError
{
    public string Code { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? TechnicalDetails { get; }

    public DomainError(string code, params string[] arguments)
    {
        Code = code;
        Arguments = arguments;
    }

    // 诊断构造必须显式提供参数数组，避免 (code, path) 被误解析为技术详情
    public DomainError(string code, string? technicalDetails, string[] arguments)
    {
        Code = code;
        Arguments = arguments;
        TechnicalDetails = technicalDetails;
    }

    public override string ToString() => Code;
}
