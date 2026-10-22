using Avalonia.Controls;
using Avalonia.VisualTree;
using GitBinder.Domain.Accounts;
using GitBinder.Desktop.ViewModels;

namespace GitBinder.Desktop.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
    }

    /// <summary>Escape 只取消当前行编辑，不保存，也不打断正在进行的写入或传输</summary>
    public bool TryCancelInlineEdit(Control? focusedControl)
    {
        if (DataContext is not ProjectsViewModel viewModel) return false;
        Control[] focusedAncestors = focusedControl is null ? []
            : focusedControl.GetVisualAncestors().OfType<Control>().Prepend(focusedControl).ToArray();
        var editor = focusedAncestors.FirstOrDefault(control =>
            control.Classes.Contains("project-remote-editor") || control.Classes.Contains("project-path-editor"));
        var focusedItem = focusedAncestors.Select(control => control.DataContext)
            .OfType<ProjectItemViewModel>().FirstOrDefault();
        var item = focusedItem is { IsRemoteEditorOpen: true } or { IsPathEditorOpen: true }
            ? focusedItem
            : viewModel.FilteredItems.LastOrDefault(candidate => candidate.IsRemoteEditorOpen || candidate.IsPathEditorOpen);
        if (item is null) return false;
        if (!item.CanManageProject) return true;

        if (item.IsPathEditorOpen && (editor?.Classes.Contains("project-path-editor") is true || !item.IsRemoteEditorOpen))
            viewModel.CancelEditPathCommand.Execute(item);
        else
            viewModel.CancelEditRemoteCommand.Execute(item);
        return true;
    }

    private async void OnProjectGroupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: ProjectItemViewModel item }
            && !item.IsRestoringSelection
            && DataContext is ProjectsViewModel viewModel
            && e.AddedItems.OfType<ProjectGroupItemViewModel>().FirstOrDefault() is { } group)
            await viewModel.SwitchProjectGroupAsync(item, group);
    }

    private async void OnBindingAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: ProjectItemViewModel item }
            || item.IsRestoringSelection
            || DataContext is not ProjectsViewModel viewModel
            || e.AddedItems.OfType<Account>().FirstOrDefault() is not { } selectedAccount)
        {
            return;
        }

        await viewModel.SwitchBindingAccountAsync(item, selectedAccount);
    }
}
