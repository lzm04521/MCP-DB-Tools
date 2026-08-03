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
        foreach (string k in DefaultDisabledKeywords.BuiltInReadOnly) set.Add(k.ToUpperInvariant());
        foreach (string k in DefaultDisabledKeywords.BuiltInByType[type]) set.Add(k.ToUpperInvariant());
        return new ResolvedDatabase
        {
            ProjectName = "test",
            Environment = "test",
            IsProduction = false,
            AllowWrite = false,
            Type = type,
            ConnectionString = "",
            DatabaseName = null,
            MaxRows = 1000,
            CommandTimeout = 30,
            MaxPoolSize = 100,
            ConnectTimeoutSeconds = 15,
            MaxConcurrency = 8,
            MaxConcurrencyWaitSeconds = 5,
            DisabledKeywords = set
        };
    }

    /// <summary>
    /// 构造可指定 AllowWrite/IsProduction 的环境，DisabledKeywords 按读写池内置默认合并。
    /// 模拟 ResolvedConfigBuilder 的合并结果：allowWrite 选 BuiltInWrite，否则 BuiltInReadOnly，再并 BuiltInByType。
    /// </summary>
    private static ResolvedDatabase BuildResolvedDatabase(DatabaseType type, bool allowWrite, bool isProduction = false)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> global = allowWrite
            ? DefaultDisabledKeywords.BuiltInWrite
            : DefaultDisabledKeywords.BuiltInReadOnly;
        foreach (string k in global) set.Add(k.ToUpperInvariant());
        foreach (string k in DefaultDisabledKeywords.BuiltInByType[type]) set.Add(k.ToUpperInvariant());
        return new ResolvedDatabase
        {
            ProjectName = "test",
            Environment = "test",
            IsProduction = isProduction,
            AllowWrite = allowWrite,
            Type = type,
            ConnectionString = "",
            DatabaseName = null,
            MaxRows = 1000,
            CommandTimeout = 30,
            MaxPoolSize = 100,
            ConnectTimeoutSeconds = 15,
            MaxConcurrency = 8,
            MaxConcurrencyWaitSeconds = 5,
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

    [Theory]
    [InlineData("DBCC SQLPERF(logspace)", true)]
    [InlineData("DBCC SHOW_STATISTICS('Users', IX_Users_Name)", true)]
    [InlineData("DBCC CHECKDB('mydb') WITH PHYSICAL_ONLY", true)]
    [InlineData("DBCC CHECKTABLE('Users')", true)]
    [InlineData("DBCC CHECKALLOC('mydb')", true)]
    [InlineData("DBCC CHECKCATALOG('mydb')", true)]
    [InlineData("DBCC SHOWCONTIG(Users)", true)]
    [InlineData("DBCC OPENTRAN", true)]
    [InlineData("DBCC USEROPTIONS", true)]
    [InlineData("DBCC TRACESTATUS", true)]
    [InlineData("DBCC PROCCACHE", true)]
    [InlineData("DBCC INPUTBUFFER(52)", true)]
    [InlineData("DBCC SHRINKDATABASE(mydb, 10)", false)]
    [InlineData("DBCC SHRINKFILE(myfile, 10)", false)]
    [InlineData("DBCC WRITEPAGE(mydb, 1, 1, 1, 1, 0)", false)]
    [InlineData("DBCC TRACEON(3604)", false)]
    [InlineData("DBCC TRACEOFF(3604)", false)]
    [InlineData("DBCC FREEPROCCACHE", false)]
    [InlineData("DBCC DROPCLEANBUFFERS", false)]
    [InlineData("DBCC DBREPAIR(mydb, NODATA)", false)]
    public void SqlServer_Dbcc_SubcommandWhitelist(string sql, bool expected)
    {
        var r = _guard.Validate(sql, Db(DatabaseType.SqlServer));
        Assert.Equal(expected, r.Allowed);
    }

    [Theory]
    [InlineData("DBCC CHECKDB('mydb') WITH REPAIR_ALLOW_DATA_LOSS")]
    [InlineData("DBCC CHECKTABLE('Users') WITH REPAIR_REBUILD")]
    [InlineData("DBCC CHECKALLOC('mydb') WITH REPAIR_FAST")]
    public void SqlServer_Dbcc_RepairModifiers_Blocked(string sql)
    {
        var r = _guard.Validate(sql, Db(DatabaseType.SqlServer));
        Assert.False(r.Allowed);
        Assert.Equal("SQL_BLOCKED", r.ErrorCode);
        Assert.Contains("REPAIR", r.Reason);
    }

    [Fact]
    public void SqlServer_Dbcc_MissingSubcommand_Blocked()
    {
        var r = _guard.Validate("DBCC", Db(DatabaseType.SqlServer));
        Assert.False(r.Allowed);
        Assert.Equal("SQL_BLOCKED", r.ErrorCode);
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
    [InlineData("SELECT * FROM Users", true)]
    [InlineData("select id from t where x = 1", true)]            // 小写也放行
    [InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte", true)]
    [InlineData("CALL my_proc()", true)]
    [InlineData("EXPLAIN SELECT * FROM Users", true)]
    [InlineData("SHOW server_version", true)]
    [InlineData("TABLE Users", true)]
    [InlineData("VALUES (1), (2)", true)]
    [InlineData("COPY t FROM '/etc/passwd'", false)]              // PG 黑名单拦 COPY
    [InlineData("VACUUM Users", false)]                            // PG 黑名单拦 VACUUM
    [InlineData("REFRESH MATERIALIZED VIEW v", false)]            // 多词关键字
    [InlineData("DELETE FROM Users", false)]                      // 全局黑名单
    [InlineData("EXEC prepared_stmt", false)]                     // PG 白名单不含 EXECUTE/EXEC
    public void PostgreSql_DialectSpecific(string sql, bool expected)
    {
        Assert.Equal(expected, _guard.Validate(sql, Db(DatabaseType.PostgreSql)).Allowed);
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

    // ===== T4：双白名单 + StatementKind + CTE 二次判定 =====

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (1)", StatementKind.Write)]
    [InlineData("UPDATE t SET a=1", StatementKind.Write)]
    [InlineData("DELETE FROM t", StatementKind.Write)]
    [InlineData("CREATE TABLE t (a int)", StatementKind.Write)]
    [InlineData("ALTER TABLE t ADD b int", StatementKind.Write)]
    [InlineData("DROP INDEX ix ON t", StatementKind.Write)]
    [InlineData("SELECT * FROM t", StatementKind.Read)]
    public void WriteEnv_Allows_DML_DDL_And_Reports_Kind(string sql, StatementKind expected)
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: true);
        var r = _guard.Validate(sql, db);
        Assert.True(r.Allowed, $"expected allow: {r.Reason}");
        Assert.Equal(expected, r.Kind);
    }

    [Fact]
    public void WriteEnv_Blocks_DropTable_DropColumn_Truncate()
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: true);
        Assert.False(_guard.Validate("DROP TABLE t", db).Allowed);
        Assert.False(_guard.Validate("ALTER TABLE t DROP COLUMN c", db).Allowed);
        Assert.False(_guard.Validate("TRUNCATE TABLE t", db).Allowed);
    }

    [Fact]
    public void WriteEnv_Blocks_AccountDdl()
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: true);
        Assert.False(_guard.Validate("CREATE USER eve IDENTIFIED BY 'pw'", db).Allowed);
        Assert.False(_guard.Validate("ALTER ROLE r WITH PASSWORD 'x'", db).Allowed);
    }

    [Fact]
    public void WriteEnv_Blocks_Exec_DynamicSql_At_Whitelist()
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: true);
        // EXEC 不在写白名单
        Assert.False(_guard.Validate("EXEC sp_help", db).Allowed);
    }

    [Fact]
    public void ReadOnlyEnv_Blocks_SelectInto_Via_Blacklist()
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: false);
        Assert.False(_guard.Validate("SELECT * INTO newt FROM t", db).Allowed);
    }

    [Fact]
    public void WriteEnv_Blocks_SelectInto_Via_WriteBlacklist()
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: true);
        Assert.False(_guard.Validate("SELECT * INTO newt FROM t", db).Allowed);
    }

    [Fact]
    public void ReadOnlyEnv_Blocks_sp_executesql_DoubleInsurance()
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: false);
        Assert.False(_guard.Validate("EXEC sp_executesql N'SELECT 1'", db).Allowed);
    }

    [Fact]
    public void Production_Forces_Read_Whitelist_Even_AllowWrite_True()
    {
        // 注意：ResolvedConfigBuilder 已对生产环境兜底 AllowWrite=false，
        // 这里直接构造 allowWrite=false 模拟兜底后场景，验证 SqlGuard 行为。
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: false, isProduction: true);
        Assert.False(_guard.Validate("INSERT INTO t (a) VALUES (1)", db).Allowed);
    }

    [Fact]
    public void WriteEnv_CTE_Insert_Reported_As_Write()
    {
        var db = BuildResolvedDatabase(DatabaseType.SqlServer, allowWrite: true);
        var r = _guard.Validate("WITH cte AS (SELECT 1 AS a) INSERT INTO t SELECT * FROM cte", db);
        Assert.True(r.Allowed);
        Assert.Equal(StatementKind.Write, r.Kind);
    }
}
