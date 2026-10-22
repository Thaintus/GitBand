using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Git;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Projects;
using GitBinder.Application.Settings;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Bindings;
using GitBinder.Domain.Common;
using GitBinder.Domain.GlobalMode;
using GitBinder.Domain.Projects;

namespace GitBinder.Tests.Fakes;

/// <summary>测试私钥导入端口；不复制或读取真实私钥文件。</summary>
public sealed class FakePrivateKeyImporter : GitBinder.Application.Common.IPrivateKeyImporter
{
    public Task<string> ImportAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(sourcePath + ".imported");
    }
}

/// <summary>内存账号仓储。</summary>
public sealed class FakeAccountRepository : IAccountRepository, GitBinder.Domain.Services.IAccountReader
{
    private readonly Dictionary<Guid, Account> _accounts = [];

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_accounts.GetValueOrDefault(id));

    public Task<Account?> GetDefaultAsync(CancellationToken ct = default)
        => Task.FromResult(_accounts.Values.FirstOrDefault(a => a.IsDefault && a.Enabled));

    public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Account>>(_accounts.Values.ToList());

    public Task AddAsync(Account account, CancellationToken ct = default)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Account account, CancellationToken ct = default)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _accounts.Remove(id);
        return Task.CompletedTask;
    }

    public Account? GetById(Guid id) => _accounts.GetValueOrDefault(id);

    public Account? GetDefault() => _accounts.Values.FirstOrDefault(a => a.IsDefault && a.Enabled);
}

