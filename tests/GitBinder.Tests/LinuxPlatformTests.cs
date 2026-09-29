using GitBinder.Application.Common;
using GitBinder.Application.Security;
using GitBinder.Platform;
using GitBinder.Platform.Linux;
using Microsoft.Extensions.DependencyInjection;

namespace GitBinder.Tests;

public sealed class LinuxPlatformTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("///", "/")]
    [InlineData("/home/Developer/Repo///", "/home/Developer/Repo")]
    [InlineData("/data/Repo\\Name", "/data/Repo\\Name")]
    [InlineData("", "")]
    public void Normalize_PreservesRootCaseAndLiteralBackslashes(string input, string expected)
        => Assert.Equal(expected, new LinuxPathNormalizer().Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/data")]
    [InlineData("~/data")]
    [InlineData("C:\\data")]
    public void DataDirectory_IgnoresInvalidXdgDataHome(string? xdgDataHome)
    {
        var data = new LinuxApplicationDataPath(xdgDataHome, "/home/developer");
        Assert.Equal(Path.Combine("/home/developer", ".local", "share", "GitBinder"), data.DataDirectory);
    }

    [Fact]
    public void DataDirectory_UsesAbsoluteXdgDataHomeWithoutCreatingFiles()
    {
        var data = new LinuxApplicationDataPath("/custom/app data", "/home/developer");
        Assert.Equal(Path.Combine("/custom/app data", "GitBinder"), data.DataDirectory);
        Assert.Equal(Path.Combine(data.DataDirectory, "gitbinder.db"), data.DatabasePath);
        Assert.Equal(Path.Combine(data.DataDirectory, "keys"), data.KeysDirectory);
        Assert.Equal(Path.Combine(data.DataDirectory, "logs"), data.LogDirectory);
    }

    [Fact]
    public void DependencyInjection_OnLinux_ResolvesAdaptersWithoutConnectingToKeyring()
    {
        if (!OperatingSystem.IsLinux()) return;
        var services = new ServiceCollection();
        services.AddSingleton<ICommandExecutor>(new RecordingExecutor());
        services.AddPlatform();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<LinuxApplicationDataPath>(provider.GetRequiredService<IApplicationDataPath>());
        Assert.IsType<LinuxPathNormalizer>(provider.GetRequiredService<IPathNormalizer>());
        Assert.IsType<LinuxPrivateKeyImporter>(provider.GetRequiredService<IPrivateKeyImporter>());
        Assert.IsType<LinuxSecretServiceStore>(provider.GetRequiredService<ISecretStore>());
        Assert.IsType<LinuxShellService>(provider.GetRequiredService<IShellService>());
    }

    [Fact]
    public async Task SecretStore_PreCanceledRequest_DoesNotRequireNativeLibraries()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LinuxSecretServiceStore().GetAsync("gitbinder/test/not-a-real-secret", cancellation.Token));
    }

    [Fact]
    public void Canonicalize_OnLinux_ResolvesLinksAndPreservesCase()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "gitbinder-linux-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root, "Project")).FullName;
            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, repository);
            var normalizer = new LinuxPathNormalizer();
            Assert.Equal(normalizer.Canonicalize(repository), normalizer.Canonicalize(link));
            Assert.False(normalizer.Equals(Path.Combine(root, "Project"), Path.Combine(root, "project")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PrivateKeyImport_OnLinux_CreatesRestrictedFileAndDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "gitbinder-linux-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "dummy-key");
            await File.WriteAllTextAsync(source, "not-a-real-private-key");
            var data = new IsolatedDataPath(Path.Combine(root, "app"));
            var imported = await new LinuxPrivateKeyImporter(data).ImportAsync(source);
            Assert.Equal("not-a-real-private-key", await File.ReadAllTextAsync(imported));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(imported));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(data.KeysDirectory));
            Assert.True(File.Exists(source));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PrivateKeyImport_OnLinux_RejectsSymbolicLinkKeyDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "gitbinder-linux-key-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = new IsolatedDataPath(Path.Combine(root, "app"));
            Directory.CreateDirectory(data.DataDirectory);
            var outside = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
            Directory.CreateSymbolicLink(data.KeysDirectory, outside);
            var source = Path.Combine(root, "dummy-key");
            await File.WriteAllTextAsync(source, "not-a-real-private-key");
            await Assert.ThrowsAsync<IOException>(() => new LinuxPrivateKeyImporter(data).ImportAsync(source));
            Assert.Empty(Directory.EnumerateFiles(outside));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OpenDirectory_UsesArgumentArrayWithoutShellEvaluation()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitbinder-linux-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = new RecordingExecutor();
            new LinuxShellService(executor).OpenDirectory(root);
            Assert.Equal("/usr/bin/xdg-open", executor.Executable);
            Assert.Equal(new[] { Path.GetFullPath(root) }, executor.Arguments);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public string? Executable { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            Executable = executable;
            Arguments = arguments;
            return Task.FromResult(new CommandResult());
        }
        public Task<CommandResult> ExecuteWithOptionsAsync(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory, CommandExecutionOptions options, CancellationToken cancellationToken = default)
            => ExecuteAsync(executable, arguments, workingDirectory, cancellationToken);
    }

    private sealed class IsolatedDataPath(string root) : IApplicationDataPath
    {
        public string DataDirectory => root;
        public string DatabasePath => Path.Combine(root, "gitbinder.db");
        public string LogDirectory => Path.Combine(root, "logs");
        public string KeysDirectory => Path.Combine(root, "keys");
    }
}
