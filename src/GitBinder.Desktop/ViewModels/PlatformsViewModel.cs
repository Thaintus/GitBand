using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitBinder.Application.Accounts;
using GitBinder.Application.Common;
using GitBinder.Application.Settings;
using GitBinder.Domain.Accounts;

namespace GitBinder.Desktop.ViewModels;

/// <summary>
/// 平台管理页 ViewModel：维护平台名称与 Host。
/// </summary>
public partial class PlatformsViewModel : ViewModelBase
{
    private readonly PlatformService _platformService;
    private readonly ILocalizationService _localization;
    private readonly Func<string, string, string, Func<string, string, Task<string?>>, Task<bool>> _editPlatform;

    public ObservableCollection<PlatformItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _hasItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private ObservableCollection<PlatformItemViewModel> _filteredItems = [];

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public bool HasNoMatches => HasItems && FilteredItems.Count == 0;

    private string _feedback = string.Empty;
    public string Feedback
    {
        get => _feedback;
        set => SetNotice(ref _feedback, value);
    }

    /// <summary>阻止新建和编辑命令重复打开模态窗口。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand))]
    private bool _isEditorOpen;

    public PlatformsViewModel(PlatformService platformService, ILocalizationService localization,
        Func<string, string, string, Func<string, string, Task<string?>>, Task<bool>>? editPlatform = null,
        ListOrderService? listOrder = null)
    {
        _platformService = platformService;
        _localization = localization;
        _editPlatform = editPlatform ?? DialogHelper.EditPlatformAsync;
        _listOrder = listOrder;
        // 页面 VM 为单例，只订阅一次；不让每个条目长期订阅语言服务。
        _localization.CultureChanged += OnCultureChanged;
    }

