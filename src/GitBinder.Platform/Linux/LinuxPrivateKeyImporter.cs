using System.Runtime.Versioning;
using GitBinder.Application.Common;

namespace GitBinder.Platform.Linux;

[SupportedOSPlatform("linux")]
public sealed class LinuxPrivateKeyImporter(IApplicationDataPath dataPath) : IPrivateKeyImporter
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode KeyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<string> ImportAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Directory.CreateDirectory(dataPath.KeysDirectory, DirectoryMode);
        if (directory.LinkTarget is not null)
            throw new IOException("The private key directory must not be a symbolic link.");
        File.SetUnixFileMode(directory.FullName, DirectoryMode);
        var destination = Path.Combine(directory.FullName,
            Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath));
        var created = false;
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            // 创建瞬间即为 0600，禁止先复制再 chmod 导致短暂暴露。
            await using var target = new FileStream(destination, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = KeyMode,
            });
            created = true;
            await source.CopyToAsync(target, ct);
            return destination;
        }
        catch
        {
            if (created) File.Delete(destination); // 仅删除本次创建的不完整副本。
            throw;
        }
    }
}
