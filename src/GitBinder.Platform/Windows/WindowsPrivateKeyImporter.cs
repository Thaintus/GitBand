using GitBinder.Application.Common;

namespace GitBinder.Platform.Windows;

/// <summary>保持 Windows 原有的用户应用目录私钥复制行为。</summary>
public sealed class WindowsPrivateKeyImporter(IApplicationDataPath dataPath) : IPrivateKeyImporter
{
    public async Task<string> ImportAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dataPath.KeysDirectory);
        var destination = Path.Combine(dataPath.KeysDirectory,
            Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath));
        await Task.Run(() => File.Copy(sourcePath, destination, overwrite: false), ct);
        return destination;
    }
}
