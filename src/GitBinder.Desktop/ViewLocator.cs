using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GitBinder.Desktop.ViewModels;

namespace GitBinder.Desktop;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    private readonly ConditionalWeakTable<ProjectsViewModel, Views.ProjectsView> _projectViews = new();

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // 页面 VM 为单例；复用项目视图，避免往返导航反复创建卡片、绑定和下拉框。
        if (param is ProjectsViewModel projects)
            return _projectViews.GetValue(projects, _ => new Views.ProjectsView());
        
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        
        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
