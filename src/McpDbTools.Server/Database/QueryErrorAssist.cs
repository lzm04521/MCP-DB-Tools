using System.Text.RegularExpressions;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Database;

/// <summary>
/// db_query 错误自愈的纯函数部分（doc/20260901 P1）：猜列名/表名类失败（约占全部失败 40%）
/// 的错误消息分类、SQL 表名启发式提取、辅助文本格式化。查询编排在 DbQueryTool（走并发闸门与审计）。
/// </summary>
public static class QueryErrorAssist
{
    public enum AssistKind { None, InvalidColumn, InvalidTable }

    /// <summary>分类结果：Kind + 从错误消息提取的坏列名/表名（ORA-00942 不含名字，为 null）。</summary>
    public sealed record ErrorSignal(AssistKind Kind, string? BadName);

    // 四方言"列名/表名不存在"错误家族（IgnoreCase，匹配错误消息任意位置）
    private static readonly Regex SsInvalidColumn = new(@"Invalid column name '(?<n>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SsInvalidTable = new(@"Invalid object name '(?<n>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MyUnknownColumn = new(@"Unknown column '(?<n>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MyTableMissing = new(@"Table '(?<n>[^']+)' doesn't exist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OraInvalidIdentifier = new(@"ORA-00904:\s*""(?<n>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex OraTableMissing = new(@"ORA-00942", RegexOptions.Compiled);
    private static readonly Regex PgColumnMissing = new(@"column ""(?<n>[^""]+)"" does not exist", RegexOptions.Compiled);
    private static readonly Regex PgRelationMissing = new(@"relation ""(?<n>[^""]+)"" does not exist", RegexOptions.Compiled);

    /// <summary>
    /// 按数据库类型分类错误消息。未命中已知家族返回 None（超时/语法错/权限等不做辅助）。
    /// </summary>
    public static ErrorSignal Classify(DatabaseType type, string errorMessage)
    {
        Match m;
        switch (type)
        {
            case DatabaseType.SqlServer:
                m = SsInvalidColumn.Match(errorMessage);
                if (m.Success) return new ErrorSignal(AssistKind.InvalidColumn, m.Groups["n"].Value);
                m = SsInvalidTable.Match(errorMessage);
                if (m.Success) return new ErrorSignal(AssistKind.InvalidTable, m.Groups["n"].Value);
                break;
            case DatabaseType.MySql:
                m = MyUnknownColumn.Match(errorMessage);
                if (m.Success) return new ErrorSignal(AssistKind.InvalidColumn, m.Groups["n"].Value);
                m = MyTableMissing.Match(errorMessage);
                if (m.Success) return new ErrorSignal(AssistKind.InvalidTable, m.Groups["n"].Value);
                break;
            case DatabaseType.Oracle:
                m = OraInvalidIdentifier.Match(errorMessage);
                if (m.Success) return new ErrorSignal(AssistKind.InvalidColumn, m.Groups["n"].Value);
                if (OraTableMissing.IsMatch(errorMessage)) return new ErrorSignal(AssistKind.InvalidTable, null);
                break;
            case DatabaseType.PostgreSql:
                m = PgColumnMissing.Match(errorMessage);
                if (m.Success) return new ErrorSignal(AssistKind.InvalidColumn, m.Groups["n"].Value);
                m = PgRelationMissing.Match(errorMessage);
                if (m.Success) return new ErrorSignal(AssistKind.InvalidTable, m.Groups["n"].Value);
                break;
        }
        return new ErrorSignal(AssistKind.None, null);
    }

    // FROM/JOIN/UPDATE/INTO 后的标识符（可带 schema 前缀）；首字符须字母/下划线——天然跳过子查询括号
    private static readonly Regex TableRefPattern = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+([A-Za-z_][A-Za-z0-9_$#]*(?:\.[A-Za-z_][A-Za-z0-9_$#]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 从 SQL 启发式提取引用的表名（不含 CTE 剔除——CTE 名查列清单会空结果被自然跳过）。
    /// 按出现顺序去重（忽略大小写），最多 3 个。提取不到返回空列表（调用方不辅助）。
    /// </summary>
    public static IReadOnlyList<string> ExtractTableNames(string sql)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (Match m in TableRefPattern.Matches(sql))
        {
            if (seen.Add(m.Groups[1].Value))
            {
                names.Add(m.Groups[1].Value);
                if (names.Count >= 3)
                {
                    break;
                }
            }
        }
        return names;
    }

    // 坏名进入 LIKE/列清单查询前的最小安全校验：纯标识符（拒绝引号/空白/通配符混入消息提取值）
    private static readonly Regex PlainIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_$#]*$", RegexOptions.Compiled);

    /// <summary>坏名是否为纯标识符（可安全用于辅助查询）；schema.table 形式取末段后校验。</summary>
    public static bool IsPlainIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string bare = StripSchema(name);
        return PlainIdentifier.IsMatch(bare);
    }

    /// <summary>去掉 schema 前缀取末段（dbo.Users → Users；无点原样返回）。</summary>
    public static string StripSchema(string name)
    {
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
    }

    /// <summary>列名错误辅助文本：每表一行 "表 X 列: a, b, c"（每表截前 15 列，超出加 …）。</summary>
    public static string FormatColumnAssist(IReadOnlyList<(string Table, IReadOnlyList<string> Columns)> tables)
    {
        var sb = new System.Text.StringBuilder();
        foreach ((string table, IReadOnlyList<string> columns) in tables)
        {
            sb.Append("表 ").Append(table).Append(" 列: ").Append(string.Join(", ", columns.Take(15)));
            if (columns.Count > 15)
            {
                sb.Append(", …");
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>表名错误辅助文本：相近表清单（截前 10）；无相近时引导 db_schema。</summary>
    public static string FormatTableAssist(IReadOnlyList<string> candidates) =>
        candidates.Count == 0
            ? "未找到相近表，可用 db_schema 查表清单"
            : $"相近表: {string.Join(", ", candidates.Take(10))}{(candidates.Count > 10 ? ", …" : "")}";
}
