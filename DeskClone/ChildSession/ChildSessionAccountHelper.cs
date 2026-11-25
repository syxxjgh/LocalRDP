using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DeskClone.ChildSession;

/// <summary>
/// 桌面分身登录账户的辅助工具：识别「与当前主桌面相同的 Windows 账户」。
///
/// 桌面分身必须登录一个与当前主桌面不同的账户。如果 RDP 没有被明确指定登录账户，
/// 它会沿用当前用户的默认凭据（CredSSP 单点登录），最终分身会话与主桌面登录同一个账户，
/// 导致登录状态冲突。这里提供账户解析、同账户判定和候选账户枚举。
/// </summary>
internal static class ChildSessionAccountHelper
{
    private const int NameUserPrincipal = 8;
    private const int FilterNormalAccount = 0x0002;
    private const uint UserAccountDisabledFlag = 0x0002;
    private const int MaxPreferredLength = -1;

    private static readonly string[] BuiltInAccountNames =
    [
        "Guest",
        "DefaultAccount",
        "WDAGUtilityAccount"
    ];

    internal readonly record struct ParsedAccount(
        string Domain,
        string UserName,
        string NormalizedAccount);

    internal static string GetCurrentWindowsAccountName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    internal static SecurityIdentifier? GetCurrentWindowsSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    internal static string? TryGetCurrentUserPrincipalName()
    {
        try
        {
            var size = 256;
            var builder = new StringBuilder(size);
            if (GetUserNameEx(NameUserPrincipal, builder, ref size))
            {
                var value = builder.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException
                                              or COMException)
        {
            // UPN 只用于辅助比对，读取失败时忽略。
        }

        return null;
    }

    /// <summary>
    /// 解析用户输入的账户：支持 <c>用户名</c>、<c>域或计算机\用户名</c>、<c>.\用户名</c>、<c>user@domain</c>。
    /// </summary>
    internal static ParsedAccount ParseAccount(string accountInput)
    {
        var input = accountInput?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            throw new ArgumentException("登录账户不能为空。", nameof(accountInput));
        }

        var separatorIndex = input.IndexOf('\\');
        if (separatorIndex >= 0)
        {
            var domain = input[..separatorIndex].Trim();
            var user = input[(separatorIndex + 1)..].Trim();
            if (user.Length == 0)
            {
                throw new ArgumentException("登录账户缺少用户名。", nameof(accountInput));
            }

            if (domain.Length == 0 || domain == ".")
            {
                domain = Environment.MachineName;
            }

            return new ParsedAccount(domain, user, $"{domain}\\{user}");
        }

        if (input.Contains('@'))
        {
            return new ParsedAccount(string.Empty, input, input);
        }

        return new ParsedAccount(
            Environment.MachineName,
            input,
            $"{Environment.MachineName}\\{input}");
    }

