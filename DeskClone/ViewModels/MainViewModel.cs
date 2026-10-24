using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DeskClone.ChildSession;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DeskClone.ViewModels;

/// <summary>一个可选的 16:9 桌面分辨率。</summary>
public sealed record ResolutionOption(int Width, int Height)
{
    public string Label => $"{Width} × {Height}";
}

public partial class MainViewModel : ObservableObject
{
    private readonly ChildSessionService _childSessionService;
    private readonly Core.SettingsService _settingsService;
    private readonly Core.AppSettings _settings;
    private readonly DispatcherTimer _notificationTimer;
    private bool _startRequested;
    private bool _resolutionUpdateInProgress;

    /// <summary>主流 16:9 分辨率，共 5 档。</summary>
    public IReadOnlyList<ResolutionOption> Resolutions { get; } =
    [
        new ResolutionOption(1280, 720),
        new ResolutionOption(1600, 900),
        new ResolutionOption(1920, 1080),
        new ResolutionOption(2560, 1440),
        new ResolutionOption(3840, 2160)
    ];

    [ObservableProperty]
    private ResolutionOption? _selectedResolution;

    /// <summary>主操作按钮文案：检测到存活分身时显示“重新连接”。</summary>
    [ObservableProperty]
    private string _startButtonText = "启动并连接";

    /// <summary>是否在程序启动时自动重连存活的分身会话（持久化设置）。</summary>
    [ObservableProperty]
    private bool _isAutoReconnectOnStartup;

    [ObservableProperty]
    private Brush _connectionStatusBrush = Brushes.Red;

    [ObservableProperty]
    private string _connectionStatusToolTip = "桌面分身未启动";

    [ObservableProperty]
    private string _connectionStatusTitle = "桌面分身尚未启动";

    [ObservableProperty]
    private string _connectionStatusDescription = "点击“启动并连接”，将创建独立桌面并建立 RDP 连接。";

    [ObservableProperty]
    private string _rdpStatusText = "RDP 未连接";

    [ObservableProperty]
    private bool _isRdpConnected;

