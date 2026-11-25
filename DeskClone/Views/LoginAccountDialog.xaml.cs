using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskClone.ChildSession;
using Wpf.Ui.Controls;

namespace DeskClone.Views;

/// <summary>用户在登录确认对话框中的选择。</summary>
internal sealed record LoginAccountSelection(
    string AccountName,
    string? Password,
    bool ConfirmBeforeNextConnect,
    bool ShouldLogoffConflictingSession);

/// <summary>
/// 桌面分身登录前的账户确认对话框：禁止选择与当前主桌面相同的 Windows 账户。
/// </summary>
public partial class LoginAccountDialog : FluentWindow
{
    private static readonly TimeSpan ValidationDelay = TimeSpan.FromMilliseconds(300);

    private readonly bool _logoffConflictingSession;
    private readonly DispatcherTimer _validationTimer;
    private LoginAccountSelection? _result;

    private LoginAccountDialog(
        string currentDesktopAccount,
        IReadOnlyList<string> candidateAccounts,
        string initialAccount,
        bool confirmBeforeNextConnect,
        uint? existingChildSessionId,
        string? existingChildSessionAccount,
        bool existingSessionIsCurrentUser)
    {
        InitializeComponent();
        _logoffConflictingSession = existingSessionIsCurrentUser;

        CurrentAccountText.Text =
            $"当前主桌面账户：{currentDesktopAccount}（已禁止使用此账户登录桌面分身，否则会与主桌面的登录状态冲突）";
        foreach (var account in candidateAccounts)
        {
            AccountComboBox.Items.Add(account);
        }

        AccountComboBox.Text = initialAccount;
        ConfirmEachTimeCheckBox.IsChecked = confirmBeforeNextConnect;

        if (existingChildSessionId is { } sessionId)
        {
            ExistingSessionBanner.Visibility = Visibility.Visible;
            if (existingSessionIsCurrentUser)
            {
                ExistingSessionBanner.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0x6B, 0x6B));
                ExistingSessionText.Text =
                    $"检测到后台存活的分身会话（会话 {sessionId}）登录的是当前主桌面账户 {existingChildSessionAccount}。"
                    + "继续前会先注销该会话（其中运行的程序会关闭），再使用下面选择的账户重新登录。";
                ConfirmButton.Content = "注销冲突分身并登录";
            }
            else if (!string.IsNullOrWhiteSpace(existingChildSessionAccount))
            {
                ExistingSessionBanner.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x2F, 0xA4, 0xE7));
                ExistingSessionText.Text =
                    $"检测到后台存活的分身会话（会话 {sessionId}），当前登录账户：{existingChildSessionAccount}。";
            }
            else
            {
                ExistingSessionBanner.Visibility = Visibility.Collapsed;
            }
        }

        // 账户校验会做名称解析，输入时延迟执行，避免每次按键都触发解析。
        _validationTimer = new DispatcherTimer { Interval = ValidationDelay };
        _validationTimer.Tick += (_, _) =>
        {
            _validationTimer.Stop();
            Validate();
        };
        AccountComboBox.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) =>
            {
                ConfirmButton.IsEnabled = false;
                _validationTimer.Stop();
                _validationTimer.Start();
            }));
        AccountComboBox.SelectionChanged += (_, _) => Validate();
        Loaded += (_, _) => Validate();
    }

    /// <summary>弹出对话框；用户取消时返回 null。</summary>
    internal static LoginAccountSelection? Prompt(
        Window? owner,
        string currentDesktopAccount,
        IReadOnlyList<string> candidateAccounts,
        string initialAccount,
        bool confirmBeforeNextConnect,
        uint? existingChildSessionId,
        string? existingChildSessionAccount,
        bool existingSessionIsCurrentUser)
    {
        var dialog = new LoginAccountDialog(
            currentDesktopAccount,
            candidateAccounts,
            initialAccount,
            confirmBeforeNextConnect,
            existingChildSessionId,
            existingChildSessionAccount,
            existingSessionIsCurrentUser);
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog._result;
    }

    private string? Validate()
    {
        var text = AccountComboBox.Text?.Trim() ?? string.Empty;
        string? error = null;
        if (text.Length == 0)
        {
            error = "请输入或选择一个独立的 Windows 登录账户。";
        }
        else if (ChildSessionAccountHelper.IsCurrentWindowsUser(text))
        {
            error = "不能使用与当前主桌面相同的 Windows 账户，请选择其他独立账户。";
        }

        ValidationText.Text = error ?? string.Empty;
        ValidationText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.IsEnabled = error is null;
        return error;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _validationTimer.Stop();
        if (Validate() is not null)
        {
            return;
        }

        var password = PasswordInput.Password;
        _result = new LoginAccountSelection(
            AccountComboBox.Text.Trim(),
            string.IsNullOrEmpty(password) ? null : password,
            ConfirmEachTimeCheckBox.IsChecked == true,
            _logoffConflictingSession);
        PasswordInput.Clear();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _result = null;
        PasswordInput.Clear();
        Close();
    }
}
