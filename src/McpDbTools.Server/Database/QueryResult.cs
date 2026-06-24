using System.Data;
using System.Text.Json;

namespace McpDbTools.Server.Database;

/// <summary>
/// 查询执行结果。序列化为 AI 友好的 JSON：columns 与 rows 分离，rows 用二维数组压缩 token。
/// </summary>
public sealed class QueryResult
{
    public bool Success { get; init; }
    public string? Project { get; init; }
    public string? Environment { get; init; }
    public string? DatabaseType { get; init; }
    public int RowCount { get; init; }
    public int MaxRows { get; init; }
    public bool Truncated { get; init; }
    public long ExecutionTimeMs { get; init; }
    public List<string> Columns { get; init; } = new();
    /// <summary>行数据，每行为 object?[]（值可能为 null）。序列化时输出为二维数组。</summary>
    public List<object?[]> Rows { get; init; } = new();
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>序列化为 JSON 字符串返回给 MCP 客户端。</summary>
    public string ToJson()
    {
        // object?[] 默认序列化为 JSON 数组，正是设计的二维数组结构；DBNull 已在读取时转 null
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>驼峰命名 + 不转义非 ASCII（中文直接输出，避免 \uXXXX 占用 token）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static QueryResult Ok(string project, string dbType, List<string> columns, List<object?[]> rows, int maxRows, bool truncated, long elapsedMs, string? environment = null) => new()
    {
        Success = true,
        Project = project,
        Environment = environment,
        DatabaseType = dbType,
        Columns = columns,
        Rows = rows,
        RowCount = rows.Count,
        MaxRows = maxRows,
        Truncated = truncated,
        ExecutionTimeMs = elapsedMs
    };

    public static QueryResult Fail(string project, string dbType, string error, string errorCode, long elapsedMs = 0, string? environment = null) => new()
    {
        Success = false,
        Project = project,
        Environment = environment,
        DatabaseType = dbType,
        Error = error,
        ErrorCode = errorCode,
        ExecutionTimeMs = elapsedMs
    };
}