    [ObservableProperty]
    private bool _isConnectionPromptVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionInteractionEnabled))]
    private bool _isSessionClosing;

    [ObservableProperty]
    private bool _isTopmost;

    [ObservableProperty]
    private bool _isAdaptive = true;

    [ObservableProperty]
    private bool _isOneToOne;

    [ObservableProperty]
    private bool _keepAspectRatio = true;

    [ObservableProperty]
    private bool _sendSystemShortcutsToRemote = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchModeMenuHeader))]
    [NotifyPropertyChangedFor(nameof(WatchModeMenuToolTip))]
    private bool _isWatchModeEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioButtonToolTip))]
    private bool _isAudioMuted;

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private string _notificationTitle = "";

    [ObservableProperty]
    private string _notificationMessage = "";

    [ObservableProperty]
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolutionApplyPendingText))]
    private bool _isResolutionReconnectPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSendSystemShortcutsToRemoteCommand))]
    private bool _isSystemShortcutsReconnectPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleAudioMutedCommand))]
    private bool _isAudioReconnectPending;

    public string TopmostButtonToolTip => IsTopmost ? "取消置顶" : "置顶";

    public string AudioButtonToolTip => IsAudioMuted
        ? "开启桌面分身声音，不影响主桌面的其他程序；切换时会自动重新连接 RDP"
        : "关闭桌面分身声音，不影响主桌面的其他程序；切换时会自动重新连接 RDP";

    public string WatchModeMenuHeader => IsWatchModeEnabled ? "控制模式" : "观看模式";

    public string WatchModeMenuToolTip => IsWatchModeEnabled
        ? "退出观看模式"
        : "桌面分身画面仅可观看，键鼠不会生效";

    public string IsResolutionApplyPendingText => IsResolutionReconnectPending
        ? "正在切换分辨率…"
        : "桌面分辨率";

    public bool HasChildSession => _childSessionService.ChildSessionId is not null;

    public bool IsSessionInteractionEnabled => !IsSessionClosing;

    public MainViewModel(
        ChildSessionService childSessionService,
        Core.SettingsService settingsService)
    {
        _childSessionService = childSessionService;
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        _isAutoReconnectOnStartup = _settings.AutoReconnectOnStartup;
        _isTopmost = _childSessionService.TopmostEnabled;
        _isAdaptive = _childSessionService.SmartSizingEnabled;
        _isOneToOne = !_isAdaptive;
        _keepAspectRatio = _childSessionService.KeepAspectRatio;
        _sendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
        _isAudioMuted = _childSessionService.AudioMuted;
        _isWatchModeEnabled = _childSessionService.WatchModeEnabled;

        // 初始选中项来自持久化设置；不在列表中的历史值回退到最接近的默认档位，
        // 但不回写设置，避免用户手改配置文件后被悄悄覆盖。
        var size = _childSessionService.DesktopSize;
        _resolutionUpdateInProgress = true;
        SelectedResolution = FindClosestResolution(size.Width, size.Height);
        _resolutionUpdateInProgress = false;

        _notificationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _notificationTimer.Tick += OnNotificationTimerTick;
        _childSessionService.StateChanged += OnChildSessionStateChanged;
        _childSessionService.ConnectionFailed += OnChildSessionConnectionFailed;
        _childSessionService.SystemShortcutsReconnectCompleted +=
            OnSystemShortcutsReconnectCompleted;
        _childSessionService.AudioReconnectCompleted += OnAudioReconnectCompleted;
        _childSessionService.ResolutionReconnectCompleted += OnResolutionReconnectCompleted;
        UpdateConnectionStatus();
    }

    partial void OnSelectedResolutionChanged(ResolutionOption? value)
    {
        if (_resolutionUpdateInProgress || value is null)
        {
            return;
        }

        if (IsResolutionReconnectPending)
        {
            // 上一次切换的重连还没完成，忽略本次选择并回退显示。
            RevertSelectionToService();
            return;
        }

        try
        {
            var reconnectStarted = _childSessionService.SetResolution(
                value.Width,
                value.Height);
            IsResolutionReconnectPending = reconnectStarted;
            UpdateConnectionStatus();

            ShowNotification(
                reconnectStarted ? "正在切换分辨率" : "分辨率已设置",
                reconnectStarted
                    ? $"RDP 正在以 {value.Label} 自动重连，分身会话不受影响，其中运行的程序不会退出。"
                    : $"桌面分辨率已设为 {value.Label}，将在下次连接时生效。",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsResolutionReconnectPending = false;
            RevertSelectionToService();
            ShowError(exception);
            UpdateConnectionStatus();
        }
    }

    private void RevertSelectionToService()
    {
        var size = _childSessionService.DesktopSize;
        _resolutionUpdateInProgress = true;
        SelectedResolution = FindClosestResolution(size.Width, size.Height);
        _resolutionUpdateInProgress = false;
    }

    private ResolutionOption FindClosestResolution(int width, int height)
    {
        ResolutionOption? exact = null;
        foreach (var option in Resolutions)
        {
            if (option.Width == width && option.Height == height)
            {
                exact = option;
                break;
            }
        }

        if (exact is not null)
        {
            return exact;
        }

        // 非标准值：找面积最接近的档位用于界面显示。
        ResolutionOption closest = Resolutions[0];
        var target = (double)width * height;
        foreach (var option in Resolutions)
        {
            var area = (double)option.Width * option.Height;
            var closestArea = (double)closest.Width * closest.Height;
            if (Math.Abs(area - target) < Math.Abs(closestArea - target))
            {
                closest = option;
            }
        }

        return closest;
    }

    /// <summary>
    /// 程序启动时若开启了“自动重连”且检测到存活分身会话，自动建立 RDP 连接。
    /// 失败不阻塞启动——用户仍可手动点击“重新连接”。
    /// </summary>
    public async Task TryAutoReconnectAsync()
    {
        if (!_settings.AutoReconnectOnStartup
            || _childSessionService.ChildSessionId is null
            || _childSessionService.ConnectedState == 1)
        {
            return;
        }

        // 等窗口完成首次渲染，再触发连接流程。
        await Task.Delay(800);
        await StartAsync();
    }

    [RelayCommand]
    private void ToggleAutoReconnectOnStartup()
    {
        _settings.AutoReconnectOnStartup = !_settings.AutoReconnectOnStartup;
        _settingsService.QueueSave();
        IsAutoReconnectOnStartup = _settings.AutoReconnectOnStartup;
        ShowNotification(
            "自动重连已" + (IsAutoReconnectOnStartup ? "开启" : "关闭"),
            IsAutoReconnectOnStartup
                ? "下次启动程序时，若分身会话仍在后台运行，将自动重新连接。"
                : "下次启动程序时需要手动点击“重新连接”。",
            InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 退出程序但保留分身会话：仅断开 RDP，分身内的程序继续在后台运行，
    /// 下次启动程序可重新连接。
    /// </summary>
    public void ExitPreservingSession()
    {
        _childSessionService.PreserveChildSessionOnExit();
    }

    public async Task LogoffAndHideAsync()
    {
        if (IsSessionClosing)
        {
            return;
        }

        IsSessionClosing = true;
        UpdateConnectionStatus();

        try
        {
            // 先让“正在关闭”状态完成渲染，再执行可能耗时的注销操作。
            await Dispatcher.Yield(DispatcherPriority.Render);
            await _childSessionService.LogoffAndHideAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsSessionClosing = false;
            UpdateConnectionStatus();
        }
    }

    partial void OnIsTopmostChanged(bool value)
    {
        OnPropertyChanged(nameof(TopmostButtonToolTip));
    }

    /// <summary>
    /// 启动桌面分身的命令入口。启动前依次检查 RDP Wrapper 兼容性与环境预检结论，
    /// 命中时先请用户确认；用户选择不继续则中止启动。
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (_childSessionService.ConnectedState != 1
            && _childSessionService.IsRdpWrapperEnabled())
        {
            var result = MessageBox.Show(
                "检测到系统已安装并启用 RDP Wrapper。\n\n"
                + "RDP Wrapper 提供了更强大的远程多用户支持，但与当前的桌面分身功能不兼容，"
                + "可能导致桌面分身无法正常启动。\n\n是否仍要继续？",
                "RDP Wrapper 兼容性提醒",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (_childSessionService.ConnectedState != 1
            && Execute(_childSessionService.RefreshEnvironmentCheck)
            && _childSessionService.HasEnvironmentIssue)
        {
            // 默认按钮为「是」：这些条件只是风险提示，不应该挡住本来可以连上的用户。
            var result = MessageBox.Show(
                _childSessionService.EnvironmentIssueSummary
                + "\n\n检测到可能影响桌面分身的环境设置。是否仍要继续？",
                "桌面分身环境检查",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _startRequested = true;
        IsConnectionPromptVisible = false;

        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await ExecuteAsync(_childSessionService.StartAsync);
        }
        finally
        {
            _startRequested = false;
            UpdateConnectionStatus();
        }
    }

    [RelayCommand]
    private void Hide()
    {
        Execute(_childSessionService.HideWindow);
    }

    [RelayCommand]
    private void SwitchWindow()
    {
        Execute(_childSessionService.ShowChildSessionTaskView);
    }

    [RelayCommand]
    private void UseAdaptive()
    {
        if (!Execute(() => _childSessionService.SetSmartSizing(true)))
        {
            return;
        }

        IsAdaptive = true;
        IsOneToOne = false;
        OnPropertyChanged(nameof(IsAdaptive));
        OnPropertyChanged(nameof(IsOneToOne));
    }

    [RelayCommand]
    private void UseOneToOne()
    {
        if (!Execute(() => _childSessionService.SetSmartSizing(false)))
        {
            return;
        }

        IsAdaptive = false;
        IsOneToOne = true;
        OnPropertyChanged(nameof(IsAdaptive));
        OnPropertyChanged(nameof(IsOneToOne));
    }

    [RelayCommand]
    private void ToggleKeepAspectRatio()
    {
        if (!Execute(() => _childSessionService.SetKeepAspectRatio(!KeepAspectRatio)))
        {
            return;
        }

        KeepAspectRatio = _childSessionService.KeepAspectRatio;
    }

    [RelayCommand(CanExecute = nameof(CanToggleSendSystemShortcutsToRemote))]
    private void ToggleSendSystemShortcutsToRemote()
    {
        try
        {
            var reconnectStarted = _childSessionService.SetSendSystemShortcutsToRemote(
                !SendSystemShortcutsToRemote);
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            IsSystemShortcutsReconnectPending = reconnectStarted;
            UpdateConnectionStatus();

            ShowNotification(
                reconnectStarted ? "正在应用设置" : "设置已保存",
                reconnectStarted
                    ? "RDP 正在自动重新连接，连接完成后系统组合键设置生效。"
                    : "当前没有桌面分身会话，系统组合键设置将在下次连接时生效。",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsSystemShortcutsReconnectPending = false;
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            ShowNotification(
                "设置切换失败",
                exception.GetBaseException().Message,
                InfoBarSeverity.Error,
                TimeSpan.FromSeconds(8));
            UpdateConnectionStatus();
        }
    }

    private bool CanToggleSendSystemShortcutsToRemote()
    {
        return !IsSystemShortcutsReconnectPending;
    }

    [RelayCommand]
    private void ToggleWatchMode()
    {
        var enabled = !IsWatchModeEnabled;
        if (!Execute(() => _childSessionService.SetWatchModeEnabled(enabled)))
        {
            IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
            return;
        }

        IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        ShowNotification(
            enabled ? "观看模式已开启" : "观看模式已关闭",
            enabled
                ? "本机键鼠不会对分身生效，但控制中心的组合键仍然生效。"
                : "本机键鼠恢复操作桌面分身。",
            enabled ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
    }

    [RelayCommand(CanExecute = nameof(CanToggleAudioMuted))]
    private void ToggleAudioMuted()
    {
        try
        {
            var reconnectStarted = _childSessionService.SetAudioMuted(!IsAudioMuted);
            IsAudioMuted = _childSessionService.AudioMuted;
            IsAudioReconnectPending = reconnectStarted;
            UpdateConnectionStatus();

            ShowNotification(
                reconnectStarted ? "正在应用音频设置" : "音频设置已保存",
                reconnectStarted
                    ? "RDP 正在自动重新连接，连接完成后音频设置生效。"
                    : IsAudioMuted
                        ? "桌面分身将在下次连接时保持静音。"
                        : "桌面分身将在下次连接时播放声音。",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsAudioReconnectPending = false;
            IsAudioMuted = _childSessionService.AudioMuted;
            ShowNotification(
                "音频设置切换失败",
                exception.GetBaseException().Message,
                InfoBarSeverity.Error,
                TimeSpan.FromSeconds(8));
            UpdateConnectionStatus();
        }
    }

    private bool CanToggleAudioMuted()
    {
        return !IsAudioReconnectPending;
    }

    [RelayCommand]
    private void ShowDesktop()
    {
        Execute(_childSessionService.ShowChildSessionDesktop);
    }

    [RelayCommand]
    private void ShowTaskView()
    {
        Execute(_childSessionService.ShowChildSessionTaskView);
    }

    [RelayCommand]
    private async Task LaunchExecutableAsync()
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "选择要在桌面分身中以管理员权限启动的程序",
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
            Multiselect = false
        };

        if (fileDialog.ShowDialog() != true)
        {
            return;
        }

        var executablePath = Path.GetFullPath(fileDialog.FileName);
        await ExecuteAsync(() => _childSessionService.LaunchExecutableAsync(executablePath));
    }

    [RelayCommand]
    private void ToggleTopmost()
    {
        if (!Execute(() => _childSessionService.SetTopmost(!IsTopmost)))
        {
            return;
        }

        IsTopmost = _childSessionService.TopmostEnabled;
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            UpdateConnectionStatus();
        }
    }

    private bool Execute(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
        finally
        {
            UpdateConnectionStatus();
        }
    }

    private void OnChildSessionStateChanged(object? sender, EventArgs e)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            UpdateConnectionStatus();
            return;
        }

        _ = Application.Current.Dispatcher.BeginInvoke(UpdateConnectionStatus);
    }

    private void OnChildSessionConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsSystemShortcutsReconnectPending = false;
                IsAudioReconnectPending = false;
                IsResolutionReconnectPending = false;
                UpdateConnectionStatus();
                ShowNotification(
                    "RDP 连接失败",
                    e.Message,
                    InfoBarSeverity.Error,
                    TimeSpan.FromSeconds(10));
            }));
    }

    private void OnSystemShortcutsReconnectCompleted(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsSystemShortcutsReconnectPending = false;
                UpdateConnectionStatus();
                ShowNotification(
                    "设置已生效",
                    SendSystemShortcutsToRemote
                        ? "系统组合键现在会发送到桌面分身。"
                        : "系统组合键现在会在本机生效。",
                    InfoBarSeverity.Success);
            }));
    }

    private void OnAudioReconnectCompleted(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsAudioReconnectPending = false;
                IsAudioMuted = _childSessionService.AudioMuted;
                UpdateConnectionStatus();
                ShowNotification(
                    IsAudioMuted ? "桌面分身已静音" : "桌面分身声音已开启",
                    IsAudioMuted
                        ? "仅桌面分身的声音已关闭，主桌面其他程序不受影响。"
                        : "桌面分身的声音已恢复。",
                    InfoBarSeverity.Success);
            }));
    }

    private void OnResolutionReconnectCompleted(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsResolutionReconnectPending = false;
                RevertSelectionToService();
                UpdateConnectionStatus();
                var size = _childSessionService.DesktopSize;
                ShowNotification(
                    "分辨率已切换",
                    $"桌面分身当前分辨率为 {size.Width} × {size.Height}。",
                    InfoBarSeverity.Success);
            }));
    }

    private void ShowNotification(
        string title,
        string message,
        InfoBarSeverity severity,
        TimeSpan? duration = null)
    {
        _notificationTimer.Stop();
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
        _notificationTimer.Interval = duration ?? TimeSpan.FromSeconds(5);
        _notificationTimer.Start();
    }

    private void OnNotificationTimerTick(object? sender, EventArgs e)
    {
        _notificationTimer.Stop();
        IsNotificationOpen = false;
    }

    private void UpdateConnectionStatus()
    {
        var connectedState = _childSessionService.ConnectedState;
        var childSessionId = _childSessionService.ChildSessionId;
        IsTopmost = _childSessionService.TopmostEnabled;
        IsAdaptive = _childSessionService.SmartSizingEnabled;
        IsOneToOne = !IsAdaptive;
        KeepAspectRatio = _childSessionService.KeepAspectRatio;
        SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
        IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        IsAudioMuted = _childSessionService.AudioMuted;
        IsRdpConnected = connectedState == 1;
        IsConnectionPromptVisible = IsSessionClosing || connectedState == 0 && !_startRequested;
        StartButtonText = childSessionId is not null && connectedState != 1
            ? "重新连接"
            : "启动并连接";

        if (IsSessionClosing)
        {
            ConnectionStatusBrush = Brushes.Orange;
            ConnectionStatusTitle = "正在关闭桌面分身";
            ConnectionStatusDescription = "正在断开 RDP 并注销桌面分身会话，此过程可能需要一些时间。";
            RdpStatusText = connectedState == 0 ? "RDP 已断开" : "正在断开 RDP";
            ConnectionStatusToolTip = "关闭期间已暂停桌面分身操作，完成后窗口会自动隐藏。";
            OnPropertyChanged(nameof(HasChildSession));
            return;
        }

        if (IsRdpConnected)
        {
            ConnectionStatusBrush = Brushes.LimeGreen;
            ConnectionStatusTitle = "桌面分身已连接";
            ConnectionStatusDescription = "RDP 连接正常，可以直接在桌面分身中操作。";
        }
        else if (connectedState == 2)
        {
            ConnectionStatusBrush = childSessionId is null ? Brushes.Red : Brushes.DodgerBlue;
            ConnectionStatusTitle = "正在连接桌面分身";
            ConnectionStatusDescription = "正在创建或恢复 RDP 连接，请稍候。";
        }
        else if (childSessionId is not null)
        {
            ConnectionStatusBrush = Brushes.DodgerBlue;
            ConnectionStatusTitle = "桌面分身已启动，等待连接";
            ConnectionStatusDescription = "桌面分身会话仍在运行，点击“启动并连接”可重新建立 RDP 连接。";
        }
        else
        {
            ConnectionStatusBrush = Brushes.Red;
            ConnectionStatusTitle = "桌面分身尚未启动";
            ConnectionStatusDescription = "点击“启动并连接”，将创建独立桌面并建立 RDP 连接。";
        }

        RdpStatusText = connectedState switch
        {
            0 => "RDP 未连接",
            1 => "RDP 已连接",
            2 => "RDP 正在连接",
            _ => $"RDP 状态未知（{connectedState}）"
        };
        ConnectionStatusToolTip = _childSessionService.StatusText;
        OnPropertyChanged(nameof(HasChildSession));
    }

    private static void ShowError(Exception exception)
    {
        var actualException = exception.GetBaseException();
        var suggestion = actualException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
            ? "\n\n操作被系统拒绝，请确认 DeskClone 正在以管理员权限运行。"
            : string.Empty;

        MessageBox.Show(
            $"{actualException.Message}{suggestion}",
            "桌面分身",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
