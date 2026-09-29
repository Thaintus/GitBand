using System.Runtime.Versioning;
using GitBinder.Application.Common;

namespace GitBinder.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacPrivateKeyImporter(IApplicationDataPath dataPath) : IPrivateKeyImporter
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
            // 权限在创建时即为 0600，不能先写入再 chmod，避免短暂暴露私钥。
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
            if (created) File.Delete(destination); // 只删除本次创建的、不完整的副本。
            throw;
        }
    }
}
