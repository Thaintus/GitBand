using System.Collections.ObjectModel;
using GitBinder.Application.Settings;

namespace GitBinder.Desktop.ViewModels;

public interface IListOrderHost
{
    bool CanReorder(ListOrderScope scope, object item);
    ListOrderPreview? BeginReorderPreview(ListOrderScope scope, object source);
    void ReportReorderFailure();
}

public abstract class ListOrderPreview
{
    public abstract bool IsValid { get; }
    public abstract bool HasChanges { get; }
    public abstract bool Move(object target, bool after);
    public abstract void Cancel();
    public abstract Task CommitAsync();
}

/// <summary>仅移动展示集合。提交前持有原顺序，取消时不覆盖外部加载/筛选产生的新集合。</summary>
public sealed class ListOrderPreview<T> : ListOrderPreview where T : class
{
    private readonly ObservableCollection<T> _items;
    private readonly T _source;
    private readonly T[] _original;
    private T[] _expected;
    private readonly Func<bool> _canContinue;
    private readonly Func<IReadOnlyList<T>, IReadOnlyList<T>, Task<bool>> _save;
    private readonly Func<T, bool> _canMove;
    private bool _finished;
    private bool _committing;

    public ListOrderPreview(ObservableCollection<T> items, T source, Func<bool> canContinue,
        Func<IReadOnlyList<T>, IReadOnlyList<T>, Task<bool>> save, Func<T, bool>? canMove = null)
    {
        _items = items;
        _source = source;
        _original = _expected = items.ToArray();
        _canContinue = canContinue;
        _save = save;
        _canMove = canMove ?? (_ => true);
    }

    private bool MatchesExpected => _items.SequenceEqual(_expected, ReferenceEqualityComparer.Instance);
    public override bool IsValid => !_finished && !_committing && MatchesExpected && _canContinue();
    public override bool HasChanges => !_original.SequenceEqual(_expected, ReferenceEqualityComparer.Instance);

    public override bool Move(object target, bool after)
    {
        if (!IsValid || target is not T row || ReferenceEquals(row, _source) || !_canMove(row)) return false;
        var next = _expected.Where(item => !ReferenceEquals(item, _source)).ToList();
        var targetIndex = next.FindIndex(item => ReferenceEquals(item, row));
        if (targetIndex < 0) return false;
        next.Insert(targetIndex + (after ? 1 : 0), _source);
        if (_expected.SequenceEqual(next, ReferenceEqualityComparer.Instance)) return false;
        _expected = next.ToArray();
        ListOrdering.Synchronize(_items, _expected);
        return true;
    }

    public override void Cancel()
    {
        if (_finished || _committing) return;
        _finished = true;
        // 若筛选/加载已换掉条目，保留新数据，不能把旧行重新放回。
        if (MatchesExpected && HasChanges) ListOrdering.Synchronize(_items, _original);
    }

    public override async Task CommitAsync()
    {
        if (!IsValid) { Cancel(); return; }
        if (!HasChanges) { _finished = true; return; }
        var saved = false;
        _committing = true;
        try { saved = await _save(_original, _expected); }
        finally
        {
            _committing = false;
            if (saved) _finished = true;
            else Cancel();
        }
    }
}

internal static class ListOrdering
{
    // Move 保留条目与集合实例，避免排序后重建所有卡片及重置滚动位置。
    public static void Synchronize<T>(ObservableCollection<T> items, IReadOnlyList<T> next)
    {
        for (var i = 0; i < next.Count; i++)
        {
            if (i < items.Count && EqualityComparer<T>.Default.Equals(items[i], next[i])) continue;
            var oldIndex = items.IndexOf(next[i]);
            if (oldIndex >= 0) items.Move(oldIndex, i);
            else items.Insert(i, next[i]);
        }
        while (items.Count > next.Count) items.RemoveAt(items.Count - 1);
    }

    public static void Apply<T>(ObservableCollection<T> items, IReadOnlyList<Guid> ids, Func<T, Guid> getId)
    {
        var ranks = ids.Select((id, index) => (id, index)).ToDictionary(entry => entry.id, entry => entry.index);
        Synchronize(items, items.OrderBy(item => ranks.GetValueOrDefault(getId(item), int.MaxValue)).ToArray());
    }
}
