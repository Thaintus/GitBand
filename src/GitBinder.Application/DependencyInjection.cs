using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.GlobalMode;
using GitBinder.Application.Projects;
using GitBinder.Application.Security;
using GitBinder.Application.Settings;
using GitBinder.Domain.Accounts;
using GitBinder.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GitBinder.Application;

/// <summary>
/// Application 层依赖注入注册。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // 应用服务。
        services.AddSingleton<ListOrderService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<AccountIdentitySyncService>();
        services.AddSingleton<PlatformService>();
        services.AddSingleton<ProjectService>();
        services.AddSingleton<ProjectGroupService>();
        services.AddSingleton<ProjectTransferService>();
        services.AddSingleton<BindingService>();
        services.AddSingleton<IGlobalModeApplier>(sp => sp.GetRequiredService<BindingService>());
        services.AddSingleton<GlobalModeService>();
        services.AddSingleton<SecretService>();

        // 账号删除守卫。
        services.AddSingleton<IAccountDeletionGuard, BindingDeletionGuard>();

        // EffectiveAccountResolver 依赖适配（同步读取 SQLite）。
        services.AddSingleton<EffectiveAccountResolver>();
        services.AddSingleton<IAccountReader, AccountReaderAdapter>();
        services.AddSingleton<IBindingReader, BindingReaderAdapter>();
        services.AddSingleton<IGlobalModeState, GlobalModeStateAdapter>();

        return services;
    }
}

/// <summary>
/// 账号删除守卫，通过 BindingRepository 统计绑定数量。
/// </summary>
public sealed class BindingDeletionGuard : IAccountDeletionGuard
{
    private readonly IBindingRepository _bindingRepository;

    public BindingDeletionGuard(IBindingRepository bindingRepository)
    {
        _bindingRepository = bindingRepository;
    }

    public Task<int> CountBindingsAsync(Guid accountId, CancellationToken ct = default)
        => _bindingRepository.CountByAccountIdAsync(accountId, ct);
}

/// <summary>IAccountReader 适配。</summary>
internal sealed class AccountReaderAdapter : IAccountReader
{
    private readonly IAccountRepository _repository;

    public AccountReaderAdapter(IAccountRepository repository) => _repository = repository;

    public Account? GetById(Guid id) => _repository.GetByIdAsync(id).GetAwaiter().GetResult();

    public Account? GetDefault() => _repository.GetDefaultAsync().GetAwaiter().GetResult();
}

/// <summary>IBindingReader 适配。</summary>
internal sealed class BindingReaderAdapter : IBindingReader
{
    private readonly IBindingRepository _repository;

    public BindingReaderAdapter(IBindingRepository repository) => _repository = repository;

    public Guid? GetAccountIdByProject(Guid projectId)
        => _repository.GetByProjectIdAsync(projectId).GetAwaiter().GetResult()?.AccountId;
}

/// <summary>IGlobalModeState 适配，同步读取 settings。</summary>
internal sealed class GlobalModeStateAdapter : IGlobalModeState
{
    private readonly ISettingsRepository _settings;

    public GlobalModeStateAdapter(ISettingsRepository settings) => _settings = settings;

    public bool IsEnabled
    {
        get
        {
            var value = _settings.GetAsync("global_mode.enabled").GetAwaiter().GetResult();
            return bool.TryParse(value, out var en) && en;
        }
    }

    public Guid? GlobalAccountId
    {
        get
        {
            var value = _settings.GetAsync("global_mode.account_id").GetAwaiter().GetResult();
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }
}
