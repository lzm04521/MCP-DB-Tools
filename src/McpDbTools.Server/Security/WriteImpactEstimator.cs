using System.Text.RegularExpressions;

namespace McpDbTools.Server.Security;

/// <summary>
/// 写操作影响行数预估：UPDATE/DELETE 标准形态 → SELECT COUNT(*) 只读变换。
/// <para>严格形态匹配、宁拒不猜（与 SqlGuard 哲学一致）：SET 段含括号即拒——
/// SET 值一旦有子查询，正则无法可靠定位顶层 WHERE（字符串字面量中的括号会破坏配平，
/// 与 SqlGuard 已知限制同理，不做轻量解析）。</para>
/// <para>仅接受：UPDATE 表 SET ... WHERE ...（SET 段不含括号）、DELETE FROM 表 [WHERE ...]；
/// 表别名/多表/TOP/LIMIT 修饰/CTE 前缀/INSERT/DDL 一律拒绝（DRYRUN_UNSUPPORTED 由调用方返回）。</para>
/// <para>WHERE 段整段保留原文（含子查询），不重排不重写。</para>
/// </summary>
public static class WriteImpactEstimator
{
    private static readonly Regex CommentPattern = new(@"/\*.*?\*/|--[^\r\n]*", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    // 无别名单表 UPDATE：表名后紧跟 SET（别名/多表形态自然不匹配 → 拒绝）
    private static readonly Regex UpdatePattern = new(@"^UPDATE\s+(?<table>[A-Za-z][\w$#.]*)\s+SET\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DeletePattern = new(@"^DELETE\s+FROM\s+(?<table>[A-Za-z][\w$#.]*)\s*(?<where>\bWHERE\b.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    public static bool TryBuildCountSql(string sql, out string countSql, out string reason)
    {
        countSql = string.Empty;
        // 与 SqlGuard 一致的预处理：去注释 + 空白归一（保留原文大小写，WHERE 段原样拼接）
        string normalized = WhitespacePattern.Replace(CommentPattern.Replace(sql, " "), " ").Trim();

        Match update = UpdatePattern.Match(normalized);
        if (update.Success)
        {
            string rest = normalized[update.Length..];
            Match where = Regex.Match(rest, @"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!where.Success)
            {
                reason = "UPDATE 无 WHERE 子句（将更新全表），不支持 dryRun 预估";
                return false;
            }
            string setSegment = rest[..where.Index];
            string whereSegment = rest[where.Index..];
            if (setSegment.Contains('('))
            {
                // SET 值含子查询时无法可靠定位顶层 WHERE（字符串字面量中的括号破坏配平），宁拒不猜
                reason = "UPDATE 的 SET 段含子查询（括号），无法可靠预估，不支持 dryRun";
                return false;
            }
            if (Regex.IsMatch(whereSegment, @"\bLIMIT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                reason = "UPDATE 带 LIMIT 修饰，不支持 dryRun 预估";
                return false;
            }
            countSql = $"SELECT COUNT(*) FROM {update.Groups["table"].Value} {whereSegment}";
            reason = string.Empty;
            return true;
        }

        Match delete = DeletePattern.Match(normalized);
        if (delete.Success)
        {
            string whereSegment = delete.Groups["where"].Value;
            if (Regex.IsMatch(whereSegment, @"\bLIMIT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                reason = "DELETE 带 LIMIT 修饰，不支持 dryRun 预估";
                return false;
            }
            countSql = string.IsNullOrWhiteSpace(whereSegment)
                ? $"SELECT COUNT(*) FROM {delete.Groups["table"].Value}"
                : $"SELECT COUNT(*) FROM {delete.Groups["table"].Value} {whereSegment}";
            reason = string.Empty;
            return true;
        }

        reason = "非标准形态（INSERT/DDL/别名/多表/TOP/CTE 等），不支持 dryRun 预估";
        return false;
    }
}
