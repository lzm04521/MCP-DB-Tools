using System.Text.Json.Serialization;

namespace McpDbTools.Server.Configuration;

/// <summary>
/// 数据库类型枚举。新增类型时同步更新 <see cref="DatabaseProviderFactory"/> 与 SqlGuard 白名单。
/// </summary>
[JsonConverter(typeof(DatabaseTypeJsonConverter))]
public enum DatabaseType
{
    SqlServer,
    MySql,
    Oracle
}

/// <summary>
/// 单个环境（数据库实例）的配置。
/// 对应 config.json 中某项目 environments 节点下的一个条目（环境名 → DatabaseConfig）。
/// </summary>
public sealed class DatabaseConfig
{
    /// <summary>环境显示名，仅用于 Admin UI 展示，不影响 MCP 调用参数。</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>是否生产环境。用于 Admin UI 风险提示。</summary>
    [JsonPropertyName("isProduction")]
    public bool IsProduction { get; init; }

    /// <summary>数据库类型</summary>
    [JsonPropertyName("type")]
    public DatabaseType Type { get; init; }

    /// <summary>ADO.NET 连接字符串</summary>
    [JsonPropertyName("connectionString")]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>最大返回行数，默认 1000。运行时由 MCP Tool 的 limit 参数覆盖。</summary>
    [JsonPropertyName("maxRows")]
    public int MaxRows { get; init; } = 1000;

    /// <summary>命令执行超时（秒），默认 30。</summary>
    [JsonPropertyName("commandTimeout")]
    public int CommandTimeout { get; init; } = 30;

    /// <summary>
    /// 第三层：项目额外阻止关键字，叠加到全局与按类型默认之上。
    /// 仅可追加，不能缩减上层阻止集合。
    /// </summary>
    [JsonPropertyName("disabledKeywords")]
    public List<string> DisabledKeywords { get; init; } = new();
}

/// <summary>
/// 单个项目的配置：含默认环境与多环境数据库实例。
/// 对应 config.json 中 databases 节点下的一个条目（项目名 → ProjectConfig）。
/// </summary>
public sealed class ProjectConfig
{
    /// <summary>项目显示名，仅用于 Admin UI 展示，不影响 MCP 调用参数。</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>
    /// 默认环境名。db_query 未显式指定 environment 时使用。
    /// 为空时调用方必须显式指定 environment，否则返回 ENVIRONMENT_REQUIRED 错误。
    /// </summary>
    [JsonPropertyName("defaultEnvironment")]
    public string? DefaultEnvironment { get; init; }

    /// <summary>环境名 → 该环境的数据库配置。环境名自由命名（如 dev/test/prod/xiqing-prod）。</summary>
    [JsonPropertyName("environments")]
    public Dictionary<string, DatabaseConfig> Environments { get; init; } = new();
}

/// <summary>
/// 审计日志配置（对应 config.json 中 audit 节点）。默认关闭。
/// </summary>
public sealed class AuditConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("logPath")]
    public string LogPath { get; init; } = "logs/audit.log";

    [JsonPropertyName("maxFileSizeMB")]
    public int MaxFileSizeMB { get; init; } = 10;

    [JsonPropertyName("maxRetentionDays")]
    public int MaxRetentionDays { get; init; } = 30;
}

/// <summary>
/// 配置文件根模型（对应 config.json 整体结构）。
/// 三层阻止关键字：全局通用 → 按类型 → 按项目。
/// </summary>
public sealed class DatabasesConfig
{
    /// <summary>第一层：全局通用阻止关键字。未配置时使用 <see cref="DefaultDisabledKeywords.BuiltIn"/>。</summary>
    [JsonPropertyName("defaultDisabledKeywords")]
    public List<string>? DefaultDisabledKeywords { get; init; }

    /// <summary>第二层：按数据库类型追加的阻止关键字。未配置时为空。</summary>
    [JsonPropertyName("defaultDisabledKeywordsByType")]
    public Dictionary<DatabaseType, List<string>>? DefaultDisabledKeywordsByType { get; init; }

    /// <summary>审计日志配置。</summary>
    [JsonPropertyName("audit")]
    public AuditConfig Audit { get; init; } = new();

    /// <summary>项目名 → 项目配置（含多环境）。JSON 节点名为 databases。</summary>
    [JsonPropertyName("databases")]
    public Dictionary<string, ProjectConfig> Projects { get; init; } = new();
}
