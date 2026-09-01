using McpDbTools.Server.Database;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Tests;

/// <summary>
/// 四方言元数据 SQL 模板测试：段结构、权威表源、参数化过滤（防模板误改退化）。
/// </summary>
public class SchemaDialectsTests
{
    public static readonly DatabaseType[] AllTypes =
        { DatabaseType.SqlServer, DatabaseType.MySql, DatabaseType.Oracle, DatabaseType.PostgreSql };

    public static TheoryData<DatabaseType> AllTypesData()
    {
        var data = new TheoryData<DatabaseType>();
        foreach (DatabaseType type in AllTypes) data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTypesData))]
    public void Tables_SingleTemplate_QueriesCatalog(DatabaseType type)
    {
        var templates = SchemaDialects.Tables(type);
        SchemaSectionTemplate t = Assert.Single(templates);
        Assert.Equal("tables", t.Name);
        Assert.False(t.HasTableParam);
        Assert.Contains(TablesSourceFor(type), t.Sql); // 各方言权威表源，防误改
    }

    [Theory]
    [MemberData(nameof(AllTypesData))]
    public void TableDetail_ThreeSections_AllParameterized(DatabaseType type)
    {
        var templates = SchemaDialects.TableDetail(type);
        Assert.Equal(new[] { "columns", "indexes", "foreignKeys" }, templates.Select(t => t.Name));
        Assert.All(templates, t => Assert.True(t.HasTableParam));
        Assert.All(templates, t => Assert.Contains(SchemaDialects.TableParamName, t.Sql));
    }

    [Theory]
    [MemberData(nameof(AllTypesData))]
    public void Templates_ContainNoStringInterpolationOfTable(DatabaseType type)
    {
        // 模板均为固定文本：不含表名字面量拼接痕迹（参数化由执行层经 @table 注入）
        Assert.All(SchemaDialects.Tables(type), t => Assert.DoesNotContain("{table}", t.Sql));
        Assert.All(SchemaDialects.TableDetail(type), t => Assert.DoesNotContain("{table}", t.Sql));
    }

    // ───────── 模糊搜索模板（doc/20260901 P2）：LIKE + ESCAPE '!' + 参数化，防误改退化 ─────────

    [Theory]
    [MemberData(nameof(AllTypesData))]
    public void TablesLikeSql_AllDialects_ParameterizedLikeWithEscape(DatabaseType type)
    {
        string sql = SchemaDialects.TablesLikeSql(type);
        Assert.Contains(TablesSourceFor(type), sql); // 与表清单同一权威表源
        Assert.Contains(SchemaDialects.TableParamName, sql);
        Assert.Contains("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE '!'", sql);
    }

    [Theory]
    [MemberData(nameof(AllTypesData))]
    public void TablesLikeSql_DictionaryCaseNormalization(DatabaseType type)
    {
        // Oracle/PG 字典区分大小写，模式值在 SQL 内 UPPER/LOWER 归一；其余方言原生不敏感不动
        string sql = SchemaDialects.TablesLikeSql(type);
        if (type == DatabaseType.Oracle) Assert.Contains("UPPER(@table)", sql);
        else if (type == DatabaseType.PostgreSql) Assert.Contains("LOWER(@table)", sql);
        else Assert.DoesNotContain("UPPER(@table)", sql);
    }

    [Theory]
    [MemberData(nameof(AllTypesData))]
    public void ColumnSearchSql_Exact_EqualsParam_NoLike(DatabaseType type)
    {
        string sql = SchemaDialects.ColumnSearchSql(type, exact: true);
        // 精确匹配形态：Oracle/PG 在 SQL 内做大小写归一
        string equals = type switch
        {
            DatabaseType.Oracle => "= UPPER(@table)",
            DatabaseType.PostgreSql => "= LOWER(@table)",
            _ => "= @table"
        };
        Assert.Contains(equals, sql);
        Assert.DoesNotContain("LIKE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllTypesData))]
    public void ColumnSearchSql_Fuzzy_LikeWithEscape(DatabaseType type)
    {
        string sql = SchemaDialects.ColumnSearchSql(type, exact: false);
        Assert.Contains("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE '!'", sql);
        Assert.Contains(SchemaDialects.TableParamName, sql);
    }

    [Theory]
    [InlineData("AB_C", "AB!_C")]
    [InlineData("A!B", "A!!B")]
    [InlineData("A!_B", "A!!!_B")] // 先转义 ! 再转义 _，避免自嵌套
    [InlineData("AUD%", "AUD%")]   // % 保留为通配符
    public void EscapeLikePattern_UnderscoreAndBangEscaped_PercentKept(string input, string expected)
    {
        Assert.Equal(expected, SchemaDialects.EscapeLikePattern(input));
    }

    private static string TablesSourceFor(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => "sys.tables",
        DatabaseType.MySql => "information_schema.TABLES",
        DatabaseType.Oracle => "all_tables",
        DatabaseType.PostgreSql => "pg_catalog.pg_class",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
