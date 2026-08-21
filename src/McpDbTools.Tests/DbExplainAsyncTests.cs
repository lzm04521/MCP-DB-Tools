using McpDbTools.Server.Database;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Tests;

/// <summary>
/// EXPLAIN 前缀构造纯逻辑测试：基类默认实现仅服务前缀方言（MySQL/PG），
/// SqlServer/Oracle 误走基类即抛错暴露（会话式实现由子类 override）。
/// </summary>
public class DbExplainAsyncTests
{
    [Theory]
    [InlineData(DatabaseType.MySql)]
    [InlineData(DatabaseType.PostgreSql)]
    public void BuildExplainSql_MySqlAndPg_PrefixesExplain(DatabaseType type)
    {
        string sql = ExplainSqlBuilder.Build(type, "SELECT * FROM t ORDER BY id");
        Assert.Equal("EXPLAIN SELECT * FROM t ORDER BY id", sql);
    }

    [Theory]
    [InlineData(DatabaseType.SqlServer)]
    [InlineData(DatabaseType.Oracle)]
    public void BuildExplainSql_ServerDialects_NotPrefixed_Throws(DatabaseType type)
    {
        Assert.ThrowsAny<Exception>(() => ExplainSqlBuilder.Build(type, "SELECT 1"));
    }

    [Fact]
    public void BuildExplainSql_StripsTrailingSemicolon()
    {
        string sql = ExplainSqlBuilder.Build(DatabaseType.MySql, "SELECT 1;");
        Assert.Equal("EXPLAIN SELECT 1", sql);
    }
}
