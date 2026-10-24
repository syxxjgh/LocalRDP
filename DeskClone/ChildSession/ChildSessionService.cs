using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DeskClone.Core;
using DeskClone.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace DeskClone.ChildSession;

/// <summary>
/// 桌面分身的核心编排服务：环境预检、启用 Child Session、维护 RDP 连接、
/// 处理连接重试，以及所有需要重连才能生效的 RDP 设置（含分辨率）。
/// </summary>
public sealed class ChildSessionService : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);
    private const int InitialConnectionRetryCount = 3;
    private const int RdpSettingsReconnectRetryCount = 2;
    private const int ErrorTimeout = 1460;

    private readonly IServiceProvider _serviceProvider;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly ILogger<ChildSessionService> _logger;
    private readonly DispatcherTimer _statusTimer;
    private readonly SemaphoreSlim _launchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();

    private MainWindow? _desktopWindow;
    private TaskCompletionSource<bool>? _connectionAttemptCompletionSource;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionFailure;
    private int _initialConnectionRetriesRemaining;
    private bool _connectionRetryInProgress;
    private RdpSettingChange _pendingRdpSettingChanges;
    private int _rdpSettingsReconnectRetriesRemaining;
    private bool _rdpSettingsReconnectRetryInProgress;
    private bool _statusTickInProgress;
    private bool _disposed;
    private bool _preserveSessionOnDispose;
    private string? _lastOperationMessage;
    private IReadOnlyList<ChildSessionEnvironmentCheck.Issue> _environmentIssues = [];

    public event EventHandler? StateChanged;

    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    public event EventHandler? SystemShortcutsReconnectCompleted;

    public event EventHandler? AudioReconnectCompleted;

    /// <summary>分辨率切换后的重连完成时触发，界面据此清除“正在应用”状态。</summary>
    public event EventHandler? ResolutionReconnectCompleted;

    public string StatusText { get; private set; } = "桌面分身尚未启动";

    public bool IsDesktopVisible => _desktopWindow?.IsVisible == true;

    public int ConnectedState { get; private set; }

    public uint? ChildSessionId { get; private set; }

    public bool SendSystemShortcutsToRemote { get; private set; } = true;

    public bool TopmostEnabled => _settings.TopmostEnabled;

    public bool SmartSizingEnabled => _settings.SmartSizingEnabled;

    public bool KeepAspectRatio => _settings.KeepAspectRatio;

    public bool AudioMuted => _settings.AudioMuted;

    public bool WatchModeEnabled => _settings.WatchModeEnabled;

    /// <summary>当前配置的桌面分身分辨率。</summary>
    public DrawingSize DesktopSize => new(_settings.DesktopWidth, _settings.DesktopHeight);

    /// <summary>
    /// 系统是否已安装并启用 RDP Wrapper。它与桌面分身功能不兼容，界面会在启动前据此提示用户。
    /// </summary>
    public bool IsRdpWrapperEnabled()
    {
        return ChildSessionNativeMethods.IsRdpWrapperEnabled();
    }

    /// <summary>
    /// 最近一次启动前环境预检是否发现了可能影响桌面分身的环境设置。
    /// </summary>
    public bool HasEnvironmentIssue => _environmentIssues.Count > 0;

    /// <summary>
    /// 最近一次启动前环境预检的结论文本，供界面提示与问题排查使用。
    /// </summary>
    public string EnvironmentIssueSummary =>
        ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues);

    public ChildSessionService(
        IServiceProvider serviceProvider,
        SettingsService settingsService,
        ILogger<ChildSessionService> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        SendSystemShortcutsToRemote = _settings.SendSystemShortcutsToRemote;
        _logger = logger;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
        RefreshState();
    }

    /// <summary>
    /// 启动桌面分身：先做一次环境预检，确保 Child Sessions 已启用，然后建立 RDP 连接并等待登录完成。
    /// 已经处于已连接状态时直接返回。
    /// </summary>
    public async Task StartAsync()
    {
        ThrowIfDisposed();
        RefreshEnvironmentCheck();
        EnsureChildSessionsEnabled();
        RefreshState();

        if (ConnectedState == 1)
        {
            return;
        }

        var completionSource = _connectionAttemptCompletionSource;
        if (completionSource is null || completionSource.Task.IsCompleted)
        {
            completionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionAttemptCompletionSource = completionSource;
            _lastConnectionFailure = null;

            try
            {
                ConnectCore("正在启动桌面分身");
            }
            catch
            {
                _initialConnectionRetriesRemaining = 0;
                completionSource.TrySetResult(false);
                _connectionAttemptCompletionSource = null;
                throw;
            }
        }

        try
        {
            await completionSource.Task.WaitAsync(
                ConnectionTimeout,
                _disposeCancellationTokenSource.Token);
        }
        catch (TimeoutException)
        {
            if (!completionSource.Task.IsCompleted)
            {
                _initialConnectionRetriesRemaining = 0;
                CompleteConnectionFailure(CreateConnectionTimeoutFailure());
                TryDisconnectRdpHost();
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionAttemptCompletionSource, completionSource))
            {
                _connectionAttemptCompletionSource = null;
            }
        }
    }

    public void ShowWindow()
    {
        ThrowIfDisposed();
        ShowDesktopWindow(EnsureDesktopWindow());
        RefreshState();
    }

    public void HideWindow()
    {
        ThrowIfDisposed();
        _desktopWindow?.Hide();
        RefreshState("已隐藏桌面分身，RDP 连接保持不变");
    }

    public void ShowChildSessionDesktop()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendShowDesktopShortcut();
        RefreshState("已向桌面分身发送 Win+D");
    }

    public void ShowChildSessionTaskView()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendTaskViewShortcut();
        RefreshState("已向桌面分身发送 Win+Tab");
    }

    public void SetSmartSizing(bool enabled)
    {
        ThrowIfDisposed();
        EnsureDesktopWindow().RdpHost.SetSmartSizing(enabled);
        _settings.SmartSizingEnabled = enabled;
        _settingsService.QueueSave();
        RefreshState(enabled ? "窗口显示模式已切换为自适应" : "窗口显示模式已切换为 1:1");
    }

    public void SetKeepAspectRatio(bool enabled)
    {
        ThrowIfDisposed();
        _settings.KeepAspectRatio = enabled;
        _settingsService.QueueSave();
        RefreshState(enabled ? "桌面分身窗口将保持宽高比" : "桌面分身窗口不再保持宽高比");
    }

    public void SetTopmost(bool enabled)
    {
        ThrowIfDisposed();
        _settings.TopmostEnabled = enabled;
        _settingsService.QueueSave();
        RefreshState(enabled ? "桌面分身窗口已置顶" : "桌面分身窗口已取消置顶");
    }

    public bool SetSendSystemShortcutsToRemote(bool enabled)
    {
        ThrowIfDisposed();
        if (SendSystemShortcutsToRemote == enabled)
        {
            return false;
        }

        SendSystemShortcutsToRemote = enabled;
        _settings.SendSystemShortcutsToRemote = enabled;
        _settingsService.QueueSave();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetSendSystemShortcutsToRemote(enabled);
        var target = enabled ? "桌面分身" : "本机";
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.SystemShortcuts,
            $"系统组合键已改为在{target}生效");
    }

    public bool SetAudioMuted(bool muted)
    {
        ThrowIfDisposed();
        if (AudioMuted == muted)
        {
            return false;
        }

        _settings.AudioMuted = muted;
        _settingsService.QueueSave();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetAudioMuted(muted);
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Audio,
            muted ? "桌面分身声音已关闭" : "桌面分身声音已开启");
    }

    /// <summary>
    /// 切换桌面分身分辨率。RDP 桌面尺寸在连接时确定，已连接状态下需要
    /// 断开并重连 RDP 才能生效——分身会话本身不受影响，其中的程序继续运行。
    /// 返回值表示是否触发了自动重连。
    /// </summary>
    public bool SetResolution(int width, int height)
    {
        ThrowIfDisposed();

        if (width == _settings.DesktopWidth && height == _settings.DesktopHeight)
        {
            return false;
        }

        _settings.DesktopWidth = width;
        _settings.DesktopHeight = height;
        _settingsService.QueueSave();

        var window = _desktopWindow;
        if (window is null || (window.RdpHost.ConnectedState == 0 && ChildSessionId is null))
        {
            RefreshState($"桌面分辨率已设为 {width} × {height}，将在下次连接时生效");
            return false;
        }

        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Resolution,
            $"桌面分辨率切换为 {width} × {height}");
    }

    public void SetWatchModeEnabled(bool enabled)
    {
        ThrowIfDisposed();
        _settings.WatchModeEnabled = enabled;
        _settingsService.QueueSave();
        _desktopWindow?.RdpHost.SetInputBlocked(enabled);
        RefreshState(enabled
            ? "已进入观看模式，本机鼠标和键盘不会进入桌面分身"
            : "已退出观看模式，可以操作桌面分身");
    }

    public async Task LaunchExecutableAsync(string executablePath)
    {
        ThrowIfDisposed();
        var childSessionId = GetRequiredChildSessionId();

        await _launchSemaphore.WaitAsync();
        try
        {
            RefreshState($"正在以管理员权限启动 {System.IO.Path.GetFileName(executablePath)}");
            await ChildSessionProcessLauncher.LaunchElevatedAsync(childSessionId, executablePath);
            RefreshState(
                $"已在桌面分身（会话 {childSessionId}）中以管理员权限启动 {System.IO.Path.GetFileName(executablePath)}");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>
    /// 断开 RDP、注销桌面分身会话并隐藏桌面分身窗口。
    /// </summary>
    /// <param name="expectedSessionId">需要注销的预期会话标识；为空时注销当前桌面分身会话。</param>
    public async Task LogoffAndHideAsync(uint? expectedSessionId = null)
    {
        ThrowIfDisposed();

        await _launchSemaphore.WaitAsync();
        try
        {
            var activeSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            if (expectedSessionId is { } expected
                && activeSessionId is { } active
                && active != expected)
            {
                throw new InvalidOperationException(
                    $"桌面分身会话已从 {expected} 变更为 {active}，已取消注销操作。");
            }

            // 完成目标会话校验后再取消连接和重试状态，避免旧菜单项影响刚创建的新会话。
            _initialConnectionRetriesRemaining = 0;
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _connectionAttemptCompletionSource?.TrySetResult(false);

            TryDisconnectRdpHost();
            RefreshState("正在断开 RDP 并注销桌面分身");

            var terminatedSessionId = await Task.Run(() =>
                expectedSessionId is { } targetSessionId
                    ? ChildSessionNativeMethods.TerminateChildSession(targetSessionId)
                    : ChildSessionNativeMethods.TerminateChildSession());
            _desktopWindow?.Hide();
            RefreshState(terminatedSessionId is null
                ? "当前没有桌面分身会话，桌面分身窗口已隐藏"
                : $"桌面分身会话 {terminatedSessionId.Value} 已注销，桌面分身窗口已隐藏");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    public void RefreshState(string? operationMessage = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            _lastOperationMessage = operationMessage;
        }

        try
        {
            var enabled = ChildSessionNativeMethods.IsChildSessionsEnabled();
            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            ConnectedState = _desktopWindow?.RdpHost.ConnectedState ?? 0;

            var connectionText = ConnectedState switch
            {
                0 => "未连接",
                1 => "已连接",
                2 => "正在连接",
                _ => $"未知连接状态 {ConnectedState}"
            };
            var resolutionText = $"{_settings.DesktopWidth} × {_settings.DesktopHeight}";
            var mainText = _lastOperationMessage ?? connectionText;

            StatusText =
                $"{mainText} | RDP：{connectionText} | 分辨率：{resolutionText} | 功能已启用：{enabled}";
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            StatusText = exception.GetBaseException().Message;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 标记下次 Dispose 时只断开 RDP、不注销分身会话。
    /// 用于“保留分身，仅退出程序”的退出方式——分身会话由系统维持，
    /// 下次启动程序后可重新连接，其中的程序不会退出。
    /// </summary>
    public void PreserveChildSessionOnExit()
    {
        _preserveSessionOnDispose = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _disposeCancellationTokenSource.Cancel();
        _connectionAttemptCompletionSource?.TrySetCanceled();
        _connectionAttemptCompletionSource = null;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;

        if (_desktopWindow is not null)
        {
            _desktopWindow.IsVisibleChanged -= OnDesktopWindowVisibilityChanged;
            _desktopWindow.RdpHost.ConnectionFailed -= OnRdpConnectionFailed;
            _desktopWindow.RdpHost.LoginCompleted -= OnRdpLoginCompleted;
            TryDisconnectRdpHost();
        }

        if (!_preserveSessionOnDispose)
        {
            try
            {
                _ = ChildSessionNativeMethods.TerminateChildSession(wait: false);
            }
            catch (Exception exception) when (IsExpectedChildSessionException(exception))
            {
                // 应用正在退出，Child Session 清理失败不应阻止主程序关闭。
            }
        }
        else
        {
            _logger.LogInformation("已按用户要求保留桌面分身会话，仅断开 RDP 连接。");
        }

        if (_desktopWindow is not null)
        {
            _desktopWindow.AllowClose = true;
            _desktopWindow.Close();
            _desktopWindow = null;
        }

        _launchSemaphore.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    private void ConnectCore(string operationMessage)
    {
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);

        if (window.RdpHost.ConnectedState == 0)
        {
            _initialConnectionRetriesRemaining
                = ChildSessionNativeMethods.TryGetChildSessionId() is null
                    ? InitialConnectionRetryCount
                    : 0;
            window.RdpHost.ConnectToChildSession(DesktopSize);
        }

        RefreshState(operationMessage);
    }

    private MainWindow EnsureDesktopWindow()
    {
        if (_desktopWindow is not null)
        {
            return _desktopWindow;
        }

        _desktopWindow = _serviceProvider.GetRequiredService<MainWindow>();
        _desktopWindow.IsVisibleChanged += OnDesktopWindowVisibilityChanged;
        _desktopWindow.RdpHost.ConnectionFailed += OnRdpConnectionFailed;
        _desktopWindow.RdpHost.LoginCompleted += OnRdpLoginCompleted;
        _desktopWindow.RdpHost.SetSmartSizing(_settings.SmartSizingEnabled);
        _desktopWindow.RdpHost.SetSendSystemShortcutsToRemote(SendSystemShortcutsToRemote);
        _desktopWindow.RdpHost.SetAudioMuted(AudioMuted);
        return _desktopWindow;
    }

    private static void ShowDesktopWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_statusTickInProgress || _disposed)
        {
            return;
        }

        _statusTickInProgress = true;
        try
        {
            RefreshState();
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(exception.GetBaseException().Message);
        }
        finally
        {
            _statusTickInProgress = false;
        }
    }

    /// <summary>
    /// 取当前桌面分身会话 ID；没有可用会话时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    private uint GetRequiredChildSessionId()
    {
        var childSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSessionId is null)
        {
            throw new InvalidOperationException("当前没有可用的桌面分身，请先启动桌面分身。");
        }

        return childSessionId.Value;
    }

    /// <summary>
    /// 收集 Windows 环境结论，供界面在启动前提示用户，也供失败诊断使用。
    /// 这些检查全部是只读的，不会修改系统设置。
    /// </summary>
    public void RefreshEnvironmentCheck()
    {
        ThrowIfDisposed();
        _environmentIssues = ChildSessionEnvironmentCheck.Collect();

        foreach (var issue in _environmentIssues)
        {
            _logger.LogWarning(
                "桌面分身环境预检：{IssueTitle}。{IssueMessage}",
                issue.Title,
                issue.Message);
        }
    }

    private void EnsureChildSessionsEnabled()
    {
        if (!ChildSessionNativeMethods.IsChildSessionsEnabled())
        {
            ChildSessionNativeMethods.EnableChildSessions();
        }
    }

    private void OnDesktopWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshState();
    }

    private void OnRdpConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _lastConnectionFailure = e;

        if (_pendingRdpSettingChanges != RdpSettingChange.None)
        {
            if (_rdpSettingsReconnectRetryInProgress)
            {
                return;
            }

            if (_rdpSettingsReconnectRetriesRemaining > 0)
            {
                RetryRdpSettingsReconnectAsync(e);
                return;
            }

            _pendingRdpSettingChanges = RdpSettingChange.None;
        }

        if (_initialConnectionRetriesRemaining > 0
            && !_connectionRetryInProgress)
        {
            RetryInitialConnectionAsync(e);
            return;
        }

        CompleteConnectionFailure(e);
    }

    // 使用 OnLoginComplete 等待实际登录完成，避免依赖固定时长的延时。
    private async void OnRdpLoginCompleted(object? sender, EventArgs e)
    {
        _lastConnectionFailure = null;
        _initialConnectionRetriesRemaining = 0;
        _connectionRetryInProgress = false;
        _connectionAttemptCompletionSource?.TrySetResult(true);

        // 输入窗口每次连接都会新建，观看模式需要重新施加到新窗口上。
        _desktopWindow?.RdpHost.SetInputBlocked(WatchModeEnabled);

        var completedSettingChanges = _pendingRdpSettingChanges;
        if (completedSettingChanges != RdpSettingChange.None)
        {
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _rdpSettingsReconnectRetryInProgress = false;
            RefreshState("自动重新连接完成，RDP 设置已生效");

            if (completedSettingChanges.HasFlag(RdpSettingChange.SystemShortcuts))
            {
                SystemShortcutsReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (completedSettingChanges.HasFlag(RdpSettingChange.Audio))
            {
                AudioReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (completedSettingChanges.HasFlag(RdpSettingChange.Resolution))
            {
                ResolutionReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            RefreshState("桌面分身登录初始化完成");
        }

        await Task.Yield();
    }

    private async void RetryRdpSettingsReconnectAsync(
        ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _rdpSettingsReconnectRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = RdpSettingsReconnectRetryCount
                          - _rdpSettingsReconnectRetriesRemaining
                          + 1;
        _rdpSettingsReconnectRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << retryNumber);

        _logger.LogWarning(
            "RDP 设置切换后的自动重连失败，将在 {RetryDelaySeconds} 秒后进行第 {RetryNumber} 次重试。"
            + "错误：{ErrorMessage}，错误代码：{ErrorCode}，扩展错误代码：{ExtendedErrorCode}",
            retryDelay.TotalSeconds,
            retryNumber,
            firstFailure.Message,
            firstFailure.ErrorCode,
            firstFailure.ExtendedErrorCode);
        RefreshState(
            $"RDP 自动重连暂未成功，{retryDelay.TotalSeconds:0} 秒后重试"
            + $"（{retryNumber}/{RdpSettingsReconnectRetryCount}）");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || _pendingRdpSettingChanges == RdpSettingChange.None)
            {
                return;
            }

            RefreshState(
                $"正在重试 RDP 自动连接（{retryNumber}/{RdpSettingsReconnectRetryCount}）");
            _rdpSettingsReconnectRetryInProgress = false;
            EnsureDesktopWindow().RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"RDP 自动重新连接失败：{actualException.Message}",
                errorCode);
        }
        finally
        {
            _rdpSettingsReconnectRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    private async void RetryInitialConnectionAsync(ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _connectionRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = InitialConnectionRetryCount - _initialConnectionRetriesRemaining + 1;
        _initialConnectionRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << (retryNumber - 1));

        _logger.LogWarning(
            "桌面分身首次初始化连接失败，将在 {RetryDelaySeconds} 秒后进行第 {RetryNumber} 次重试。"
            + "错误：{ErrorMessage}，错误代码：{ErrorCode}，扩展错误代码：{ExtendedErrorCode}",
            retryDelay.TotalSeconds,
            retryNumber,
            firstFailure.Message,
            firstFailure.ErrorCode,
            firstFailure.ExtendedErrorCode);
        RefreshState(
            $"桌面分身首次初始化尚未完成，{retryDelay.TotalSeconds:0} 秒后自动重试"
            + $"（{retryNumber}/{InitialConnectionRetryCount}）");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed)
            {
                return;
            }

            RefreshState();
            if (_desktopWindow is null)
            {
                return;
            }

            RefreshState(
                $"正在重试桌面分身连接（{retryNumber}/{InitialConnectionRetryCount}）");
            _connectionRetryInProgress = false;
            // OnLogonError 触发后 ActiveX 可能仍处于连接状态，必须先断开再重连。
            // 否则这里直接退出会丢失已捕获的真实错误，最终只剩外层连接超时。
            _desktopWindow.RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"重试桌面分身连接失败：{actualException.Message}",
                errorCode);
        }
        finally
        {
            _connectionRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    /// <summary>
    /// 连接失败的统一收尾：「重试耗尽」与「连接超时」两条路径都经由这里附加诊断信息，
    /// 再更新状态并通知订阅者。
    /// </summary>
    private void CompleteConnectionFailure(ChildSessionConnectionFailedEventArgs e)
    {
        // 统一在这里补充诊断，让「重试耗尽」和「连接超时」两条失败路径给出同样的信息。
        var diagnosed = new ChildSessionConnectionFailedEventArgs(
            $"{e.Message}{BuildFailureDiagnosis()}",
            e.ErrorCode,
            e.ExtendedErrorCode);
        _lastConnectionFailure = diagnosed;
        _initialConnectionRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);
        _logger.LogError(
            "桌面分身 RDP 连接失败：{ErrorMessage}，错误代码：{ErrorCode}，扩展错误代码：{ExtendedErrorCode}",
            diagnosed.Message,
            diagnosed.ErrorCode,
            diagnosed.ExtendedErrorCode);
        RefreshState(diagnosed.Message);
        ConnectionFailed?.Invoke(this, diagnosed);
    }

    /// <summary>
    /// 构造连接超时的失败信息，并附上 RDP ActiveX 最后报告的失败原因（如果有）。
    /// </summary>
    private ChildSessionConnectionFailedEventArgs CreateConnectionTimeoutFailure()
    {
        var timeoutMessage =
            $"桌面分身连接及登录初始化未能在 {ConnectionTimeout.TotalSeconds:0} 秒内完成。";
        var lastDiagnostic =
            _desktopWindow?.RdpHost.LastConnectionDiagnostic
            ?? _lastConnectionFailure;
        if (lastDiagnostic is null)
        {
            return new ChildSessionConnectionFailedEventArgs(
                $"{timeoutMessage}\n\nRDP ActiveX 未报告更具体的失败原因。",
                ErrorTimeout);
        }

        return new ChildSessionConnectionFailedEventArgs(
            $"{timeoutMessage}\n\nRDP ActiveX 最后报告：\n{lastDiagnostic.Message}",
            lastDiagnostic.ErrorCode,
            lastDiagnostic.ExtendedErrorCode);
    }

    /// <summary>
    /// 连接失败后的补充诊断：报告是否存在桌面分身会话，并附上环境预检结论。
    /// RDP ActiveX 自身只回报「发生内部错误」和断开原因，单独看这些信息无法定位问题。
    /// </summary>
    private string BuildFailureDiagnosis()
    {
        var builder = new StringBuilder();

        if (ChildSessionNativeMethods.TryGetChildSessionId() is null)
        {
            builder.AppendLine().AppendLine();
            builder.Append(
                "诊断：当前没有可用的桌面分身会话，说明 Windows 未能建立 RDP 会话。"
                + "该状态本身无法确定原因——系统无法创建会话与凭据未通过验证都会得到这个结果，"
                + "请结合上面的 RDP ActiveX 错误信息一并判断。");
        }

        if (_environmentIssues.Count > 0)
        {
            builder.AppendLine().AppendLine();
            builder.Append(ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 尝试断开 RDP 主机连接。ActiveX 正在自行断开时可能返回 COM 错误，忽略后仍继续注销 Child Session。
    /// </summary>
    private void TryDisconnectRdpHost()
    {
        try
        {
            _desktopWindow?.RdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 正在自行断开时可能返回 COM 错误，仍可继续注销 Child Session。
        }
    }

    private static bool IsExpectedChildSessionException(Exception exception)
    {
        return exception is Win32Exception
            or COMException
            or EntryPointNotFoundException
            or TargetInvocationException
            or InvalidCastException
            or InvalidOperationException
            or FileNotFoundException
            or ArgumentException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool ReconnectForRdpSettingChange(
        MainWindow window,
        RdpSettingChange settingChange,
        string operationMessage)
    {
        RefreshState();
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState($"{operationMessage}，将在下次 RDP 连接后应用");
            return false;
        }

        var previousSettingChanges = _pendingRdpSettingChanges;
        _pendingRdpSettingChanges |= settingChange;
        _rdpSettingsReconnectRetriesRemaining = RdpSettingsReconnectRetryCount;
        try
        {
            window.RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch
        {
            _pendingRdpSettingChanges = previousSettingChanges;
            if (_pendingRdpSettingChanges == RdpSettingChange.None)
            {
                _rdpSettingsReconnectRetriesRemaining = 0;
            }

            throw;
        }

        RefreshState($"{operationMessage}，正在自动重新连接 RDP");
        return true;
    }

    [Flags]
    private enum RdpSettingChange
    {
        None = 0,
        SystemShortcuts = 1,
        Audio = 2,
        Resolution = 4
    }
}
