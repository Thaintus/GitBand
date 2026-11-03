using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Domain.Common;
using GitBinder.Domain.Projects;

namespace GitBinder.Application.Projects;

/// <summary>
/// 项目应用服务：负责本地目录/仓库登记、校验与 Remote 解析
/// </summary>
public sealed class ProjectService
{
    private readonly IProjectRepository _repository;
    private readonly IGitService _gitService;
    private readonly IPathNormalizer _pathNormalizer;
    private readonly IAccountRepository _accountRepository;
    private readonly BindingService _bindingService;
    private readonly SemaphoreSlim _registrationGate = new(1, 1);

    public ProjectService(
        IProjectRepository repository,
        IGitService gitService,
        IPathNormalizer pathNormalizer,
        IAccountRepository accountRepository,
        BindingService bindingService)
    {
        _repository = repository;
        _gitService = gitService;
        _pathNormalizer = pathNormalizer;
        _accountRepository = accountRepository;
        _bindingService = bindingService;
    }

    public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default)
        => _repository.GetAllAsync(ct);

    /// <summary>手动刷新时只读该目录自身仓库的提交身份，不修改 Git 或绑定记录。</summary>
    public async Task<Result<RepositoryIdentity>> ReadCurrentIdentityAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var project = await _repository.GetByIdAsync(id, ct);
            if (project is null) return Result<RepositoryIdentity>.Failure(new DomainError("PROJECT_NOT_FOUND"));
            var root = await _gitService.GetRepositoryRootAsync(project.RepositoryPath, ct);
            if (string.IsNullOrWhiteSpace(root) || !_pathNormalizer.Equals(root, project.RepositoryPath))
                return Result<RepositoryIdentity>.Failure(new DomainError("PROJECT_CONFIGURATION_READ_FAILED"));
            var identity = await _gitService.GetCurrentIdentityAsync(project.RepositoryPath, ct);
            return identity is null
                ? Result<RepositoryIdentity>.Failure(new DomainError("PROJECT_CONFIGURATION_READ_FAILED"))
                : Result<RepositoryIdentity>.Success(identity);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Result<RepositoryIdentity>.Failure(new DomainError("PROJECT_CONFIGURATION_READ_FAILED"));
        }
    }

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _repository.GetByIdAsync(id, ct);

    /// <summary>登记本地项目；普通目录不初始化 Git，有效仓库读取现有元数据</summary>
    public async Task<Result<Project>> AddAsync(string path, CancellationToken ct = default, bool bindDefaultAccount = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<Project>.Failure(new DomainError("PROJECT_PATH_REQUIRED"));
        }

        var canonical = _pathNormalizer.Canonicalize(path);

        var isRepository = await _gitService.ValidateRepositoryAsync(canonical, ct);
        ct.ThrowIfCancellationRequested();
        if (!isRepository && !Directory.Exists(canonical))
            return Result<Project>.Failure(new DomainError("PROJECT_DIRECTORY_NOT_FOUND", arguments: [canonical]));

        var root = isRepository ? await _gitService.GetRepositoryRootAsync(canonical, ct) ?? canonical : canonical;
        var rootKey = _pathNormalizer.Canonicalize(root);
        // 按实际根目录与平台路径规则去重，不能通过子目录或 Windows 大小写绕过登记检查
        if ((await _repository.GetAllAsync(ct)).Any(existing => _pathNormalizer.Equals(existing.RepositoryPath, rootKey)))
            return Result<Project>.Failure(new DomainError("PROJECT_ALREADY_EXISTS", arguments: [root]));

        var gitDir = isRepository ? await _gitService.GetGitDirAsync(root, ct) : string.Empty;
        var originUrl = isRepository ? await _gitService.GetOriginUrlAsync(root, ct) : string.Empty;
        var branch = isRepository ? await _gitService.GetCurrentBranchAsync(root, ct) : string.Empty;
        if (gitDir is null || originUrl is null || branch is null || (isRepository && string.IsNullOrWhiteSpace(gitDir)))
            return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
        if (HasHttpUserInfo(originUrl))
            return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_CREDENTIAL_FORBIDDEN"));

        var (protocol, host) = _gitService.ParseRemote(originUrl);

        var project = new Project
        {
            Name = Path.GetFileName(_pathNormalizer.Normalize(root)),
            RepositoryPath = root,
            CanonicalPath = rootKey,
            GitDir = gitDir,
            OriginUrl = originUrl,
            RemoteProtocol = protocol,
            RemoteHost = host,
            CurrentBranch = branch,
        };

        var registered = await SaveRegistrationAsync(project, isNew: true, ct);
        if (!registered.IsSuccess) return registered;

        // 只有已初始化 Git 的新项目才默认绑定；登记普通目录不得写 Git 配置或创建快照
        var defaultAccount = isRepository && bindDefaultAccount ? await _accountRepository.GetDefaultAsync(ct) : null;
        if (defaultAccount is not null)
        {
            await _bindingService.BindAsync(project.Id, defaultAccount.Id, ct);
        }

        return Result<Project>.Success(project);
    }

    /// <summary>移除登记（仅移除记录，不删除源码）。同时清理绑定与快照。</summary>
    public async Task<Result> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var project = await _repository.GetByIdAsync(id, ct);
        if (project is null)
        {
            return Result.Failure(new DomainError("PROJECT_NOT_FOUND", id.ToString()));
        }

        // 先解绑（恢复仓库配置并删除绑定/快照）。
        await _bindingService.UnbindAsync(id, ct);

        await _repository.DeleteAsync(id, ct);
        return Result.Success();
    }

    /// <summary>按用户显式操作初始化当前项目目录，读取元数据后保留项目身份，不自动绑定账号</summary>
    public async Task<Result<Project>> InitializeRepositoryAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var project = await _repository.GetByIdAsync(id, ct);
            if (project is null)
                return Result<Project>.Failure(new DomainError("PROJECT_NOT_FOUND"));

            if (project.IsLocalDirectory)
            {
                if (!Directory.Exists(project.RepositoryPath))
                    return Result<Project>.Failure(new DomainError("PROJECT_DIRECTORY_NOT_FOUND", arguments: [project.RepositoryPath]));

                var initialization = await _gitService.InitializeRepositoryAsync(project.RepositoryPath, ct);
                if (!initialization.IsSuccess)
                    return Result<Project>.Failure(initialization.Error!);
            }

            var isRepository = await _gitService.ValidateRepositoryAsync(project.RepositoryPath, ct);
            ct.ThrowIfCancellationRequested();
            if (!isRepository)
                return Result<Project>.Failure(new DomainError(project.IsLocalDirectory
                    ? "PROJECT_GIT_INITIALIZATION_FAILED" : "PROJECT_REPOSITORY_NOT_FOUND", arguments: [project.RepositoryPath]));

            var root = await _gitService.GetRepositoryRootAsync(project.RepositoryPath, ct);
            if (string.IsNullOrWhiteSpace(root))
                return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
            // 显式初始化后仍核对根目录，不能把登记目录替换成父仓库或其他项目
            if (!_pathNormalizer.Equals(root, project.RepositoryPath))
                return Result<Project>.Failure(new DomainError("PROJECT_REPOSITORY_ROOT_MISMATCH"));
            if (!project.IsLocalDirectory)
                return Result<Project>.Success(project);

            if ((await _repository.GetAllAsync(ct)).Any(other => other.Id != id
                && _pathNormalizer.Equals(other.RepositoryPath, root)))
                return Result<Project>.Failure(new DomainError("PROJECT_ALREADY_EXISTS", arguments: [root]));

            var gitDir = await _gitService.GetGitDirAsync(root, ct);
            var origin = await _gitService.GetOriginUrlAsync(root, ct);
            var branch = await _gitService.GetCurrentBranchAsync(root, ct);
            if (string.IsNullOrWhiteSpace(gitDir) || origin is null || branch is null)
                return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
            if (HasHttpUserInfo(origin))
                return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_CREDENTIAL_FORBIDDEN"));

            var updated = CopyProject(project);
            updated.RepositoryPath = root;
            updated.CanonicalPath = _pathNormalizer.Canonicalize(root);
            updated.GitDir = gitDir;
            updated.OriginUrl = origin;
            (updated.RemoteProtocol, updated.RemoteHost) = _gitService.ParseRemote(origin);
            updated.CurrentBranch = branch;
            updated.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.UpdateAsync(updated, ct);
            return Result<Project>.Success(updated);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 初始化产物归用户所有，后续读取或登记失败时保留目录中的 Git 数据
            return Result<Project>.Failure(new DomainError("PROJECT_GIT_INITIALIZATION_FAILED"));
        }
    }

    /// <summary>保存 origin 地址；普通目录先显式初始化 Git，已有绑定按新地址重应用，不进行联网或传输</summary>
    public async Task<Result<Project>> UpdateOriginUrlAsync(
        Guid id,
        string originUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_URL_REQUIRED"));
        }

        var normalizedUrl = originUrl.Trim();
        if (HasHttpUserInfo(normalizedUrl))
        {
            return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_CREDENTIAL_FORBIDDEN"));
        }

        var project = await _repository.GetByIdAsync(id, ct);
        if (project is null)
        {
            return Result<Project>.Failure(new DomainError("PROJECT_NOT_FOUND", id.ToString()));
        }

        if (project.IsLocalDirectory)
        {
            // 先校验地址再创建 Git 数据，输入错误不能触发目录初始化
            if (!GitTransferRemote.TryParse(normalizedUrl, out _))
                return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_URL_INVALID"));

            var initialization = await InitializeRepositoryAsync(id, ct);
            if (!initialization.IsSuccess)
                return Result<Project>.Failure(initialization.Error!);
            project = initialization.Value!;
        }

        if (!await _gitService.ValidateRepositoryAsync(project.RepositoryPath, ct))
        {
            return Result<Project>.Failure(new DomainError("PROJECT_REPOSITORY_NOT_FOUND", project.RepositoryPath));
        }
        ct.ThrowIfCancellationRequested();
        var root = await _gitService.GetRepositoryRootAsync(project.RepositoryPath, ct);
        if (string.IsNullOrWhiteSpace(root))
            return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
        // 本项目的 Git 数据缺失时，不能沿父目录发现的仓库修改远程地址或重应用账号
        if (!_pathNormalizer.Equals(root, project.RepositoryPath))
            return Result<Project>.Failure(new DomainError("PROJECT_REPOSITORY_ROOT_MISMATCH"));

        var update = await _gitService.SetOriginUrlAsync(project.RepositoryPath, normalizedUrl, ct);
        if (!update.IsSuccess)
        {
            return Result<Project>.Failure(update.Error!);
        }

        var updated = CopyProject(project);
        updated.OriginUrl = await _gitService.GetOriginUrlAsync(project.RepositoryPath, ct) ?? normalizedUrl;
        var (protocol, host) = _gitService.ParseRemote(updated.OriginUrl);
        updated.RemoteProtocol = protocol;
        updated.RemoteHost = host;
        updated.UpdatedAt = DateTimeOffset.UtcNow;

        await _repository.UpdateAsync(updated, ct);
        try
        {
            var binding = await _bindingService.GetByProjectIdAsync(id, ct);
            if (binding is not null)
            {
                // 保留原绑定账号；BindingService 统一处理新协议配置与全局模式优先级
                var reapply = await _bindingService.BindAsync(id, binding.AccountId, ct);
                if (!reapply.IsSuccess)
                    return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_SAVED_BINDING_APPLY_FAILED"));
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_SAVED_BINDING_APPLY_FAILED"));
        }

        return Result<Project>.Success(updated);
    }

    /// <summary>读取本地元数据；普通目录自行初始化 Git 后可激活，不联网、不写 Git 配置或自动绑定</summary>
    public async Task<Result<Project>> RefreshMetadataAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var project = await _repository.GetByIdAsync(id, ct);
            if (project is null)
                return Result<Project>.Failure(new DomainError("PROJECT_NOT_FOUND"));
            var isRepository = await _gitService.ValidateRepositoryAsync(project.RepositoryPath, ct);
            ct.ThrowIfCancellationRequested();
            if (!isRepository)
            {
                if (project.IsLocalDirectory && Directory.Exists(project.RepositoryPath))
                    return Result<Project>.Success(project);
                return Result<Project>.Failure(new DomainError(project.IsLocalDirectory
                    ? "PROJECT_DIRECTORY_NOT_FOUND" : "PROJECT_REPOSITORY_NOT_FOUND", arguments: [project.RepositoryPath]));
            }

            var gitDir = project.GitDir;
            var branch = project.CurrentBranch;
            if (project.IsLocalDirectory)
            {
                var root = await _gitService.GetRepositoryRootAsync(project.RepositoryPath, ct);
                if (string.IsNullOrWhiteSpace(root))
                    return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
                // Git 会向上寻找父仓库；页面刷新不能将普通目录静默转换为另一个项目
                if (!_pathNormalizer.Equals(root, project.RepositoryPath))
                    return Result<Project>.Success(project);
                if ((await _repository.GetAllAsync(ct)).Any(other => other.Id != id
                    && _pathNormalizer.Equals(other.RepositoryPath, root)))
                    return Result<Project>.Failure(new DomainError("PROJECT_ALREADY_EXISTS", arguments: [root]));
                gitDir = await _gitService.GetGitDirAsync(root, ct);
                branch = await _gitService.GetCurrentBranchAsync(root, ct);
                if (string.IsNullOrWhiteSpace(gitDir) || branch is null)
                    return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
            }

            var origin = await _gitService.GetOriginUrlAsync(project.RepositoryPath, ct);
            if (origin is null)
                return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
            if (HasHttpUserInfo(origin))
                return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_CREDENTIAL_FORBIDDEN"));
            var (protocol, host) = _gitService.ParseRemote(origin);
            if (project.OriginUrl == origin && project.RemoteProtocol == protocol && project.RemoteHost == host
                && project.GitDir == gitDir && project.CurrentBranch == branch)
                return Result<Project>.Success(project);

            var updated = CopyProject(project);
            updated.OriginUrl = origin;
            updated.RemoteProtocol = protocol;
            updated.RemoteHost = host;
            updated.GitDir = gitDir;
            updated.CurrentBranch = branch;
            updated.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.UpdateAsync(updated, ct);
            return Result<Project>.Success(updated);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
        }
    }

    /// <summary>重新定位已移动的同一项目；已有仓库不能降级为普通目录，不搬移文件或修改 Git 配置</summary>
    public async Task<Result<Project>> UpdatePathAsync(Guid id, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<Project>.Failure(new DomainError("PROJECT_PATH_REQUIRED"));
        if (!Path.IsPathFullyQualified(path.Trim()))
            return Result<Project>.Failure(new DomainError("PROJECT_PATH_ABSOLUTE_REQUIRED"));

        try
        {
            var project = await _repository.GetByIdAsync(id, ct);
            if (project is null)
                return Result<Project>.Failure(new DomainError("PROJECT_NOT_FOUND"));
            var canonical = _pathNormalizer.Canonicalize(path.Trim());
            var isRepository = await _gitService.ValidateRepositoryAsync(canonical, ct);
            ct.ThrowIfCancellationRequested();
            if (!isRepository && !project.IsLocalDirectory)
                return Result<Project>.Failure(new DomainError("PROJECT_REPOSITORY_NOT_FOUND", arguments: [canonical]));
            if (!isRepository && !Directory.Exists(canonical))
                return Result<Project>.Failure(new DomainError("PROJECT_DIRECTORY_NOT_FOUND", arguments: [canonical]));

            var root = isRepository ? await _gitService.GetRepositoryRootAsync(canonical, ct) : canonical;
            if (string.IsNullOrWhiteSpace(root))
                return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
            var rootKey = _pathNormalizer.Canonicalize(root);
            // 必须按实际仓库根去重，不能通过选择子目录绕过重复检查。
            var projects = await _repository.GetAllAsync(ct);
            if (projects.Any(other => other.Id != id && _pathNormalizer.Equals(other.RepositoryPath, rootKey)))
                return Result<Project>.Failure(new DomainError("PROJECT_ALREADY_EXISTS", arguments: [root]));

            var gitDir = isRepository ? await _gitService.GetGitDirAsync(root, ct) : string.Empty;
            var origin = isRepository ? await _gitService.GetOriginUrlAsync(root, ct) : string.Empty;
            var branch = isRepository ? await _gitService.GetCurrentBranchAsync(root, ct) : string.Empty;
            if (gitDir is null || origin is null || branch is null || (isRepository && string.IsNullOrWhiteSpace(gitDir)))
                return Result<Project>.Failure(new DomainError("PROJECT_METADATA_READ_FAILED"));
            if (HasHttpUserInfo(origin))
                return Result<Project>.Failure(new DomainError("PROJECT_REMOTE_CREDENTIAL_FORBIDDEN"));

            var updated = CopyProject(project);
            updated.RepositoryPath = root;
            updated.CanonicalPath = rootKey;
            updated.GitDir = gitDir;
            updated.OriginUrl = origin;
            (updated.RemoteProtocol, updated.RemoteHost) = _gitService.ParseRemote(origin);
            updated.CurrentBranch = branch;
            updated.LastTestResult = string.Empty;
            updated.LastTestAt = null;
            updated.UpdatedAt = DateTimeOffset.UtcNow;
            return await SaveRegistrationAsync(updated, isNew: false, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Result<Project>.Failure(new DomainError("PROJECT_PATH_UPDATE_FAILED"));
        }
    }

    private async Task<Result<Project>> SaveRegistrationAsync(Project project, bool isNew, CancellationToken ct)
    {
        // 新增与路径迁移共用最终判重和写入门禁，元数据读取及普通拉取不占用此锁
        await _registrationGate.WaitAsync(ct);
        try
        {
            var registered = await _repository.GetAllAsync(ct);
            if (registered.Any(other => other.Id != project.Id
                && _pathNormalizer.Equals(other.RepositoryPath, project.CanonicalPath)))
                return Result<Project>.Failure(new DomainError("PROJECT_ALREADY_EXISTS", arguments: [project.RepositoryPath]));

            if (isNew) await _repository.AddAsync(project, ct);
            else await _repository.UpdateAsync(project, ct);
            return Result<Project>.Success(project);
        }
        finally { _registrationGate.Release(); }
    }

    private static Project CopyProject(Project project) => new()
    {
        Id = project.Id, Name = project.Name,
        RepositoryPath = project.RepositoryPath, CanonicalPath = project.CanonicalPath,
        GitDir = project.GitDir, OriginUrl = project.OriginUrl,
        RemoteProtocol = project.RemoteProtocol, RemoteHost = project.RemoteHost,
        CurrentBranch = project.CurrentBranch,
        LastTestResult = project.LastTestResult, LastTestAt = project.LastTestAt,
        CreatedAt = project.CreatedAt, UpdatedAt = project.UpdatedAt,
    };

    private static bool HasHttpUserInfo(string originUrl)
    {
        return Uri.TryCreate(originUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrEmpty(uri.UserInfo);
    }

}
