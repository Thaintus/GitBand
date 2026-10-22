using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Domain.Accounts;

namespace GitBinder.Desktop.ViewModels;

/// <summary>
/// 账号编辑窗口 ViewModel（三块设计）：
/// 1. Git 信息：提交姓名、提交邮箱、别名（默认提交姓名）
/// 2. HTTPS 账密：账号、密码（可选；编辑时凭据留空保留已有凭据）
/// 3. SSH 私钥：上传私钥
/// 认证方式由填写内容自动推断。
/// </summary>
public partial class AccountEditViewModel : ViewModelBase
{
    private readonly AccountService _accountService;
    private readonly PlatformService _platformService;
    private readonly ILocalizationService _localization;
    private readonly FilePickerDelegate _filePicker;
    private readonly AccountIdentitySyncService? _identitySyncService;
    private readonly ProjectsViewModel? _projects;
    private readonly AccountsViewModel? _accountList;

    private Guid? _editingId;
    private bool _hasStoredHttpsCredential;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    // ===== 块 1：Git 信息 =====
    [ObservableProperty]
    private string _gitName = string.Empty;

    [ObservableProperty]
    private string _gitEmail = string.Empty;

    [ObservableProperty]
    private string _alias = string.Empty;

    [ObservableProperty]
    private PlatformOption? _selectedPlatform;

    // ===== 块 2：HTTPS 账密 =====
    [ObservableProperty]
    private string _httpsUsername = string.Empty;

    [ObservableProperty]
    private string _httpsCredential = string.Empty;

    // ===== 块 3：SSH 私钥 =====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKey))]
    private string _sshPrivateKeyPath = string.Empty;

    [ObservableProperty]
    private string _sshKeyFileName = string.Empty;

    /// <summary>是否已上传私钥（控制"移除"按钮显示）。</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(SshPrivateKeyPath);

    private string _error = string.Empty;
    public string Error
    {
        get => _error;
        set => SetNotice(ref _error, value);
    }

    public ObservableCollection<PlatformOption> Platforms { get; } = [];

    /// <summary>保存成功后返回 true。</summary>
    public bool Saved { get; private set; }

    /// <summary>本次编辑中已有账号数据持久化，关闭后列表仍应刷新。</summary>
    public bool HasPersistedChanges { get; private set; }
    internal bool HasRefreshedAccountList { get; private set; }

    /// <summary>请求关闭窗口（保存成功时触发）。</summary>
    public event Action? CloseRequested;

    public AccountEditViewModel(
        AccountService accountService,
        PlatformService platformService,
        ILocalizationService localization,
        FilePickerDelegate filePicker,
        AccountIdentitySyncService? identitySyncService = null,
        ProjectsViewModel? projects = null,
        AccountsViewModel? accountList = null)
    {
        _accountService = accountService;
        _platformService = platformService;
        _localization = localization;
        _filePicker = filePicker;
        _identitySyncService = identitySyncService;
        _projects = projects;
        _accountList = accountList;
    }

    /// <summary>加载平台目录（每次打开窗口时调用，仅启用的平台）。</summary>
    public async Task LoadPlatformsAsync()
    {
        var all = await _platformService.GetAllAsync();
        Platforms.Clear();
        foreach (var p in all.Where(p => p.Enabled))
        {
            Platforms.Add(new PlatformOption(p.Id, p.Name, p.Host));
        }
    }

    /// <summary>初始化新建。</summary>
    public void InitializeForCreate()
    {
        _editingId = null;
        HasPersistedChanges = false;
        HasRefreshedAccountList = false;
        _hasStoredHttpsCredential = false;
        WindowTitle = _localization.GetString("Accounts.Edit.New");
        GitName = string.Empty;
        GitEmail = string.Empty;
        Alias = string.Empty;
        HttpsUsername = string.Empty;
        HttpsCredential = string.Empty;
        SshPrivateKeyPath = string.Empty;
        SshKeyFileName = string.Empty;
        SelectedPlatform = Platforms.Count > 0 ? Platforms[0] : null;
        Error = string.Empty;
        Saved = false;
    }

