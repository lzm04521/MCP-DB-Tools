using System.Text.RegularExpressions;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Security;

/// <summary>SQL 语句分类，供 DbQueryTool 选执行路径。</summary>
public enum StatementKind { Read, Write }

/// <summary>
/// SQL 校验结果。
/// </summary>
public sealed record SqlGuardResult
{
    public bool Allowed { get; init; }
    public StatementKind Kind { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;

    public static SqlGuardResult Allow(StatementKind kind = StatementKind.Read) => new() { Allowed = true, Kind = kind };
    public static SqlGuardResult Deny(string reason, string code) => new() { Allowed = false, Reason = reason, ErrorCode = code };
}

/// <summary>
/// SQL 安全守卫：白名单（按数据库类型）+ 黑名单（三层合并的阻止关键字）双重校验。
/// </summary>
public interface ISqlGuard
{
    /// <summary>校验 SQL 是否允许执行。</summary>
    SqlGuardResult Validate(string sql, ResolvedDatabase database);
}

/// <summary>
/// SQL 安全守卫实现。
/// <para>
/// 校验流程：
/// <list type="number">
/// <item>去除注释（-- 与 /* */）并规范化空白</item>
/// <item>提取首句首关键字，按数据库类型做白名单判断</item>
/// <item>对整段 SQL 做黑名单检查，拦截多语句注入（如 SELECT 1; DROP TABLE x）</item>
/// </list>
/// </para>
/// <para>
/// 已知限制：不解析字符串字面量，字符串内的关键字可能被误判。安全工具宁可误拒，故可接受。
/// </para>
/// </summary>
public sealed class SqlGuard : ISqlGuard
{
    // 块注释 /* ... */（跨行）与行注释 -- ...
    private static readonly Regex CommentPattern = new(@"/\*.*?\*/|--[^\r\n]*", RegexOptions.Singleline | RegexOptions.Compiled);
    // 连续空白归一
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>白名单：按数据库类型允许的首关键字集合。</summary>
    private static readonly IReadOnlyDictionary<DatabaseType, IReadOnlySet<string>> WhitelistByType =
        new Dictionary<DatabaseType, IReadOnlySet<string>>
        {
            [DatabaseType.SqlServer] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SELECT", "WITH", "EXEC", "EXECUTE", "DBCC",
                "SP_HELP", "SP_TABLES", "SP_COLUMNS", "SP_PKEYS", "SP_SPACEUSED", "SP_HELPTEXT"
            },
            [DatabaseType.MySql] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SELECT", "WITH", "EXEC", "EXECUTE", "CALL",
                "SHOW", "DESCRIBE", "DESC", "EXPLAIN"
            },
            [DatabaseType.Oracle] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SELECT", "WITH", "EXEC", "EXECUTE", "CALL",
                "DESCRIBE", "DESC"
            },
            [DatabaseType.PostgreSql] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SELECT", "WITH", "CALL", "EXPLAIN", "SHOW", "TABLE", "VALUES"
            }
        };

    /// <summary>
    /// 写白名单：在只读白名单基础上追加的写动词首关键字。仅用于 AllowWrite=true 环境。
    /// <para>不放 EXEC/EXECUTE/CALL/PREPARE —— 动态 SQL 是多语句注入主路径，必须经黑名单拦截。</para>
    /// </summary>
    private static readonly IReadOnlySet<string> WriteVerbKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "MERGE", "REPLACE"
        };

    /// <summary>
    /// 动态 SQL 入口关键字。写环境下即使首关键字在只读白名单也拒绝（多语句注入防线），
    /// 只读环境继续按只读白名单放行（保持既有行为）。
    /// </summary>
    private static readonly IReadOnlySet<string> DynamicSqlKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EXEC", "EXECUTE", "CALL", "PREPARE"
        };

    /// <summary>SqlServer DBCC 只读诊断子命令白名单（仅基础只读形态，修复选项单独拦截）。</summary>
    private static readonly IReadOnlySet<string> SqlServerDbccReadOnlySubcommands =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SHOW_STATISTICS", "SHOWCONTIG",
            "CHECKDB", "CHECKTABLE", "CHECKALLOC", "CHECKCATALOG",
            "SQLPERF", "INPUTBUFFER", "OPENTRAN", "USEROPTIONS",
            "TRACESTATUS", "PROCCACHE"
        };

    /// <summary>DBCC 修复选项修饰符（即使子命令在白名单也拒）。</summary>
    private static readonly IReadOnlySet<string> DbccRepairModifiers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "REPAIR_ALLOW_DATA_LOSS", "REPAIR_REBUILD", "REPAIR_FAST", "REPAIR"
        };

    public SqlGuardResult Validate(string sql, ResolvedDatabase database)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlGuardResult.Deny("SQL 语句为空", "SQL_PARSE_ERROR");
        }

        // 1. 去注释 + 规范化
        string cleaned = CommentPattern.Replace(sql, " ");
        string normalized = WhitespacePattern.Replace(cleaned, " ").Trim().ToUpperInvariant();

        if (normalized.Length == 0)
        {
            return SqlGuardResult.Deny("SQL 语句去注释后为空", "SQL_PARSE_ERROR");
        }

        // 2. 白名单：提取首句首关键字
        //    多语句按分号切分，只取第一句判断首关键字
        string firstStatement = normalized.Split(';')[0].Trim();
        string firstKeyword = firstStatement.Split(' ', 2)[0];

        // 双白名单：AllowWrite=true 时允许只读白名单 ∪ 写动词首关键字；
        // AllowWrite=false（含生产兜底）只放行只读白名单，行为同今天。
        // 写环境额外排斥 EXEC/EXECUTE/CALL/PREPARE：这些是动态 SQL 入口，
        // 即使在只读白名单也拒绝，避免绕过黑名单（如 sp_executesql）。
        WhitelistByType.TryGetValue(database.Type, out var whitelist);
        bool allowWrite = database.AllowWrite;
        bool inReadOnlyWhitelist = whitelist is not null && whitelist.Contains(firstKeyword);
        bool isWriteVerb = WriteVerbKeywords.Contains(firstKeyword);
        bool isDynamicSql = DynamicSqlKeywords.Contains(firstKeyword);
        bool whitelistPass = allowWrite
            ? ((inReadOnlyWhitelist || isWriteVerb) && !isDynamicSql)
            : inReadOnlyWhitelist;

        if (!whitelistPass)
        {
            return SqlGuardResult.Deny(
                $"不允许的语句类型: {firstKeyword}（数据库类型 {database.Type} " +
                $"{(allowWrite ? "允许只读与写操作（动态 SQL 入口拒）" : "仅允许只读查询")}）",
                "SQL_BLOCKED");
        }

        // 2.5 DBCC 子命令二次校验：仅放行只读诊断白名单子命令，禁带修复选项
        if (firstKeyword.Equals("DBCC", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateDbcc(firstStatement, normalized);
        }

        // 3. 黑名单：对整段 SQL 检查阻止关键字（含多语句，拦截注入）
        //    每个关键字用词边界匹配，避免误伤包含该词的标识符
        foreach (string keyword in database.DisabledKeywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            string pattern = BuildKeywordPattern(keyword);
            if (Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return SqlGuardResult.Deny(
                    $"SQL 包含被阻止的关键字: {keyword}",
                    "SQL_BLOCKED");
            }
        }

        // 4. CTE 写二次判定：WITH 开头时扫整段是否含写动词（如 WITH cte AS (...) INSERT ...）。
        //    写动词首关键字（INSERT/UPDATE/...）直接标 Write；
        //    WITH 包裹的写语句需要二次扫描，因为首关键字是 WITH 而非写动词。
        StatementKind kind = StatementKind.Read;
        if (allowWrite)
        {
            if (isWriteVerb)
            {
                kind = StatementKind.Write;
            }
            else if (firstKeyword.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string verb in WriteVerbKeywords)
                {
                    if (Regex.IsMatch(normalized, $@"\b{verb}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        kind = StatementKind.Write;
                        break;
                    }
                }
            }
        }
        return SqlGuardResult.Allow(kind);
    }

    /// <summary>
    /// DBCC 子命令二次校验：仅放行只读诊断白名单子命令，且禁带修复选项。
    /// </summary>
    private static SqlGuardResult ValidateDbcc(string firstStatement, string normalized)
    {
        var tokens = firstStatement.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return SqlGuardResult.Deny("DBCC 缺少子命令", "SQL_BLOCKED");
        }

        // 子命令可能紧贴括号，如 SQLPERF(logspace)
        string sub = tokens[1];
        int paren = sub.IndexOf('(');
        if (paren >= 0) sub = sub[..paren];

        if (!SqlServerDbccReadOnlySubcommands.Contains(sub))
        {
            return SqlGuardResult.Deny(
                $"不允许的 DBCC 子命令: {sub}（仅允许只读诊断子命令）",
                "SQL_BLOCKED");
        }

        // 修复选项检测：CHECKDB/CHECKTABLE/CHECKALLOC 带 REPAIR_* 时写数据
        foreach (string mod in DbccRepairModifiers)
        {
            string pattern = @$"\b{Regex.Escape(mod)}\b";
            if (Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return SqlGuardResult.Deny(
                    $"DBCC 修复选项不允许: {mod}",
                    "SQL_BLOCKED");
            }
        }

        return SqlGuardResult.Allow();
    }

    /// <summary>
    /// 构造关键字的词边界匹配模式。
    /// 默认按 \s+ 相邻连接多词关键字（BULK INSERT, ALTER DATABASE 等短语型）。
    /// SELECT INTO 特殊：SELECT 与 INTO 之间可能有列/星号（"SELECT * INTO newt"），
    /// 但又不能误伤 "SELECT ... INSERT INTO"（INSERT INTO 已有表是合法写），
    /// 故用 negative-lookahead 排除中间出现 INSERT/UPDATE/DELETE 等写动词的场景。
    /// </summary>
    private static string BuildKeywordPattern(string keyword)
    {
        string trimmed = keyword.Trim();

        // SELECT INTO 在 SQL Server 是建表写法，SELECT 与 INTO 间可能有列/星号；
        // 用 negative-lookahead 排除 "SELECT ... INSERT INTO" 这种合法的 INSERT INTO 已有表场景。
        if (trimmed.Equals("SELECT INTO", StringComparison.OrdinalIgnoreCase))
        {
            return @"\bSELECT\b(?:(?!\b(?:INSERT|UPDATE|DELETE|MERGE|REPLACE|CREATE|ALTER|DROP)\b)[^;])*?\bINTO\b";
        }

        // 默认：相邻连接（仅空白），多词短语如 BULK INSERT / ALTER DATABASE / DROP TABLE
        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string joined = string.Join(@"\s+", parts.Select(Regex.Escape));
        return @$"\b{joined}\b";
    }
}
