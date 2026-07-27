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

            // 零值兜底：total 为 0 时启动回填为 audit_log COUNT（老库升级 / counter 表新建但已有历史日志）。
            // 初始全新部署 audit_log 也空 → COUNT=0 → 保持 0，不写库，不盲目同步。
            // audit_log 表在 AuditLogger.EnsureInitialized 中先于此处建立；独立测试场景未建表时 catch 视同 COUNT=0。
            // 并发安全：本块在 _persistLock 内，与 Increment 持久化段串行；内存 Exchange 与 Increment 的 Interlocked 竞态最差偏差 1，下次重启 Load 校正。
            if (Interlocked.Read(ref _total) == 0)
            {
                long logCount = 0;
                try
                {
                    using var logCmd = connection.CreateCommand();
                    logCmd.CommandText = "SELECT COUNT(*) FROM audit_log";
                    var cv = logCmd.ExecuteScalar();
                    logCount = cv is null or DBNull ? 0 : Convert.ToInt64(cv, CultureInfo.InvariantCulture);
                }
                catch (SqliteException)
                {
                    // audit_log 表尚未建立（独立 AuditCounter 测试 / 未经 AuditLogger 初始化）→ 视同空库
                    logCount = 0;
                }
                if (logCount > 0)
                {
                    using var upd = connection.CreateCommand();
                    upd.CommandText = "UPDATE audit_counter_total SET total = @total WHERE id = 1";
                    upd.Parameters.AddWithValue("@total", logCount);
                    upd.ExecuteNonQuery();
                    Interlocked.Exchange(ref _total, logCount);
                    _logger.LogInformation("审计计数器 total 为 0，启动时回填为 audit_log COUNT = {Total}", logCount);
                }
            }

            // 同步本地今日 key 与 today 计数
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            Volatile.Write(ref _todayDateKey, today);
            Interlocked.Exchange(ref _todayCount, ReadDaily(connection, today));
        }
    }

    /// <summary>
    /// 应写计数 +1：内存 Interlocked 累加 + 同步持久化 UPDATE。
    /// <para>在 AuditLogger.Log() 入队前调用。崩溃在 Increment 后入队前 → 计数器有、库没有 → 对账发现丢。</para>
    /// <para>持久化失败：catch + 记 Error，内存仍累加（方案 i）。</para>
    /// </summary>
    public void Increment(string localDateKey)
    {
        // 内存累加（无锁快路径）
        Interlocked.Increment(ref _total);

        // 跨日处理：先确保 today key 与今日计数对齐，再累加今日
        string current = Volatile.Read(ref _todayDateKey);
        if (localDateKey != current)
        {
            EnsureToday(localDateKey);
        }
        Interlocked.Increment(ref _todayCount);

        // 同步持久化（锁内串行，SQLite 单连接非线程安全）
        try
        {
            lock (_persistLock)
            {
                using var connection = OpenConnection();
                EnsureTables(connection);
                using var tx = connection.BeginTransaction();
                using (var cmdTotal = connection.CreateCommand())
                {
                    cmdTotal.CommandText = "UPDATE audit_counter_total SET total = total + 1 WHERE id = 1";
                    cmdTotal.ExecuteNonQuery();
                }
                using (var cmdDaily = connection.CreateCommand())
                {
                    cmdDaily.CommandText = """
                        INSERT INTO audit_counter_daily(date, count) VALUES(@date, 1)
                        ON CONFLICT(date) DO UPDATE SET count = count + 1
                        """;
                    cmdDaily.Parameters.AddWithValue("@date", localDateKey);
                    cmdDaily.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            // 方案 i：内存已加、持久化未加；不影响审计主流程，但必须上报
            _logger.LogError(ex, "审计计数器持久化失败（内存计数仍已累加）");
        }
    }

    /// <summary>跨日时重置内存今日 key 与今日计数（从持久化表读今日行）。</summary>
    private void EnsureToday(string dateKey)
    {
        lock (_persistLock)
        {
            if (Volatile.Read(ref _todayDateKey) == dateKey)
            {
                return; // 双检：并发已切换
            }
            using var connection = OpenConnection();
            EnsureTables(connection);
            // 先就位今日计数，再发布新 dateKey：避免并发 Increment 读到新 key 后 +1 被此处 Exchange 抹掉
            long loaded = ReadDaily(connection, dateKey);
            Interlocked.Exchange(ref _todayCount, loaded);
            Volatile.Write(ref _todayDateKey, dateKey);
        }
    }

    /// <summary>
    /// 设定 total 为指定绝对值。daily 不动。
    /// <para>当前内置调用方已切换为 <see cref="DecrementTotal"/>（增量扣减），本方法作为公共 API 保留供外部/测试复用。</para>
    /// <para>调用方传入复用的连接（与 DELETE 同连接），避免多连接写锁冲突。内存与持久化同步。</para>
    /// </summary>
    public void ResetTotalToCount(SqliteConnection connection, long count)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE audit_counter_total SET total = @total WHERE id = 1";
        cmd.Parameters.AddWithValue("@total", count);
        cmd.ExecuteNonQuery();
        Interlocked.Exchange(ref _total, count);
        _logger.LogInformation("审计计数器 total 已重置为 {Total}", count);
    }

    /// <summary>
    /// 清理（自动/手动）后按本次删除条数增量扣减 total。daily 不动。
    /// <para>调用方传入复用的连接（与 DELETE 同连接），避免多连接写锁冲突。</para>
    /// <para>持久化使用 MAX(0, total - @n) 防止负数；执行后读回真实值同步内存镜像。</para>
    /// <para>n &lt;= 0 时为无操作，避免 deleted=0 的清理改动计数器。</para>
    /// </summary>
    public void DecrementTotal(SqliteConnection connection, long n)
    {
        if (n <= 0)
        {
            return;
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE audit_counter_total SET total = MAX(0, total - @n) WHERE id = 1";
            cmd.Parameters.AddWithValue("@n", n);
            cmd.ExecuteNonQuery();
        }
        long newVal;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT total FROM audit_counter_total WHERE id = 1";
            newVal = Convert.ToInt64(read.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }
        Interlocked.Exchange(ref _total, newVal);
        _logger.LogInformation("审计计数器 total 增量扣减 {Decrement}（清理后），新值 {Total}", n, newVal);
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
