using System.IO;
using Microsoft.Extensions.Logging;

namespace DeskClone.Logging;

/// <summary>
/// 写入程序目录 log\deskclone.log 的极简文件日志。
/// 日志超过 5MB 时直接截断重写，避免长期运行占满磁盘。
/// </summary>
public sealed class FileLogger<T> : ILogger<T>
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var path = ResolveLogPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {typeof(T).Name}: {formatter(state, exception)}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    File.WriteAllText(path, line + Environment.NewLine);
                }
                else
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 日志失败必须被吞掉，绝不能影响功能本身。
        }
    }

    private static string ResolveLogPath()
    {
        var localDir = Path.Combine(AppContext.BaseDirectory, "log");
        try
        {
            Directory.CreateDirectory(localDir);
            using var probe = new FileStream(
                Path.Combine(localDir, "deskclone.log"),
                FileMode.OpenOrCreate,
                FileAccess.Write);
            return Path.Combine(localDir, "deskclone.log");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskClone",
                "deskclone.log");
        }
    }
}
