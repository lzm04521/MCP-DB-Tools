using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpDbTools.Server.Configuration;

/// <summary>
/// 数据库类型 JSON 转换器：读取时大小写不敏感，写出时使用 config.json 约定的小写值。
/// </summary>
public sealed class DatabaseTypeJsonConverter : JsonConverter<DatabaseType>
{
    public override DatabaseType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, DatabaseType value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToJsonValue(value));

    public override DatabaseType ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Parse(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, DatabaseType value, JsonSerializerOptions options)
        => writer.WritePropertyName(ToJsonValue(value));

    private static DatabaseType Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "sqlserver" => DatabaseType.SqlServer,
            "mysql" => DatabaseType.MySql,
            "oracle" => DatabaseType.Oracle,
            _ => throw new JsonException($"不支持的数据库类型: {value}")
        };
    }

    private static string ToJsonValue(DatabaseType value)
    {
        return value switch
        {
            DatabaseType.SqlServer => "sqlserver",
            DatabaseType.MySql => "mysql",
            DatabaseType.Oracle => "oracle",
            _ => throw new JsonException($"不支持的数据库类型: {value}")
        };
    }
}
