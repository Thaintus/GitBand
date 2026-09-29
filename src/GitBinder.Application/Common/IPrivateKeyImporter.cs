namespace GitBinder.Application.Common;

/// <summary>由平台适配器复制私钥，并在写入前设置对应系统的安全权限。</summary>
public interface IPrivateKeyImporter
{
    Task<string> ImportAsync(string sourcePath, CancellationToken ct = default);
}
