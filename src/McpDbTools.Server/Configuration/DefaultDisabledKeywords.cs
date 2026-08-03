namespace McpDbTools.Server.Configuration;

/// <summary>
/// 内置默认阻止关键字集合。当 config.json 未提供 defaultDisabledKeywords 时回退到此值。
/// 集中维护，便于版本升级时统一调整。
/// </summary>
public static class DefaultDisabledKeywords
{
    /// <summary>
    /// 只读环境（AllowWrite=false）的全局默认阻止关键字。
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInReadOnly = new[]
    {
        "DROP",
        "DELETE",
        "UPDATE",
        "INSERT",
        "ALTER",
        "CREATE",
        "TRUNCATE",
        "MERGE",
        "GRANT",
        "REVOKE",
        "REPLACE",
        "BACKUP",
        "RESTORE",
        "KILL",
        "SHUTDOWN",
        "SELECT INTO",
        "sp_executesql",
        "EXECUTE IMMEDIATE",
        "PREPARE"
    };

    /// <summary>
    /// 写环境（AllowWrite=true）的全局默认阻止关键字。
    /// 放开 DML 业务写（INSERT/UPDATE/DELETE/MERGE/REPLACE）与 DDL 新增改（CREATE/ALTER/DROP INDEX），
    /// 保留结构删除、字段约束删除、库级修改、账号权限、系统级、动态 SQL 等。
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInWrite = new[]
    {
        // 结构删除（DROP INDEX 不在此列，允许删索引）
        "DROP TABLE",
        "DROP DATABASE",
        "DROP SCHEMA",
        "DROP VIEW",
        "DROP PROCEDURE",
        "DROP FUNCTION",
        "DROP TRIGGER",
        "DROP TYPE",
        "DROP SYNONYM",
        "DROP SEQUENCE",
        // 字段/约束删除
        "DROP COLUMN",
        "DROP CONSTRAINT",
        // 库级修改
        "ALTER DATABASE",
        "ALTER SCHEMA",
        // 清表
        "TRUNCATE",
        // 快捷建表写（强制显式 CREATE+INSERT）
        "SELECT INTO",
        // 权限/账号
        "GRANT",
        "REVOKE",
        "CREATE USER",
        "CREATE ROLE",
        "ALTER USER",
        "ALTER ROLE",
        "DROP USER",
        "DROP ROLE",
        // 系统级
        "BACKUP",
        "RESTORE",
        "KILL",
        "SHUTDOWN",
        // MySQL 复制/binlog/全局配置
        "RESET MASTER",
        "RESET REPLICA",
        "PURGE BINARY LOGS",
        "SET GLOBAL",
        "SET PERSIST",
        // SQL Server 数据页直写
        "DBCC WRITEPAGE",
        // 动态 SQL（多语句注入防线）
        "sp_executesql",
        "EXECUTE IMMEDIATE",
        "PREPARE"
    };

    /// <summary>按数据库类型追加的阻止关键字。覆盖各数据库特有的危险命令。</summary>
    public static readonly IReadOnlyDictionary<DatabaseType, IReadOnlyList<string>> BuiltInByType =
        new Dictionary<DatabaseType, IReadOnlyList<string>>
        {
            [DatabaseType.SqlServer] = new[]
            {
                "BULK INSERT",
                "OPENROWSET",
                "OPENDATASOURCE",
                "xp_cmdshell",
                "sp_configure"
            },
            [DatabaseType.MySql] = new[]
            {
                "LOAD DATA",
                "FLUSH",
                "OPTIMIZE",
                "REPAIR",
                "CHECKSUM",
                "HANDLER"
            },
            [DatabaseType.Oracle] = new[]
            {
                "FLASHBACK",
                "PURGE",
                "ALTER SYSTEM",
                "ALTER DATABASE",
                "AUDIT",
                "NOAUDIT"
            },
            [DatabaseType.PostgreSql] = new[]
            {
                "COPY",
                "VACUUM",
                "REINDEX",
                "CLUSTER",
                "REFRESH MATERIALIZED VIEW",
                "ANALYZE",
                "NOTIFY"
            }
        };
}
