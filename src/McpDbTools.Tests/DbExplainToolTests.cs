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
/// db_explain 工具测试：Kind==Write 拒绝（不依赖环境开关）、只读语句分发 provider、解析矩阵。
/// </summary>
public class DbExplainToolTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbe-" + Guid.NewGuid().ToString("N"));

    public DbExplainToolTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { /* 测试清理 */ } }

    private DbExplainTool CreateTool(ExplainStubProvider stub, string databasesJson)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, $$"""{"databases":{{databasesJson}}}""");
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var audit = new AuditLogger(options, loggerFactory.CreateLogger<AuditLogger>(), new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>()));
        var factory = new DatabaseProviderFactory(
            new Dictionary<DatabaseType, IDatabaseProvider> { [stub.DatabaseType] = stub });
        return new DbExplainTool(store, new SqlGuard(), factory, audit, new QueryConcurrencyLimiter());
    }

    [Fact]
    public async Task ProjectNotFound_ReturnsProjectNotFoundCode()
    {
        var tool = CreateTool(new ExplainStubProvider(DatabaseType.MySql),
            """{"erp":{"environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.Explain("nope", "SELECT 1");

        Assert.StartsWith("FAIL PROJECT_NOT_FOUND", text);
    }

    [Fact]
    public async Task WriteStatement_EvenInWriteEnvironment_ReturnsSqlBlocked()
    {
        // 关键安全断言：写环境（allowWrite=true）下 SqlGuard 对写语句返回 Allowed，
        // db_explain 必须显式按 Kind==Write 拒绝，不依赖环境开关兜底
        var stub = new ExplainStubProvider(DatabaseType.MySql);
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs","allowWrite":true}}}}""");

        string text = await tool.Explain("erp", "UPDATE t SET a=1 WHERE id=1");

        Assert.StartsWith("FAIL SQL_BLOCKED", text);
        Assert.False(stub.ExplainCalled);
    }

    [Fact]
    public async Task ReadStatement_CallsProviderExplain_AndReturnsPlanText()
    {
        var stub = new ExplainStubProvider(DatabaseType.MySql)
        {
            ExplainResult = QueryResult.Ok("erp", "MySql",
                new List<string> { "id", "select_type", "table", "rows" },
                new List<object?[]> { new object?[] { 1, "SIMPLE", "t", 100 } }, 1000, false, 3, "dev"),
        };
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.Explain("erp", "SELECT * FROM t WHERE id=1");

        Assert.True(stub.ExplainCalled);
        // text 缺省：状态行 + 表头 + TSV（与 db_query 读形状同构）
        Assert.Equal(
            "OK 1 rows @erp/dev (mysql) 3ms\n" +
            "id\tselect_type\ttable\trows\n" +
            "1\tSIMPLE\tt\t100",
            text);
    }

    [Fact]
    public async Task UnhandledException_WrappedAsQueryUnhandled_NotEscaping()
    {
        // provider 抛非 DbException 逃逸异常：工具层兜底包装为 FAIL QUERY_UNHANDLED（doc/20260828 §9）
        var stub = new ExplainStubProvider(DatabaseType.MySql) { ExplainThrows = new InvalidOperationException("boom") };
        var tool = CreateTool(stub,
            """{"erp":{"defaultEnvironment":"dev","environments":{"dev":{"type":"mysql","connectionString":"cs"}}}}""");

        string text = await tool.Explain("erp", "SELECT * FROM t");

        Assert.StartsWith("FAIL QUERY_UNHANDLED @erp/dev: 未处理异常: boom", text);
    }

    /// <summary>db_explain 专用 stub：预设 ExplainAsync 结果 + spy；可注入逃逸异常验证工具层兜底。</summary>
    internal sealed class ExplainStubProvider : IDatabaseProvider
    {
        public DatabaseType DatabaseType { get; }
        public QueryResult ExplainResult { get; set; } = QueryResult.Ok("erp", "MySql", new List<string>(), new List<object?[]>(), 1000, false, 1, "dev");
        public bool ExplainCalled { get; private set; }
        public Exception? ExplainThrows { get; set; }
        public ExplainStubProvider(DatabaseType type) => DatabaseType = type;

        public Task<QueryResult> ExplainAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
        {
            ExplainCalled = true;
            return ExplainThrows is not null ? Task.FromException<QueryResult>(ExplainThrows) : Task.FromResult(ExplainResult);
        }

        public Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct)
            => throw new NotSupportedException("db_explain 不走普通查询");
        public Task<QueryResult> ExecuteNonQueryAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<(bool Success, long ElapsedMs, string? Error)> TestConnectionAsync(string cs, int t, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SchemaSection>> GetSchemaAsync(string project, ResolvedDatabase db, string? table, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<QueryResult> ExecuteSchemaQueryAsync(string project, ResolvedDatabase db, string sql, string paramValue, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
