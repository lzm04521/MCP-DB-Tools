using System.Text.Json;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

/// <summary>
/// db_schema 工具测试：项目/环境解析矩阵、表名标识符校验、两级加载、sample 采样（stub 注入）。
/// </summary>
public class DbSchemaToolTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbs-" + Guid.NewGuid().ToString("N"));

    public DbSchemaToolTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { /* 测试清理 */ } }

    private DbSchemaTool CreateTool(SchemaStubProvider stub, string databasesJson)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, $$"""{"databases":{{databasesJson}}}""");
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>()));
        var factory = new DatabaseProviderFactory(
            new Dictionary<DatabaseType, IDatabaseProvider> { [stub.DatabaseType] = stub });
        return new DbSchemaTool(store, factory, audit, new QueryConcurrencyLimiter());
    }

    [Fact]
    public async Task ProjectNotFound_ReturnsProjectNotFoundCode()
    {
        var tool = CreateTool(new SchemaStubProvider(DatabaseType.MySql),
            """{"erp":{"environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("nope");

        Assert.StartsWith("FAIL PROJECT_NOT_FOUND", text);
    }

    [Fact]
    public async Task EnvironmentNotFound_ReturnsCode_AndListsAvailable()
    {
        var tool = CreateTool(new SchemaStubProvider(DatabaseType.MySql),
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("erp", environment: "staging");

        Assert.StartsWith("FAIL ENVIRONMENT_NOT_FOUND", text);
        Assert.Contains("dev", text);
    }

    [Fact]
    public async Task TableListMode_ReturnsTextSections()
    {
        var stub = new SchemaStubProvider(DatabaseType.MySql);
        stub.Sections = new List<SchemaSection>
        {
            new("tables", QueryResult.Ok("erp", "MySql",
                new List<string> { "schema_name", "table_name" },
                new List<object?[]> { new object?[] { null, "orders" } }, 1000, false, 5, "dev")),
        };
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("erp");

        // text：头部状态行 + "# 段名 (行数)" + 表头行 + TSV 数据（段体与 db_query 同一编码）
        Assert.Equal(
            "OK tables @erp/dev (mysql)\n" +
            "# tables (1)\n" +
            "schema_name\ttable_name\n" +
            "\\N\torders",
            text);
    }

    [Fact]
    public async Task TableMode_ReturnsTextSections_WithSample()
    {
        var stub = new SchemaStubProvider(DatabaseType.MySql);
        stub.Sections = new List<SchemaSection>
        {
            new("columns", QueryResult.Ok("erp", "MySql", new List<string> { "column_name" },
                new List<object?[]> { new object?[] { "id" } }, 1000, false, 1, "dev")),
        };
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("erp", table: "orders", sample: 5);

        // 末段 sample：stub ExecuteQueryAsync 返回单列单行 c/1
        Assert.Equal(
            "OK table=orders @erp/dev (mysql)\n" +
            "# columns (1)\n" +
            "column_name\n" +
            "id\n" +
            "# sample (1)\n" +
            "c\n" +
            "1",
            text);
    }

    [Fact]
    public async Task SchemaFailure_ShortCircuits_AndReturnsErrorCode()
    {
        var stub = new SchemaStubProvider(DatabaseType.MySql);
        stub.Sections = new List<SchemaSection>
        {
            new("columns", QueryResult.Fail("erp", "MySql", "元数据查询错误: 表不存在", "QUERY_ERROR", 3, "dev")),
        };
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("erp", table: "nope");

        Assert.StartsWith("FAIL QUERY_ERROR", text);
    }

    [Fact]
    public async Task UnhandledException_WrappedAsQueryUnhandled_NotEscaping()
    {
        // provider 抛非 DbException 逃逸异常：工具层兜底包装为 FAIL QUERY_UNHANDLED（与 db_query 阶段 3 同模式），
        // 不让异常崩到 MCP SDK 层（doc/20260828 §9）
        var stub = new SchemaStubProvider(DatabaseType.MySql) { GetSchemaThrows = new InvalidOperationException("boom") };
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("erp");

        Assert.StartsWith("FAIL QUERY_UNHANDLED @erp/dev: 未处理异常: boom", text);
    }

    [Theory]
    [InlineData("orders; DROP TABLE x")]
    [InlineData("订单")]
    [InlineData("\"quoted\"")]
    [InlineData("a b")]
    public async Task Table_InvalidIdentifier_ReturnsParameterError(string table)
    {
        var tool = CreateTool(new SchemaStubProvider(DatabaseType.MySql),
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("erp", table: table);

        Assert.StartsWith("FAIL PARAMETER_ERROR", text);
    }

    [Fact]
    public async Task Sample_WithoutTable_ReturnsParameterError()
    {
        var tool = CreateTool(new SchemaStubProvider(DatabaseType.MySql),
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.GetSchema("erp", sample: 5);

        Assert.StartsWith("FAIL PARAMETER_ERROR", text);
    }

    [Fact]
    public async Task Sample_ValidTable_ExecutesSampledQuery_CappedByMaxRows()
    {
        var stub = new SchemaStubProvider(DatabaseType.MySql);
        stub.Sections = new List<SchemaSection>
        {
            new("columns", QueryResult.Ok("erp", "MySql", new List<string> { "column_name" },
                new List<object?[]> { new object?[] { "id" } }, 1000, false, 1, "dev")),
        };
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs","maxRows":20}}}}""");

        string text = await tool.GetSchema("erp", table: "orders", sample: 5);

        Assert.StartsWith("SELECT * FROM orders", stub.LastSampleSql);
        Assert.Equal(5, stub.LastSampleMaxRows); // sample < maxRows 取 sample
        Assert.StartsWith("OK table=orders @erp/dev (mysql)", text);
    }

    /// <summary>DbSchemaTool 专用 stub：预设 GetSchemaAsync 段 + 采样 spy；可注入逃逸异常验证工具层兜底。</summary>
    internal sealed class SchemaStubProvider : IDatabaseProvider
    {
        public DatabaseType DatabaseType { get; }
        public List<SchemaSection> Sections { get; set; } = new();
        public string? LastSampleSql { get; private set; }
        public int LastSampleMaxRows { get; private set; }
        public Exception? GetSchemaThrows { get; set; }
        public SchemaStubProvider(DatabaseType type) => DatabaseType = type;

        public Task<IReadOnlyList<SchemaSection>> GetSchemaAsync(string project, ResolvedDatabase db, string? table, CancellationToken ct)
            => GetSchemaThrows is not null ? Task.FromException<IReadOnlyList<SchemaSection>>(GetSchemaThrows)
                : Task.FromResult<IReadOnlyList<SchemaSection>>(Sections);

        public Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct)
        {
            LastSampleSql = sql;
            LastSampleMaxRows = maxRows;
            return Task.FromResult(QueryResult.Ok(project, db.Type.ToString(),
                new List<string> { "c" }, new List<object?[]> { new object?[] { 1 } }, maxRows, false, 1, db.Environment));
        }

        public Task<QueryResult> ExecuteNonQueryAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
            => throw new NotSupportedException("db_schema 不执行写操作");
        public Task<QueryResult> ExplainAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
            => throw new NotSupportedException("db_schema 不执行 explain");
        public Task<(bool Success, long ElapsedMs, string? Error)> TestConnectionAsync(string cs, int t, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
