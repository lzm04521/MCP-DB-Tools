using McpDbTools.Server.Configuration;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>元数据查询段模板：Name 供返回 JSON 分段，Sql 供执行，HasTableParam 标识是否带表名参数。</summary>
public sealed record SchemaSectionTemplate(string Name, string Sql, bool HasTableParam);

/// <summary>
/// 四方言元数据 SQL 模板（固定文本，表名过滤一律经 @table 参数化，无标识符拼接）。
/// <para>字段别名跨方言统一：表清单 schema_name/table_name/table_comment/row_count；
/// 列 column_name/data_type/is_nullable/column_default/column_comment/is_primary_key；
/// 索引 index_name/is_unique/is_primary/column_name/ordinal；外键 fk_name/column_name/ref_table/ref_column。</para>
/// <para>Oracle all_* 视图仅返回当前用户有权限可见的对象（权限特性，非缺陷）；
/// Oracle 元数据字典区分大小写，模板内以 UPPER(@table) 匹配（PG 对应 LOWER）。</para>
/// <para>模糊匹配（TablesLikeSql/ColumnSearchSql 非 exact）统一 ESCAPE '!'：调用方传值需经
/// QueryErrorAssist.EscapeLikePattern 转义（_→!_、!→!!，% 保留通配）。不用反斜杠——
/// MySQL NO_BACKSLASH_ESCAPES 模式下 '\' 字面量解析因模式而异。</para>
/// </summary>
public static class SchemaDialects
{
    public const string TableParamName = "@table";

    /// <summary>LIKE 模式值转义（ESCAPE '!' 约定）：!→!!、_→!_，% 保留为通配符。仅含 % 的模糊模式需转义；精确匹配传原值。</summary>
    public static string EscapeLikePattern(string pattern) => pattern.Replace("!", "!!").Replace("_", "!_");

    /// <summary>表清单模板（table=null 用）。返回单段 [tables]。</summary>
    public static IReadOnlyList<SchemaSectionTemplate> Tables(DatabaseType type) => new[]
    {
        new SchemaSectionTemplate("tables", TablesSql(type), HasTableParam: false),
    };

    /// <summary>单表详情模板（table 非空用）。返回三段 [columns, indexes, foreignKeys]。</summary>
    public static IReadOnlyList<SchemaSectionTemplate> TableDetail(DatabaseType type) => new[]
    {
        new SchemaSectionTemplate("columns", ColumnsSql(type), HasTableParam: true),
        new SchemaSectionTemplate("indexes", IndexesSql(type), HasTableParam: true),
        new SchemaSectionTemplate("foreignKeys", ForeignKeysSql(type), HasTableParam: true),
    };