    public async Task LoadAsync()
    {
        await _orderGate.WaitAsync();
        try
        {
            var all = await Task.Run(async () =>
            {
                IReadOnlyList<GitPlatform> baseline = (await _platformService.GetAllAsync())
                    .OrderByDescending(platform => platform.Enabled).ThenBy(platform => platform.SortOrder)
                    .ThenBy(platform => platform.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                return _listOrder is null ? baseline
                    : await _listOrder.ApplyAsync(ListOrderScope.Platforms, baseline, platform => platform.Id);
            });
            var existing = Items.ToDictionary(item => item.Platform.Id);
            var next = new List<PlatformItemViewModel>(all.Count);
            foreach (var p in all)
            {
                if (existing.TryGetValue(p.Id, out var item)) item.ApplySnapshot(p);
                else item = new PlatformItemViewModel(p, _localization);
                next.Add(item);
            }
            ListOrdering.Synchronize(Items, next);

            HasItems = Items.Count > 0;
            ApplyFilter();
        }
        finally { _orderGate.Release(); }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items) item.RefreshLocalization();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void ApplyFilter()
    {
        // 未手动排序时沿用默认顺序；手动排序后优先保留用户的顺序。
        ListOrdering.Synchronize(FilteredItems, Items.Where(item => ListSearch.Matches(
            SearchText, item.Name, item.Host)).ToList());
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private bool CanOpenEditor() => !IsEditorOpen && !_isOrderSaving && !_isPlatformOperation;

    [RelayCommand(CanExecute = nameof(CanOpenEditor))]
    private Task NewAsync() => OpenEditorAsync(null);

    [RelayCommand(CanExecute = nameof(CanOpenEditor))]
    private Task StartEditAsync(PlatformItemViewModel? item)
        => item is null ? Task.CompletedTask : OpenEditorAsync(item);

    private async Task OpenEditorAsync(PlatformItemViewModel? item)
    {
        if (IsEditorOpen || _isOrderSaving || _isPlatformOperation) return;
        IsEditorOpen = true;
        Feedback = string.Empty;
        bool saved;
        try
        {
            saved = await _editPlatform(
                _localization.GetString(item is null ? "Platforms.Editor.New" : "Platforms.Editor.Edit"),
                item?.Name ?? string.Empty, item?.Host ?? string.Empty,
                (name, host) => SavePlatformAsync(item?.Platform.Id, name, host));
        }
        catch (Exception)
        {
            Feedback = _localization.GetString("Platforms.Editor.OpenFailed");
            return;
        }
        finally { IsEditorOpen = false; }

        if (!saved) return;
        try { await LoadAsync(); }
        catch (Exception)
        {
            // 写入已经完成，刷新失败不能被误报为保存失败，也不能重复新建。
            Feedback = _localization.GetString("Platforms.ReloadFailed");
        }
    }

    private async Task<string?> SavePlatformAsync(Guid? id, string name, string host)
    {
        try
        {
            var result = id is Guid editingId
                ? await _platformService.UpdateAsync(editingId, name, host)
                : await _platformService.CreateAsync(name, host);
            return result.IsSuccess ? null : _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
        catch (Exception)
        {
            // 原始异常可能包含路径等信息，只返回可读且可复制的本地化错误。
            return _localization.GetString("Platforms.Editor.SaveFailed");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(PlatformItemViewModel? item)
    {
        if (item is null || _isOrderSaving || _isPlatformOperation || IsEditorOpen)
        {
            return;
        }

        _isPlatformOperation = true;
        try
        {
            var confirmed = await DialogHelper.ConfirmAsync(
                _localization.GetString("Common.Confirm"),
                _localization.GetString("Platforms.Delete.Confirm", item.Name),
                _localization.GetString("Common.Delete"));
            if (!confirmed) return;

            var result = await _platformService.DeleteAsync(item.Platform.Id);
            if (result.IsSuccess)
                await LoadAsync();
            else
                Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
        finally { _isPlatformOperation = false; }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(PlatformItemViewModel? item)
    {
        if (item is null || _isOrderSaving || _isPlatformOperation || IsEditorOpen)
        {
            return;
        }

        _isPlatformOperation = true;
        try
        {
            var result = await _platformService.SetEnabledAsync(item.Platform.Id, !item.Platform.Enabled);
            if (result.IsSuccess)
                await LoadAsync();
            else
                Feedback = _localization.GetString(result.Error!.Code, result.Error.Arguments);
        }
        finally { _isPlatformOperation = false; }
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (_isOrderSaving || _isPlatformOperation || IsEditorOpen) return;
        _isPlatformOperation = true;
        try
        {
            await _platformService.RestoreDefaultsAsync();
            await LoadAsync();
        }
        finally { _isPlatformOperation = false; }
    }
}

/// <summary>平台列表项 ViewModel。</summary>
public sealed class PlatformItemViewModel : ViewModelBase
{
    private readonly GitBinder.Application.Common.ILocalizationService _localization;
    private (string Name, string Host, bool Enabled) _display;
    private string _statusText;
    private string _toggleText;

    public GitPlatform Platform { get; private set; }

    public PlatformItemViewModel(GitPlatform platform, GitBinder.Application.Common.ILocalizationService localization)
    {
        Platform = platform;
        _localization = localization;
        _display = (platform.Name, platform.Host, platform.Enabled);
        _statusText = GetStatusText();
        _toggleText = GetToggleText();
    }

    public string Name => _display.Name;

    public string Host => _display.Host;

    public bool Enabled => _display.Enabled;

    public string StatusText => _statusText;

    public string ToggleText => _toggleText;

    public void ApplySnapshot(GitPlatform platform)
    {
        var old = _display;
        var replaced = !ReferenceEquals(Platform, platform);
        Platform = platform;
        _display = (platform.Name, platform.Host, platform.Enabled);
        if (replaced) OnPropertyChanged(nameof(Platform));
        if (old.Name != _display.Name) OnPropertyChanged(nameof(Name));
        if (old.Host != _display.Host) OnPropertyChanged(nameof(Host));
        if (old.Enabled != _display.Enabled) OnPropertyChanged(nameof(Enabled));
        RefreshLocalization();
    }

    public void RefreshLocalization()
    {
        SetProperty(ref _statusText, GetStatusText(), nameof(StatusText));
        SetProperty(ref _toggleText, GetToggleText(), nameof(ToggleText));
    }

    private string GetStatusText() => Enabled
        ? _localization.GetString("Platforms.Status.Enabled")
        : _localization.GetString("Platforms.Status.Disabled");

    private string GetToggleText() => Enabled
        ? _localization.GetString("Platforms.Disable")
        : _localization.GetString("Platforms.Enable");
}
