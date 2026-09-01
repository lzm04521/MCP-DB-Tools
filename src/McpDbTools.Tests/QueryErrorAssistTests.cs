using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

/// <summary>
/// 错误自愈纯函数测试（doc/20260901 P1）：四方言错误家族分类、SQL 表名启发式提取、
/// 坏名安全校验、辅助文本格式化（截断）。查询编排在 DbQueryToolTests 用 stub 覆盖。
/// </summary>
public class QueryErrorAssistTests
{
    // ───────── Classify：四方言"列名/表名不存在"家族 ─────────

    [Theory]
    [InlineData(DatabaseType.SqlServer, "Invalid column name 'usr_nme'.", QueryErrorAssist.AssistKind.InvalidColumn, "usr_nme")]
    [InlineData(DatabaseType.SqlServer, "SqlException: Invalid object name 'T_USER'.", QueryErrorAssist.AssistKind.InvalidTable, "T_USER")]
    [InlineData(DatabaseType.MySql, "Unknown column 'a.x' in 'field list'", QueryErrorAssist.AssistKind.InvalidColumn, "a.x")]
    [InlineData(DatabaseType.MySql, "Table 'erp.t_user' doesn't exist", QueryErrorAssist.AssistKind.InvalidTable, "erp.t_user")]
    [InlineData(DatabaseType.Oracle, "ORA-00904: \"USR_NME\": invalid identifier", QueryErrorAssist.AssistKind.InvalidColumn, "USR_NME")]
    [InlineData(DatabaseType.Oracle, "ORA-00942: table or view does not exist", QueryErrorAssist.AssistKind.InvalidTable, null)]
    [InlineData(DatabaseType.PostgreSql, "column \"usr_nme\" does not exist", QueryErrorAssist.AssistKind.InvalidColumn, "usr_nme")]
    [InlineData(DatabaseType.PostgreSql, "relation \"t_user\" does not exist", QueryErrorAssist.AssistKind.InvalidTable, "t_user")]
    public void Classify_KnownFamilies(DatabaseType type, string message, QueryErrorAssist.AssistKind kind, string? badName)
    {
        QueryErrorAssist.ErrorSignal s = QueryErrorAssist.Classify(type, message);
        Assert.Equal(kind, s.Kind);
        Assert.Equal(badName, s.BadName);
    }

    [Theory]
    [InlineData(DatabaseType.SqlServer, "Execution Timeout Expired.")]
    [InlineData(DatabaseType.Oracle, "ORA-01013: user requested cancel of current operation")]
    [InlineData(DatabaseType.Oracle, "ORA-00933: SQL command not properly ended")] // 语法错不在辅助范围
    [InlineData(DatabaseType.PostgreSql, "permission denied for table t")]
    public void Classify_UnknownFamilies_None(DatabaseType type, string message)
    {
        Assert.Equal(QueryErrorAssist.AssistKind.None, QueryErrorAssist.Classify(type, message).Kind);
    }

    // ───────── ExtractTableNames：FROM/JOIN/UPDATE/INTO 启发式提取 ─────────

    [Fact]
    public void ExtractTableNames_FromJoin()
    {
        IReadOnlyList<string> tables = QueryErrorAssist.ExtractTableNames(
            "SELECT u.id, o.no FROM T_USERS u JOIN T_ORDER o ON o.uid = u.id WHERE u.name = (SELECT c FROM DUAL)");
        Assert.Equal(new[] { "T_USERS", "T_ORDER", "DUAL" }, tables);
    }

    [Fact]
    public void ExtractTableNames_SkipsSubqueryParen_IgnoresCaseDedupe()
    {
        IReadOnlyList<string> tables = QueryErrorAssist.ExtractTableNames(
            "SELECT * FROM (SELECT 1) x JOIN t1 a JOIN T1 b");
        Assert.Equal(new[] { "t1" }, tables);
    }

    [Fact]
    public void ExtractTableNames_UpdateFromSubquery()
    {
        IReadOnlyList<string> tables = QueryErrorAssist.ExtractTableNames(
            "UPDATE T_A SET x = 1 WHERE id IN (SELECT id FROM T_B)");
        Assert.Equal(new[] { "T_A", "T_B" }, tables);
    }

    [Fact]
    public void ExtractTableNames_None()
    {
        Assert.Empty(QueryErrorAssist.ExtractTableNames("SELECT 1"));
    }

    // ───────── StripSchema / IsPlainIdentifier：坏名进辅助查询前的安全校验 ─────────

    [Theory]
    [InlineData("dbo.Users", "Users")]
    [InlineData("erp.t_user", "t_user")]
    [InlineData("T9", "T9")]
    public void StripSchema_TakesLastSegment(string input, string expected)
    {
        Assert.Equal(expected, QueryErrorAssist.StripSchema(input));
    }

    [Theory]
    [InlineData("T_USER", true)]
    [InlineData("dbo.Users", true)] // 去前缀后校验
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("T;DELETE", false)] // 消息提取值混入语句片段：拒
    [InlineData("T USER", false)]
    [InlineData("T'", false)]
    public void IsPlainIdentifier_GuardsMessageExtractedNames(string? name, bool expected)
    {
        Assert.Equal(expected, QueryErrorAssist.IsPlainIdentifier(name));
    }

    // ───────── 辅助文本格式化：列清单/相近表截断 ─────────

    [Fact]
    public void FormatColumnAssist_TruncatesAt15Columns()
    {
        List<string> columns = Enumerable.Range(1, 20).Select(i => $"C{i}").ToList();
        string text = QueryErrorAssist.FormatColumnAssist(
            new List<(string, IReadOnlyList<string>)> { ("T1", columns) });
        Assert.StartsWith("表 T1 列: C1, C2", text);
        Assert.EndsWith("…", text);
        Assert.DoesNotContain("C16", text);
    }

    [Fact]
    public void FormatTableAssist_EmptyGuidesToFallback()
    {
        Assert.Contains("db_schema", QueryErrorAssist.FormatTableAssist(new List<string>()));
    }

    [Fact]
    public void FormatTableAssist_TruncatesAt10()
    {
        string text = QueryErrorAssist.FormatTableAssist(Enumerable.Range(1, 12).Select(i => $"T{i}").ToList());
        Assert.StartsWith("相近表: T1, T2", text);
        Assert.EndsWith("…", text);
        Assert.DoesNotContain("T11", text);
    }
}
