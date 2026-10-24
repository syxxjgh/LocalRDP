using System.Windows;
using Wpf.Ui.Controls;

namespace DeskClone.Views;

/// <summary>退出方式三选一的结果。</summary>
internal enum ExitChoice
{
    /// <summary>取消退出，什么都不做。</summary>
    Cancel,

    /// <summary>注销分身会话后退出程序。</summary>
    LogoffAndExit,

    /// <summary>保留分身会话（后台继续运行）并退出程序。</summary>
    PreserveAndExit
}

/// <summary>
/// 关闭主窗口时的三选一对话框：注销分身 / 保留分身 / 取消。
/// </summary>
public partial class ExitConfirmDialog : FluentWindow
{
    internal ExitChoice Result { get; private set; } = ExitChoice.Cancel;

    public ExitConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>以指定所有者弹出对话框并返回用户的选择。</summary>
    internal static ExitChoice Prompt(Window owner)
    {
        var dialog = new ExitConfirmDialog
        {
            Owner = owner
        };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private void OnLogoffExit(object sender, RoutedEventArgs e)
    {
        Result = ExitChoice.LogoffAndExit;
        Close();
    }

    private void OnPreserveExit(object sender, RoutedEventArgs e)
    {
        Result = ExitChoice.PreserveAndExit;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = ExitChoice.Cancel;
        Close();
    }
}
