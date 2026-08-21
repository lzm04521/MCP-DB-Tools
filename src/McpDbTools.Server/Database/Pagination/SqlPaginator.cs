using System.Text.RegularExpressions;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Database;

/// <summary>TryAppend 的结果：拼接成功 / 需 ORDER BY（SqlServer、Oracle）/ 分页子句冲突。</summary>
public enum OffsetAppendOutcome { Appended, RequiresOrderBy, Conflict }

/// <summary>
/// offset 分页的方言拼接与冲突检测（无状态纯函数）。
/// <para>冲突检测（宁可误拒）：去尾分号后仍含分号=多语句 batch；末尾已含顶层
/// LIMIT/OFFSET/FETCH/FOR UPDATE|SHARE——直接追加会产生语法错误或语义错乱
/// （追加的分页子句只作用于最后一句，或如 Oracle 中 FOR UPDATE 与 OFFSET 互斥）。</para>
/// <para>ORDER BY 检测：SqlServer/Oracle 的 OFFSET..FETCH 语法要求 ORDER BY；
/// 采用"规范化全文含 ORDER BY 即放行"的宽松判定（子查询含 ORDER BY 的罕见形态误放行时，
/// 由数据库自身报错兜底），仅拦截"明显无排序"的常见场景。</para>
/// </summary>
public static class SqlPaginator
{
    private static readonly Regex CommentPattern = new(@"/\*.*?\*/|--[^\r\n]*", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    // 末尾顶层分页/锁子句：LIMIT n [OFFSET n] | OFFSET n [ROWS|ROW] [FETCH ...] | FOR UPDATE/SHARE [OF ...]
    private static readonly Regex TrailingPaginationPattern = new(
        @"(?:\bLIMIT\s+\d+(?:\s+OFFSET\s+\d+)?|\bOFFSET\s+\d+(?:\s+(?:ROWS|ROW))?(?:\s+FETCH\s+.*)?|\bFOR\s+(?:UPDATE|SHARE)(?:\s+OF\s+.*)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static OffsetAppendOutcome TryAppend(DatabaseType type, string sql, int offset, int fetch, out string paginated, out string reason)
    {
        // 与 SqlGuard 一致的规范化：去注释 → 空白归一 → 去尾分号
        string cleaned = CommentPattern.Replace(sql, " ");
        string normalized = WhitespacePattern.Replace(cleaned, " ").Trim().TrimEnd(';').Trim();

        if (normalized.Contains(';'))
        {
            (paginated, reason) = (string.Empty, "多语句 SQL 不支持 offset 分页（追加的分页子句只作用于最后一句），请拆分为单条语句");
            return OffsetAppendOutcome.Conflict;
        }

        if (TrailingPaginationPattern.IsMatch(normalized))
        {
            (paginated, reason) = (string.Empty, "SQL 末尾已含分页/锁子句（LIMIT/OFFSET/FETCH/FOR UPDATE|SHARE），请去掉后改用 offset 参数");
            return OffsetAppendOutcome.Conflict;
        }

        bool offsetFetchDialect = type is DatabaseType.SqlServer or DatabaseType.Oracle;
        if (offsetFetchDialect && !Regex.IsMatch(normalized, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase))
        {
            (paginated, reason) = (string.Empty, $"{type} 的 OFFSET..FETCH 分页语法要求 SQL 带 ORDER BY，请补 ORDER BY 后重试");
            return OffsetAppendOutcome.RequiresOrderBy;
        }

        paginated = offsetFetchDialect
            ? $"{normalized} OFFSET {offset} ROWS FETCH NEXT {fetch} ROWS ONLY"
            : $"{normalized} LIMIT {fetch} OFFSET {offset}";
        reason = string.Empty;
        return OffsetAppendOutcome.Appended;
    }
}
