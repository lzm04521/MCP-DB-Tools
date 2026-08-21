using McpDbTools.Server.Database;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Tests;

/// <summary>
/// GetSchemaAsync 的模板选择纯逻辑测试（实际执行依赖真实连接，见 T14 真库冒烟）。
/// </summary>
public class GetSchemaAsyncTests
{
    [Theory]
    [InlineData(DatabaseType.MySql)]
    [InlineData(DatabaseType.Oracle)]
    [InlineData(DatabaseType.SqlServer)]
    [InlineData(DatabaseType.PostgreSql)]
    public void BuildSections_NullOrBlankTable_SelectsTablesTemplate(DatabaseType type)
    {
        IReadOnlyList<SchemaSectionTemplate> expected = SchemaDialects.Tables(type);

        Assert.Equal(expected[0].Sql, SchemaExecutor.BuildSections(type, null)[0].Sql);
        Assert.Equal("tables", SchemaExecutor.BuildSections(type, null)[0].Name);
        Assert.Equal("tables", SchemaExecutor.BuildSections(type, "  ")[0].Name); // 空白等同 null
    }

    [Theory]
    [InlineData(DatabaseType.Oracle)]
    [InlineData(DatabaseType.SqlServer)]
    public void BuildSections_WithTable_SelectsDetailTemplates_InOrder(DatabaseType type)
    {
        var sections = SchemaExecutor.BuildSections(type, "ORDERS");
        Assert.Equal(new[] { "columns", "indexes", "foreignKeys" }, sections.Select(s => s.Name));
        Assert.All(sections, s => Assert.True(s.HasTableParam));
    }
}
