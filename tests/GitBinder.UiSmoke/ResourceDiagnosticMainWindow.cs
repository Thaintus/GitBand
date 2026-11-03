using Avalonia.Automation.Peers;
using GitBinder.Desktop.Views;

namespace GitBinder.UiSmoke;

// 仅诊断外部 UIA 查询对本隔离宿主的影响。生产 MainWindow 和正常回归仍保留辅助功能。
internal sealed class ResourceDiagnosticMainWindow : MainWindow
{
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}
