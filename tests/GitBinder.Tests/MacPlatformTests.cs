using GitBinder.Application.Common;
using GitBinder.Platform;
using GitBinder.Platform.MacOS;
using GitBinder.Platform.Linux;
using GitBinder.Platform.Windows;

namespace GitBinder.Tests;

public sealed class MacPlatformTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("///", "/")]
    [InlineData("/Users/Dev/Project///", "/Users/Dev/Project")]
    [InlineData("/Volumes/CaseSensitive/Project", "/Volumes/CaseSensitive/Project")]
    [InlineData("", "")]
    public void Normalize_PreservesRootAndCase(string input, string expected)
        => Assert.Equal(expected, new MacPathNormalizer().Normalize(input));

    [Fact]
    public void DataPathFactory_SelectsHostPlatformWithoutTouchingDisk()
    {
        if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsApplicationDataPath>(PlatformPaths.CreateApplicationDataPath());
        else if (OperatingSystem.IsMacOS())
        {
            var data = Assert.IsType<MacApplicationDataPath>(PlatformPaths.CreateApplicationDataPath());
            Assert.EndsWith("/Library/Application Support/GitBinder", data.DataDirectory);
            Assert.Equal(Path.Combine(data.DataDirectory, "gitbinder.db"), data.DatabasePath);
        }
        else if (OperatingSystem.IsLinux())
            Assert.IsType<LinuxApplicationDataPath>(PlatformPaths.CreateApplicationDataPath());
        else Assert.Throws<PlatformNotSupportedException>(() => PlatformPaths.CreateApplicationDataPath());
    }

    [Fact]
    public async Task PrivateKeyImport_OnMac_CreatesRestrictedDirectoryAndFile()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "gitbinder-key-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "dummy-key");
            await File.WriteAllTextAsync(source, "not-a-real-private-key");
            var data = new IsolatedDataPath(Path.Combine(root, "app"));
            var imported = await new MacPrivateKeyImporter(data).ImportAsync(source);
            Assert.Equal("not-a-real-private-key", await File.ReadAllTextAsync(imported));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(imported));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(data.KeysDirectory));
            Assert.True(File.Exists(source));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class IsolatedDataPath(string root) : IApplicationDataPath
    {
        public string DataDirectory => root;
        public string DatabasePath => Path.Combine(root, "gitbinder.db");
        public string LogDirectory => Path.Combine(root, "logs");
        public string KeysDirectory => Path.Combine(root, "keys");
    }
}
