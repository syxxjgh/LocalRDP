using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskClone.Core;

/// <summary>
/// 应用设置。整个应用只有桌面分身一个功能，全部持久化项都在这里。
/// </summary>
public partial class AppSettings : ObservableObject
{
    /// <summary>桌面分身的桌面宽度（像素）。</summary>
    [ObservableProperty]
    private int _desktopWidth = 1920;

    /// <summary>桌面分身的桌面高度（像素）。</summary>
    [ObservableProperty]
    private int _desktopHeight = 1080;

    /// <summary>桌面分身窗口是否置顶。</summary>
    [ObservableProperty]
    private bool _topmostEnabled = false;

    /// <summary>RDP 画面是否自适应窗口。关闭时使用 1:1 显示。</summary>
    [ObservableProperty]
    private bool _smartSizingEnabled = true;

    /// <summary>桌面分身窗口是否保持 16:9 宽高比。</summary>
    [ObservableProperty]
    private bool _keepAspectRatio = true;

    /// <summary>Alt+Tab、Windows 键等系统组合键是否发送到桌面分身。</summary>
    [ObservableProperty]
    private bool _sendSystemShortcutsToRemote = true;

    /// <summary>桌面分身的 RDP 音频是否静音，不影响主桌面的其他程序。</summary>
    [ObservableProperty]
    private bool _audioMuted = false;

    /// <summary>桌面分身是否处于观看模式。开启后键鼠不会在桌面分身内生效，仅显示画面。</summary>
    [ObservableProperty]
    private bool _watchModeEnabled = false;

    /// <summary>
    /// 程序启动时若检测到存活的分身会话，是否自动重新连接 RDP。
    /// </summary>
    [ObservableProperty]
    private bool _autoReconnectOnStartup = false;
}
