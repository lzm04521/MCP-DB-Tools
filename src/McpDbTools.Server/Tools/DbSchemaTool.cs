using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using ModelContextProtocol.Server;

namespace McpDbTools.Server.Tools;

/// <summary>
/// db_schema 元数据探索工具：两级按需加载（与 db_list 哲学一致）。
/// 不传 table 返回表清单（schema/表名/注释/估算行数）；传 table 返回该表列/索引/外键三段，
/// 可选 sample 采样前 N 行。每段与 db_query 读形状同构（columns + TSV rowset）。
/// </summary>
[McpServerToolType]
public sealed class DbSchemaTool
{
    private static readonly Regex TableIdentifierPattern = new(
        @"^[A-Za-z][A-Za-z0-9_$#]*(\.[A-Za-z][A-Za-z0-9_$#]*)?$", RegexOptions.Compiled);

    // 与 QueryResult.ToJson 一致的序列化选项（camelCase + 中文不转义）
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ConfigStore _configStore;
    private readonly DatabaseProviderFactory _providerFactory;
    private readonly AuditLogger _audit;
    private readonly IQueryConcurrencyLimiter _limiter;

    public DbSchemaTool(ConfigStore configStore, DatabaseProviderFactory providerFactory, AuditLogger audit, IQueryConcurrencyLimiter limiter)
    {
        _configStore = configStore;
        _providerFactory = providerFactory;
        _audit = audit;
        _limiter = limiter;
    }

    /// <summary>
    /// 列出数据库元数据。project=项目名(必填)；environment 可选(缺省用项目 defaultEnvironment)；
    /// table 可选——不传返回表清单(schema/表名/注释/行数)，传则返回该表列/索引/外键三段；
    /// sample 可选(>0 时附 SELECT * 采样前 N 行,需配合 table；表名仅支持字母数字下划线等常规标识符)。
    /// 每段含 columns 数组 + rowset TSV 文本(制表符分列、\N 表示 NULL)与 rowCount/truncated。
    /// </summary>
    [McpServerTool(Name = "db_schema")]
    [Description("列出数据库元数据。project=项目名(必填)；environment 可选(缺省用项目 defaultEnvironment)；table 可选——不传返回表清单(schema/表名/注释/行数)，传则返回该表列/索引/外键三段；sample 可选(>0 时附 SELECT * 采样前 N 行,需配合 table,表名仅支持常规标识符)。每段含 columns 数组 + rowset TSV 文本(制表符分列、\\N 表示 NULL)与 rowCount/truncated。Oracle 对象名以大写存储。可先用 db_list() 查项目与环境。")]
    public async Task<string> GetSchema(
        string project,
        string? environment = null,
        string? table = null,
        int? sample = null,
        CancellationToken cancellationToken = default)
    {
        // 1/2. 解析项目与环境（与 db_query 同一矩阵）
        ResolvedConfig config = _configStore.GetResolved();
        if (!config.Projects.TryGetValue(project, out ResolvedProject? proj))
        {
            return QueryResult.Fail(project, "Unknown", $"项目不存在: {project}", "PROJECT_NOT_FOUND", environment: environment).ToJson();
        }
        string env = string.IsNullOrWhiteSpace(environment) ? (proj.DefaultEnvironment ?? string.Empty) : environment;
        if (string.IsNullOrWhiteSpace(env))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"未指定环境，且项目 {project} 未配置 defaultEnvironment。可用环境: {available}", "ENVIRONMENT_REQUIRED", environment: environment).ToJson();
        }
        if (!proj.Environments.TryGetValue(env, out ResolvedDatabase? db))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"环境不存在: {env}。项目 {project} 可用环境: {available}", "ENVIRONMENT_NOT_FOUND", environment: env).ToJson();
        }

        // 3. 参数校验：table 标识符白名单（sample 采样需拼接表名，标识符无法参数化）；
        //    sample 依赖 table；sample 非正数视为未传
        bool hasTable = !string.IsNullOrWhiteSpace(table);
        if (hasTable && !TableIdentifierPattern.IsMatch(table!))
        {
            return QueryResult.Fail(project, db.Type.ToString(), $"table 参数不是合法标识符（仅支持字母/数字/下划线/$/#，可带 schema 前缀）: {table}", "PARAMETER_ERROR", environment: env).ToJson();
        }
        int sampleRows = (sample is > 0) ? sample.Value : 0;
        if (sampleRows > 0 && !hasTable)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "sample 参数需配合 table 使用。", "PARAMETER_ERROR", environment: env).ToJson();
        }

        // 4. 执行元数据查询（同一并发闸门）
        IDatabaseProvider provider = _providerFactory.Get(db.Type);
        IReadOnlyList<SchemaSection> sections;
        try
        {
            await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, cancellationToken);
            sections = await provider.GetSchemaAsync(project, db, hasTable ? table : null, cancellationToken);
        }
        catch (QueryRateLimitedException ex)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table ?? "(清单)"}", 0, 0, false, ex.Message));
            return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).ToJson();
        }

        // 5. 段失败短路透传（表不存在等由 provider 包装为 QUERY_ERROR）
        if (sections.Count == 1 && !sections[0].Result.Success)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table ?? "(清单)"}", 0, 0, false, sections[0].Result.Error));
            return sections[0].Result.ToJson();
        }

        // 6. sample 采样（表名已过标识符校验；内部生成语句不进 SqlGuard 校验路径）
        SchemaSection? sampleSection = null;
        if (sampleRows > 0)
        {
            QueryResult sampleResult;
            try
            {
                await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, cancellationToken);
                sampleResult = await provider.ExecuteQueryAsync(project, db, $"SELECT * FROM {table}", Math.Min(sampleRows, db.MaxRows), cancellationToken);
            }
            catch (QueryRateLimitedException ex)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table} sample", 0, 0, false, ex.Message));
                return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).ToJson();
            }
            if (!sampleResult.Success)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table} sample", 0, 0, false, sampleResult.Error));
                return sampleResult.ToJson();
            }
            sampleSection = new SchemaSection("sample", sampleResult);
        }

        // 7. 审计 + 组装返回（sections 数组，每段与 db_query 读形状同构）
        int totalRows = sections.Sum(s => s.Result.RowCount) + (sampleSection?.Result.RowCount ?? 0);
        _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table ?? "(清单)"}", totalRows, 0, true, null));

        var payload = new
        {
            success = true,
            project,
            environment = env,
            databaseType = db.Type.ToString(),
            table = hasTable ? table : null,
            sections = sections.Select(s => ToSectionDto(s, includeSample: false))
                .Concat(sampleSection is not null ? new[] { ToSectionDto(sampleSection, includeSample: false) } : Enumerable.Empty<object>())
                .ToList()
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>段投影：{name, columns, rowset(TSV), rowCount, truncated}——rowset 复用 QueryResult 同一编码。</summary>
    private static object ToSectionDto(SchemaSection section, bool includeSample)
        => new
        {
            name = section.Name,
            columns = section.Result.Columns,
            rowset = section.Result.BuildRowset(),
            rowCount = section.Result.RowCount,
            truncated = section.Result.Truncated
        };

    private static AuditEntry MakeEntry(string project, string environment, string dbType, string sql, int rowCount, long elapsedMs, bool success, string? error) => new()
    {
        Time = AuditLogger.NowUtcIso(),
        Project = project,
        Environment = environment,
        DatabaseType = dbType,
        Sql = sql,
        RowCount = rowCount,
        ElapsedMs = elapsedMs,
        Success = success,
        Error = success ? null : error
    };
}
