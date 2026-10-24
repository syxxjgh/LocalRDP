using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace DeskClone.Core;

/// <summary>
/// 设置的加载与落盘。修改通过 <see cref="QueueSave"/> 防抖写入，
/// 应用退出时再强制保存一次。所有调用都发生在 UI 线程。
/// </summary>
public sealed class SettingsService
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _settingsPath;
    private readonly DispatcherTimer? _saveTimer;

    public AppSettings Settings { get; }

    public SettingsService()
    {
        _settingsPath = ResolveSettingsPath();
        Settings = Load() ?? new AppSettings();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SaveDebounce
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
    }

    /// <summary>防抖保存：短时间内的多次修改只会写一次盘。</summary>
    public void QueueSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    /// <summary>立即保存。失败时静默忽略，设置丢失不应让应用崩溃。</summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 磁盘只读或权限不足时放弃保存，继续使用内存中的设置。
        }
    }

    private AppSettings? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 优先把设置放在程序目录（便携模式）；目录不可写时退回 %APPDATA%。
    /// 探针只打开文件验证可写性，绝不截断已有内容。
    /// </summary>
    private static string ResolveSettingsPath()
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        try
        {
            using var stream = new FileStream(localPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            return localPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var roamingPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskClone",
                "settings.json");
            return roamingPath;
        }
    }
}
