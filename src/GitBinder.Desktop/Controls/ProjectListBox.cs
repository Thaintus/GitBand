using Avalonia.Controls;

namespace GitBinder.Desktop.Controls;

/// <summary>项目卡片按视口虚拟化，但不跨项目复用带即时保存事件的下拉框。</summary>
public sealed class ProjectListBox : ListBox
{
    // 自定义派生控件必须显式继承 ListBox 的主题，否则只有数据而没有控件模板。
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        var needsContainer = base.NeedsContainerOverride(item, index, out _);
        // 回收容器切换 DataContext 时会同步回填 ComboBox 选择，不能让旧选项触发新项目写入。
        // null 只禁用复用池；VirtualizingStackPanel 仍只创建视口附近的项目容器。
        recycleKey = null;
        return needsContainer;
    }
}
