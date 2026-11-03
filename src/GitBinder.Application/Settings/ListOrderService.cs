using System.Text.Json;

namespace GitBinder.Application.Settings;

public enum ListOrderScope { None, Accounts, Platforms, Projects, Groups }

/// <summary>只保存展示顺序；不改实体、绑定关系或 Git 配置。</summary>
public sealed class ListOrderService(ISettingsRepository settings)
{
    public async Task<IReadOnlyList<T>> ApplyAsync<T>(ListOrderScope scope, IReadOnlyList<T> items,
        Func<T, Guid> getId, CancellationToken ct = default)
    {
        var json = await settings.GetAsync(Key(scope), ct);
        if (string.IsNullOrEmpty(json)) return items;
        Guid[] order;
        try { order = JsonSerializer.Deserialize<Guid[]>(json) ?? []; }
        catch (JsonException) { return items; }
        var ranks = order.Distinct().Select((id, index) => (id, index))
            .ToDictionary(entry => entry.id, entry => entry.index);
        // 已删除的 ID 自动忽略，新条目按原查询顺序追加到已排序条目之后。
        return items.OrderBy(item => ranks.GetValueOrDefault(getId(item), int.MaxValue)).ToArray();
    }

    public async Task<IReadOnlyList<Guid>> MoveAsync(ListOrderScope scope, IReadOnlyList<Guid> all,
        IReadOnlyList<Guid> visible, Guid source, Guid target, bool after, CancellationToken ct = default)
    {
        _ = Key(scope);
        var fullSet = all.ToHashSet();
        var visibleSet = visible.ToHashSet();
        if (fullSet.Count != all.Count || visibleSet.Count != visible.Count
            || !visibleSet.IsSubsetOf(fullSet) || !visibleSet.Contains(source) || !visibleSet.Contains(target)
            || !all.Where(visibleSet.Contains).SequenceEqual(visible))
            throw new InvalidOperationException("The list changed before reordering.");
        if (source == target) return all;
        // 只置换可见条目占用的槽位，筛选之外的项目保持原位。
        var moved = visible.Where(id => id != source).ToList();
        moved.Insert(moved.IndexOf(target) + (after ? 1 : 0), source);
        return await SaveVisibleAsync(scope, all, visible, moved, ct);
    }

    /// <summary>预览后的顺序按拖动前的可见槽位提交，隐藏条目不挪位。</summary>
    public async Task<IReadOnlyList<Guid>> SaveVisibleAsync(ListOrderScope scope, IReadOnlyList<Guid> all,
        IReadOnlyList<Guid> originalVisible, IReadOnlyList<Guid> preview, CancellationToken ct = default)
    {
        var key = Key(scope);
        var visibleSet = originalVisible.ToHashSet();
        if (all.Distinct().Count() != all.Count || visibleSet.Count != originalVisible.Count
            || preview.Count != originalVisible.Count || preview.Distinct().Count() != preview.Count
            || !visibleSet.SetEquals(preview) || !all.Where(visibleSet.Contains).SequenceEqual(originalVisible))
            throw new InvalidOperationException("The list changed before saving the preview.");
        var next = 0;
        var result = all.Select(id => visibleSet.Contains(id) ? preview[next++] : id).ToArray();
        if (!all.SequenceEqual(result))
            await settings.SetAsync(key, JsonSerializer.Serialize(result), ct);
        return result;
    }

    private static string Key(ListOrderScope scope) => scope switch
    {
        ListOrderScope.Accounts => "ui.order.accounts",
        ListOrderScope.Platforms => "ui.order.platforms",
        ListOrderScope.Projects => "ui.order.projects",
        ListOrderScope.Groups => "ui.order.groups",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}
