using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace McpDbTools.Server.Database;

/// <summary>行编码格式：Text 为默认（纯文本最省 token），Tsv/Json 为结构化回退（JSON 壳）。</summary>
public enum RowFormat
{
    Text,
    Tsv,
    Json
}

/// <summary>
/// 查询执行结果。对象层属性供审计/UI 消费（一个不删）；
/// 序列化投影（字段矩阵见 doc/20260821-MCP返回省token优化.md §3.1，text 档见 doc/20260828-MCP返回text纯文本格式.md）：
/// Text 档（默认）纯文本——首行状态行 + 表头行 + TSV 数据（分隔符为真实控制字符，无 JSON 转义税）；
/// Tsv/Json 档 JSON 壳——读成功输出 rowCount/truncated/columns + rowset(TSV) 或 rows(JSON)；
/// 写成功输出 affectedRows；失败输出 error/errorCode/executionTimeMs（Text 档失败不带时间，AI 不据此决策）。
/// </summary>
public sealed class QueryResult
{
    public bool Success { get; init; }
    public string? Project { get; init; }
    public string? Environment { get; init; }
    public string? DatabaseType { get; init; }
    public int RowCount { get; init; }
    /// <summary>写操作受影响行数；读操作为 0。</summary>
    public int AffectedRows { get; init; }
    /// <summary>写结果标志（OkWrite 设 true）。序列化据此走写形状，AffectedRows=0 的写成功（UPDATE 未命中）也输出 affectedRows。</summary>
    public bool IsWrite { get; init; }
    public int MaxRows { get; init; }
    public bool Truncated { get; init; }
    public long ExecutionTimeMs { get; init; }
    public List<string> Columns { get; init; } = new();
    /// <summary>行数据，每行为 object?[]（值可能为 null）。JSON 回退时输出二维数组；默认编码为 TSV rowset。</summary>
    public List<object?[]> Rows { get; init; } = new();
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>分页请求的实际 offset（仅 offset 分页读请求输出；null=非分页请求，不序列化）。</summary>
    public int? Offset { get; init; }
    /// <summary>续翻提示：offset + rowCount，仅 offset 分页且 truncated=true 时输出。</summary>
    public int? NextOffset { get; init; }
    /// <summary>dryRun 预估结果标志：rows 为估算影响行数（COUNT），非实际执行结果。</summary>
    public bool Estimated { get; init; }

