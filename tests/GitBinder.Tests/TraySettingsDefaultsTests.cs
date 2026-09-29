using GitBinder.Application.Common;
using GitBinder.Application.Git;
using GitBinder.Application.Security;
using GitBinder.Desktop.Localization;
using GitBinder.Desktop.ViewModels;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public sealed class TraySettingsDefaultsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task UnsetOrInvalidPreference_UsesPlatformDefault(string? stored)
    {
        var settings = new FakeSettingsRepository();
        if (stored is not null) await settings.SetAsync("settings.close_to_tray_on_close", stored);
        var vm = Create(settings);
        Assert.Equal(!OperatingSystem.IsLinux(), vm.CloseToTrayOnClose);
        await vm.LoadAsync();
        Assert.Equal(!OperatingSystem.IsLinux(), vm.CloseToTrayOnClose);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitPreference_IsPreservedOnEveryPlatform(bool value)
    {
        var settings = new FakeSettingsRepository();
        await settings.SetAsync("settings.close_to_tray_on_close", value.ToString());
        var vm = Create(settings);
        await vm.LoadAsync();
        Assert.Equal(value, vm.CloseToTrayOnClose);
    }

    private static SettingsViewModel Create(FakeSettingsRepository settings)
        => new(new FixturePaths(), new EmptyLocator(), new EmptyLocator(), new LocalizationService(), settings);

    private sealed class EmptyLocator : IGitLocator, ISshLocator
    {
        public Task<string?> LocateAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FixturePaths : IApplicationDataPath
    {
        public string DataDirectory => "fixture-data";
        public string DatabasePath => "fixture-db";
        public string LogDirectory => "fixture-logs";
        public string KeysDirectory => "fixture-keys";
    }
}
