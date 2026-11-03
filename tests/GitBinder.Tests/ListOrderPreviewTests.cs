using System.Collections.ObjectModel;
using GitBinder.Desktop.ViewModels;

namespace GitBinder.Tests;

/// <summary>只操作内存展示集合，不创建 Avalonia 控件或访问用户数据。</summary>
public sealed class ListOrderPreviewTests
{
    [Fact]
    public async Task MultipleMoves_ShowLatestPositionAndSaveOnlyWhenCommitted()
    {
        var a = new Row("A");
        var b = new Row("B");
        var c = new Row("C");
        var d = new Row("D");
        ObservableCollection<Row> items = [a, b, c, d];
        var calls = 0;
        Row[]? savedOriginal = null;
        Row[]? savedPreview = null;
        var preview = new ListOrderPreview<Row>(items, a, () => true, (original, order) =>
        {
            calls++;
            savedOriginal = original.ToArray();
            savedPreview = order.ToArray();
            return Task.FromResult(true);
        });

        Assert.True(preview.Move(c, after: true));
        Assert.Equal(new[] { b, c, a, d }, items);
        Assert.True(preview.Move(d, after: true));
        Assert.Equal(new[] { b, c, d, a }, items);
        Assert.True(preview.Move(c, after: false));
        Assert.Equal(new[] { b, a, c, d }, items);
        Assert.Equal(0, calls);

        await preview.CommitAsync();

        Assert.Equal(1, calls);
        Assert.Equal(new[] { a, b, c, d }, savedOriginal);
        Assert.Equal(new[] { b, a, c, d }, savedPreview);
        Assert.Equal(savedPreview, items);
        preview.Cancel();
        Assert.Equal(new[] { b, a, c, d }, items);
        await preview.CommitAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancel_RestoresOriginalPositionsWithoutSaving()
    {
        var a = new Row("A");
        var b = new Row("B");
        var c = new Row("C");
        ObservableCollection<Row> items = [a, b, c];
        var calls = 0;
        var preview = new ListOrderPreview<Row>(items, c, () => true, (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });

        Assert.True(preview.Move(a, after: false));
        Assert.Equal(new[] { c, a, b }, items);
        preview.Cancel();
        await preview.CommitAsync();

        Assert.Equal(new[] { a, b, c }, items);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailure_RestoresOriginalPositions(bool throws)
    {
        var a = new Row("A");
        var b = new Row("B");
        ObservableCollection<Row> items = [a, b];
        var preview = new ListOrderPreview<Row>(items, a, () => true, (_, _) => throws
            ? Task.FromException<bool>(new IOException("Isolated save failure"))
            : Task.FromResult(false));
        Assert.True(preview.Move(b, after: true));

        if (throws) await Assert.ThrowsAsync<IOException>(() => preview.CommitAsync());
        else await preview.CommitAsync();

        Assert.Equal(new[] { a, b }, items);
        Assert.False(preview.IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalFilterOrReload_DoesNotRestoreStaleRows(bool reload)
    {
        var a = new Row("A");
        var b = new Row("B");
        var c = new Row("C");
        ObservableCollection<Row> items = [a, b, c];
        var calls = 0;
        var preview = new ListOrderPreview<Row>(items, a, () => true, (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        Assert.True(preview.Move(c, after: true));

        Row[] current = reload ? [new Row("A"), new Row("B")] : [c];
        items.Clear();
        foreach (var row in current) items.Add(row);
        Assert.False(preview.IsValid);
        preview.Cancel();
        await preview.CommitAsync();

        Assert.Equal(current, items);
        Assert.DoesNotContain(a, items);
        Assert.DoesNotContain(b, items);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConflictBeforeRelease_CancelsPreviewWithoutSaving()
    {
        var a = new Row("A");
        var b = new Row("B");
        ObservableCollection<Row> items = [a, b];
        var canContinue = true;
        var calls = 0;
        var preview = new ListOrderPreview<Row>(items, a, () => canContinue, (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        Assert.True(preview.Move(b, after: true));
        canContinue = false;

        await preview.CommitAsync();

        Assert.Equal(new[] { a, b }, items);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FixedGroupEntrances_AreNotValidDropTargets()
    {
        var all = new Row("All", isFixed: true);
        var ungrouped = new Row("Ungrouped", isFixed: true);
        var a = new Row("A");
        var b = new Row("B");
        ObservableCollection<Row> items = [all, ungrouped, a, b];
        var preview = new ListOrderPreview<Row>(items, a, () => true,
            (_, _) => Task.FromResult(true), row => !row.IsFixed);

        Assert.False(preview.Move(all, after: false));
        Assert.False(preview.Move(ungrouped, after: true));
        Assert.Equal(new[] { all, ungrouped, a, b }, items);
        Assert.True(preview.Move(b, after: true));
        Assert.Equal(new[] { all, ungrouped, b, a }, items);
        preview.Cancel();
        Assert.Equal(new[] { all, ungrouped, a, b }, items);
    }

    [Fact]
    public async Task ReturningToOriginalPosition_DoesNotWriteOrder()
    {
        var a = new Row("A");
        var b = new Row("B");
        var c = new Row("C");
        ObservableCollection<Row> items = [a, b, c];
        var calls = 0;
        var preview = new ListOrderPreview<Row>(items, a, () => true, (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        Assert.True(preview.Move(c, after: true));
        Assert.True(preview.Move(b, after: false));
        Assert.False(preview.HasChanges);

        await preview.CommitAsync();

        Assert.Equal(new[] { a, b, c }, items);
        Assert.Equal(0, calls);
    }

    private sealed class Row(string name, bool isFixed = false)
    {
        public string Name { get; } = name;
        public bool IsFixed { get; } = isFixed;
    }
}
