using System.Collections.ObjectModel;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Configuration;

/// <summary>
/// 三层阻止关键字合并后的最终配置快照。
/// <para>合并逻辑：全局通用 ∪ 按类型 ∪ 按环境，全部转大写去重。</para>
/// <para>合并发生在加载时，运行时 SqlGuard 只做查找，无计算开销。</para>
/// </summary>
public sealed record ResolvedConfig
{
    /// <summary>当前配置中的所有项目（项目名 → ResolvedProject）。</summary>
    public required IReadOnlyDictionary<string, ResolvedProject> Projects { get; init; }
}

/// <summary>
/// 单个项目的最终配置：默认环境 + 各环境的数据库实例。
/// </summary>
public sealed record ResolvedProject
{
    public required string ProjectName { get; init; }

    /// <summary>默认环境名。db_query 未指定 environment 时使用；为空则必须显式指定。</summary>
    public required string? DefaultEnvironment { get; init; }

    /// <summary>环境名 → 该环境的最终数据库配置（含合并后的阻止关键字）。</summary>
    public required IReadOnlyDictionary<string, ResolvedDatabase> Environments { get; init; }
}

/// <summary>
/// 单个环境（数据库实例）的最终配置（含合并后的阻止关键字集合）。
/// </summary>
public sealed record ResolvedDatabase
{
    public required string ProjectName { get; init; }

    /// <summary>所属环境名（如 dev/test/prod/xiqing-prod）。</summary>
    public required string Environment { get; init; }

    /// <summary>是否生产环境。供 db_list 展示给 Agent，便于在生产环境查询时谨慎操作。</summary>
    public required bool IsProduction { get; init; }

    public required DatabaseType Type { get; init; }
    public required string ConnectionString { get; init; }

    /// <summary>
    /// 当前连接默认库/schema 标识，供 db_list 返回给 Agent，避免 USE 切库。
    /// 按类型语义：SqlServer=Initial Catalog；MySQL=Database；Oracle=User Id。
    /// 键缺失/解析失败/空白 → null。
    /// </summary>
    public required string? DatabaseName { get; init; }

    public required int MaxRows { get; init; }
    public required int CommandTimeout { get; init; }

    /// <summary>连接池上限（已应用全局默认/环境级覆盖）。</summary>
    public required int MaxPoolSize { get; init; }

    /// <summary>建连超时秒数（已应用全局默认/环境级覆盖）。</summary>
    public required int ConnectTimeoutSeconds { get; init; }

    /// <summary>该环境最大并发查询数（已应用全局默认/环境级覆盖）。</summary>
    public required int MaxConcurrency { get; init; }

    /// <summary>超载排队最长等待秒数（来自全局默认，内置默认 5）。</summary>
    public required int MaxConcurrencyWaitSeconds { get; init; }

    /// <summary>
    /// 合并后的阻止关键字集合（大写）。空格关键字如 "BULK INSERT" 保留空格，做子串匹配。
    /// </summary>
    public required IReadOnlySet<string> DisabledKeywords { get; init; }
}

/// <summary>将三层配置合并为 ResolvedConfig。</summary>
public static class ResolvedConfigBuilder
{
    // 并发与连接池相关内置默认值（全局配置缺失时回退到此）
    private const int DefaultMaxConcurrency = 10;
    private const int DefaultMaxConcurrencyWaitSeconds = 5;
    private const int DefaultMaxPoolSize = 100;
    private const int DefaultConnectTimeoutSeconds = 60;

