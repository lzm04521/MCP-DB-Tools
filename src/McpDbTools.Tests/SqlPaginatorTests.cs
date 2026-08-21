using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Tests;

/// <summary>
/// offset 分页方言拼接测试：四方言产物、ORDER BY 检测、分页子句冲突检测（多语句/自带分页锁子句）。
/// </summary>
public class SqlPaginatorTests
{
    [Theory]
    [InlineData(DatabaseType.MySql)]
    [InlineData(DatabaseType.PostgreSql)]
    public void LimitDialects_AppendLimitOffset(DatabaseType type)
    {
        var outcome = SqlPaginator.TryAppend(type, "SELECT * FROM t ORDER BY id", 20, 10, out string sql, out _);
        Assert.Equal(OffsetAppendOutcome.Appended, outcome);
        Assert.Equal("SELECT * FROM t ORDER BY id LIMIT 10 OFFSET 20", sql);
    }

    [Theory]
    [InlineData(DatabaseType.SqlServer)]
    [InlineData(DatabaseType.Oracle)]
    public void OffsetFetchDialects_AppendOffsetFetch(DatabaseType type)
    {
        var outcome = SqlPaginator.TryAppend(type, "SELECT * FROM t ORDER BY id", 20, 10, out string sql, out _);
        Assert.Equal(OffsetAppendOutcome.Appended, outcome);
        Assert.Equal("SELECT * FROM t ORDER BY id OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", sql);
    }

    [Fact]
    public void TrailingSemicolon_StrippedBeforeConflictCheck()
    {
        var outcome = SqlPaginator.TryAppend(DatabaseType.MySql, "SELECT * FROM t ORDER BY id;", 0, 10, out string sql, out _);
        Assert.Equal(OffsetAppendOutcome.Appended, outcome);
        Assert.Equal("SELECT * FROM t ORDER BY id LIMIT 10 OFFSET 0", sql);
    }

    [Theory]
    [InlineData(DatabaseType.SqlServer)]
    [InlineData(DatabaseType.Oracle)]
    public void OffsetFetchDialects_WithoutOrderBy_Rejected(DatabaseType type)
    {
        var outcome = SqlPaginator.TryAppend(type, "SELECT * FROM t", 0, 10, out _, out string reason);
        Assert.Equal(OffsetAppendOutcome.RequiresOrderBy, outcome);
        Assert.Contains("ORDER BY", reason);
    }

    [Theory]
    [InlineData(DatabaseType.MySql)]
    [InlineData(DatabaseType.PostgreSql)]
    public void LimitDialects_WithoutOrderBy_StillAppended(DatabaseType type)
    {
        // LIMIT/OFFSET 语法不强制 ORDER BY，放行（顺序稳定性由 Agent 自行负责，工具描述中提示）
        var outcome = SqlPaginator.TryAppend(type, "SELECT * FROM t", 0, 10, out _, out _);
        Assert.Equal(OffsetAppendOutcome.Appended, outcome);
    }

    [Theory]
    [InlineData("SELECT * FROM t LIMIT 10")]
    [InlineData("SELECT * FROM t LIMIT 10 OFFSET 5")]
    [InlineData("SELECT * FROM t OFFSET 5 ROWS")]
    [InlineData("SELECT * FROM t OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t FOR SHARE")]
    [InlineData("SELECT 1; SELECT 2")]
    public void ConflictForms_Rejected(string sql)
    {
        var outcome = SqlPaginator.TryAppend(DatabaseType.MySql, sql, 0, 10, out _, out string reason);
        Assert.Equal(OffsetAppendOutcome.Conflict, outcome);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void SubqueryLimit_NotAtEnd_IsNotConflict()
    {
        // 子查询内的 LIMIT 不处于语句末尾，不冲突；外层无分页子句 → 正常追加
        var outcome = SqlPaginator.TryAppend(DatabaseType.MySql, "SELECT * FROM (SELECT * FROM t LIMIT 100) sub ORDER BY id", 0, 10, out _, out _);
        Assert.Equal(OffsetAppendOutcome.Appended, outcome);
    }

    [Fact]
    public void Cte_EndAppend_IsAppended()
    {
        var outcome = SqlPaginator.TryAppend(DatabaseType.SqlServer, "WITH c AS (SELECT 1 AS id) SELECT * FROM c ORDER BY id", 5, 10, out string sql, out _);
        Assert.Equal(OffsetAppendOutcome.Appended, outcome);
        Assert.EndsWith("OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY", sql);
    }

    [Fact]
    public void QuotedStringEndingWithForUpdate_NotTreatedAsConflict()
    {
        // 字符串字面量末尾的 FOR UPDATE 带闭合引号，不匹配"语句以 FOR UPDATE 结尾"
        var outcome = SqlPaginator.TryAppend(DatabaseType.PostgreSql, "SELECT * FROM t WHERE memo = 'wait FOR UPDATE' ORDER BY id", 0, 10, out _, out _);
        Assert.Equal(OffsetAppendOutcome.Appended, outcome);
    }
}
