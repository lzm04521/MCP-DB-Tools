using System.Text.RegularExpressions;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Security;

/// <summary>
/// SQL 校验结果。
/// </summary>
public sealed record SqlGuardResult
{
    public bool Allowed { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;

    public static SqlGuardResult Allow() => new() { Allowed = true };
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
                "SELECT", "WITH", "EXEC", "EXECUTE",
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
            }
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

        if (!WhitelistByType.TryGetValue(database.Type, out var whitelist) ||
            !whitelist.Contains(firstKeyword))
        {
            return SqlGuardResult.Deny(
                $"不允许的语句类型: {firstKeyword}（数据库类型 {database.Type} 仅允许只读查询）",
                "SQL_BLOCKED");
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

        return SqlGuardResult.Allow();
    }

    /// <summary>
    /// 构造关键字的词边界匹配模式。
    /// 单词关键字（DROP）用 \b 包围；多词关键字（BULK INSERT）整体用 \b 包围。
    /// </summary>
    private static string BuildKeywordPattern(string keyword)
    {
        string escaped = Regex.Escape(keyword.Trim());
        return @$"\b{escaped}\b";
    }
}
