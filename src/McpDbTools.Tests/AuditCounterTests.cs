using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Tests;

public class AuditCounterTests : IDisposable
{
    private readonly string _tempDir;

    public AuditCounterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbcounter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (IOptions<ConfigStoreOptions> options, string dbPath) CreateOptions()
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, "{\"databases\":{}}");
        return (Options.Create(new ConfigStoreOptions { ConfigPath = configPath }),
                Path.Combine(_tempDir, "audit.db"));
    }

    private static string TodayKey() => DateTime.Today.ToString("yyyy-MM-dd");

    [Fact]
    public void Load_OnEmptyDb_ReturnsZero()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());

        counter.Load();

        Assert.Equal(0, counter.TotalCurrent);
        Assert.Equal(0, counter.TodayCount);
        Assert.Equal(TodayKey(), counter.TodayDateKey);
    }

    [Fact]
    public void Increment_UpdatesMemoryAndPersists()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        string today = TodayKey();
        counter.Increment(today);
        counter.Increment(today);
        counter.Increment(today);

        Assert.Equal(3, counter.TotalCurrent);
        Assert.Equal(3, counter.TodayCount);

        // 持久化校验：新建实例从磁盘读
        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(3, counter2.TotalCurrent);
        Assert.Equal(3, counter2.TodayCount);
    }

    [Fact]
    public void Increment_Concurrent_IsThreadSafe()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        string today = TodayKey();
        const int Threads = 8;
        const int PerThread = 200;
        var tasks = new Task[Threads];
        int started = 0;
        using var mre = new ManualResetEventSlim(false);
        for (int t = 0; t < Threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                Interlocked.Increment(ref started);
                mre.Wait();
                for (int i = 0; i < PerThread; i++)
                {
                    counter.Increment(today);
                }
            });
        }
        while (Interlocked.CompareExchange(ref started, 0, 0) < Threads) { Thread.Yield(); }
        mre.Set();
        Task.WaitAll(tasks);

        long expected = (long)Threads * PerThread;
        Assert.Equal(expected, counter.TotalCurrent);
        Assert.Equal(expected, counter.TodayCount);

        // 重启一致性
        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(expected, counter2.TotalCurrent);
    }

    [Fact]
    public void ResetTotalToCount_SetsTotal_DailyUntouched()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        string today = TodayKey();
        counter.Increment(today);
        counter.Increment(today);
        Assert.Equal(2, counter.TotalCurrent);

        // 模拟清理后 audit_log 只剩 5 条
        using (var conn = counter.OpenConnection())
        {
            counter.ResetTotalToCount(conn, 5);
        }
        Assert.Equal(5, counter.TotalCurrent);

        // daily 不动
        Assert.Equal(2, counter.TodayCount);

        // 持久化校验
        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(5, counter2.TotalCurrent);
        Assert.Equal(2, counter2.TodayCount);
    }

    [Fact]
    public void Load_WhenTotalZeroAndAuditLogHasRows_BackfillsCount()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        // 直接建 audit_log 表并插入 3 行（不经 Increment，模拟老库历史）
        using (var conn = counter.OpenConnection())
        {
            using (var t = conn.CreateCommand())
            {
                t.CommandText = "CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL)";
                t.ExecuteNonQuery();
            }
            for (int i = 0; i < 3; i++)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO audit_log(time) VALUES (@t)";
                ins.Parameters.AddWithValue("@t", $"2026-01-0{i + 1}T00:00:00.000Z");
                ins.ExecuteNonQuery();
            }
        }

        // 新实例 Load：total=0 → COUNT audit_log=3 → 回填
        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(3, counter2.TotalCurrent);

        // 持久化校验：再起实例 total 已非 0，不再回填
        var counter3 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter3.Load();
        Assert.Equal(3, counter3.TotalCurrent);
    }

    [Fact]
    public void Load_WhenTotalZeroAndAuditLogEmpty_StaysZero()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        // 建空 audit_log 表
        using (var conn = counter.OpenConnection())
        using (var t = conn.CreateCommand())
        {
            t.CommandText = "CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL)";
            t.ExecuteNonQuery();
        }

        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        // COUNT=0 → total 保持 0（不写库）
        Assert.Equal(0, counter2.TotalCurrent);
    }

    [Fact]
    public void Load_WhenTotalNonZero_DoesNotTouchAuditLog()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        string today = TodayKey();
        for (int i = 0; i < 5; i++) counter.Increment(today);
        Assert.Equal(5, counter.TotalCurrent);

        // audit_log 插 2 行（与 total=5 不同，验证不被 COUNT 覆盖）
        using (var conn = counter.OpenConnection())
        {
            using (var t = conn.CreateCommand())
            {
                t.CommandText = "CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL)";
                t.ExecuteNonQuery();
            }
            for (int i = 0; i < 2; i++)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO audit_log(time) VALUES (@t)";
                ins.Parameters.AddWithValue("@t", $"2026-01-0{i + 1}T00:00:00.000Z");
                ins.ExecuteNonQuery();
            }
        }

        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(5, counter2.TotalCurrent); // 非 0 → 不回填，不被 COUNT=2 覆盖
    }

    [Fact]
    public void DecrementTotal_ReducesTotal_DailyUntouched()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        string today = TodayKey();
        for (int i = 0; i < 10; i++) counter.Increment(today);
        Assert.Equal(10, counter.TotalCurrent);

        using (var conn = counter.OpenConnection())
        {
            counter.DecrementTotal(conn, 3);
        }
        Assert.Equal(7, counter.TotalCurrent);
        Assert.Equal(10, counter.TodayCount); // daily 不动

        // 持久化校验
        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(7, counter2.TotalCurrent);
    }

    [Fact]
    public void DecrementTotal_ClampsToZero()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        string today = TodayKey();
        counter.Increment(today);
        counter.Increment(today);
        Assert.Equal(2, counter.TotalCurrent);

        using (var conn = counter.OpenConnection())
        {
            counter.DecrementTotal(conn, 100); // deleted > total
        }
        Assert.Equal(0, counter.TotalCurrent); // MAX(0, ...) clamp

        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(0, counter2.TotalCurrent);
    }

    [Fact]
    public void DecrementTotal_ZeroOrNegative_NoOp()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter.Load();

        string today = TodayKey();
        for (int i = 0; i < 5; i++) counter.Increment(today);

        using (var conn = counter.OpenConnection())
        {
            counter.DecrementTotal(conn, 0);
            counter.DecrementTotal(conn, -3);
        }
        Assert.Equal(5, counter.TotalCurrent);

        var counter2 = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        counter2.Load();
        Assert.Equal(5, counter2.TotalCurrent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理 */ }
    }
}
