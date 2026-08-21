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

    private static string TablesSourceFor(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => "sys.tables",
        DatabaseType.MySql => "information_schema.TABLES",
        DatabaseType.Oracle => "all_tables",
        DatabaseType.PostgreSql => "pg_catalog.pg_class",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
