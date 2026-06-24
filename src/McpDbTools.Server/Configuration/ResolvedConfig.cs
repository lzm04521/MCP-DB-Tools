using System.Collections.ObjectModel;

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

    /// <summary>审计日志配置。</summary>
    public required AuditConfig Audit { get; init; }
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

    public required DatabaseType Type { get; init; }
    public required string ConnectionString { get; init; }
    public required int MaxRows { get; init; }
    public required int CommandTimeout { get; init; }

    /// <summary>
    /// 合并后的阻止关键字集合（大写）。空格关键字如 "BULK INSERT" 保留空格，做子串匹配。
    /// </summary>
    public required IReadOnlySet<string> DisabledKeywords { get; init; }
}

/// <summary>将三层配置合并为 ResolvedConfig。</summary>
public static class ResolvedConfigBuilder
{
    public static ResolvedConfig Build(DatabasesConfig raw)
    {
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

                envs[envName] = new ResolvedDatabase
                {
                    ProjectName = projectName,
                    Environment = envName,
                    Type = db.Type,
                    ConnectionString = db.ConnectionString,
                    MaxRows = db.MaxRows <= 0 ? 1000 : db.MaxRows,
                    CommandTimeout = db.CommandTimeout <= 0 ? 30 : db.CommandTimeout,
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
            Projects = new ReadOnlyDictionary<string, ResolvedProject>(projects),
            Audit = raw.Audit
        };
    }
}