    /// <summary>初始化编辑。</summary>
    public void InitializeForEdit(Account account)
    {
        _editingId = account.Id;
        HasPersistedChanges = false;
        HasRefreshedAccountList = false;
        // 只保留凭据引用状态，不解密或回填密码；兼容旧版误降级的认证类型。
        _hasStoredHttpsCredential = !string.IsNullOrWhiteSpace(account.HttpsSecretId);
        WindowTitle = _localization.GetString("Accounts.Edit.Title");
        GitName = account.GitName;
        GitEmail = account.GitEmail;
        Alias = account.Alias;
        HttpsUsername = account.HttpsUsername;
        HttpsCredential = string.Empty;
        SshPrivateKeyPath = account.SshPrivateKeyPath;
        SshKeyFileName = string.IsNullOrEmpty(account.SshPrivateKeyPath)
            ? string.Empty
            : Path.GetFileName(account.SshPrivateKeyPath);
        SelectedPlatform = FindPlatform(account.PlatformId, account.PlatformName);
        Error = string.Empty;
        Saved = false;
    }

    /// <summary>上传 SSH 私钥：选择文件并导入到应用 keys 目录。</summary>
    [RelayCommand]
    private async Task UploadKeyAsync()
    {
        var picked = await _filePicker();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        var result = await _accountService.ImportPrivateKeyAsync(picked);
        if (result.IsSuccess)
        {
            SshPrivateKeyPath = result.Value!;
            SshKeyFileName = Path.GetFileName(result.Value!);
            Error = string.Empty;
        }
        else
        {
            Error = _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
    }

    /// <summary>经确认后移除当前表单中的已上传私钥。</summary>
    [RelayCommand]
    private async Task ClearKeyAsync()
    {
        var confirmed = await DialogHelper.ConfirmAsync(
            _localization.GetString("Common.Confirm"),
            _localization.GetString("Accounts.ClearKey.Confirm"),
            _localization.GetString("Common.Delete"));
        if (!confirmed)
        {
            return;
        }

        SshPrivateKeyPath = string.Empty;
        SshKeyFileName = string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Saved = false;
        HasRefreshedAccountList = false;
        if (_projects is not null && !_projects.TryBeginExternalOperation())
        {
            Error = _localization.GetString("Accounts.OperationBusy");
            return;
        }

        var shouldClose = false;
        try
        {
            shouldClose = await SaveAndSynchronizeAsync();
            if (HasPersistedChanges)
            {
                var refreshFailed = false;
                if (_accountList is not null)
                {
                    try { await _accountList.LoadAsync(); HasRefreshedAccountList = true; }
                    catch (Exception) { refreshFailed = true; }
                }
                if (_projects is not null)
                {
                    try { await _projects.ReloadAfterExternalOperationAsync(); }
                    catch (Exception) { refreshFailed = true; }
                }
                if (refreshFailed)
                {
                    var refreshError = _localization.GetString("Accounts.RefreshFailed");
                    Error = string.IsNullOrWhiteSpace(Error) ? refreshError : Error + " " + refreshError;
                    shouldClose = false;
                }
            }
        }
        finally { _projects?.EndExternalOperation(); }

        if (!shouldClose) return;
        Saved = true;
        CloseRequested?.Invoke();
    }

    private async Task<bool> SaveAndSynchronizeAsync()
    {
        var synchronizeIdentity = _editingId.HasValue;
        try
        {
            if (!await SaveAccountAsync())
            {
                return false;
            }
        }
        catch (Exception)
        {
            // 存储/加密失败时保留表单供重试，不让异步命令异常终止应用。
            // 不展示异常原文，避免暴露凭据、私钥路径等敏感信息。
            Error = _localization.GetString("Accounts.Edit.SaveFailed");
            return false;
        }

        // 到此账号已持久化。同步失败必须保留这个事实，不能提示账号保存失败。
        if (synchronizeIdentity && _identitySyncService is not null && _editingId is Guid accountId)
        {
            try
            {
                var result = await _identitySyncService.SyncAsync(accountId);
                if (!result.IsSuccess)
                {
                    Error = _localization.GetString("Accounts.Edit.IdentitySyncFailed");
                    return false;
                }
            }
            catch (Exception)
            {
                // 同步期间的非预期异常也不能逃逸或暴露配置/凭据细节。
                Error = _localization.GetString("Accounts.Edit.IdentitySyncFailed");
                return false;
            }
        }

        return true;
    }

    private async Task<bool> SaveAccountAsync()
    {
        Error = string.Empty;

        // 别名默认提交姓名。
        if (string.IsNullOrWhiteSpace(Alias))
        {
            Alias = GitName.Trim();
        }

        // 校验：提交姓名必填。
        if (string.IsNullOrWhiteSpace(GitName))
        {
            Error = _localization.GetString("ACCOUNT_NAME_REQUIRED");
            return false;
        }

        // 编辑时空凭据表示不变更，不能因此丢失已有 HTTPS 能力。
        var hasHttps = !string.IsNullOrWhiteSpace(HttpsUsername)
            && (!string.IsNullOrWhiteSpace(HttpsCredential) || _hasStoredHttpsCredential);
        var hasSsh = !string.IsNullOrWhiteSpace(SshPrivateKeyPath);
        var authType = (hasHttps, hasSsh) switch
        {
            (true, true) => AuthenticationType.Both,
            (true, false) => AuthenticationType.Https,
            (false, true) => AuthenticationType.Ssh,
            _ => AuthenticationType.None,
        };

        // 平台 → Host。
        var platformId = SelectedPlatform?.Id;
        var platformName = SelectedPlatform?.Name ?? string.Empty;
        var host = SelectedPlatform?.Host ?? string.Empty;

        // HTTPS 账号作为平台用户名。
        var username = string.IsNullOrWhiteSpace(HttpsUsername) ? GitName.Trim() : HttpsUsername.Trim();

        if (_editingId is Guid id)
        {
            var update = new UpdateAccountInput
            {
                Id = id,
                Username = username,
                Alias = Alias.Trim(),
                PlatformId = platformId,
                PlatformName = platformName,
                Host = host,
                GitName = GitName.Trim(),
                GitEmail = GitEmail.Trim(),
                AuthenticationType = authType,
                SshPrivateKeyPath = SshPrivateKeyPath.Trim(),
                HttpsUsername = HttpsUsername.Trim(),
                HttpsCredential = HttpsCredential,
            };
            var result = await _accountService.UpdateAsync(update);
            if (!result.IsSuccess)
            {
                Error = _localization.GetString(result.Error!.Code, result.Error.Arguments);
                return false;
            }
            _hasStoredHttpsCredential = !string.IsNullOrWhiteSpace(result.Value!.HttpsSecretId);
        }
        else
        {
            var create = new CreateAccountInput
            {
                Username = username,
                Alias = Alias.Trim(),
                PlatformId = platformId,
                PlatformName = platformName,
                Host = host,
                GitName = GitName.Trim(),
                GitEmail = GitEmail.Trim(),
                AuthenticationType = authType,
                SshPrivateKeyPath = SshPrivateKeyPath.Trim(),
                HttpsUsername = HttpsUsername.Trim(),
                HttpsCredential = HttpsCredential,
            };
            var result = await _accountService.CreateAsync(create);
            if (!result.IsSuccess)
            {
                Error = _localization.GetString(result.Error!.Code, result.Error.Arguments);
                return false;
            }
            // 保存后的重试按编辑处理，避免同一表单重复创建账号。
            _editingId = result.Value!.Id;
            _hasStoredHttpsCredential = !string.IsNullOrWhiteSpace(result.Value.HttpsSecretId);
        }

        HasPersistedChanges = true;
        return true;
    }

    private PlatformOption? FindPlatform(Guid? platformId, string platformName)
    {
        if (platformId is Guid id)
        {
            var byId = Platforms.FirstOrDefault(p => p.Id == id);
            if (byId is not null)
            {
                return byId;
            }
        }

        return Platforms.FirstOrDefault(p => p.Name == platformName);
    }
}

/// <summary>平台选项（来自平台目录）。</summary>
public sealed class PlatformOption
{
    public Guid Id { get; }

    public string Name { get; }

    public string Host { get; }

    public PlatformOption(Guid id, string name, string host)
    {
        Id = id;
        Name = name;
        Host = host;
    }

    public override string ToString() => Name;
}
