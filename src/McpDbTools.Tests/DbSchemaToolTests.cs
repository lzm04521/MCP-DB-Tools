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

        string json = await tool.GetSchema("nope");

        Assert.Equal("PROJECT_NOT_FOUND", JsonDocument.Parse(json).RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task EnvironmentNotFound_ReturnsCode_AndListsAvailable()
    {
        var tool = CreateTool(new SchemaStubProvider(DatabaseType.MySql),
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string json = await tool.GetSchema("erp", environment: "staging");

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("ENVIRONMENT_NOT_FOUND", root.GetProperty("errorCode").GetString());
        Assert.Contains("dev", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TableListMode_ReturnsSectionsJson()
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

        string json = await tool.GetSchema("erp");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("dev", doc.RootElement.GetProperty("environment").GetString());
        JsonElement section = doc.RootElement.GetProperty("sections")[0];
        Assert.Equal("tables", section.GetProperty("name").GetString());
        Assert.Equal(1, section.GetProperty("rowCount").GetInt32());
        // rowset 为 TSV 文本（与 db_query 缺省编码同构）
        Assert.Equal("\\N\torders", section.GetProperty("rowset").GetString());
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

        string json = await tool.GetSchema("erp", table: "nope");

        Assert.Equal("QUERY_ERROR", JsonDocument.Parse(json).RootElement.GetProperty("errorCode").GetString());
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

        string json = await tool.GetSchema("erp", table: table);

        Assert.Equal("PARAMETER_ERROR", JsonDocument.Parse(json).RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Sample_WithoutTable_ReturnsParameterError()
    {
        var tool = CreateTool(new SchemaStubProvider(DatabaseType.MySql),
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string json = await tool.GetSchema("erp", sample: 5);

        Assert.Equal("PARAMETER_ERROR", JsonDocument.Parse(json).RootElement.GetProperty("errorCode").GetString());
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

        string json = await tool.GetSchema("erp", table: "orders", sample: 5);

        Assert.StartsWith("SELECT * FROM orders", stub.LastSampleSql);
        Assert.Equal(5, stub.LastSampleMaxRows); // sample < maxRows 取 sample
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("success").GetBoolean());
    }

    /// <summary>DbSchemaTool 专用 stub：预设 GetSchemaAsync 段 + 采样 spy。</summary>
    internal sealed class SchemaStubProvider : IDatabaseProvider
    {
        public DatabaseType DatabaseType { get; }
        public List<SchemaSection> Sections { get; set; } = new();
        public string? LastSampleSql { get; private set; }
        public int LastSampleMaxRows { get; private set; }
        public SchemaStubProvider(DatabaseType type) => DatabaseType = type;

        public Task<IReadOnlyList<SchemaSection>> GetSchemaAsync(string project, ResolvedDatabase db, string? table, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SchemaSection>>(Sections);

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