    public static ResolvedConfig Build(DatabasesConfig raw)
    {
        // 全局并发/池默认值：未配置或非法 → 内置默认
        int globalMaxConcurrency = raw.DefaultMaxConcurrency is int mc && mc > 0 ? mc : DefaultMaxConcurrency;
        int globalWaitSeconds = raw.DefaultMaxConcurrencyWaitSeconds is int w && w > 0 ? w : DefaultMaxConcurrencyWaitSeconds;
        int globalMaxPoolSize = raw.DefaultMaxPoolSize is int p && p > 0 ? p : DefaultMaxPoolSize;
        int globalConnectTimeout = raw.DefaultConnectTimeoutSeconds is int t && t > 0 ? t : DefaultConnectTimeoutSeconds;

        // 第一层：全局通用。未配置则用内置默认
        IEnumerable<string> global = raw.DefaultDisabledKeywords is { Count: > 0 }
            ? raw.DefaultDisabledKeywords
            : DefaultDisabledKeywords.BuiltIn;

        var projects = new Dictionary<string, ResolvedProject>(StringComparer.OrdinalIgnoreCase);
        foreach ((string projectName, ProjectConfig proj) in raw.Projects)
        {
            var envs = new Dictionary<string, ResolvedDatabase>(StringComparer.OrdinalIgnoreCase);
            foreach ((string envName, DatabaseConfig db) in proj.Environments)
            {
                // 第二层：按类型。未配置则用内置默认
                IReadOnlyList<string> byType =
                    raw.DefaultDisabledKeywordsByType is not null &&
                    raw.DefaultDisabledKeywordsByType.TryGetValue(db.Type, out List<string>? kw) && kw is { Count: > 0 }
                        ? kw
                        : DefaultDisabledKeywords.BuiltInByType.TryGetValue(db.Type, out IReadOnlyList<string>? builtIn)
                            ? builtIn
                            : Array.Empty<string>();

                // 第三层：环境额外
                IEnumerable<string> env = db.DisabledKeywords;

                // 合并：全部转大写去重
                var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string k in global.Concat(byType).Concat(env))
                {
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        merged.Add(k.Trim().ToUpperInvariant());
                    }
                }

                // 环境级覆盖全局默认（<=0 回退全局）
                int maxPoolSize = db.MaxPoolSize > 0 ? db.MaxPoolSize : globalMaxPoolSize;
                int connectTimeout = db.ConnectTimeoutSeconds > 0 ? db.ConnectTimeoutSeconds : globalConnectTimeout;
                int maxConcurrency = db.MaxConcurrency > 0 ? db.MaxConcurrency : globalMaxConcurrency;

                string finalConnectionString = BuildConnectionString(db.ConnectionString, db.Type, maxPoolSize, connectTimeout);

                envs[envName] = new ResolvedDatabase
                {
                    ProjectName = projectName,
                    Environment = envName,
                    IsProduction = db.IsProduction,
                    Type = db.Type,
                    ConnectionString = finalConnectionString,
                    DatabaseName = ResolveDatabaseName(db.ConnectionString, db.Type),
                    MaxRows = db.MaxRows <= 0 ? 1000 : db.MaxRows,
                    CommandTimeout = db.CommandTimeout <= 0 ? 600 : db.CommandTimeout,
                    MaxPoolSize = maxPoolSize,
                    ConnectTimeoutSeconds = connectTimeout,
                    MaxConcurrency = maxConcurrency,
                    MaxConcurrencyWaitSeconds = globalWaitSeconds,
                    DisabledKeywords = merged
                };
            }

            projects[projectName] = new ResolvedProject
            {
                ProjectName = projectName,
                DefaultEnvironment = proj.DefaultEnvironment,
                Environments = new ReadOnlyDictionary<string, ResolvedDatabase>(envs)
            };
        }

        return new ResolvedConfig
        {
            Projects = new ReadOnlyDictionary<string, ResolvedProject>(projects)
        };
    }

    /// <summary>
    /// 用各驱动官方 ConnectionStringBuilder 将连接池与建连超时参数拼接到连接串。
    /// 仅在解析成功时覆盖；解析失败（畸形串）保留原串，不阻断查询。
    /// </summary>
    private static string BuildConnectionString(string raw, DatabaseType type, int maxPoolSize, int connectTimeoutSeconds)
    {
        try
        {
            DbConnectionStringBuilder b = type switch
            {
                DatabaseType.SqlServer => new SqlConnectionStringBuilder(raw),
                DatabaseType.MySql => new MySqlConnectionStringBuilder(raw),
                DatabaseType.Oracle => new OracleConnectionStringBuilder(raw),
                _ => null! // 理论不可达：枚举已覆盖全部类型
            };
            // 各驱动的键名不同，统一用字符串索引器写入，避免依赖具体属性名
            (string poolKey, string timeoutKey) = type switch
            {
                DatabaseType.SqlServer => ("Max Pool Size", "Connect Timeout"),
                DatabaseType.MySql => ("Maximum Pool Size", "Connection Timeout"),
                DatabaseType.Oracle => ("Max Pool Size", "Connection Timeout"),
                _ => (string.Empty, string.Empty)
            };
            b[poolKey] = maxPoolSize;
            b[timeoutKey] = connectTimeoutSeconds;
            return b.ConnectionString;
        }
        catch
        {
            // 畸形连接串：保留原值，运行时由驱动报错
            return raw;
        }
    }

    /// <summary>
    /// 从原始连接串提取默认库/schema 标识（按类型分流），供 db_list 返回。
    /// SqlServer=Initial Catalog；MySQL=Database；Oracle=User Id。
    /// 畸形串/键缺失/空白 → null，不抛、不阻断列表。
    /// 只读单属性，绝不输出连接串整体或 Password 键。
    /// </summary>
    private static string? ResolveDatabaseName(string raw, DatabaseType type)
    {
        try
        {
            string? v = type switch
            {
                DatabaseType.SqlServer => new SqlConnectionStringBuilder(raw).InitialCatalog,
                DatabaseType.MySql => new MySqlConnectionStringBuilder(raw).Database,
                DatabaseType.Oracle => new OracleConnectionStringBuilder(raw).UserID,
                _ => null
            };
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch
        {
            // 畸形连接串：返回 null，运行时由驱动报错；此处不阻断列表
            return null;
        }
    }
}
