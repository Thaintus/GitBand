using GitBinder.Domain.Accounts;
using GitBinder.Domain.Projects;

namespace GitBinder.Domain.Services;

/// <summary>
/// 有效账号解析器。任何 Git 身份相关操作都必须经由它统一决策。
/// 规则：Global Mode > Project Binding > Default Account。
/// </summary>
public sealed class EffectiveAccountResolver
{
    private readonly IGlobalModeState _globalModeState;
    private readonly IBindingReader _bindingReader;
    private readonly IAccountReader _accountReader;

    public EffectiveAccountResolver(
        IGlobalModeState globalModeState,
        IBindingReader bindingReader,
        IAccountReader accountReader)
    {
        _globalModeState = globalModeState;
        _bindingReader = bindingReader;
        _accountReader = accountReader;
    }

    /// <summary>解析指定仓库路径的有效账号。</summary>
    public Account? Resolve(Project? project)
    {
        // 1. Global Mode 优先级最高。
        if (_globalModeState.IsEnabled)
        {
            var globalId = _globalModeState.GlobalAccountId;
            if (globalId is Guid g)
            {
                var account = _accountReader.GetById(g);
                if (account is not null && account.Enabled)
                {
                    return account;
                }
            }

            // 回退到默认账号。
            return _accountReader.GetDefault();
        }

        // 2. Project Binding。
        if (project is not null)
        {
            var bindingAccountId = _bindingReader.GetAccountIdByProject(project.Id);
            if (bindingAccountId is Guid id)
            {
                var account = _accountReader.GetById(id);
                if (account is not null && account.Enabled)
                {
                    return account;
                }
            }
        }

        // 3. Default Account。
        return _accountReader.GetDefault();
    }

    /// <summary>克隆前尚无项目绑定：全局模式优先，否则使用用户明确选择的账号。</summary>
    public Account? ResolveForClone(Account selectedAccount)
        => _globalModeState.IsEnabled ? Resolve(null) : selectedAccount;

    /// <summary>解析原因描述码，供 GUI 展示 Status。</summary>
    public EffectiveReason ResolveReason(Project? project)
    {
        if (_globalModeState.IsEnabled)
        {
            return EffectiveReason.GlobalMode;
        }

        if (project is not null && _bindingReader.GetAccountIdByProject(project.Id) is not null)
        {
            return EffectiveReason.Binding;
        }

        return EffectiveReason.Default;
    }
    /// <summary>页面展示复用本轮账号和全局状态，实际认证仍使用实时解析入口。</summary>
    public DisplaySnapshot CaptureDisplaySnapshot(IReadOnlyList<Account> accounts)
    {
        var enabled = _globalModeState.IsEnabled;
        return new DisplaySnapshot(enabled, enabled ? _globalModeState.GlobalAccountId : null, accounts);
    }

    public sealed class DisplaySnapshot
    {
        private readonly IGlobalModeState _state;
        private readonly IAccountReader _accounts;
        internal DisplaySnapshot(bool enabled, Guid? globalId, IReadOnlyList<Account> accounts)
        {
            _state = new DisplayState(enabled, globalId);
            _accounts = new DisplayAccounts(accounts);
        }
        public (Account? Account, EffectiveReason Reason) Resolve(Project project, Guid? boundAccountId)
        {
            // 使用该项目本次读取的绑定，不能复用整轮开始前的旧绑定。
            var resolver = new EffectiveAccountResolver(_state, new DisplayBinding(project.Id, boundAccountId), _accounts);
            return (resolver.Resolve(project), resolver.ResolveReason(project));
        }
    }

    private sealed record DisplayState(bool IsEnabled, Guid? GlobalAccountId) : IGlobalModeState;
    private sealed record DisplayBinding(Guid ProjectId, Guid? AccountId) : IBindingReader
    {
        public Guid? GetAccountIdByProject(Guid projectId) => projectId == ProjectId ? AccountId : null;
    }
    private sealed class DisplayAccounts(IReadOnlyList<Account> accounts) : IAccountReader
    {
        private readonly Dictionary<Guid, Account> _byId = accounts.ToDictionary(account => account.Id);
        private readonly Account? _default = accounts.FirstOrDefault(account => account.IsDefault && account.Enabled);
        public Account? GetById(Guid id) => _byId.GetValueOrDefault(id);
        public Account? GetDefault() => _default;
    }
}

/// <summary>有效账号来源。</summary>
public enum EffectiveReason
{
    GlobalMode,
    Binding,
    Default,
}

/// <summary>Global Mode 状态读取。</summary>
public interface IGlobalModeState
{
    bool IsEnabled { get; }
    Guid? GlobalAccountId { get; }
}

/// <summary>绑定关系读取。</summary>
public interface IBindingReader
{
    Guid? GetAccountIdByProject(Guid projectId);
}

/// <summary>账号读取。</summary>
public interface IAccountReader
{
    Account? GetById(Guid id);
    Account? GetDefault();
}
