using GitBinder.Application.Bindings;
using GitBinder.Application.GlobalMode;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Projects;
using GitBinder.Infrastructure.Common;
using GitBinder.Infrastructure.Git;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public sealed class GlobalModeGitConfigIntegrationTests
{
    [Fact]
    public async Task EnableAndDisable_WriteExpectedIdentityToIsolatedGitRepository()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"gitbinder-global-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);

        try
        {
            var executor = new CommandExecutor();
            var initialized = await executor.ExecuteAsync("git", ["init", "--quiet"], repositoryPath);
            Assert.True(initialized.IsSuccess, initialized.StdErr);

            var accounts = new FakeAccountRepository();
            var bindings = new FakeBindingRepository();
            var projects = new FakeProjectRepository();
            var gitConfig = new GitConfigApplier(executor, null!);
            var bindingService = new BindingService(
                bindings,
                accounts,
                projects,
                new FakeSecretStore(),
                new FakeGitService(),
                gitConfig,
                new FakeSnapshotRepository(),
                new FakeGitTransfer());
            var globalMode = new GlobalModeService(new FakeSettingsRepository(), accounts, bindingService);

            var bound = new Account
            {
                Alias = "bound",
                Username = "bound",
                GitName = "Bound Identity",
                GitEmail = "bound@example.com",
                Enabled = true,
            };
            var global = new Account
            {
                Alias = "global",
                Username = "global",
                GitName = "Global Identity",
                GitEmail = "global@example.com",
                Enabled = true,
            };
            await accounts.AddAsync(bound);
            await accounts.AddAsync(global);

            var project = new Project { RepositoryPath = repositoryPath, GitDir = Path.Combine(repositoryPath, ".git") };
            await projects.AddAsync(project);
            await bindings.AddAsync(new Binding { ProjectId = project.Id, AccountId = bound.Id });

            var enabled = await globalMode.EnableAsync(global.Id);
            var globalIdentity = await gitConfig.ReadIdentityAsync(repositoryPath);

            Assert.True(enabled.IsSuccess);
            Assert.Equal(("Global Identity", "global@example.com"), globalIdentity);

            var disabled = await globalMode.DisableAsync();
            var boundIdentity = await gitConfig.ReadIdentityAsync(repositoryPath);

            Assert.True(disabled.IsSuccess);
            Assert.Equal(("Bound Identity", "bound@example.com"), boundIdentity);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }
}
