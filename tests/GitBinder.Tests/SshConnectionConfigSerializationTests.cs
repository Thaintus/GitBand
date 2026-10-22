using System.Reflection;
using System.Text;
using GitBinder.Application.Common;
using GitBinder.Infrastructure.Common;
using GitBinder.Infrastructure.Git;
using GitBinder.Infrastructure.Ssh;

namespace GitBinder.Tests;

/// <summary>仅用隔离配置运行本机 ssh -G，不读取用户 SSH 配置、不连接主机或 Agent</summary>
public sealed class SshConnectionConfigSerializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbinder ssh scalar-" + Guid.NewGuid().ToString("N"));

    public SshConnectionConfigSerializationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("hostname", "example.org", "example.org")]
    [InlineData("port", "2222", "2222")]
    [InlineData("identityagent", "none", "none")]
    [InlineData("identityagent", "C:/First Last/agent.sock", "\"C:/First Last/agent.sock\"")]
    [InlineData("hostkeyalias", "alias \"quoted\"", "\"alias \\\"quoted\\\"\"")]
    [InlineData("hostkeyalias", "owner's alias", "\"owner's alias\"")]
    [InlineData("identityagent", @"C:\First Last\agent.sock", "\"C:\\\\First Last\\\\agent.sock\"")]
    [InlineData("proxycommand", "\"C:/Program Files/proxy.exe\" --host %h --port %p", "\"C:/Program Files/proxy.exe\" --host %h --port %p")]
    [InlineData("userknownhostsfile", "~/.ssh/known_hosts ~/.ssh/known_hosts2", "~/.ssh/known_hosts ~/.ssh/known_hosts2")]
    [InlineData("globalknownhostsfile", "C:/First Last/known_hosts C:/Other File/hosts", "C:/First Last/known_hosts C:/Other File/hosts")]
    public void Serialize_QuotesScalarsAndLeavesCommandAndListOutputUnchanged(string key, string value, string expected)
        => Assert.Equal(expected, Serialize(key, value));

    [Theory]
    [InlineData("identityagent", "C:/First Last/agent.sock")]
    [InlineData("identityagent", @"C:\First Last\agent.sock")]
    [InlineData("hostkeyalias", "alias with spaces")]
    [InlineData("hostkeyalias", "alias \"quoted\"")]
    [InlineData("hostkeyalias", @"alias\with\slashes")]
    [InlineData("hostkeyalias", "owner's #alias \\\"mixed\\\"")]
    public async Task ScalarValue_RoundTripsThroughInstalledSshParsersWithoutConnecting(string key, string value)
    {
        var executables = await FindSshExecutablesAsync();
        Assert.NotEmpty(executables);
        var config = Path.Combine(_root, "scalar.conf");
        await File.WriteAllTextAsync(config,
            "Host fixture.invalid\n    HostName fixture.invalid\n    " + key + " " + Serialize(key, value) + "\n",
            new UTF8Encoding(false));
        foreach (var executable in executables)
        {
            var result = await new CommandExecutor().ExecuteWithOptionsAsync(executable,
                ["-G", "-F", config, "-o", "BatchMode=yes", "-o", "CanonicalizeHostname=no",
                    "-o", "UserKnownHostsFile=none", "-o", "GlobalKnownHostsFile=none", "fixture.invalid"],
                _root, new CommandExecutionOptions { Timeout = TimeSpan.FromSeconds(10) });

            Assert.True(result.IsSuccess, $"Isolated ssh -G failed with exit code {result.ExitCode}");
            var resolved = Assert.Single(result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => line.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(value, resolved[(key.Length + 1)..].TrimEnd('\r'));
        }
    }

    private static string Serialize(string key, string value)
    {
        var type = typeof(GitTransfer).Assembly.GetType("GitBinder.Infrastructure.Git.SshConnectionConfig", throwOnError: true)!;
        var method = type.GetMethod("SerializeValue", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [key, value])!;
    }

    private static async Task<IReadOnlyList<string>> FindSshExecutablesAsync()
    {
        var candidates = new List<string>();
        var located = await new SshLocator().LocateAsync();
        if (located is not null) candidates.Add(located);
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows)) candidates.Add(Path.Combine(windows, "System32", "OpenSSH", "ssh.exe"));
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles)) candidates.Add(Path.Combine(programFiles, "Git", "usr", "bin", "ssh.exe"));
        }
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
