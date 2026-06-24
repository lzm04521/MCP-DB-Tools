namespace McpDbTools.Server.Configuration;

/// <summary>
/// 内置默认阻止关键字集合。当 config.json 未提供 defaultDisabledKeywords 时回退到此值。
/// 集中维护，便于版本升级时统一调整。
/// </summary>
public static class DefaultDisabledKeywords
{
    /// <summary>
    /// 全局通用阻止关键字（所有数据库类型生效）。
    /// 注意：此处为写操作的通用动词，数据库特有命令放在 defaultDisabledKeywordsByType。
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltIn = new[]
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
        "SHUTDOWN"
    };

    /// <summary>
    /// 按数据库类型追加的阻止关键字。覆盖各数据库特有的危险命令。
    /// </summary>
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
            }
        };
}