    /// <summary>
    /// 判断给定账户是否就是当前主桌面正在使用的 Windows 账户。
    /// 优先按 SID 比较，能正确识别 <c>.\user</c>、<c>MACHINE\user</c>、微软账户邮箱等不同写法。
    /// </summary>
    internal static bool IsCurrentWindowsUser(string? accountInput)
    {
        if (string.IsNullOrWhiteSpace(accountInput))
        {
            return false;
        }

        var input = accountInput.Trim();
        var currentSid = GetCurrentWindowsSid();
        if (currentSid is not null)
        {
            foreach (var candidate in EnumerateLookupCandidates(input))
            {
                if (TryTranslateToSid(candidate) is { } sid && sid.Equals(currentSid))
                {
                    return true;
                }
            }
        }

        var bareName = input;
        var separatorIndex = input.LastIndexOf('\\');
        if (separatorIndex >= 0)
        {
            bareName = input[(separatorIndex + 1)..];
        }

        var currentName = GetCurrentWindowsAccountName();
        var currentBareName = currentName.Contains('\\')
            ? currentName[(currentName.LastIndexOf('\\') + 1)..]
            : currentName;
        var userPrincipalName = TryGetCurrentUserPrincipalName();

        if (string.Equals(input, currentName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(input, Environment.UserName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(input, userPrincipalName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 带有明确域/计算机名的输入只在域名也匹配时才视为同一账户，避免把同名的其他域账户误判。
        var hasExplicitDomain = separatorIndex > 0
                                && !input.StartsWith(".\\", StringComparison.Ordinal);
        if (!hasExplicitDomain)
        {
            return string.Equals(bareName, currentBareName, StringComparison.OrdinalIgnoreCase);
        }

        var inputDomain = input[..separatorIndex];
        var currentDomain = currentName.Contains('\\')
            ? currentName[..currentName.LastIndexOf('\\')]
            : Environment.MachineName;
        return string.Equals(inputDomain, currentDomain, StringComparison.OrdinalIgnoreCase)
               && string.Equals(bareName, currentBareName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 枚举本机已启用的普通本地账户（不含当前主桌面账户和系统内置账户），格式为 <c>计算机名\用户名</c>。
    /// </summary>
    internal static IReadOnlyList<string> GetCandidateLocalAccounts()
    {
        var result = new List<string>();
        var buffer = IntPtr.Zero;
        try
        {
            var status = NetUserEnum(
                null,
                1,
                FilterNormalAccount,
                out buffer,
                MaxPreferredLength,
                out var entriesRead,
                out _,
                IntPtr.Zero);
            if (status != 0 || buffer == IntPtr.Zero)
            {
                return result;
            }

            var entrySize = Marshal.SizeOf<UserInfo1>();
            for (var index = 0; index < entriesRead; index++)
            {
                var entry = Marshal.PtrToStructure<UserInfo1>(buffer + index * entrySize);
                if ((entry.Flags & UserAccountDisabledFlag) != 0)
                {
                    continue;
                }

                var name = Marshal.PtrToStringUni(entry.Name);
                if (string.IsNullOrWhiteSpace(name)
                    || Array.Exists(
                        BuiltInAccountNames,
                        builtIn => string.Equals(builtIn, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var fullName = $"{Environment.MachineName}\\{name}";
                if (IsCurrentWindowsUser(fullName))
                {
                    continue;
                }

                result.Add(fullName);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException
                                              or COMException
                                              or SEHException
                                              or ArgumentException)
        {
            result.Clear();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }

        return result;
    }

    /// <summary>
    /// 读取系统 RDP 客户端为 localhost 记录的最近登录用户名提示。
    /// </summary>
    internal static string? TryGetRdpLocalhostUsernameHint()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(
                @"Software\Microsoft\Terminal Server Client\Servers\localhost");
            var hint = key?.GetValue("UsernameHint") as string;
            return string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 为登录对话框推荐一个初始账户，保证推荐结果不是当前主桌面账户。
    /// </summary>
    internal static string SuggestLoginAccount(
        string? savedAccount,
        string? existingSessionAccount,
        IReadOnlyList<string> candidates)
    {
        string?[] priorities =
        [
            existingSessionAccount,
            savedAccount,
            TryGetRdpLocalhostUsernameHint(),
            candidates.Count > 0 ? candidates[0] : null
        ];

        foreach (var account in priorities)
        {
            if (!string.IsNullOrWhiteSpace(account) && !IsCurrentWindowsUser(account))
            {
                return account.Trim();
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string input)
    {
        yield return input;

        string? normalized = null;
        try
        {
            normalized = ParseAccount(input).NormalizedAccount;
        }
        catch (ArgumentException)
        {
            // 非法输入不参与规范化比对。
        }

        if (normalized is not null
            && !string.Equals(normalized, input, StringComparison.OrdinalIgnoreCase))
        {
            yield return normalized;
        }
    }

    private static SecurityIdentifier? TryTranslateToSid(string account)
    {
        try
        {
            return new NTAccount(account).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception exception) when (exception is IdentityNotMappedException
                                              or SystemException
                                              or ArgumentException)
        {
            return null;
        }
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameEx(
        int nameFormat,
        StringBuilder userName,
        ref int size);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(
        string? serverName,
        int level,
        int filter,
        out IntPtr buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo1
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint PasswordAge;
        public uint Privilege;
        public IntPtr HomeDirectory;
        public IntPtr Comment;
        public uint Flags;
        public IntPtr ScriptPath;
    }
}