    /// <summary>
    /// 表名模糊搜索 SQL（table 含 % 的模糊模式与 P1 错误自愈共用）。
    /// @table 绑定 LIKE 模式值（% 通配、_ 字面，经 EscapeLikePattern 转义 + ESCAPE '!'）。
    /// 输出列与表清单模板同构。
    /// </summary>
    public static string TablesLikeSql(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => """
            SELECT s.name AS schema_name, t.name AS table_name,
                   CAST(ep.value AS nvarchar(500)) AS table_comment,
                   SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS row_count
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.partitions p ON p.object_id = t.object_id
            LEFT JOIN sys.extended_properties ep ON ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
            WHERE t.name LIKE @table ESCAPE '!'
            GROUP BY s.name, t.name, CAST(ep.value AS nvarchar(500))
            ORDER BY schema_name, table_name
            """,
        DatabaseType.MySql => """
            SELECT TABLE_SCHEMA AS schema_name, TABLE_NAME AS table_name,
                   TABLE_COMMENT AS table_comment, TABLE_ROWS AS row_count
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'
              AND TABLE_NAME LIKE @table ESCAPE '!'
            ORDER BY table_name
            """,
        DatabaseType.Oracle => """
            SELECT t.owner AS schema_name, t.table_name AS table_name,
                   c.comments AS table_comment, t.num_rows AS row_count
            FROM all_tables t
            LEFT JOIN all_tab_comments c ON c.owner = t.owner AND c.table_name = t.table_name
            WHERE t.table_name LIKE UPPER(@table) ESCAPE '!'
            ORDER BY t.owner, t.table_name
            """,
        DatabaseType.PostgreSql => """
            SELECT n.nspname AS schema_name, c.relname AS table_name,
                   pg_catalog.obj_description(c.oid, 'pg_class') AS table_comment,
                   c.reltuples::bigint AS row_count
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND c.relname LIKE LOWER(@table) ESCAPE '!'
            ORDER BY n.nspname, c.relname
            """,
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}"),
    };

    /// <summary>
    /// 列名反查 SQL（db_schema column 参数）：返回含该列的表清单，输出列 schema_name/table_name/column_name。
    /// @table 绑定列名（exact）或 LIKE 模式值（非 exact，% 通配、_ 字面）。
    /// </summary>
    public static string ColumnSearchSql(DatabaseType type, bool exact) => type switch
    {
        DatabaseType.SqlServer => exact
            ? """
              SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name
              FROM sys.tables t
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.columns c ON c.object_id = t.object_id
              WHERE c.name = @table
              ORDER BY s.name, t.name, c.column_id
              """
            : """
              SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name
              FROM sys.tables t
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.columns c ON c.object_id = t.object_id
              WHERE c.name LIKE @table ESCAPE '!'
              ORDER BY s.name, t.name, c.column_id
              """,
        DatabaseType.MySql => exact
            ? """
              SELECT TABLE_SCHEMA AS schema_name, TABLE_NAME AS table_name, COLUMN_NAME AS column_name
              FROM information_schema.COLUMNS
              WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME = @table
              ORDER BY TABLE_NAME, ORDINAL_POSITION
              """
            : """
              SELECT TABLE_SCHEMA AS schema_name, TABLE_NAME AS table_name, COLUMN_NAME AS column_name
              FROM information_schema.COLUMNS
              WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME LIKE @table ESCAPE '!'
              ORDER BY TABLE_NAME, ORDINAL_POSITION
              """,
        DatabaseType.Oracle => exact
            ? """
              SELECT c.owner AS schema_name, c.table_name AS table_name, c.column_name AS column_name
              FROM all_tab_columns c
              WHERE c.column_name = UPPER(@table)
              ORDER BY c.owner, c.table_name, c.column_id
              """
            : """
              SELECT c.owner AS schema_name, c.table_name AS table_name, c.column_name AS column_name
              FROM all_tab_columns c
              WHERE c.column_name LIKE UPPER(@table) ESCAPE '!'
              ORDER BY c.owner, c.table_name, c.column_id
              """,
        DatabaseType.PostgreSql => exact
            ? """
              SELECT n.nspname AS schema_name, t.relname AS table_name, a.attname AS column_name
              FROM pg_attribute a
              JOIN pg_class t ON t.oid = a.attrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              WHERE a.attnum > 0 AND NOT a.attisdropped AND t.relkind = 'r'
                AND a.attname = LOWER(@table)
              ORDER BY n.nspname, t.relname, a.attnum
              """
            : """
              SELECT n.nspname AS schema_name, t.relname AS table_name, a.attname AS column_name
              FROM pg_attribute a
              JOIN pg_class t ON t.oid = a.attrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              WHERE a.attnum > 0 AND NOT a.attisdropped AND t.relkind = 'r'
                AND a.attname LIKE LOWER(@table) ESCAPE '!'
              ORDER BY n.nspname, t.relname, a.attnum
              """,
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}"),
    };

    private static string TablesSql(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => """
            SELECT s.name AS schema_name, t.name AS table_name,
                   CAST(ep.value AS nvarchar(500)) AS table_comment,
                   SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS row_count
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.partitions p ON p.object_id = t.object_id
            LEFT JOIN sys.extended_properties ep ON ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
            GROUP BY s.name, t.name, CAST(ep.value AS nvarchar(500))
            ORDER BY schema_name, table_name
            """,
        DatabaseType.MySql => """
            SELECT TABLE_SCHEMA AS schema_name, TABLE_NAME AS table_name,
                   TABLE_COMMENT AS table_comment, TABLE_ROWS AS row_count
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY table_name
            """,
        DatabaseType.Oracle => """
            SELECT t.owner AS schema_name, t.table_name AS table_name,
                   c.comments AS table_comment, t.num_rows AS row_count
            FROM all_tables t
            LEFT JOIN all_tab_comments c ON c.owner = t.owner AND c.table_name = t.table_name
            ORDER BY t.owner, t.table_name
            """,
        DatabaseType.PostgreSql => """
            SELECT n.nspname AS schema_name, c.relname AS table_name,
                   pg_catalog.obj_description(c.oid, 'pg_class') AS table_comment,
                   c.reltuples::bigint AS row_count
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY n.nspname, c.relname
            """,
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}"),
    };

    /// <summary>单表列清单 SQL（TableDetail 组装用；P1 错误自愈附列清单复用，故 internal）。</summary>
    internal static string ColumnsSql(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => """
            SELECT c.name AS column_name, tp.name AS data_type,
                   CASE WHEN c.is_nullable = 1 THEN 1 ELSE 0 END AS is_nullable,
                   OBJECT_DEFINITION(c.default_object_id) AS column_default,
                   CAST(ep.value AS nvarchar(500)) AS column_comment,
                   CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS is_primary_key
            FROM sys.columns c
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            LEFT JOIN sys.extended_properties ep ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
            LEFT JOIN (SELECT ic.object_id, ic.column_id FROM sys.indexes i
                       JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                       WHERE i.is_primary_key = 1) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@table)
            ORDER BY c.column_id
            """,
        DatabaseType.MySql => """
            SELECT COLUMN_NAME AS column_name, COLUMN_TYPE AS data_type,
                   CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS is_nullable,
                   COLUMN_DEFAULT AS column_default, COLUMN_COMMENT AS column_comment,
                   CASE WHEN COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END AS is_primary_key
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """,
        DatabaseType.Oracle => """
            SELECT c.column_name, c.data_type,
                   CASE WHEN c.nullable = 'Y' THEN 1 ELSE 0 END AS is_nullable,
                   c.data_default AS column_default, cc.comments AS column_comment,
                   CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS is_primary_key
            FROM all_tab_columns c
            LEFT JOIN all_col_comments cc ON cc.owner = c.owner AND cc.table_name = c.table_name AND cc.column_name = c.column_name
            LEFT JOIN (SELECT ac.owner, ac.table_name, acc.column_name FROM all_constraints ac
                       JOIN all_cons_columns acc ON acc.owner = ac.owner AND acc.constraint_name = ac.constraint_name
                       WHERE ac.constraint_type = 'P') pk
                  ON pk.owner = c.owner AND pk.table_name = c.table_name AND pk.column_name = c.column_name
            WHERE c.table_name = UPPER(@table)
            ORDER BY c.column_id
            """,
        DatabaseType.PostgreSql => """
            SELECT cols.column_name, cols.data_type,
                   CASE WHEN cols.is_nullable = 'YES' THEN 1 ELSE 0 END AS is_nullable,
                   cols.column_default, '' AS column_comment,
                   CASE WHEN tc.constraint_type = 'PRIMARY KEY' THEN 1 ELSE 0 END AS is_primary_key
            FROM information_schema.columns cols
            LEFT JOIN information_schema.key_column_usage kcu
                  ON kcu.table_schema = cols.table_schema AND kcu.table_name = cols.table_name AND kcu.column_name = cols.column_name
            LEFT JOIN information_schema.table_constraints tc
                  ON tc.constraint_name = kcu.constraint_name AND tc.constraint_type = 'PRIMARY KEY'
            WHERE cols.table_schema = current_schema() AND cols.table_name = @table
            ORDER BY cols.ordinal_position
            """,
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}"),
    };

    private static string IndexesSql(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => """
            SELECT i.name AS index_name, CASE WHEN i.is_unique = 1 THEN 1 ELSE 0 END AS is_unique,
                   CASE WHEN i.is_primary_key = 1 THEN 1 ELSE 0 END AS is_primary,
                   c.name AS column_name, ic.key_ordinal AS ordinal
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.index_id > 0 AND i.is_disabled = 0 AND i.object_id = OBJECT_ID(@table)
            ORDER BY i.index_id, ic.key_ordinal
            """,
        DatabaseType.MySql => """
            SELECT INDEX_NAME AS index_name, CASE WHEN NON_UNIQUE = 0 THEN 1 ELSE 0 END AS is_unique,
                   CASE WHEN INDEX_NAME = 'PRIMARY' THEN 1 ELSE 0 END AS is_primary,
                   COLUMN_NAME AS column_name, SEQ_IN_INDEX AS ordinal
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """,
        DatabaseType.Oracle => """
            SELECT i.index_name, CASE WHEN i.uniqueness = 'UNIQUE' THEN 1 ELSE 0 END AS is_unique,
                   CASE WHEN c.constraint_type = 'P' THEN 1 ELSE 0 END AS is_primary,
                   ic.column_name, ic.column_position AS ordinal
            FROM all_indexes i
            JOIN all_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
            LEFT JOIN all_constraints c ON c.owner = i.owner AND c.index_name = i.index_name AND c.constraint_type = 'P'
            WHERE i.table_name = UPPER(@table)
            ORDER BY i.index_name, ic.column_position
            """,
        DatabaseType.PostgreSql => """
            SELECT i.relname AS index_name,
                   CASE WHEN ix.indisunique THEN 1 ELSE 0 END AS is_unique,
                   CASE WHEN ix.indisprimary THEN 1 ELSE 0 END AS is_primary,
                   a.attname AS column_name, array_position(ix.indkey, a.attnum) AS ordinal
            FROM pg_class t
            JOIN pg_index ix ON ix.indrelid = t.oid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
            WHERE t.relname = @table AND t.relkind = 'r'
            ORDER BY i.relname, ordinal
            """,
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}"),
    };

    private static string ForeignKeysSql(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => """
            SELECT fk.name AS fk_name, pc.name AS column_name,
                   rt.name AS ref_table, rc.name AS ref_column
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@table)
            ORDER BY fk.name, pc.column_id
            """,
        DatabaseType.MySql => """
            SELECT kcu.CONSTRAINT_NAME AS fk_name, kcu.COLUMN_NAME AS column_name,
                   kcu.REFERENCED_TABLE_NAME AS ref_table, kcu.REFERENCED_COLUMN_NAME AS ref_column
            FROM information_schema.KEY_COLUMN_USAGE kcu
            WHERE kcu.TABLE_SCHEMA = DATABASE() AND kcu.TABLE_NAME = @table
              AND kcu.REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY kcu.CONSTRAINT_NAME, kcu.ORDINAL_POSITION
            """,
        DatabaseType.Oracle => """
            SELECT ac.constraint_name AS fk_name, acc.column_name,
                   rc.table_name AS ref_table, rcc.column_name AS ref_column
            FROM all_constraints ac
            JOIN all_cons_columns acc ON acc.owner = ac.owner AND acc.constraint_name = ac.constraint_name
            JOIN all_constraints rc ON rc.owner = ac.r_owner AND rc.constraint_name = ac.r_constraint_name
            JOIN all_cons_columns rcc ON rcc.owner = rc.owner AND rcc.constraint_name = rc.constraint_name
                  AND rcc.position = acc.position
            WHERE ac.constraint_type = 'R' AND ac.table_name = UPPER(@table)
            ORDER BY ac.constraint_name, acc.position
            """,
        DatabaseType.PostgreSql => """
            SELECT con.conname AS fk_name, a.attname AS column_name,
                   rt.relname AS ref_table, ra.attname AS ref_column
            FROM pg_constraint con
            JOIN pg_class t ON t.oid = con.conrelid
            JOIN pg_class rt ON rt.oid = con.confrelid
            JOIN LATERAL unnest(con.conkey, con.confkey) AS keys(colnum, refnum) ON true
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = keys.colnum
            JOIN pg_attribute ra ON ra.attrelid = rt.oid AND ra.attnum = keys.refnum
            WHERE con.contype = 'f' AND t.relname = @table
            ORDER BY con.conname, a.attnum
            """,
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}"),
    };
}
