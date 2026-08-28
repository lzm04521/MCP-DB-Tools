using System.Text.Json;
using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

public class QueryResultTests
{
    /// <summary>json 回退格式：行数据为二维数组（与旧版输出结构一致）。</summary>
    [Fact]
    public void Serialize_JsonFallback_ColumnsAndRows_AsArrays()
    {
        var result = QueryResult.Ok("erp", "SqlServer",
            new List<string> { "Id", "Name" },
            new List<object?[]> { new object?[] { 1, "张三" }, new object?[] { 2, null } },
            maxRows: 1000, truncated: false, elapsedMs: 5);

        using JsonDocument doc = JsonDocument.Parse(result.Serialize(RowFormat.Json));

        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
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
        Assert.False(doc.RootElement.TryGetProperty("rowset", out _));  // json 格式无 rowset
    }

    [Fact]
    public void Serialize_Failure_ContainsErrorAndCode()
    {
        var result = QueryResult.Fail("erp", "SqlServer", "被阻止", "SQL_BLOCKED");

        using JsonDocument doc = JsonDocument.Parse(result.Serialize(RowFormat.Tsv));
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("被阻止", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("SQL_BLOCKED", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.False(doc.RootElement.TryGetProperty("columns", out _));   // 失败无行数据
        Assert.Equal("tsv", doc.RootElement.GetProperty("format").GetString());
        Assert.True(doc.RootElement.TryGetProperty("executionTimeMs", out _));  // 失败保留耗时
    }

    // ───────── TSV 默认编码规则（spec §3.3/3.4 逐条对应） ─────────

    [Fact]
    public void Serialize_TsvDefault_EncodesCells_AndOmitsTrailingNewline()
    {
        var result = QueryResult.Ok("erp", "SqlServer",
            new List<string> { "Id", "Name", "Memo" },
            new List<object?[]>
            {
                new object?[] { 1, "张三", null },
                new object?[] { 2, "", "a\tb\nc\\d" },
                new object?[] { 3, true, new byte[] { 1, 2 } },
                new object?[] { 4, new DateTime(2026, 8, 21, 10, 30, 0), 1.5d },
            },
            maxRows: 1000, truncated: false, elapsedMs: 5);

        using var doc = JsonDocument.Parse(result.Serialize(RowFormat.Tsv));

        Assert.Equal("tsv", doc.RootElement.GetProperty("format").GetString());
        // 逐 cell：NULL→\N；空串→空字段；tab/LF/反斜杠转义；bool；byte[]→<binary NB>占位(json 档经 JsonSerializer 仍为 base64)；DateTime→"O"；double→round-trip；
        // 行间 \n、末行无 \n
        Assert.Equal(
            "1\t张三\t\\N\n2\t\ta\\tb\\nc\\\\d\n3\ttrue\t<binary 2B>\n4\t2026-08-21T10:30:00.0000000\t1.5",
            doc.RootElement.GetProperty("rowset").GetString());
    }

    [Fact]
    public void Serialize_EmptyRows_RowsetIsEmptyString()
    {
        var result = QueryResult.Ok("erp", "SqlServer", new List<string> { "Id" },
            new List<object?[]>(), maxRows: 1000, truncated: false, elapsedMs: 5);

        using var doc = JsonDocument.Parse(result.Serialize(RowFormat.Tsv));

        Assert.Equal(0, doc.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal("", doc.RootElement.GetProperty("rowset").GetString());
    }

    // ───────── 字段矩阵（spec §3.1：读/写/失败三分支） ─────────

    [Fact]
    public void Serialize_TsvReadSuccess_OmitsVerboseMetadata()
    {
        var result = QueryResult.Ok("erp", "SqlServer", new List<string> { "Id" },
            new List<object?[]> { new object?[] { 1 } }, 1000, false, 5, "test");

        using var doc = JsonDocument.Parse(result.Serialize(RowFormat.Tsv));

        Assert.False(doc.RootElement.TryGetProperty("affectedRows", out _));     // 读成功恒 0，删
        Assert.False(doc.RootElement.TryGetProperty("maxRows", out _));          // 删
        Assert.False(doc.RootElement.TryGetProperty("executionTimeMs", out _));  // 删
        Assert.False(doc.RootElement.TryGetProperty("rows", out _));             // 默认无 rows
        Assert.Equal("test", doc.RootElement.GetProperty("environment").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("rowCount").GetInt32());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Serialize_Write_ReturnsAffectedRows_WithoutReadShape()
    {
        var result = QueryResult.OkWrite("erp", "SqlServer", 3, 12, "test");

        using var doc = JsonDocument.Parse(result.Serialize(RowFormat.Tsv));

        Assert.Equal(3, doc.RootElement.GetProperty("affectedRows").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("rowCount", out _));          // 与 affectedRows 重复，删
        Assert.False(doc.RootElement.TryGetProperty("executionTimeMs", out _));
        Assert.False(doc.RootElement.TryGetProperty("columns", out _));
        Assert.False(doc.RootElement.TryGetProperty("rowset", out _));
    }

    [Fact]
    public void Serialize_WriteZeroAffected_StillWriteShape()
    {
        // UPDATE 命中 0 行：IsWrite 判定（非 AffectedRows>0），仍输出 affectedRows:0
        var result = QueryResult.OkWrite("erp", "SqlServer", 0, 12, "test");

        using var doc = JsonDocument.Parse(result.Serialize(RowFormat.Tsv));

        Assert.Equal(0, doc.RootElement.GetProperty("affectedRows").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("columns", out _));
    }

    [Fact]
    public void Serialize_TruncatedTrue_PreservedInTsvShape()
    {
        var result = QueryResult.Ok("erp", "SqlServer", new List<string> { "Id" },
            new List<object?[]> { new object?[] { 1 } }, 1000, truncated: true, elapsedMs: 5);

        using var doc = JsonDocument.Parse(result.Serialize(RowFormat.Tsv));

        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    // ───────── text 档（缺省）：状态行 + 表头 + TSV（doc/20260828-MCP返回text纯文本格式.md §3.2） ─────────

    [Fact]
    public void Serialize_TextDefault_ReadSuccess_StatusHeaderRows()
    {
        var result = QueryResult.Ok("erp", "SqlServer",
            new List<string> { "Id", "Name" },
            new List<object?[]> { new object?[] { 1, "张三" }, new object?[] { 2, null } },
            maxRows: 1000, truncated: false, elapsedMs: 5, environment: "test");

        // 缺省 Text：状态行 + 表头行 + TSV 数据（真实 tab/LF，无 JSON 转义）
        Assert.Equal("OK 2 rows @erp/test (sqlserver)\nId\tName\n1\t张三\n2\t\\N", result.Serialize());
    }

    [Fact]
    public void Serialize_Text_TruncatedOffsetAnnotations()
    {
        var result = QueryResult.Ok("erp", "SqlServer", new List<string> { "Id" },
            new List<object?[]> { new object?[] { 1 } }, 1000, truncated: true, elapsedMs: 5,
            environment: "test", offset: 200, nextOffset: 201);

        Assert.Equal("OK 1 rows @erp/test (sqlserver, offset=200) [truncated, nextOffset=201]\nId\n1", result.Serialize());
    }

    [Fact]
    public void Serialize_Text_NoEnvNoTruncated_MinimalStatusLine()
    {
        var result = QueryResult.Ok("erp", "SqlServer", new List<string> { "Id" },
            new List<object?[]> { new object?[] { 1 } }, 1000, truncated: false, elapsedMs: 5);

        // 未截断无任何标记；无环境省略 /env 段
        Assert.Equal("OK 1 rows @erp (sqlserver)\nId\n1", result.Serialize());
    }

    [Fact]
    public void Serialize_TextEmptyRows_HeaderOnly()
    {
        var result = QueryResult.Ok("erp", "SqlServer", new List<string> { "Id" },
            new List<object?[]>(), 1000, false, 5, "test");

        // 0 行仍输出表头行（列名对 AI 有信息量）
        Assert.Equal("OK 0 rows @erp/test (sqlserver)\nId", result.Serialize());
    }

    [Fact]
    public void Serialize_TextWrite_AffectedStatusLine()
    {
        Assert.Equal("OK 3 affected @erp/test (sqlserver)",
            QueryResult.OkWrite("erp", "SqlServer", 3, 12, "test").Serialize());
        // UPDATE 命中 0 行同样是写成功
        Assert.Equal("OK 0 affected @erp/test (sqlserver)",
            QueryResult.OkWrite("erp", "SqlServer", 0, 12, "test").Serialize());
    }

    [Fact]
    public void Serialize_TextEstimated_CountFoldedIntoStatusLine()
    {
        // dryRun：COUNT 单行折叠为 "~N affected (estimated)"，不输出表体
        var result = QueryResult.Ok("erp", "SqlServer",
            new List<string> { "" },
            new List<object?[]> { new object?[] { 12345L } },
            maxRows: 1, truncated: false, elapsedMs: 5, environment: "test", estimated: true);

        Assert.Equal("OK ~12345 affected (estimated) @erp/test (sqlserver)", result.Serialize());
    }

    [Fact]
    public void Serialize_TextFail_CodeTargetAndError_NoTime()
    {
        Assert.Equal("FAIL SQL_BLOCKED @erp/test: 被阻止",
            QueryResult.Fail("erp", "SqlServer", "被阻止", "SQL_BLOCKED", environment: "test").Serialize());
    }

    [Fact]
    public void Serialize_TextFail_UnknownEnv_OmitsEnvPart()
    {
        // environment="Unknown"（环境解析失败占位）时省略 /env 段
        Assert.Equal("FAIL PROJECT_NOT_FOUND @erp: 项目不存在",
            QueryResult.Fail("erp", "Unknown", "项目不存在", "PROJECT_NOT_FOUND").Serialize());
    }

    [Fact]
    public void Serialize_TextBinaryPlaceholder()
    {
        var result = QueryResult.Ok("erp", "SqlServer", new List<string> { "Data" },
            new List<object?[]> { new object?[] { new byte[] { 1, 2, 3 } } }, 1000, false, 5, "test");

        Assert.Equal("OK 1 rows @erp/test (sqlserver)\nData\n<binary 3B>", result.Serialize());
    }
}
