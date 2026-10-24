using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using DeskClone.Controls;
using DeskClone.ViewModels;
using Wpf.Ui.Controls;

namespace DeskClone.Views;

/// <summary>
/// 应用主窗口：内嵌 RDP ActiveX 的桌面分身窗口，带系统托盘图标。
/// 隐藏窗口后可从托盘唤回；关闭时可选注销分身或保留分身仅退出程序。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private bool _closeRequestInProgress;

    internal RdpActiveXHost RdpHost { get; } = new();

    internal bool AllowClose { get; set; }

    public MainWindow(MainViewModel viewModel)
    {
        DataContext = _viewModel = viewModel;
        InitializeComponent();
        RdpFormsHost.Child = RdpHost;

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "桌面分身",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _trayIcon.ContextMenuStrip = BuildTrayMenu();

        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        try
        {
            // 开启了“启动时自动重连”且分身仍在后台时，直接恢复 RDP 连接。
            await _viewModel.TryAutoReconnectAsync();
        }
        catch
        {
            // 自动重连失败不阻塞启动，用户可手动点击“重新连接”。
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose || Dispatcher.HasShutdownStarted)
        {
            base.OnClosing(e);
            return;
        }

        if (_closeRequestInProgress)
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }

        if (!_viewModel.HasChildSession)
        {
            // 没有分身会话，直接退出。
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);

        var choice = ExitConfirmDialog.Prompt(this);
        switch (choice)
        {
            case ExitChoice.LogoffAndExit:
                _ = CloseAfterLogoffAsync();
                break;

            case ExitChoice.PreserveAndExit:
                _ = ExitPreservingAsync();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        RdpFormsHost.Child = null;
        RdpHost.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    /// <summary>注销分身会话，完成后真正关闭窗口退出程序。</summary>
    private async Task CloseAfterLogoffAsync()
    {
        if (_closeRequestInProgress)
        {
            return;
        }

        _closeRequestInProgress = true;
        try
        {
            await _viewModel.LogoffAndHideAsync();
        }
        finally
        {
            _closeRequestInProgress = false;
            AllowClose = true;
            Close();
        }
    }

    /// <summary>
    /// 保留分身会话并退出程序：断开 RDP、弹出托盘气泡提示后关闭。
    /// </summary>
    private async Task ExitPreservingAsync()
    {
        if (_closeRequestInProgress)
        {
            return;
        }

        _closeRequestInProgress = true;
        try
        {
            _viewModel.ExitPreservingSession();
            _trayIcon.ShowBalloonTip(
                2500,
                "桌面分身",
                "分身会话已保留，正在后台运行；重新打开程序即可重新连接。",
                System.Windows.Forms.ToolTipIcon.Info);
            // 等气泡展示完再退出，否则托盘图标随进程销毁，气泡一闪而过。
            await Task.Delay(2800);
        }
        finally
        {
            _closeRequestInProgress = false;
            AllowClose = true;
            Close();
        }
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示窗口");
        showItem.Font = new System.Drawing.Font(showItem.Font, System.Drawing.FontStyle.Bold);
        showItem.Click += (_, _) => ShowFromTray();

        var hideItem = new System.Windows.Forms.ToolStripMenuItem("隐藏窗口（保持 RDP 连接）");
        hideItem.Click += (_, _) => _viewModel.HideCommand.Execute(null);

        var logoffExitItem = new System.Windows.Forms.ToolStripMenuItem("关闭分身并退出");
        logoffExitItem.Click += (_, _) => _ = CloseAfterLogoffAsync();

        var preserveExitItem = new System.Windows.Forms.ToolStripMenuItem("保留分身，仅退出程序");
        preserveExitItem.Click += (_, _) => _ = ExitPreservingAsync();

        menu.Items.Add(showItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(logoffExitItem);
        menu.Items.Add(preserveExitItem);
        return menu;
    }

    /// <summary>
    /// 程序化的托盘图标：蓝底白字的简单圆形徽标，免去携带 .ico 资源文件。
    /// </summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        const int size = 32;
        using var bitmap = new System.Drawing.Bitmap(size, size);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new System.Drawing.Drawing2D.GraphicsPath();
            background.AddEllipse(1, 1, size - 2, size - 2);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x2F, 0xA4, 0xE7));
            graphics.FillPath(fill, background);
            using var font = new System.Drawing.Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold);
            var textSize = graphics.MeasureString("分", font);
            using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            graphics.DrawString(
                "分",
                font,
                textBrush,
                (size - textSize.Width) / 2f,
                (size - textSize.Height) / 2f);
        }

        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
}
