namespace GitBinder.Infrastructure.Git;

/// <summary>只读临时 SSH 配置，兼容 Windows OpenSSH 与 Git 自带 OpenSSH，不依赖空设备</summary>
internal sealed class EmptySshConfig : IDisposable
{
    private readonly FileStream _file;
    public string Path { get; }

    public EmptySshConfig(string content = "")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gitbinder-ssh-{Guid.NewGuid():N}.conf");
        // 先创建并写入，再只读持有，避免 Windows OpenSSH 拒绝已有写句柄
        // 不使用 DeleteOnClose，避免要求 SSH 以 FILE_SHARE_DELETE 打开文件
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(Path, options))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) writer.Write(content);
        _file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void Dispose()
    {
        _file.Dispose();
        // 仅清理本实例创建的唯一空文件；清理失败不覆盖真实 Git 操作结果。
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
