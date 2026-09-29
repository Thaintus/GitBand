using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.Security;
using GitBinder.Application.Settings;
using GitBinder.Desktop;

namespace GitBinder.Desktop.ViewModels;

/// <summary>
/// 设置页 ViewModel。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private const string LanguageKey = "settings.language";

    private readonly IApplicationDataPath _dataPath;
    private readonly IGitLocator _gitLocator;
    private readonly ISshLocator _sshLocator;
    private readonly ILocalizationService _localization;
    private readonly ISettingsRepository _settings;

    [ObservableProperty]
    private string _realGitPath = string.Empty;

    [ObservableProperty]
    private string _gitVersion = string.Empty;

    [ObservableProperty]
    private string _sshExecutable = string.Empty;

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    private string _dataDirectory = string.Empty;

    [ObservableProperty]
    private CultureOption _selectedLanguage;

    /// <summary>关闭主窗口时是否隐藏到系统托盘；使用与主窗口一致的平台默认值。</summary>
    [ObservableProperty]
    private bool _closeToTrayOnClose = DesktopSettingKeys.DefaultCloseToTrayOnClose;

    public ObservableCollection<CultureOption> Languages { get; } = [];

    public SettingsViewModel(
        IApplicationDataPath dataPath,
        IGitLocator gitLocator,
        ISshLocator sshLocator,
        ILocalizationService localization,
        ISettingsRepository settings)
    {
        _dataPath = dataPath;
        _gitLocator = gitLocator;
        _sshLocator = sshLocator;
        _localization = localization;
        _settings = settings;

        Languages.Add(new CultureOption("zh-CN", "简体中文"));
        Languages.Add(new CultureOption("en-US", "English"));

        _selectedLanguage = Languages[0];
    }

    public async Task LoadAsync()
    {
        DatabasePath = _dataPath.DatabasePath;
        DataDirectory = _dataPath.DataDirectory;

        RealGitPath = await _gitLocator.LocateAsync() ?? string.Empty;
        SshExecutable = await _sshLocator.LocateAsync() ?? string.Empty;

        // 同步当前语言选中项。
        var current = _localization.CurrentCulture.Name;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Name == current) ?? Languages[0];

        var closeToTray = await _settings.GetAsync(DesktopSettingKeys.CloseToTrayOnClose);
        CloseToTrayOnClose = bool.TryParse(closeToTray, out var enabled)
            ? enabled : DesktopSettingKeys.DefaultCloseToTrayOnClose;
    }

    partial void OnSelectedLanguageChanged(CultureOption value)
    {
        if (value is null)
        {
            return;
        }

        var culture = new CultureInfo(value.Name);
        _ = _localization.SetCultureAsync(culture);

        // 持久化语言选择。
        _ = _settings.SetAsync(LanguageKey, value.Name);
    }

    partial void OnCloseToTrayOnCloseChanged(bool value)
        => _ = _settings.SetAsync(DesktopSettingKeys.CloseToTrayOnClose, value.ToString());

    [RelayCommand]
    private async Task DetectAsync()
    {
        await LoadAsync();
    }
}

/// <summary>语言选项。</summary>
public sealed class CultureOption
{
    public string Name { get; }

    public string Label { get; }

    public CultureOption(string name, string label)
    {
        Name = name;
        Label = label;
    }

    public override string ToString() => Label;
}
