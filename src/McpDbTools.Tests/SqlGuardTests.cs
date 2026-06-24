using McpDbTools.Server.Configuration;
using McpDbTools.Server.Security;

namespace McpDbTools.Tests;

public class SqlGuardTests
{
    private readonly SqlGuard _guard = new();

    /// <summary>构造带内置默认阻止关键字的项目（模拟真实三层合并结果）。</summary>
    private static ResolvedDatabase Db(DatabaseType type)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in DefaultDisabledKeywords.BuiltIn) set.Add(k.ToUpperInvariant());
        foreach (string k in DefaultDisabledKeywords.BuiltInByType[type]) set.Add(k.ToUpperInvariant());
        return new ResolvedDatabase
        {
            ProjectName = "test",
            Environment = "test",
            Type = type,
            ConnectionString = "",
            MaxRows = 1000,
            CommandTimeout = 30,
            DisabledKeywords = set
        };
    }

    [Theory]
    [InlineData("SELECT * FROM Users", true)]
    [InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte", true)]
    [InlineData("EXEC sp_help 'Users'", true)]
    [InlineData("select id from t where x = 1", true)]  // 小写也应允许
    [InlineData("DROP TABLE Users", false)]
    [InlineData("DELETE FROM Users", false)]
    [InlineData("INSERT INTO Users VALUES(1)", false)]
    [InlineData("UPDATE Users SET name='x'", false)]
    [InlineData("TRUNCATE TABLE Users", false)]
    [InlineData("CREATE TABLE t(id int)", false)]
    [InlineData("ALTER TABLE t ADD c int", false)]
    public void SqlServer_BasicWhitelist(string sql, bool expected)
    {
        var r = _guard.Validate(sql, Db(DatabaseType.SqlServer));
        Assert.Equal(expected, r.Allowed);
    }

    [Fact]
    public void MultiStatement_Injection_Blocked()
    {
        // SELECT 通过白名单，但黑名单扫描全文拦截 DROP
        var r = _guard.Validate("SELECT 1; DROP TABLE Users", Db(DatabaseType.SqlServer));
        Assert.False(r.Allowed);
        Assert.Equal("SQL_BLOCKED", r.ErrorCode);
    }

    [Fact]
    public void BlacklistKeyword_InSelect_Blocked()
    {
        // xp_cmdshell 在黑名单，即使首关键字是 SELECT 也拦截
        var r = _guard.Validate("SELECT * FROM xp_cmdshell('dir')", Db(DatabaseType.SqlServer));
        Assert.False(r.Allowed);
        Assert.Equal("SQL_BLOCKED", r.ErrorCode);
    }

    [Fact]
    public void MultiWordKeyword_BulkInsert_Blocked()
    {
        var r = _guard.Validate("SELECT 1; BULK INSERT t FROM 'f'", Db(DatabaseType.SqlServer));
        Assert.False(r.Allowed);
    }

    [Fact]
    public void WordBoundary_PreventsFalsePositive()
    {
        // 列名 DropColumn 不应触发 DROP 黑名单（\b 边界匹配）
        var r = _guard.Validate("SELECT DropColumn FROM MyTable", Db(DatabaseType.SqlServer));
        Assert.True(r.Allowed);
    }

    [Fact]
    public void Comment_RemovedBeforeValidation()
    {
        // 注释里的 DROP 应被去除，不影响白名单判断
        var r = _guard.Validate("SELECT 1 /* DROP TABLE x */ FROM t", Db(DatabaseType.SqlServer));
        Assert.True(r.Allowed);
    }

    [Fact]
    public void Show_NotAllowed_OnSqlServer()
    {
        Assert.False(_guard.Validate("SHOW TABLES", Db(DatabaseType.SqlServer)).Allowed);
    }

    [Fact]
    public void Show_Allowed_OnMySql()
    {
        Assert.True(_guard.Validate("SHOW TABLES", Db(DatabaseType.MySql)).Allowed);
    }

    [Theory]
    [InlineData("DESCRIBE Users", true)]
    [InlineData("DESC Users", true)]
    [InlineData("EXPLAIN SELECT * FROM Users", true)]
    [InlineData("OPTIMIZE TABLE Users", false)]
    [InlineData("LOAD DATA INFILE 'x' INTO TABLE t", false)]
    [InlineData("FLUSH TABLES", false)]
    public void MySql_DialectSpecific(string sql, bool expected)
    {
        Assert.Equal(expected, _guard.Validate(sql, Db(DatabaseType.MySql)).Allowed);
    }

    [Theory]
    [InlineData("SELECT * FROM ALL_TABLES", true)]
    [InlineData("DESC MyTable", true)]
    [InlineData("EXEC my_pkg.my_proc", true)]
    [InlineData("FLASHBACK TABLE t TO TIMESTAMP", false)]
    [InlineData("PURGE RECYCLEBIN", false)]
    [InlineData("ALTER SYSTEM FLUSH SHARED_POOL", false)]
    public void Oracle_DialectSpecific(string sql, bool expected)
    {
        Assert.Equal(expected, _guard.Validate(sql, Db(DatabaseType.Oracle)).Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/* only comment */")]
    public void EmptyOrCommentOnly_Rejected(string sql)
    {
        var r = _guard.Validate(sql, Db(DatabaseType.SqlServer));
        Assert.False(r.Allowed);
        Assert.Equal("SQL_PARSE_ERROR", r.ErrorCode);
    }

    [Fact]
    public void DeniedResult_ContainsReason()
    {
        var r = _guard.Validate("DROP TABLE x", Db(DatabaseType.SqlServer));
        Assert.False(r.Allowed);
        Assert.Contains("DROP", r.Reason);
    }
}
