namespace GitBinder.Desktop;

/// <summary>桌面端设置键。</summary>
internal static class DesktopSettingKeys
{
    /// <summary>关闭主窗口时是否继续驻留系统托盘。</summary>
    public const string CloseToTrayOnClose = "settings.close_to_tray_on_close";

    // Linux 桌面不一定提供托盘宿主。默认退出，避免窗口隐藏后无法从托盘找回；用户仍可手动开启。
    public static bool DefaultCloseToTrayOnClose => !OperatingSystem.IsLinux();
}