/// <summary>内存项目仓储。</summary>
public sealed class FakeProjectRepository : IProjectRepository
{
    private readonly Dictionary<Guid, Project> _projects = [];

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_projects.GetValueOrDefault(id));

    public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Project>>(_projects.Values.ToList());

    public Task<Project?> FindByPathAsync(string canonicalPath, CancellationToken ct = default)
        => Task.FromResult(_projects.Values.FirstOrDefault(p =>
            string.Equals(p.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(Project project, CancellationToken ct = default)
    {
        _projects[project.Id] = project;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Project project, CancellationToken ct = default)
    {
        _projects[project.Id] = project;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _projects.Remove(id);
        return Task.CompletedTask;
    }
}

/// <summary>内存绑定仓储。</summary>
public sealed class FakeBindingRepository : IBindingRepository, GitBinder.Domain.Services.IBindingReader
{
    private readonly Dictionary<Guid, Binding> _bindings = [];

    public Task<Binding?> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult(_bindings.Values.FirstOrDefault(b => b.ProjectId == projectId));

    public Task<IReadOnlyList<Binding>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Binding>>(_bindings.Values.ToList());

    public Task<IReadOnlyList<Binding>> GetByAccountIdAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Binding>>(_bindings.Values.Where(b => b.AccountId == accountId).ToList());

    public Task<int> CountByAccountIdAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult(_bindings.Values.Count(b => b.AccountId == accountId));

    public Task AddAsync(Binding binding, CancellationToken ct = default)
    {
        _bindings[binding.Id] = binding;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Binding binding, CancellationToken ct = default)
    {
        _bindings[binding.Id] = binding;
        return Task.CompletedTask;
    }

    public Task DeleteByProjectIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var key = _bindings.Values.FirstOrDefault(b => b.ProjectId == projectId)?.Id;
        if (key is Guid g)
        {
            _bindings.Remove(g);
        }

        return Task.CompletedTask;
    }

    public Guid? GetAccountIdByProject(Guid projectId)
        => _bindings.Values.FirstOrDefault(b => b.ProjectId == projectId)?.AccountId;
}

/// <summary>内存 settings 仓储。</summary>
public sealed class FakeSettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, string> _settings = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_settings.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _settings[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _settings.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>内存快照仓储。</summary>
public sealed class FakeSnapshotRepository : IRepositorySnapshotRepository
{
    private readonly Dictionary<Guid, RepositorySnapshot> _snapshots = [];

    public Task<RepositorySnapshot?> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        => Task.FromResult(_snapshots.GetValueOrDefault(projectId));

    public Task SaveAsync(RepositorySnapshot snapshot, CancellationToken ct = default)
    {
        _snapshots[snapshot.ProjectId] = snapshot;
        return Task.CompletedTask;
    }

    public Task DeleteByProjectIdAsync(Guid projectId, CancellationToken ct = default)
    {
        _snapshots.Remove(projectId);
        return Task.CompletedTask;
    }
}

/// <summary>内存 Secret Store。</summary>
public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _secrets[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_secrets.GetValueOrDefault(key));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _secrets.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>克隆/拉取 Fake，不执行任何 Git 命令或网络操作。</summary>
public sealed class FakeGitTransfer : IGitTransfer
{
    public int CloneCount { get; private set; }
    public int PullCount { get; private set; }
    public int TestCount { get; private set; }
    public Guid? UsedAccountId { get; private set; }
    public Result CloneResult { get; set; } = Result.Success();
    public Result PullResult { get; set; } = Result.Success();
    public Result TestResult { get; set; } = Result.Success();
    public Action<string>? OnClone { get; set; }
    public Func<string, Account, CancellationToken, Task<Result>>? OnPull { get; set; }
    public Func<string, Account, CancellationToken, Task<Result>>? OnTest { get; set; }

    public Task<Result> TestAsync(string path, Account account, CancellationToken ct = default)
    {
        TestCount++;
        UsedAccountId = account.Id;
        return OnTest?.Invoke(path, account, ct) ?? Task.FromResult(TestResult);
    }

    public Task<Result> CloneAsync(string url, string destination, Account account, CancellationToken ct = default)
    {
        CloneCount++;
        UsedAccountId = account.Id;
        OnClone?.Invoke(destination);
        return Task.FromResult(CloneResult);
    }

    public Task<Result> PullAsync(string path, Account account, CancellationToken ct = default)
    {
        PullCount++;
        UsedAccountId = account.Id;
        return OnPull?.Invoke(path, account, ct) ?? Task.FromResult(PullResult);
    }
}

/// <summary>假 Git 服务：记录 config 应用与恢复调用，不真正执行 Git。</summary>
public sealed class FakeGitService : IGitService
{
    public bool InitializeSucceeds { get; set; } = true;

    public int InitializeCount { get; private set; }

    public string? LastInitializedPath { get; private set; }

    public Func<string, CancellationToken, Task<Result>>? OnInitialize { get; set; }

    public Func<string, bool>? ValidateRepository { get; set; }

    public bool ValidateResult { get; set; } = true;

    public string RepositoryRoot { get; set; } = "D:\\repo";

    public string OriginUrl { get; set; } = "git@github.com:company/project.git";

    public string CurrentBranch { get; set; } = "main";

    public bool SetOriginUrlSucceeds { get; set; } = true;

    public bool OriginReadFails { get; set; }

    public string? LastSetOriginUrl { get; private set; }

    public Task<Result> InitializeRepositoryAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        InitializeCount++;
        LastInitializedPath = path;
        if (OnInitialize is not null)
            return OnInitialize(path, ct);
        if (!InitializeSucceeds)
            return Task.FromResult(Result.Failure(new DomainError("PROJECT_GIT_INITIALIZATION_FAILED")));

        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var previousValidation = ValidateRepository;
        var previousResult = ValidateResult;
        var alreadyRepository = (previousValidation?.Invoke(path) ?? previousResult)
            && string.Equals(RepositoryRoot, path, comparer);
        RepositoryRoot = path;
        ValidateResult = true;
        ValidateRepository = candidate => string.Equals(candidate, path, comparer)
            || (previousValidation?.Invoke(candidate) ?? previousResult);
        if (!alreadyRepository)
            OriginUrl = string.Empty;
        return Task.FromResult(Result.Success());
    }

    public Task<bool> ValidateRepositoryAsync(string path, CancellationToken ct = default)
        => Task.FromResult(ValidateRepository?.Invoke(path) ?? ValidateResult);

    public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(RepositoryRoot);

    public Task<string?> GetGitDirAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(Path.Combine(RepositoryRoot, ".git"));

    public Task<string?> GetOriginUrlAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(OriginReadFails ? null : OriginUrl);

    public Task<string?> GetTransferOriginUrlAsync(string path, CancellationToken ct = default)
        => GetOriginUrlAsync(path, ct);

    public Task<Result> SetTransferOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
        => SetOriginUrlAsync(path, originUrl, ct);

    public Task<Result> SetOriginUrlAsync(string path, string originUrl, CancellationToken ct = default)
    {
        if (!SetOriginUrlSucceeds)
        {
            return Task.FromResult(Result.Failure(new DomainError("PROJECT_REMOTE_UPDATE_FAILED", path)));
        }

        LastSetOriginUrl = originUrl;
        OriginUrl = originUrl;
        return Task.FromResult(Result.Success());
    }

    public Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(CurrentBranch);

    public (RemoteProtocol Protocol, string Host) ParseRemote(string url)
    {
        if (url.StartsWith("git@"))
        {
            var at = url.IndexOf('@');
            var colon = url.IndexOf(':', at + 1);
            return (RemoteProtocol.Ssh, url.Substring(at + 1, colon - at - 1));
        }

        if (url.StartsWith("https://"))
        {
            var rest = url["https://".Length..];
            var slash = rest.IndexOf('/');
            return (RemoteProtocol.Https, slash >= 0 ? rest[..slash] : rest);
        }

        return (RemoteProtocol.Unknown, string.Empty);
    }

    public Task<string?> GetGitVersionAsync(CancellationToken ct = default)
        => Task.FromResult<string?>("git version 2.45.1");
}

/// <summary>假 Git Config 应用器：记录调用，可断言。</summary>
public sealed class FakeGitConfigApplier : IGitConfigApplier
{
    public string StoredName { get; private set; } = "orig-name";
    public string StoredEmail { get; private set; } = "orig@example.com";
    public GitEmailConfigSnapshot StoredEmailConfig { get; set; } = new("orig@example.com", null, null);
    public string StoredSshCommand { get; private set; } = string.Empty;
    public string StoredCredentialHelper { get; private set; } = string.Empty;

    public int ApplyIdentityCount { get; private set; }
    public int ApplySshCommandCount { get; private set; }
    public int ApplyCredentialHelperCount { get; private set; }
    public int RestoreCount { get; private set; }

    public bool ThrowOnApply { get; set; }

    public string? LastRestoredName { get; private set; }
    public string? LastRestoredSsh { get; private set; }
    public GitEmailConfigSnapshot? LastRestoredEmailConfig { get; private set; }

    public (string Name, string Email) AppliedIdentity { get; private set; }
    public string AppliedSshCommand { get; private set; } = string.Empty;
    public Guid? AppliedCredentialAccountId { get; private set; }

    public string BuildSshCommand(Account account)
        => account.AuthenticationType is AuthenticationType.Ssh or AuthenticationType.Both
            && !string.IsNullOrWhiteSpace(account.SshPrivateKeyPath)
            ? $"ssh -i \"{account.SshPrivateKeyPath}\" -o IdentitiesOnly=yes"
            : "ssh -F NUL -i NUL -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityAgent=none -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no";

    public Task<(string Name, string Email)> ReadIdentityAsync(string repositoryPath, CancellationToken ct = default)
        => Task.FromResult((StoredName, StoredEmail));

    public Task<GitEmailConfigSnapshot> ReadEmailConfigAsync(string repositoryPath, CancellationToken ct = default)
        => Task.FromResult(StoredEmailConfig);

    public Task<string> ReadSshCommandAsync(string repositoryPath, CancellationToken ct = default)
        => Task.FromResult(StoredSshCommand);

    public Task<string> ReadCredentialHelperAsync(string repositoryPath, CancellationToken ct = default)
        => Task.FromResult(StoredCredentialHelper);

    public Task ApplyIdentityAsync(string repositoryPath, string gitName, string gitEmail, CancellationToken ct = default)
    {
        if (ThrowOnApply)
        {
            throw new InvalidOperationException("Failed to write local Git config.");
        }

        ApplyIdentityCount++;
        AppliedIdentity = (gitName, gitEmail);
        return Task.CompletedTask;
    }

    public Task ApplySshCommandAsync(string repositoryPath, string sshCommand, CancellationToken ct = default)
    {
        if (ThrowOnApply)
        {
            throw new InvalidOperationException("Failed to write local Git config.");
        }

        ApplySshCommandCount++;
        AppliedSshCommand = sshCommand;
        return Task.CompletedTask;
    }

    public Task ApplyCredentialHelperAsync(string repositoryPath, Guid? accountId, CancellationToken ct = default)
    {
        if (ThrowOnApply)
        {
            throw new InvalidOperationException("Failed to write local Git config.");
        }

        ApplyCredentialHelperCount++;
        AppliedCredentialAccountId = accountId;
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string repositoryPath, string? name, string? email, string? sshCommand, string? credentialHelper, CancellationToken ct = default, GitEmailConfigSnapshot? emailConfig = null)
    {
        RestoreCount++;
        LastRestoredName = name;
        LastRestoredSsh = sshCommand;
        LastRestoredEmailConfig = emailConfig;
        return Task.CompletedTask;
    }
}

/// <summary>假 Git 全局配置应用器：保存写入与恢复请求，不访问真实用户配置。</summary>
public sealed class FakeGlobalGitConfigApplier : IGlobalGitConfigApplier
{
    public GlobalGitConfigSnapshot CurrentSnapshot { get; set; } = new(
        "Original Name",
        "original@example.com",
        "ssh -i original-key",
        null,
        new GitEmailConfigSnapshot("original@example.com", null, null));

    public Account? AppliedAccount { get; private set; }

    public GlobalGitConfigSnapshot? RestoredSnapshot { get; private set; }

    public bool FailApply { get; set; }

    public bool FailRestore { get; set; }

    public Task<GlobalGitConfigSnapshot> CaptureAsync(CancellationToken ct = default)
        => Task.FromResult(CurrentSnapshot);

    public Task<Result> ApplyAsync(Account account, CancellationToken ct = default)
    {
        if (FailApply)
        {
            return Task.FromResult(Result.Failure(new DomainError("GLOBALMODE_GLOBAL_CONFIG_FAILED", "test")));
        }

        AppliedAccount = account;
        return Task.FromResult(Result.Success());
    }

    public Task<Result> RestoreAsync(GlobalGitConfigSnapshot snapshot, CancellationToken ct = default)
    {
        if (FailRestore)
        {
            return Task.FromResult(Result.Failure(new DomainError("GLOBALMODE_GLOBAL_CONFIG_FAILED", "test")));
        }

        RestoredSnapshot = snapshot;
        return Task.FromResult(Result.Success());
    }
}

/// <summary>假 Global Mode 状态读取。</summary>
public sealed class FakeGlobalModeState : GitBinder.Domain.Services.IGlobalModeState
{
    public bool IsEnabled { get; set; }

    public Guid? GlobalAccountId { get; set; }
}