    // json 回退分支序列化 Rows 用；中文不转义（与旧实现一致）
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 手写 writer 同样必须关掉非 ASCII 转义，否则中文变 \uXXXX，token 反而变多
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>按指定行编码序列化为 MCP 返回：Text 档纯文本（BuildText），Tsv/Json 档 JSON 壳（字段矩阵见类注释）。</summary>
    public string Serialize(RowFormat format = RowFormat.Text)
    {
        if (format == RowFormat.Text) return BuildText();
        // ArrayBufferWriter 无非托管资源，不需要 using/Dispose
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", Success);
            WriteStringOrNull(writer, "project", Project);
            WriteStringOrNull(writer, "environment", Environment);
            WriteStringOrNull(writer, "databaseType", DatabaseType);
            writer.WriteString("format", format == RowFormat.Json ? "json" : "tsv");

            if (!Success)
            {
                WriteStringOrNull(writer, "error", Error);
                WriteStringOrNull(writer, "errorCode", ErrorCode);
                writer.WriteNumber("executionTimeMs", ExecutionTimeMs);
            }
            else if (IsWrite)
            {
                writer.WriteNumber("affectedRows", AffectedRows);
            }
            else
            {
                writer.WriteNumber("rowCount", RowCount);
                writer.WriteBoolean("truncated", Truncated);
                // offset 分页请求：回显 offset；truncated 时附 nextOffset 供 Agent 续翻
                if (Offset is int off) writer.WriteNumber("offset", off);
                if (NextOffset is int next) writer.WriteNumber("nextOffset", next);
                if (Estimated)
                {
                    writer.WriteBoolean("estimated", true);
                    writer.WriteString("note", "估算影响行数（不含触发器影响，忽略唯一约束冲突；实际执行可能不同）");
                }
                writer.WriteStartArray("columns");
                foreach (string col in Columns) writer.WriteStringValue(col);
                writer.WriteEndArray();
                if (format == RowFormat.Json)
                {
                    // 与旧 JsonSerializer.Serialize(this) 的 rows 输出字节一致
                    writer.WritePropertyName("rows");
                    writer.WriteRawValue(JsonSerializer.Serialize(Rows, JsonOptions));
                }
                else
                {
                    writer.WriteString("rowset", BuildRowset());
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    // ───────── Text 档（缺省）：纯文本，省 JSON 壳键名与字符串内层转义税 ─────────

    /// <summary>
    /// Text 档序列化：首行状态行（OK/FAIL + 行数 + @项目/环境 + (类型,分页) + [截断]），
    /// 其后表头行 + TSV 数据（复用 BuildRowset 编码）。失败为 FAIL 单行 + error 原文（可多行跟随）。
    /// </summary>
    internal string BuildText()
    {
        var sb = new StringBuilder();
        if (!Success)
        {
            sb.Append("FAIL ").Append(ErrorCode ?? "ERROR");
            AppendTextTarget(sb);
            if (!string.IsNullOrEmpty(Error)) sb.Append(": ").Append(Error);
            return sb.ToString();
        }

        sb.Append("OK ");
        if (IsWrite)
        {
            sb.Append(AffectedRows).Append(" affected");
            AppendTextTarget(sb);
            AppendTextDbType(sb);
            return sb.ToString();
        }

        // dryRun 预估：COUNT 单行结果折叠为 "~N affected (estimated)" 状态行，避免表体与状态行重复输出数字
        if (Estimated && Rows.Count == 1 && Rows[0].Length >= 1)
        {
            sb.Append('~');
            AppendCell(sb, Rows[0][0]);
            sb.Append(" affected (estimated)");
            AppendTextTarget(sb);
            AppendTextDbType(sb);
            return sb.ToString();
        }

        sb.Append(RowCount).Append(" rows");
        AppendTextTarget(sb);
        // 类型与分页回显合并进同一括号： (sqlserver, offset=200)
        string? type = NormalizeTextDbType();
        bool hasOffset = Offset is int;
        if (type is not null || hasOffset)
        {
            sb.Append(" (");
            if (type is not null) sb.Append(type);
            if (type is not null && hasOffset) sb.Append(", ");
            if (hasOffset) sb.Append("offset=").Append(Offset!.Value);
            sb.Append(')');
        }
        // 未截断不输出任何标记（省 "truncated":false）；截断时附续翻提示
        if (Truncated)
        {
            sb.Append(" [truncated");
            if (NextOffset is int next) sb.Append(", nextOffset=").Append(next);
            sb.Append(']');
        }
        AppendTextBody(sb);
        return sb.ToString();
    }

    /// <summary>追加表头行 + 数据行（无状态行）。internal 供 db_schema 段拼装复用同一编码。</summary>
    internal void AppendTextBody(StringBuilder sb)
    {
        if (Columns.Count > 0)
        {
            sb.Append('\n');
            for (int c = 0; c < Columns.Count; c++)
            {
                if (c > 0) sb.Append('\t');
                AppendEscaped(sb, Columns[c]);
            }
        }
        if (Rows.Count > 0)
        {
            sb.Append('\n');
            sb.Append(BuildRowset());
        }
    }

    /// <summary>追加 @project/environment 定位段；project 为空不输出，environment 为空或 "Unknown" 时省略环境部分。</summary>
    private void AppendTextTarget(StringBuilder sb)
    {
        if (string.IsNullOrEmpty(Project)) return;
        sb.Append(" @").Append(Project);
        if (!string.IsNullOrEmpty(Environment) && Environment != "Unknown") sb.Append('/').Append(Environment);
    }

    private void AppendTextDbType(StringBuilder sb)
    {
        string? type = NormalizeTextDbType();
        if (type is not null) sb.Append(" (").Append(type).Append(')');
    }

    /// <summary>小写类型名（与 db_list 一致）；空或 "Unknown"（环境解析失败占位）不输出。</summary>
    private string? NormalizeTextDbType() =>
        string.IsNullOrEmpty(DatabaseType) || DatabaseType == "Unknown"
            ? null
            : DatabaseType!.ToLowerInvariant();

    /// <summary>TSV 编码：\t 分列、\n 行间分隔（末行无）；NULL→\N；值内 \ → \\、tab→\t、LF→\n、CR→\r。
    /// internal 供工具层组装分段返回（db_schema 等）复用同一编码。</summary>
    internal string BuildRowset()
    {
        if (Rows.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (int r = 0; r < Rows.Count; r++)
        {
            if (r > 0) sb.Append('\n');
            object?[] row = Rows[r];
            for (int c = 0; c < row.Length; c++)
            {
                if (c > 0) sb.Append('\t');
                AppendCell(sb, row[c]);
            }
        }
        return sb.ToString();
    }

    private static void AppendCell(StringBuilder sb, object? value)
    {
        if (value is null) { sb.Append("\\N"); return; }
        switch (value)
        {
            case string s: AppendEscaped(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            // base64 膨胀 33% 且 AI 无法消费：TSV/Text 档输出长度占位；json 档经 JsonSerializer 序列化仍为 base64（保真回退）
            case byte[] bytes: sb.Append("<binary ").Append(bytes.Length).Append("B>"); break;
            case DateTime dt: sb.Append(dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dto: sb.Append(dto.ToString("O", CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
            case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
            case decimal dec: sb.Append(dec.ToString(CultureInfo.InvariantCulture)); break;
            default: sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    public static QueryResult Ok(string project, string dbType, List<string> columns, List<object?[]> rows, int maxRows, bool truncated, long elapsedMs, string? environment = null, int? offset = null, int? nextOffset = null, bool estimated = false) => new()
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
        ExecutionTimeMs = elapsedMs,
        Offset = offset,
        NextOffset = nextOffset,
        Estimated = estimated
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

    /// <summary>构造写操作成功结果。IsWrite=true：序列化走写形状（affectedRows）；RowCount 与 AffectedRows 同值供审计/UI 统一取用。</summary>
    public static QueryResult OkWrite(string project, string dbType, int affectedRows, long elapsedMs, string? environment = null) => new()
    {
        Success = true,
        Project = project,
        Environment = environment,
        DatabaseType = dbType,
        AffectedRows = affectedRows,
        RowCount = affectedRows,
        IsWrite = true,
        ExecutionTimeMs = elapsedMs
    };
}
