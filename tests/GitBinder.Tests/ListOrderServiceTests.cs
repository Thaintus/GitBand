using System.Text.Json;
using GitBinder.Application.Settings;
using GitBinder.Tests.Fakes;

namespace GitBinder.Tests;

public sealed class ListOrderServiceTests
{
    [Theory]
    [InlineData(ListOrderScope.Accounts)]
    [InlineData(ListOrderScope.Platforms)]
    [InlineData(ListOrderScope.Projects)]
    [InlineData(ListOrderScope.Groups)]
    public async Task Move_RestoresOrderUsingNewServiceInstance(ListOrderScope scope)
    {
        var settings = new FakeSettingsRepository();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var order = new ListOrderService(settings);
        var moved = await order.MoveAsync(scope, ids, ids, ids[3], ids[0], after: false);
        var expected = new[] { ids[3], ids[0], ids[1], ids[2] };
        Assert.Equal(expected, moved);
        Assert.Equal(expected, await new ListOrderService(settings).ApplyAsync(scope, ids, id => id));
    }

    [Fact]
    public async Task FilteredMove_PreservesHiddenSlotsAndOtherScopes()
    {
        var service = new ListOrderService(new FakeSettingsRepository());
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var result = await service.MoveAsync(ListOrderScope.Projects, ids,
            new[] { ids[0], ids[2], ids[4] }, ids[0], ids[4], after: true);
        Assert.Equal(new[] { ids[2], ids[1], ids[4], ids[3], ids[0] }, result);
        Assert.Equal(ids, await service.ApplyAsync(ListOrderScope.Accounts, ids, id => id));
    }

    [Fact]
    public async Task Apply_IgnoresDeletedIdsAndAppendsNewItems()
    {
        var settings = new FakeSettingsRepository();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        await settings.SetAsync("ui.order.groups", JsonSerializer.Serialize(new[] { b, Guid.NewGuid(), b, a }));
        Assert.Equal(new[] { b, a, c }, await new ListOrderService(settings)
            .ApplyAsync(ListOrderScope.Groups, new[] { a, c, b }, id => id));
    }

    [Fact]
    public async Task InvalidDrop_DoesNotReplacePreviouslySavedOrder()
    {
        var settings = new FakeSettingsRepository();
        var service = new ListOrderService(settings);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ids = new[] { a, b };
        await service.MoveAsync(ListOrderScope.Platforms, ids, ids, b, a, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveAsync(ListOrderScope.Platforms,
            ids, ids, a, Guid.NewGuid(), false));
        Assert.Equal(new[] { b, a }, await service.ApplyAsync(ListOrderScope.Platforms, ids, id => id));
    }

    [Fact]
    public async Task BrokenOrder_FallsBackToCurrentQueryOrder()
    {
        var settings = new FakeSettingsRepository();
        await settings.SetAsync("ui.order.accounts", "invalid-json");
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        Assert.Equal(ids, await new ListOrderService(settings).ApplyAsync(ListOrderScope.Accounts, ids, id => id));
    }

    [Fact]
    public async Task FailedWrite_LeavesCallerOrderAndStoredOrderIntact()
    {
        var settings = new FailingSettings();
        var service = new ListOrderService(settings);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await Assert.ThrowsAsync<IOException>(() => service.MoveAsync(ListOrderScope.Projects,
            ids, ids, ids[1], ids[0], false));
        Assert.Equal(ids, await service.ApplyAsync(ListOrderScope.Projects, ids, id => id));
    }

    [Fact]
    public async Task SavePreview_UsesOriginalVisibleSlotsAndPersistsFinalPreview()
    {
        var settings = new FakeSettingsRepository();
        var service = new ListOrderService(settings);
        var all = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var visible = new[] { all[0], all[2], all[4] };
        var preview = new[] { all[4], all[0], all[2] };

        var result = await service.SaveVisibleAsync(ListOrderScope.Projects, all, visible, preview);

        var expected = new[] { all[4], all[1], all[0], all[3], all[2] };
        Assert.Equal(expected, result);
        Assert.Equal(expected, await new ListOrderService(settings)
            .ApplyAsync(ListOrderScope.Projects, all, id => id));
        Assert.Equal(all, await service.ApplyAsync(ListOrderScope.Accounts, all, id => id));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("stale_original")]
    public async Task InvalidPreview_DoesNotReplacePreviouslySavedOrder(string invalidCase)
    {
        var settings = new FakeSettingsRepository();
        var service = new ListOrderService(settings);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var all = new[] { a, b };
        await service.SaveVisibleAsync(ListOrderScope.Groups, all, all, new[] { b, a });
        var before = await settings.GetAsync("ui.order.groups");
        var original = invalidCase == "stale_original" ? new[] { b, a } : all;
        var preview = invalidCase switch
        {
            "missing" => new[] { b },
            "duplicate" => new[] { b, b },
            "foreign" => new[] { b, c },
            _ => all,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveVisibleAsync(ListOrderScope.Groups, all, original, preview));

        Assert.Equal(before, await settings.GetAsync("ui.order.groups"));
    }

    private sealed class FailingSettings : ISettingsRepository
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new IOException();
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
