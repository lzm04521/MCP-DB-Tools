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
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>());
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
        public Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct)
            => Task.FromResult(_result);
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
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>());
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

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
