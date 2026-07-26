using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Audit;

/// <summary>
/// 审计日志持久化计数器：总数 + 按本地日。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>独立累加，平时不依赖 SELECT COUNT(*)（仅清理重置与 UI 对账显示时用）。</item>
/// <item>应写语义：Log() 入队前 Increment（与审计 INSERT 解耦），进程在队列未消费时崩溃也能被对账发现。</item>
/// <item>多线程安全：内存 Interlocked 累加；持久化 UPDATE 在锁内串行。</item>
/// <item>SQLite WAL + 默认 synchronous=FULL：UPDATE commit 返回即落盘。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AuditCounter
{
    private readonly string _connectionString;
    private readonly ILogger<AuditCounter> _logger;
    private readonly object _persistLock = new();

    private long _total;            // 内存镜像
    private long _todayCount;       // 内存镜像
    private string _todayDateKey = DateTime.Today.ToString("yyyy-MM-dd");

    public AuditCounter(IOptions<ConfigStoreOptions> options, ILogger<AuditCounter> logger)
    {
        // audit.db 路径与 AuditLogger 完全一致（同目录、同文件）
        string dir = DataDirectoryResolver.EnsureExists(options.Value.ConfigPath);
        string dbPath = Path.Combine(dir, "audit.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _logger = logger;
    }

    public long TotalCurrent => Interlocked.Read(ref _total);
    public long TodayCount => Interlocked.Read(ref _todayCount);
    public string TodayDateKey => Volatile.Read(ref _todayDateKey);

    /// <summary>建表（幂等）+ 从持久化表加载内存快照。AuditLogger.EnsureInitialized 后调用一次。</summary>
    public void Load()
    {
        lock (_persistLock)
        {
            using var connection = OpenConnection();
            EnsureTables(connection);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT total FROM audit_counter_total WHERE id = 1";
                var v = cmd.ExecuteScalar();
                Interlocked.Exchange(ref _total, v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture));
            }

            // 同步本地今日 key 与 today 计数
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            Volatile.Write(ref _todayDateKey, today);
            Interlocked.Exchange(ref _todayCount, ReadDaily(connection, today));
        }
    }

    private static long ReadDaily(SqliteConnection connection, string dateKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count FROM audit_counter_daily WHERE date = @date";
        cmd.Parameters.AddWithValue("@date", dateKey);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static void EnsureTables(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        using var t1 = connection.CreateCommand();
        t1.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_counter_total (
                id    INTEGER PRIMARY KEY,
                total INTEGER NOT NULL DEFAULT 0
            )
            """;
        t1.ExecuteNonQuery();

        // 单行占位（id=1），幂等
        using var seed = connection.CreateCommand();
        seed.CommandText = "INSERT OR IGNORE INTO audit_counter_total(id, total) VALUES(1, 0)";
        seed.ExecuteNonQuery();

        using var t2 = connection.CreateCommand();
        t2.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_counter_daily (
                date  TEXT PRIMARY KEY,
                count INTEGER NOT NULL DEFAULT 0
            )
            """;
        t2.ExecuteNonQuery();
    }

    internal SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=3000;";
        busy.ExecuteNonQuery();
        return connection;
    }
}
