using System.Text.Json;
using GitBinder.Application.Accounts;
using GitBinder.Application.Settings;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Common;
using GitBinder.Domain.GlobalMode;

namespace GitBinder.Application.GlobalMode;

/// <summary>
/// Global Mode 应用服务：管理全局 Git 身份并保留绑定数据。
/// </summary>
public sealed class GlobalModeService
{
    private const string EnabledKey = "global_mode.enabled";
    private const string AccountIdKey = "global_mode.account_id";
    private const string GlobalGitSnapshotKey = "global_mode.git_config_snapshot";

    private readonly ISettingsRepository _settings;
    private readonly IAccountRepository _accountRepository;
    private readonly IGlobalModeApplier _applier;
    private readonly IGlobalGitConfigApplier _globalGitConfigApplier;

    public GlobalModeService(
        ISettingsRepository settings,
        IAccountRepository accountRepository,
        IGlobalModeApplier? applier = null,
        IGlobalGitConfigApplier? globalGitConfigApplier = null)
    {
        _settings = settings;
        _accountRepository = accountRepository;
        _applier = applier ?? NoopGlobalModeApplier.Instance;
        _globalGitConfigApplier = globalGitConfigApplier ?? NoopGlobalGitConfigApplier.Instance;
    }

    public async Task<GlobalModeConfig> GetConfigAsync(CancellationToken ct = default)
    {
        var enabledStr = await _settings.GetAsync(EnabledKey, ct);
        var accountIdStr = await _settings.GetAsync(AccountIdKey, ct);

        var config = new GlobalModeConfig
        {
            Enabled = bool.TryParse(enabledStr, out var en) && en,
        };

        if (Guid.TryParse(accountIdStr, out var id))
        {
            config.GlobalAccountId = id;
        }

        return config;
    }

    /// <summary>开启 Global Mode（默认使用 Default Account）。</summary>
    public async Task<Result> EnableAsync(Guid? accountId, CancellationToken ct = default)
    {
        var account = accountId is Guid g
            ? await _accountRepository.GetByIdAsync(g, ct)
            : await _accountRepository.GetDefaultAsync(ct);

        if (account is null || !account.Enabled)
        {
            return Result.Failure(new DomainError("GLOBALMODE_ACCOUNT_INVALID"));
        }

        var existingConfig = await GetConfigAsync(ct);
        var snapshot = await GetOrCaptureSnapshotAsync(ct);
        var result = await ApplyAccountAsync(account, ct);
        if (!result.IsSuccess)
        {
            if (!existingConfig.Enabled)
            {
                await RestoreAfterFailedEnableAsync(snapshot, ct);
            }

            return result;
        }

        await _settings.SetAsync(EnabledKey, "true", ct);
        await _settings.SetAsync(AccountIdKey, account.Id.ToString(), ct);
        return Result.Success();
    }

    /// <summary>切换 Global Account。</summary>
    public async Task<Result> SwitchAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await _accountRepository.GetByIdAsync(accountId, ct);
        if (account is null || !account.Enabled)
        {
            return Result.Failure(new DomainError("GLOBALMODE_ACCOUNT_INVALID"));
        }

        await GetOrCaptureSnapshotAsync(ct);
        var beforeSwitch = await _globalGitConfigApplier.CaptureAsync(ct);
        var result = await ApplyAccountAsync(account, ct);
        if (!result.IsSuccess)
        {
            await _globalGitConfigApplier.RestoreAsync(beforeSwitch, ct);
            return result;
        }

