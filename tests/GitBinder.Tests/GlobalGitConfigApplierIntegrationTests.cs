using System.Diagnostics;
using System.Text;
using GitBinder.Application.Common;
using GitBinder.Domain.Accounts;
using GitBinder.Infrastructure.Git;

namespace GitBinder.Tests;

/// <summary>
/// 验证 git config --global 的写入方式；通过 GIT_CONFIG_GLOBAL 隔离，
/// 不会读取或修改开发机的真实 Git 全局配置。
/// </summary>
public sealed class GlobalGitConfigApplierIntegrationTests
{
    [Fact]
    public async Task ApplyAndRestore_ChangesIsolatedGlobalGitConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gitbinder-global-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var executor = new IsolatedGlobalGitCommandExecutor(Path.Combine(directory, "gitconfig"));
            var applier = new GitConfigApplier(executor, null!);
            var original = new Account
            {
                GitName = "Original Identity",
                GitEmail = "original@example.com",
                AuthenticationType = AuthenticationType.Ssh,
                SshPrivateKeyPath = @"C:\keys\original",
            };
            var target = new Account
            {
                GitName = "Global Identity",
                GitEmail = "global@example.com",
                AuthenticationType = AuthenticationType.Ssh,
                SshPrivateKeyPath = @"C:\keys\global",
            };

            Assert.True((await applier.ApplyAsync(original)).IsSuccess);
            var snapshot = await applier.CaptureAsync();

            var applied = await applier.ApplyAsync(target);
            var current = await applier.CaptureAsync();

            Assert.True(applied.IsSuccess);
            Assert.Equal(target.GitName, current.UserName);
            Assert.Equal(target.GitEmail, current.UserEmail);
            var expectedKeyPath = OperatingSystem.IsWindows()
                ? target.SshPrivateKeyPath.Replace('\\', '/') : target.SshPrivateKeyPath;
            Assert.Contains(expectedKeyPath, current.SshCommand ?? string.Empty);

            var restored = await applier.RestoreAsync(snapshot);
            var afterRestore = await applier.CaptureAsync();

            Assert.True(restored.IsSuccess);
            Assert.Equal(snapshot.UserName, afterRestore.UserName);
            Assert.Equal(snapshot.UserEmail, afterRestore.UserEmail);
            Assert.Equal(snapshot.SshCommand, afterRestore.SshCommand);
            Assert.Equal(snapshot.CredentialHelpers, afterRestore.CredentialHelpers);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class IsolatedGlobalGitCommandExecutor : ICommandExecutor
    {
        private readonly string _globalConfigPath;

        public IsolatedGlobalGitCommandExecutor(string globalConfigPath)
        {
            _globalConfigPath = globalConfigPath;
        }

        public async Task<CommandResult> ExecuteAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory ?? string.Empty,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = _globalConfigPath;
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return new CommandResult { ExitCode = -1, StdErr = "Unable to start Git." };
                }

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken));
                return new CommandResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = await stdout,
                    StdErr = await stderr,
                };
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -1, StdErr = ex.Message };
            }
        }
    }
}
