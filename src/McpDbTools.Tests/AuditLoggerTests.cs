using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
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
    private (ConfigStore store, AuditLogger logger, string dbPath) Create()
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
        var logger = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>());
        return (store, logger, Path.Combine(_tempDir, "audit.db"));
    }

    /// <summary>以「当前 UTC 往前 daysAgo 天」生成 ISO 时间，保证各条记录时间互不相同、可稳定排序。</summary>
    private static string Iso(int daysAgo) => DateTime.UtcNow.AddDays(-daysAgo)
        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Log_AlwaysWrites_GlobalAuditOn()
    {
        // 需求 3：审计全局开启，不再依赖开关。任何 Log 调用都应写入。
        var (store, logger, dbPath) = Create();
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
        var (store, logger, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true, time: Iso(3)));
            logger.Log(MakeEntry("SELECT 2", true, time: Iso(1)));
            logger.Log(MakeEntry("DROP x", false, error: "blocked", time: Iso(2)));
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
        var (store, logger, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true, project: "erp"));
            logger.Log(MakeEntry("DROP x", false, project: "erp", error: "blocked"));
            logger.Log(MakeEntry("SELECT 2", true, project: "crm"));
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
        var (store, logger, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("A", true, time: Iso(5)));
            logger.Log(MakeEntry("B", true, time: Iso(3)));
            logger.Log(MakeEntry("C", true, time: Iso(1)));
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
        var (store, logger, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT * FROM Users", true));
            logger.Log(MakeEntry("select id from orders", true));
            logger.Log(MakeEntry("DELETE FROM t", false, error: "x"));
            logger.Log(MakeEntry("100%off", true));
        }

        Assert.Equal(2, logger.Query(new AuditLogQuery { SqlContains = "select" }).Total);
        Assert.Equal(1, logger.Query(new AuditLogQuery { SqlContains = "users" }).Total);
        // 含通配符 % _ 应被转义，按字面匹配
        Assert.Equal(1, logger.Query(new AuditLogQuery { SqlContains = "100%off" }).Total);
    }

    [Fact]
    public void Query_PaginationWorks()
    {
        var (store, logger, _) = Create();
        using (store)
        {
            for (int i = 0; i < 7; i++)
            {
                // 每个 i 用不同的偏移天数，保证时间互不相同、可稳定排序
                logger.Log(MakeEntry($"SELECT {i}", true, time: Iso(7 - i)));
            }
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
        var (store, logger, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("SELECT 1", true));
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
        var (store, logger, _) = Create();
        using (store)
        {
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 'a''b'", Success = true });
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 1; DROP x", Success = false, Error = "blocked 'x'" });
        }

        Assert.Equal(2, logger.Query(new AuditLogQuery()).Total);
        Assert.Equal("SELECT 'a''b'", logger.Query(new AuditLogQuery { Success = true }).Items[0].Sql);
        Assert.Equal("blocked 'x'", logger.Query(new AuditLogQuery { Success = false }).Items[0].Error);
    }

    [Fact]
    public void DeleteOlderThan_RemovesOldKeepsNew()
    {
        // 用 1/4/10 天偏移构造新旧记录，便于验证 DeleteOlderThan 按天数删除的行为
        var (store, logger, _) = Create();
        using (store)
        {
            logger.Log(MakeEntry("old", true, time: Iso(10)));   // 10 天前
            logger.Log(MakeEntry("mid", true, time: Iso(4)));    // 4 天前
            logger.Log(MakeEntry("new", true, time: Iso(1)));    // 1 天前

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
        var (store, logger, _) = Create();
        using (store)
        {
            Assert.Throws<ArgumentException>(() => logger.DeleteOlderThan(0));
            Assert.Throws<ArgumentException>(() => logger.DeleteOlderThan(-5));
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

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