        await _settings.SetAsync(AccountIdKey, account.Id.ToString(), ct);
        return Result.Success();
    }

    /// <summary>关闭 Global Mode，恢复用户原有全局配置及各绑定仓库本地身份。</summary>
    public async Task<Result> DisableAsync(CancellationToken ct = default)
    {
        var boundRestore = await _applier.RestoreBoundAccountsAsync(ct);
        if (!boundRestore.IsSuccess)
        {
            return boundRestore;
        }

        var snapshot = await GetSnapshotAsync(ct);
        var globalRestore = await _globalGitConfigApplier.RestoreAsync(
            snapshot ?? new GlobalGitConfigSnapshot(null, null, null),
            ct);
        if (!globalRestore.IsSuccess)
        {
            return globalRestore;
        }

        await _settings.SetAsync(EnabledKey, "false", ct);
        await _settings.DeleteAsync(GlobalGitSnapshotKey, ct);
        return Result.Success();
    }

    /// <summary>
    /// 显式重新应用当前有效账号的兼容入口，桌面启动不调用。
    /// 全局模式开启时应用全局账号；关闭时重新应用已有绑定。
    /// </summary>
    public async Task<Result> EnsureAppliedAsync(CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);
        if (!config.Enabled)
        {
            return await _applier.RestoreBoundAccountsAsync(ct);
        }

        var account = config.GlobalAccountId is Guid id
            ? await _accountRepository.GetByIdAsync(id, ct)
            : await _accountRepository.GetDefaultAsync(ct);
        if (account is null || !account.Enabled)
        {
            return Result.Failure(new DomainError("GLOBALMODE_ACCOUNT_INVALID"));
        }

        await GetOrCaptureSnapshotAsync(ct);
        return await ApplyAccountAsync(account, ct);
    }

    private async Task<Result> ApplyAccountAsync(Account account, CancellationToken ct)
    {
        var globalApply = await _globalGitConfigApplier.ApplyAsync(account, ct);
        if (!globalApply.IsSuccess)
        {
            return globalApply;
        }

        return await _applier.ApplyGlobalAccountAsync(account, ct);
    }

    private async Task<GlobalGitConfigSnapshot> GetOrCaptureSnapshotAsync(CancellationToken ct)
    {
        var stored = await GetSnapshotAsync(ct);
        if (stored is not null)
        {
            // 新增托管配置在首次覆盖前补采集，但不可用当前账号覆盖历史 user.email。
            if (stored.CredentialHelpers is null || stored.EmailConfig is null)
            {
                var current = await _globalGitConfigApplier.CaptureAsync(ct);
                stored = stored with
                {
                    CredentialHelpers = stored.CredentialHelpers ?? current.CredentialHelpers,
                    EmailConfig = stored.EmailConfig ?? new GitEmailConfigSnapshot(
                        stored.UserEmail, current.EmailConfig?.AuthorEmail, current.EmailConfig?.CommitterEmail),
                };
                await _settings.SetAsync(GlobalGitSnapshotKey, JsonSerializer.Serialize(stored), ct);
            }

            return stored!;
        }

        var snapshot = await _globalGitConfigApplier.CaptureAsync(ct);
        // 旧版本已开启全局模式但没有快照时，首次启动新版即捕获用户原先的全局配置。
        await _settings.SetAsync(GlobalGitSnapshotKey, JsonSerializer.Serialize(snapshot), ct);
        return snapshot;
    }

    private async Task<GlobalGitConfigSnapshot?> GetSnapshotAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync(GlobalGitSnapshotKey, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GlobalGitConfigSnapshot>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task RestoreAfterFailedEnableAsync(GlobalGitConfigSnapshot snapshot, CancellationToken ct)
    {
        await _applier.RestoreBoundAccountsAsync(ct);
        await _globalGitConfigApplier.RestoreAsync(snapshot, ct);
        await _settings.DeleteAsync(GlobalGitSnapshotKey, ct);
    }

    private sealed class NoopGlobalModeApplier : IGlobalModeApplier
    {
        public static NoopGlobalModeApplier Instance { get; } = new();

        public Task<Result> ApplyGlobalAccountAsync(Account account, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> RestoreBoundAccountsAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    private sealed class NoopGlobalGitConfigApplier : IGlobalGitConfigApplier
    {
        public static NoopGlobalGitConfigApplier Instance { get; } = new();

        public Task<GlobalGitConfigSnapshot> CaptureAsync(CancellationToken ct = default)
            => Task.FromResult(new GlobalGitConfigSnapshot(null, null, null));

        public Task<Result> ApplyAsync(Account account, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> RestoreAsync(GlobalGitConfigSnapshot snapshot, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }
}
