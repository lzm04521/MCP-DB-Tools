using System.Text.Json;
using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

public class QueryResultTests
{
    [Fact]
    public void ToJson_ColumnsAndRows_AsArrays()
    {
        var result = QueryResult.Ok("erp", "SqlServer",
            new List<string> { "Id", "Name" },
            new List<object?[]> { new object?[] { 1, "张三" }, new object?[] { 2, null } },
            maxRows: 1000, truncated: false, elapsedMs: 5);

        using JsonDocument doc = JsonDocument.Parse(result.ToJson());

        // 驼峰命名
        Assert.Equal("erp", doc.RootElement.GetProperty("project").GetString());
        Assert.Equal("SqlServer", doc.RootElement.GetProperty("databaseType").GetString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        // columns 为字符串数组
        Assert.Equal(new[] { "Id", "Name" }, doc.RootElement.GetProperty("columns").EnumerateArray().Select(e => e.GetString()));
        // rows 为二维数组
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(1, rows[0][0].GetInt32());
        Assert.Equal("张三", rows[0][1].GetString());  // 中文不转义
        Assert.Equal(2, rows[1][0].GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[1][1].ValueKind);  // null 原样输出
    }

    [Fact]
    public void ToJson_Failure_ContainsErrorAndCode()
    {
        var result = QueryResult.Fail("erp", "SqlServer", "被阻止", "SQL_BLOCKED");

        using JsonDocument doc = JsonDocument.Parse(result.ToJson());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("被阻止", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("SQL_BLOCKED", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.False(doc.RootElement.GetProperty("columns").GetArrayLength() > 0);
    }
}
