using System.Data.Common;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Tests;

/// <summary>
/// GetSchemaAsync 的模板选择纯逻辑测试（实际执行依赖真实连接，见 T14 真库冒烟）；
/// 另含基类异常包装测试（建连失败包装为失败段，不逃逸到 MCP SDK 层）。
/// </summary>
public class GetSchemaAsyncTests
{
    [Theory]
    [InlineData(DatabaseType.MySql)]
    [InlineData(DatabaseType.Oracle)]
    [InlineData(DatabaseType.SqlServer)]
    [InlineData(DatabaseType.PostgreSql)]
    public void BuildSections_NullOrBlankTable_SelectsTablesTemplate(DatabaseType type)
    {
        IReadOnlyList<SchemaSectionTemplate> expected = SchemaDialects.Tables(type);

        Assert.Equal(expected[0].Sql, SchemaExecutor.BuildSections(type, null)[0].Sql);
        Assert.Equal("tables", SchemaExecutor.BuildSections(type, null)[0].Name);
        Assert.Equal("tables", SchemaExecutor.BuildSections(type, "  ")[0].Name); // 空白等同 null
    }

    [Theory]
    [InlineData(DatabaseType.Oracle)]
    [InlineData(DatabaseType.SqlServer)]
    public void BuildSections_WithTable_SelectsDetailTemplates_InOrder(DatabaseType type)
    {
        var sections = SchemaExecutor.BuildSections(type, "ORDERS");
        Assert.Equal(new[] { "columns", "indexes", "foreignKeys" }, sections.Select(s => s.Name));
        Assert.All(sections, s => Assert.True(s.HasTableParam));
    }

    // ───────── 基类异常包装：建连失败不逃逸（doc/20260828 §9，复现 ORA-12154 场景） ─────────

    /// <summary>建连阶段即抛 DbException 的基类 stub（驱动级连接失败的抽象等价物）。</summary>
    private sealed class ConnectThrowingProvider : DatabaseProviderBase
    {
        public override DatabaseType DatabaseType => DatabaseType.Oracle;
        protected override DbConnection CreateConnection(string connectionString)
            => throw new FakeDbException("ORA-12154: TNS:could not resolve the connect identifier specified");
    }

    private sealed class FakeDbException(string message) : DbException(message);

    /// <summary>构造最小可用 ResolvedDatabase（必填成员给常规值，建连 stub 不实际消费连接串）。</summary>
    private static ResolvedDatabase MakeDb() => new()
    {
        ProjectName = "erp",
        Environment = "prod",
        Type = DatabaseType.Oracle,
        ConnectionString = "cs",
        DatabaseName = null,
        IsProduction = true,
        AllowWrite = false,
        MaxRows = 1000,
        CommandTimeout = 30,
        MaxPoolSize = 100,
        ConnectTimeoutSeconds = 15,
        MaxConcurrency = 4,
        MaxConcurrencyWaitSeconds = 30,
        DisabledKeywords = new HashSet<string>()
    };

    [Fact]
    public async Task GetSchemaAsync_ConnectionDbException_WrappedAsQueryErrorSection()
    {
        var provider = new ConnectThrowingProvider();

        // 修复前：OracleException 逃逸到 MCP SDK 层（"An error occurred invoking 'db_schema'"）；
        // 修复后：外层 catch DbException 包装为单失败段，调用方透传 FAIL QUERY_ERROR
        IReadOnlyList<SchemaSection> sections = await provider.GetSchemaAsync("erp", MakeDb(), null, CancellationToken.None);

        SchemaSection section = Assert.Single(sections);
        Assert.False(section.Result.Success);
        Assert.Equal("QUERY_ERROR", section.Result.ErrorCode);
        Assert.Contains("ORA-12154", section.Result.Error);
    }

    [Fact]
    public async Task GetSchemaAsync_ConnectionDbException_TableModeAlsoWrapped()
    {
        // 表详情模式（三段模板）下建连失败同样短路为单失败段，不部分执行
        var provider = new ConnectThrowingProvider();

        IReadOnlyList<SchemaSection> sections = await provider.GetSchemaAsync("erp", MakeDb(), "ORDERS", CancellationToken.None);

        SchemaSection section = Assert.Single(sections);
        Assert.False(section.Result.Success);
        Assert.Equal("QUERY_ERROR", section.Result.ErrorCode);
    }
}
