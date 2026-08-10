using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace McpDbTools.Server.Logging;

/// <summary>
/// 按日期滚动的文件日志 provider。
/// <para>
/// 写入数据目录 logs/app-yyyyMMdd.txt，UTF-8 编码，线程安全（多 category 共享一把写锁）。
/// 托盘模式无控制台窗口，文件日志是唯一可见的运行时诊断通道。
/// </para>
/// <para>
/// 启动时按保留天数清理旧文件；清理失败不影响日志功能。
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly int _retentionDays;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();

    public FileLoggerProvider(string logDir, int retentionDays = 30)
    {
        _logDir = logDir;
        _retentionDays = retentionDays > 0 ? retentionDays : 30;
        Directory.CreateDirectory(_logDir);
        PurgeOldLogs();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _logDir, _writeLock));

    public void Dispose()
    {
        // 无需释放：FileLogger 每次写入即开即关，不持有常驻句柄
    }

    /// <summary>删除早于保留天数的 app-yyyyMMdd.txt 文件。文件名无法解析日期的跳过。</summary>
    private void PurgeOldLogs()
    {
        try
        {
            DateTime cutoff = DateTime.Today.AddDays(-_retentionDays);
            const string prefix = "app-";
            foreach (string file in Directory.EnumerateFiles(_logDir, "app-*.txt"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != prefix.Length + 8 || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (DateTime.TryParseExact(name.AsSpan(prefix.Length), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime date) && date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不阻断日志写入
        }
    }
}

/// <summary>
/// 单 category 的文件日志器。每次写入以 append 方式打开当日文件，UTF-8 落盘。
/// 多实例共享 provider 传入的写锁，保证同日文件并发追加安全。
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly string _logDir;
    private readonly object _writeLock;

    internal FileLogger(string category, string logDir, object writeLock)
    {
        _category = category;
        _logDir = logDir;
        _writeLock = writeLock;
    }

    // 框架已按配置的最低级别过滤，provider 不再二次裁剪，避免丢失低于 Information 的诊断。
    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        string path = Path.Combine(_logDir, $"app-{DateTime.Today:yyyyMMdd}.txt");
        lock (_writeLock)
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}

/// <summary>FileLoggerProvider 的 DI 注册扩展。</summary>
public static class FileLoggerExtensions
{
    /// <summary>启用按日滚动的文件日志，写入 <paramref name="logDir"/>，默认保留 30 天。</summary>
    public static ILoggingBuilder AddDailyFile(this ILoggingBuilder builder, string logDir, int retentionDays = 30)
    {
        builder.AddProvider(new FileLoggerProvider(logDir, retentionDays));
        return builder;
    }
}
