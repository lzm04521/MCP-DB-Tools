using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

public class AuditLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public AuditLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>在临时目录构造 ConfigStore + AuditLogger（db 文件落在 config.json 同目录）。</summary>
    private (ConfigStore store, AuditLogger logger, AuditCounter counter, string dbPath) Create(int channelCapacity = 1000)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string json = """
        {
          "databases": {}
        }
        """;
        File.WriteAllText(configPath, json);

        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        var logger = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), counter, channelCapacity);
        return (store, logger, counter, Path.Combine(_tempDir, "audit.db"));
    }

    /// <summary>以「当前 UTC 往前 daysAgo 天」生成 ISO 时间，保证各条记录时间互不相同、可稳定排序。</summary>
    private static string Iso(int daysAgo) => DateTime.UtcNow.AddDays(-daysAgo)
        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Log_AlwaysWrites_GlobalAuditOn()
    {
        // 需求 3：审计全局开启，不再依赖开关。任何 Log 调用都应写入。
        var (store, logger, _, dbPath) = Create();
        using (store)
        {
            logger.Log(new AuditEntry
            {
                Project = "erp",
                Environment = "prod",
                DatabaseType = "SqlServer",
                Sql = "SELECT 1",
                RowCount = 5,
                ElapsedMs = 12,
                Success = true
            });
            logger.Flush();
        }

        Assert.True(File.Exists(dbPath));
        var page = logger.Query(new AuditLogQuery());
        Assert.Equal(1, page.Total);
        AuditEntry entry = Assert.Single(page.Items);
        Assert.Equal("erp", entry.Project);
        Assert.Equal("prod", entry.Environment);
        Assert.Equal("SqlServer", entry.DatabaseType);
        Assert.Equal("SELECT 1", entry.Sql);
        Assert.Equal(5, entry.RowCount);
        Assert.Equal(12, entry.ElapsedMs);
        Assert.True(entry.Success);
        Assert.Null(entry.Error);
        Assert.NotEmpty(entry.Time);
    }

    [Fact]
    public void MultipleEntries_AreOrderedByTimeDescending()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true, time: Iso(3)));
            logger.Log(MakeEntry("SELECT 2", true, time: Iso(1)));
            logger.Log(MakeEntry("DROP x", false, error: "blocked", time: Iso(2)));
            logger.Flush();
        }

        var page = logger.Query(new AuditLogQuery());
        Assert.Equal(3, page.Total);
        Assert.Equal("SELECT 2", page.Items[0].Sql); // 最新在前
        Assert.Equal("DROP x", page.Items[1].Sql);
        Assert.False(page.Items[1].Success);
        Assert.Equal("blocked", page.Items[1].Error);
        Assert.Equal("SELECT 1", page.Items[2].Sql);
    }

    [Fact]
    public void Query_FiltersByProjectAndSuccess()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true, project: "erp"));
            logger.Log(MakeEntry("DROP x", false, project: "erp", error: "blocked"));
            logger.Log(MakeEntry("SELECT 2", true, project: "crm"));
            logger.Flush();
        }

        Assert.Equal(2, logger.Query(new AuditLogQuery { Project = "erp" }).Total);
        Assert.Equal(1, logger.Query(new AuditLogQuery { Project = "crm" }).Total);
        Assert.Equal(2, logger.Query(new AuditLogQuery { Success = true }).Total);
        Assert.Equal(1, logger.Query(new AuditLogQuery { Success = false }).Total);
        Assert.Equal(1, logger.Query(new AuditLogQuery { Project = "erp", Success = false }).Total);
    }

    [Fact]
    public void Query_FiltersByTimeRange()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("A", true, time: Iso(5)));
            logger.Log(MakeEntry("B", true, time: Iso(3)));
            logger.Log(MakeEntry("C", true, time: Iso(1)));
            logger.Flush();
        }

        var mid = logger.Query(new AuditLogQuery
        {
            FromTime = Iso(4),
            ToTime = Iso(2)
        });
        Assert.Equal(1, mid.Total);
        Assert.Equal("B", mid.Items[0].Sql);
    }

    [Fact]
    public void Query_FiltersBySqlContains_CaseInsensitive()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT * FROM Users", true));
            logger.Log(MakeEntry("select id from orders", true));
            logger.Log(MakeEntry("DELETE FROM t", false, error: "x"));
            logger.Log(MakeEntry("100%off", true));
            logger.Flush();
        }

        Assert.Equal(2, logger.Query(new AuditLogQuery { SqlContains = "select" }).Total);
        Assert.Equal(1, logger.Query(new AuditLogQuery { SqlContains = "users" }).Total);
        // 含通配符 % _ 应被转义，按字面匹配
        Assert.Equal(1, logger.Query(new AuditLogQuery { SqlContains = "100%off" }).Total);
    }

    [Fact]
    public void Query_PaginationWorks()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            for (int i = 0; i < 7; i++)
            {
                // 每个 i 用不同的偏移天数，保证时间互不相同、可稳定排序
                logger.Log(MakeEntry($"SELECT {i}", true, time: Iso(7 - i)));
            }
            logger.Flush();
        }

        var page1 = logger.Query(new AuditLogQuery { Page = 1, PageSize = 3 });
        Assert.Equal(7, page1.Total);
        Assert.Equal(3, page1.Items.Count);
        Assert.Equal("SELECT 6", page1.Items[0].Sql); // 倒序，最新在前

        var page3 = logger.Query(new AuditLogQuery { Page = 3, PageSize = 3 });
        Assert.Single(page3.Items);
        Assert.Equal("SELECT 0", page3.Items[0].Sql);

        // 非法页码被归一化为 1，仍能查到全部
        Assert.Equal(7, logger.Query(new AuditLogQuery { Page = 0 }).Total);
    }

    [Fact]
    public void Query_NormalizesPageSize()
    {
        // 5000 以内合法值保留原值；超出 5000 归一化为 50
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
            logger.Flush();
        }

        // 5000 合法：原值保留
        Assert.Equal(5000, logger.Query(new AuditLogQuery { Page = 1, PageSize = 5000 }).PageSize);
        // 超出 5000：归一化为 50
        Assert.Equal(50, logger.Query(new AuditLogQuery { Page = 1, PageSize = 99999 }).PageSize);
    }

    [Fact]
    public void Log_DoesNotThrow_OnUnusualValues()
    {
        // 验证参数化写入对特殊字符、空错误等安全
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 'a''b'", Success = true });
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 1; DROP x", Success = false, Error = "blocked 'x'" });
            logger.Flush();
        }

        Assert.Equal(2, logger.Query(new AuditLogQuery()).Total);
        Assert.Equal("SELECT 'a''b'", logger.Query(new AuditLogQuery { Success = true }).Items[0].Sql);
        Assert.Equal("blocked 'x'", logger.Query(new AuditLogQuery { Success = false }).Items[0].Error);
    }

    [Fact]
    public void DeleteOlderThan_RemovesOldKeepsNew()
    {
        // 用 1/4/10 天偏移构造新旧记录，便于验证 DeleteOlderThan 按天数删除的行为
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("old", true, time: Iso(10)));   // 10 天前
            logger.Log(MakeEntry("mid", true, time: Iso(4)));    // 4 天前
            logger.Log(MakeEntry("new", true, time: Iso(1)));    // 1 天前
            logger.Flush();

            Assert.Equal(3, logger.Query(new AuditLogQuery()).Total);

            // 删除 5 天前：只剩 mid(4) 和 new(1)
            int deleted = logger.DeleteOlderThan(5);
            Assert.Equal(1, deleted);
            var after = logger.Query(new AuditLogQuery { PageSize = 100 });
            Assert.Equal(2, after.Total);
            Assert.DoesNotContain(after.Items, i => i.Sql == "old");
        }
    }

    [Fact]
    public void DeleteOlderThan_RejectsNonPositiveDays()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            Assert.Throws<ArgumentException>(() => logger.DeleteOlderThan(0));
            Assert.Throws<ArgumentException>(() => logger.DeleteOlderThan(-5));
        }
    }

    [Fact]
    public void Query_Counters_NormalPath_CounterEqualsPersisted()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
            logger.Log(MakeEntry("SELECT 2", true));
            logger.Flush(); // 等消费者落盘
        }

        var page = logger.Query(new AuditLogQuery());
        Assert.Equal(2, page.Counters.TodayCounter);
        Assert.Equal(2, page.Counters.TodayPersisted);
        Assert.Equal(2, page.Counters.TotalCounter);
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), page.Counters.TodayDateKey);
    }

    [Fact]
    public void Query_Counters_DetectsLoss_WhenCounterAheadOfPersisted()
    {
        // 验证对账能反映"应写未落盘"差值：counter 领先于 audit_log 实际落盘数
        var (store, logger, counter, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
            logger.Flush(); // 确定落盘：audit_log 今日=1，counter 今日=1

            // 手动注入一条"应写未落盘"：counter+1，audit_log 不变
            counter.Increment(DateTime.Today.ToString("yyyy-MM-dd"));

            var page = logger.Query(new AuditLogQuery());
            Assert.Equal(2, page.Counters.TodayCounter);   // counter 领先
            Assert.Equal(1, page.Counters.TodayPersisted); // 实际落盘
        }
    }

    [Fact]
    public void DeleteOlderThan_DecrementsCounter_NotCountReset()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("old", true, time: Iso(10)));
            logger.Log(MakeEntry("mid", true, time: Iso(4)));
            logger.Log(MakeEntry("new", true, time: Iso(1)));
            logger.Flush();

            // 清理前 total=3
            Assert.Equal(3, logger.Query(new AuditLogQuery()).Counters.TotalCounter);

            logger.DeleteOlderThan(5); // 删 old（1 条），剩 2

            var page = logger.Query(new AuditLogQuery());
            Assert.Equal(2, page.Counters.TotalCounter); // 增量扣减：3 - 1 = 2（非全表 COUNT 对齐）
            // daily 不动：仍为 3（今日 counter 不参与清理扣减）
            Assert.Equal(3, page.Counters.TodayCounter);
        }
    }

    [Fact]
    public void Load_BackfillsCounterWhenZeroAndLogHasRows()
    {
        // 老库升级场景：audit_log 已有历史行、counter_total.total=0
        // → 首次 Query 触发 EnsureInitialized（含 counter.Load）→ total=0 回填为 audit_log COUNT
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, "{\"databases\":{}}");
        string dbPath = Path.Combine(_tempDir, "audit.db");

        // 预置 audit_log 3 行（不经 AuditLogger，模拟老库历史；schema 与 EnsureInitialized 一致）
        using (var raw = new SqliteConnection($"Data Source={dbPath}"))
        {
            raw.Open();
            using (var t = raw.CreateCommand())
            {
                t.CommandText = """
                    CREATE TABLE audit_log (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        time TEXT NOT NULL,
                        project TEXT NOT NULL,
                        environment TEXT NOT NULL,
                        database_type TEXT NOT NULL,
                        sql TEXT NOT NULL,
                        row_count INTEGER NOT NULL DEFAULT 0,
                        elapsed_ms INTEGER NOT NULL DEFAULT 0,
                        success INTEGER NOT NULL DEFAULT 0,
                        error TEXT
                    )
                    """;
                t.ExecuteNonQuery();
            }
            for (int i = 0; i < 3; i++)
            {
                using var ins = raw.CreateCommand();
                ins.CommandText = "INSERT INTO audit_log(time,project,environment,database_type,sql) VALUES (@t,@p,@e,@d,@s)";
                ins.Parameters.AddWithValue("@t", Iso(10 - i));
                ins.Parameters.AddWithValue("@p", "erp");
                ins.Parameters.AddWithValue("@e", "prod");
                ins.Parameters.AddWithValue("@d", "SqlServer");
                ins.Parameters.AddWithValue("@s", $"SELECT {i}");
                ins.ExecuteNonQuery();
            }
        }

        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());
        var logger = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), counter);

        // 首次 Query 触发 EnsureInitialized → counter.Load → total=0 回填为 audit_log COUNT=3
        var page = logger.Query(new AuditLogQuery());
        Assert.Equal(3, counter.TotalCurrent);
        Assert.Equal(3, page.Counters.TotalCounter);
    }

    [Fact]
    public void Increment_CrossDay_RolloverTodayCount()
    {
        // 直接验证 AuditCounter 跨日（AuditLogger.Log 内部转 localDateKey）
        var (store, logger, counter, _) = Create();
        using (store)
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            string yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
            counter.Increment(today);
            counter.Increment(today);
            counter.Increment(yesterday); // 触发跨日

            Assert.Equal(3, counter.TotalCurrent);
            Assert.Equal(1, counter.TodayCount); // 切到昨日，今日计数=昨日行
            Assert.Equal(yesterday, counter.TodayDateKey);
        }
    }

    [Fact]
    public void Log_PassesTodayLocalKey_ToCounter()
    {
        // 验证 AuditLogger.Log 内部把 DateTime.Today.ToString("yyyy-MM-dd") 传给 counter
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
            logger.Flush();
        }
        var page = logger.Query(new AuditLogQuery());
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), page.Counters.TodayDateKey);
    }

    [Fact]
    public void Log_SingleEntry_CounterNotDoubleCount()
    {
        // C1 回归保护：正常路径单条 Log 不应 double-count（counter == audit_log 条数）
        var (store, logger, counter, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
            logger.Flush();
            Assert.Equal(1, counter.TotalCurrent);   // 恰好 1，不多
            Assert.Equal(1, counter.TodayCount);
        }
    }

    private static AuditEntry MakeEntry(string sql, bool success, string project = "p",
        string? error = null, string? time = null) => new()
    {
        Time = time ?? AuditLogger.NowUtcIso(),
        Project = project,
        Environment = "prod",
        DatabaseType = "SqlServer",
        Sql = sql,
        RowCount = success ? 5 : 0,
        ElapsedMs = 10,
        Success = success,
        Error = success ? null : error
    };

    /// <summary>构造带 ResultJson 的成功 AuditEntry，专供子表写入测试。</summary>
    private static AuditEntry MakeEntryWithResult(string sql, string resultJson, string? time = null) => new()
    {
        Time = time ?? AuditLogger.NowUtcIso(),
        Project = "p",
        Environment = "prod",
        DatabaseType = "SqlServer",
        Sql = sql,
        RowCount = 3,
        ElapsedMs = 10,
        Success = true,
        ResultJson = resultJson
    };

    // ============ 查询结果记录（audit_log_result 子表）============

    [Fact]
    public void SerializeResult_ProducesExpectedShape()
    {
        // columns + rows(含 null，模拟 provider 读取时已把 DBNull 转为 null 的真实路径)
        // → JSON 结构正确，字段名 camelCase，null 保持 JSON null
        var result = new QueryResult
        {
            Columns = new List<string> { "a", "b" },
            Rows = new List<object?[]> { new object?[] { 1, null } }
        };
        string json = AuditLogger.SerializeResult(result);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("a", root.GetProperty("columns")[0].GetString());
        Assert.Equal("b", root.GetProperty("columns")[1].GetString());
        Assert.Equal(1, root.GetProperty("rows")[0][0].GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("rows")[0][1].ValueKind);
    }

    [Fact]
    public void Log_WritesResultJson_WhenProvided()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntryWithResult("SELECT 1", "{\"columns\":[\"x\"],\"rows\":[]}"));
            logger.Flush();
        }

        // 经 Query 拿到 id，再经 GetResultJson 验证子表落盘
        var page = logger.Query(new AuditLogQuery());
        AuditEntry entry = Assert.Single(page.Items);
        Assert.Equal("{\"columns\":[\"x\"],\"rows\":[]}", logger.GetResultJson(entry.Id));
    }

    [Fact]
    public void Log_SkipsResultTable_WhenResultJsonNull()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));   // MakeEntry 不带 ResultJson → null
            logger.Flush();
        }

        var page = logger.Query(new AuditLogQuery());
        AuditEntry entry = Assert.Single(page.Items);
        // 子表无该记录 → GetResultJson 返回 null
        Assert.Null(logger.GetResultJson(entry.Id));
    }

    [Fact]
    public void GetResultJson_ReturnsJson_WhenExists()
    {
        var (store, logger, _, _) = Create();
        const string payload = "{\"columns\":[\"id\"],\"rows\":[[1],[2]]}";
        using (store)
        {
            logger.Log(MakeEntryWithResult("SELECT id FROM t", payload));
            logger.Flush();
        }

        long id = logger.Query(new AuditLogQuery()).Items[0].Id;
        // 逐字节相等
        Assert.Equal(payload, logger.GetResultJson(id));
    }

    [Fact]
    public void GetResultJson_ReturnsNull_WhenMissing()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
            logger.Flush();
        }

        // 老/未写入场景：返回 null（老记录、失败查询、开关关）
        Assert.Null(logger.GetResultJson(99999L));
    }

    [Fact]
    public void DeleteOlderThan_RemovesBothTables_NoOrphans()
    {
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntryWithResult("old", "{\"columns\":[],\"rows\":[]}", time: Iso(10)));
            logger.Log(MakeEntryWithResult("new", "{\"columns\":[],\"rows\":[]}", time: Iso(1)));
            logger.Flush();

            int deleted = logger.DeleteOlderThan(5);
            Assert.Equal(1, deleted);

            var after = logger.Query(new AuditLogQuery { PageSize = 100 });
            Assert.Equal(1, after.Total);
            Assert.Equal("new", after.Items[0].Sql);
            // new 的子表数据应保留
            Assert.Equal("{\"columns\":[],\"rows\":[]}", logger.GetResultJson(after.Items[0].Id));
        }
    }

    [Fact]
    public void Dispose_DrainsAllEntries_WithoutFlush()
    {
        // 守护 Dispose 排空契约：Log 后不调 Flush，仅 Dispose，全部应落盘。
        // 取代 9dd4b49 的"返回即落盘"契约（ExecuteQuery 内 Flush 已移除后）。
        var (store, logger, _, _) = Create();
        using (store)
        {
            for (int i = 0; i < 10; i++)
            {
                logger.Log(MakeEntry($"SELECT {i}", true, time: Iso(10 - i)));
            }
            logger.Dispose(); // 触发排空，不调 Flush
            var page = logger.Query(new AuditLogQuery { PageSize = 100 });
            Assert.Equal(10, page.Total);
        }
    }

    [Fact]
    public void Log_AllEntriesPersist_WithBoundedChannel()
    {
        // 容量 4，写入 50 条远超容量：验证 FullMode=Wait 不丢、不抛（阻塞等消费者腾位）。
        // 阻塞时序信任标准库 BoundedChannelFullMode.Wait 语义，此处只验证最终全落盘。
        var (store, logger, _, _) = Create(channelCapacity: 4);
        using (store)
        {
            for (int i = 0; i < 50; i++)
            {
                logger.Log(MakeEntry($"SELECT {i}", true, time: Iso(50 - i)));
            }
            logger.Flush();
            Assert.Equal(50, logger.Query(new AuditLogQuery { PageSize = 100 }).Total);
        }
    }

    [Fact]
    public void GetCounters_ReflectsWrittenEntries()
    {
        // 顶栏全局状态：写入并落盘后，三个对账数一致（计数器总数 / 当日计数器 / 今日落盘）。
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
            logger.Log(MakeEntry("SELECT 2", true));
            logger.Flush();

            AuditCounters counters = logger.GetCounters();
            Assert.Equal(2, counters.TotalCounter);
            Assert.Equal(2, counters.TodayCounter);
            Assert.Equal(2, counters.TodayPersisted);
            Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), counters.TodayDateKey);
        }
    }

    [Fact]
    public void GetCounters_TotalDecreases_AfterCleanup_DailyUntouched()
    {
        // 清理只按删除条数扣减 total（既有语义：daily 不动）；GetCounters 反映扣减后快照。
        // old 条目 entry.time 在 30 天前：TodayPersisted 只统计今日行，old 删前删后均不计入。
        var (store, logger, _, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("old", true, time: Iso(30)));
            logger.Log(MakeEntry("new", true));
            logger.Flush();

            int deleted = logger.DeleteOlderThan(7);
            Assert.Equal(1, deleted);

            AuditCounters counters = logger.GetCounters();
            Assert.Equal(1, counters.TotalCounter);   // 2 - 1
            Assert.Equal(2, counters.TodayCounter);   // 两条都在今日 Log，daily 不随清理扣减
            Assert.Equal(1, counters.TodayPersisted); // 仅 new 的 time 在今日
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
