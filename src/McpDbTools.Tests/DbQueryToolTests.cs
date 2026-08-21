using System.Text.Json;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Security;
using McpDbTools.Server.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

/// <summary>
/// db_query 的项目/环境解析逻辑测试（不连接真实数据库，覆盖 SQL 校验前的解析与错误码路径）。
/// </summary>
public class DbQueryToolTests : IDisposable
{
    private readonly string _tempDir;

    public DbQueryToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>写临时 config.json 并构造 ConfigStore + DbQueryTool（audit 落临时 db，测试结束随目录删除）。</summary>
    private DbQueryTool CreateTool(string databasesJson)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string json = $$"""{"databases":{{databasesJson}}}""";
        File.WriteAllText(configPath, json);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(
            loggerFactory.CreateLogger<ConfigStore>(),
            options);
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>()));
        return new DbQueryTool(store, new SqlGuard(), new DatabaseProviderFactory(), audit, new QueryConcurrencyLimiter());
    }

    [Fact]
    public async Task ProjectNotFound_ReturnsProjectNotFoundCode()
    {
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("nope", "SELECT 1");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PROJECT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task EnvironmentRequired_WhenNoDefaultAndNotSpecified()
    {
        // 无 defaultEnvironment，且未指定 environment
        var tool = CreateTool("""{"erp":{"environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "SELECT 1");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ENVIRONMENT_REQUIRED", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("prod", doc.RootElement.GetProperty("error").GetString()); // 提示可用环境
    }

    [Fact]
    public async Task EnvironmentNotFound_ReturnsCode_AndListsAvailable()
    {
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "SELECT 1", environment: "staging");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ENVIRONMENT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("prod", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DefaultEnvironment_Used_WhenNotSpecified_ThenSqlGuardRuns()
    {
        // 不传 environment → 走 defaultEnvironment=prod → 解析成功后进入 SQL 校验，DROP 被拦截
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "DROP TABLE x");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("SQL_BLOCKED", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("prod", doc.RootElement.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task ExplicitEnvironment_OverridesDefault()
    {
        // defaultEnvironment=test，但显式传 prod → 用 prod
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"test","environments":{"test":{"type":"sqlserver","connectionString":"cs"},"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "DROP TABLE x", environment: "prod");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("prod", doc.RootElement.GetProperty("environment").GetString());
    }

    /// <summary>Stub provider：返回预设的 QueryResult，用于验证审计是否记录结果（绕过真实数据库）。</summary>
    private sealed class StubProvider : IDatabaseProvider
    {
        private readonly QueryResult _result;
        public StubProvider(QueryResult result, DatabaseType type)
        {
            _result = result;
            DatabaseType = type;
        }
        public DatabaseType DatabaseType { get; }
        // spy 标志：T6 路由测试断言"写语句→NonQuery、读语句→Query"用
        public bool ExecuteQueryCalled { get; private set; }
        public bool ExecuteNonQueryCalled { get; private set; }
        public Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct)
        {
            ExecuteQueryCalled = true;
            return Task.FromResult(_result);
        }
        public Task<QueryResult> ExecuteNonQueryAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
        {
            ExecuteNonQueryCalled = true;
            return Task.FromResult(_result);
        }
        public Task<(bool Success, long ElapsedMs, string? Error)> TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
            => Task.FromResult<(bool, long, string?)>((true, 0, null));
    }

    /// <summary>构造带开关控制 + stub provider 的工具：switchOn 控制 AuditRecordResults。</summary>
    private (DbQueryTool tool, AuditLogger audit) CreateToolWithSwitch(bool switchOn, QueryResult stubResult)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string maintenance = switchOn
            ? "\"maintenance\":{\"auditRecordResults\":true}"
            : "\"maintenance\":{\"auditRecordResults\":false}";
        string json = "{\"databases\":{\"erp\":{\"defaultEnvironment\":\"prod\",\"environments\":{\"prod\":{\"type\":\"sqlserver\",\"connectionString\":\"cs\"}}}}," + maintenance + "}";
        File.WriteAllText(configPath, json);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>()));
        // stub provider 只挂 sqlserver 类型
        var factory = new DatabaseProviderFactory(
            new Dictionary<DatabaseType, IDatabaseProvider>
            {
                [DatabaseType.SqlServer] = new StubProvider(stubResult, DatabaseType.SqlServer)
            });
        return (new DbQueryTool(store, new SqlGuard(), factory, audit, new QueryConcurrencyLimiter()), audit);
    }

    [Fact]
    public async Task DbQuery_LogsResult_WhenSwitchOn()
    {
        var stubOk = new QueryResult
        {
            Success = true,
            Columns = new List<string> { "id", "name" },
            Rows = new List<object?[]> { new object?[] { 1, "a" } },
            RowCount = 1
        };
        var (tool, audit) = CreateToolWithSwitch(true, stubOk);
        using (audit)
        {
            await tool.ExecuteQuery("erp", "SELECT id, name FROM t");
            audit.Flush();

            // 验证主表有记录 + 子表有 result_json
            var page = audit.Query(new AuditLogQuery());
            AuditEntry entry = Assert.Single(page.Items);
            Assert.True(entry.Success);
            string? resultJson = audit.GetResultJson(entry.Id);
            Assert.NotNull(resultJson);
            Assert.Contains("\"columns\"", resultJson);
            Assert.Contains("\"rows\"", resultJson);
            Assert.Contains("id", resultJson);
        }
    }

    [Fact]
    public async Task DbQuery_SkipsResult_WhenSwitchOff()
    {
        var stubOk = new QueryResult
        {
            Success = true,
            Columns = new List<string> { "id" },
            Rows = new List<object?[]> { new object?[] { 1 } },
            RowCount = 1
        };
        var (tool, audit) = CreateToolWithSwitch(false, stubOk);
        using (audit)
        {
            await tool.ExecuteQuery("erp", "SELECT id FROM t");
            audit.Flush();

            var page = audit.Query(new AuditLogQuery());
            AuditEntry entry = Assert.Single(page.Items);
            Assert.True(entry.Success);
            // 开关关 → 子表无记录
            Assert.Null(audit.GetResultJson(entry.Id));
        }
    }

    [Fact]
    public async Task DbQuery_SkipsResult_WhenFailed()
    {
        // 开关 on，但 provider 返回失败 → 不记录结果
        var stubFail = new QueryResult
        {
            Success = false,
            Error = "boom",
            ErrorCode = "X"
        };
        var (tool, audit) = CreateToolWithSwitch(true, stubFail);
        using (audit)
        {
            await tool.ExecuteQuery("erp", "SELECT 1");
            audit.Flush();

            var page = audit.Query(new AuditLogQuery());
            AuditEntry entry = Assert.Single(page.Items);
            Assert.False(entry.Success);
            Assert.Null(audit.GetResultJson(entry.Id));
        }
    }

    [Fact]
    public async Task GuardFailure_ReturnsSqlBlocked()
    {
        // SQL 校验失败：DbQueryTool 调 SqlGuard 拦截 DROP，返回 SQL_BLOCKED
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "DROP TABLE x");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("SQL_BLOCKED", doc.RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>构造带抛异常 stub provider 的工具：验证逃逸异常路径也记审计（阶段 3）。</summary>
    private (DbQueryTool tool, AuditLogger audit) CreateToolWithThrowingProvider(Exception toThrow)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string json = "{\"databases\":{\"erp\":{\"defaultEnvironment\":\"prod\",\"environments\":{\"prod\":{\"type\":\"sqlserver\",\"connectionString\":\"cs\"}}}},\"maintenance\":{\"auditRecordResults\":true}}";
        File.WriteAllText(configPath, json);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>()));
        var factory = new DatabaseProviderFactory(
            new Dictionary<DatabaseType, IDatabaseProvider>
            {
                [DatabaseType.SqlServer] = new ThrowingProvider(toThrow, DatabaseType.SqlServer)
            });
        return (new DbQueryTool(store, new SqlGuard(), factory, audit, new QueryConcurrencyLimiter()), audit);
    }

    [Fact]
    public async Task ExecuteQuery_Audit_Logged_OnUnhandledException()
    {
        // 非 DbException 逃逸异常：ExecuteQuery 兜底 catch 记审计 + 返回 QUERY_UNHANDLED（阶段 3）
        var (tool, audit) = CreateToolWithThrowingProvider(new InvalidOperationException("boom"));
        using (audit)
        {
            string json = await tool.ExecuteQuery("erp", "SELECT 1");
            audit.Flush(); // 移除生产 Flush 后，测试同步排空以读取（Flush 的正当测试用途）
            var page = audit.Query(new AuditLogQuery());
            AuditEntry entry = Assert.Single(page.Items);
            Assert.False(entry.Success);
            Assert.Contains("boom", entry.Error);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("QUERY_UNHANDLED", doc.RootElement.GetProperty("errorCode").GetString());
        }
    }

    [Fact]
    public async Task ExecuteQuery_Audit_Logged_OnCancellation()
    {
        // OperationCanceledException：记审计 + 返回 QUERY_CANCELED（阶段 3）
        var (tool, audit) = CreateToolWithThrowingProvider(new OperationCanceledException());
        using (audit)
        {
            string json = await tool.ExecuteQuery("erp", "SELECT 1");
            audit.Flush(); // 测试同步排空
            var page = audit.Query(new AuditLogQuery());
            Assert.Single(page.Items);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("QUERY_CANCELED", doc.RootElement.GetProperty("errorCode").GetString());
        }
    }

    /// <summary>抛指定异常的 stub provider，用于测试逃逸异常路径。</summary>
    private sealed class ThrowingProvider : IDatabaseProvider
    {
        private readonly Exception _ex;
        public ThrowingProvider(Exception ex, DatabaseType type) { _ex = ex; DatabaseType = type; }
        public DatabaseType DatabaseType { get; }
        // spy 标志：与 StubProvider 一致，便于复用断言路由
        public bool ExecuteQueryCalled { get; private set; }
        public bool ExecuteNonQueryCalled { get; private set; }
        public Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct)
        {
            ExecuteQueryCalled = true;
            return Task.FromException<QueryResult>(_ex);
        }
        public Task<QueryResult> ExecuteNonQueryAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
        {
            ExecuteNonQueryCalled = true;
            return Task.FromException<QueryResult>(_ex);
        }
        public Task<(bool Success, long ElapsedMs, string? Error)> TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
            => Task.FromResult<(bool, long, string?)>((false, 0, _ex.Message));
    }

    /// <summary>
    /// 构造带 spy StubProvider + allowWrite 控制的工具：用于 T6 断言按 StatementKind 路由。
    /// allowWrite=true 时 dev 环境；否则 dev 不带 allowWrite（默认 false）。
    /// </summary>
    private (DbQueryTool tool, StubProvider spy) BuildToolWithSpyProvider(bool allowWrite)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string env = allowWrite
            ? "\"dev\":{\"type\":\"sqlserver\",\"connectionString\":\"cs\",\"allowWrite\":true}"
            : "\"dev\":{\"type\":\"sqlserver\",\"connectionString\":\"cs\"}";
        string json = "{\"databases\":{\"erp\":{\"defaultEnvironment\":\"dev\",\"environments\":{" + env + "}}}}";
        File.WriteAllText(configPath, json);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>()));
        var stub = new QueryResult
        {
            Success = true,
            Columns = new List<string> { "id" },
            Rows = new List<object?[]> { new object?[] { 1 } },
            RowCount = 1
        };
        var spy = new StubProvider(stub, DatabaseType.SqlServer);
        var factory = new DatabaseProviderFactory(
            new Dictionary<DatabaseType, IDatabaseProvider>
            {
                [DatabaseType.SqlServer] = spy
            });
        return (new DbQueryTool(store, new SqlGuard(), factory, audit, new QueryConcurrencyLimiter()), spy);
    }

    [Fact]
    public async Task WriteStatement_Routes_To_ExecuteNonQueryAsync()
    {
        // allowWrite=true → INSERT 首关键字在写白名单 → Kind=Write → 路由 ExecuteNonQueryAsync
        var (tool, spy) = BuildToolWithSpyProvider(allowWrite: true);
        await tool.ExecuteQuery("erp", "INSERT INTO t (a) VALUES (1)", "dev");
        Assert.True(spy.ExecuteNonQueryCalled);
        Assert.False(spy.ExecuteQueryCalled);
    }

    [Fact]
    public async Task ReadStatement_Routes_To_ExecuteQueryAsync()
    {
        // SELECT 即使在 allowWrite=true 环境也属 Kind=Read → 路由 ExecuteQueryAsync
        var (tool, spy) = BuildToolWithSpyProvider(allowWrite: true);
        await tool.ExecuteQuery("erp", "SELECT 1", "dev");
        Assert.True(spy.ExecuteQueryCalled);
        Assert.False(spy.ExecuteNonQueryCalled);
    }

    // ───────── format 参数：缺省 tsv / json 回退 / 宽容解析 ─────────

    [Fact]
    public async Task Format_Default_IsTsv()
    {
        var (tool, _) = BuildToolWithSpyProvider(allowWrite: false);
        string json = await tool.ExecuteQuery("erp", "SELECT 1", "dev");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("tsv", doc.RootElement.GetProperty("format").GetString());
        Assert.True(doc.RootElement.TryGetProperty("rowset", out _));
        Assert.False(doc.RootElement.TryGetProperty("rows", out _));
    }

    [Fact]
    public async Task Format_Json_ReturnsRowsArray()
    {
        var (tool, _) = BuildToolWithSpyProvider(allowWrite: false);
        string json = await tool.ExecuteQuery("erp", "SELECT 1", "dev", format: "json");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
        Assert.True(doc.RootElement.TryGetProperty("rows", out _));
        Assert.False(doc.RootElement.TryGetProperty("rowset", out _));
    }

    [Theory]
    [InlineData("JSON")]
    [InlineData(" json ")]
    public async Task Format_Json_TrimAndCaseInsensitive(string fmt)
    {
        var (tool, _) = BuildToolWithSpyProvider(allowWrite: false);
        string json = await tool.ExecuteQuery("erp", "SELECT 1", "dev", format: fmt);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Format_UnknownValue_FallsBackToTsv()
    {
        var (tool, _) = BuildToolWithSpyProvider(allowWrite: false);
        string json = await tool.ExecuteQuery("erp", "SELECT 1", "dev", format: "xml");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("tsv", doc.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Format_AppliesToFailurePath()
    {
        // 失败路径（PROJECT_NOT_FOUND）也带 format 回显
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("nope", "SELECT 1", format: "json");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PROJECT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
