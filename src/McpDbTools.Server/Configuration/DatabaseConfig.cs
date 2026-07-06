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

    /// <summary>命令执行超时（秒），默认 600。</summary>
    [JsonPropertyName("commandTimeout")]
    public int CommandTimeout { get; init; } = 600;

    /// <summary>
    /// 连接池上限。&lt;=0 表示未配置，回退到全局 defaultMaxPoolSize（内置默认 100）。
    /// 解析阶段拼接到各驱动的连接串（SQL Server: Max Pool Size；MySQL: Maximum Pool Size；Oracle: Max Pool Size）。
    /// </summary>
    [JsonPropertyName("maxPoolSize")]
    public int MaxPoolSize { get; init; }

    /// <summary>
    /// 建立连接的超时（秒）。&lt;=0 表示未配置，回退到全局 defaultConnectTimeoutSeconds（内置默认 60）。
    /// 解析阶段拼接到各驱动的连接串；同时作为 ExecuteQueryAsync.OpenAsync 的 CTS 兜底超时。
    /// </summary>
    [JsonPropertyName("connectTimeoutSeconds")]
    public int ConnectTimeoutSeconds { get; init; }

    /// <summary>
    /// 该环境最大并发查询数。&lt;=0 表示未配置，回退到全局 defaultMaxConcurrency（内置默认 10）。
    /// 由 QueryConcurrencyLimiter 在运行时强制。
    /// </summary>
    [JsonPropertyName("maxConcurrency")]
    public int MaxConcurrency { get; init; }

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
/// 配置文件根模型（对应 config.json 整体结构）。
/// 三层阻止关键字：全局通用 → 按类型 → 按项目。
/// <para>审计日志已改为本地 SQLite（audit.db）全局记录，不再由 config.json 配置；
/// 若旧文件残留 audit 节点，反序列化时会因没有对应属性而被静默忽略。</para>
/// </summary>
public sealed class DatabasesConfig
{
    /// <summary>第一层：全局通用阻止关键字。未配置时使用 <see cref="DefaultDisabledKeywords.BuiltIn"/>。</summary>
    [JsonPropertyName("defaultDisabledKeywords")]
    public List<string>? DefaultDisabledKeywords { get; init; }

    /// <summary>第二层：按数据库类型追加的阻止关键字。未配置时为空。</summary>
    [JsonPropertyName("defaultDisabledKeywordsByType")]
    public Dictionary<DatabaseType, List<string>>? DefaultDisabledKeywordsByType { get; init; }

    /// <summary>每环境最大并发查询数的全局默认值。未配置时内置默认 10。</summary>
    [JsonPropertyName("defaultMaxConcurrency")]
    public int? DefaultMaxConcurrency { get; init; }

    /// <summary>超载排队最长等待秒数的全局默认值。未配置时内置默认 5。</summary>
    [JsonPropertyName("defaultMaxConcurrencyWaitSeconds")]
    public int? DefaultMaxConcurrencyWaitSeconds { get; init; }

    /// <summary>连接池上限的全局默认值。未配置时内置默认 100。</summary>
    [JsonPropertyName("defaultMaxPoolSize")]
    public int? DefaultMaxPoolSize { get; init; }

    /// <summary>建立连接超时秒数的全局默认值。未配置时内置默认 60。</summary>
    [JsonPropertyName("defaultConnectTimeoutSeconds")]
    public int? DefaultConnectTimeoutSeconds { get; init; }

    /// <summary>
    /// 运维清理设置（审计日志/备份自动清理）。缺省时为 null，按内置默认（全部关闭）处理。
    /// 保存 projects/keywords 时由后端 ToConfig 原样透传，不会被全量替换丢失。
    /// </summary>
    [JsonPropertyName("maintenance")]
    public MaintenanceConfig? Maintenance { get; init; }

    /// <summary>项目名 → 项目配置（含多环境）。JSON 节点名为 databases。</summary>
    [JsonPropertyName("databases")]
    public Dictionary<string, ProjectConfig> Projects { get; init; } = new();
}
