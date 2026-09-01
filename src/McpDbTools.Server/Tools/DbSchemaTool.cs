using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using ModelContextProtocol.Server;

namespace McpDbTools.Server.Tools;

/// <summary>
/// db_schema 元数据探索工具：两级按需加载（与 db_list 哲学一致）+ 模糊搜索。
/// 不传 table 返回表清单（schema/表名/注释/估算行数）；传 table 标识符返回该表列/索引/外键三段（可选 sample 采样）；
/// table 含 % 为表名模糊搜索（% 通配、_ 字面）；column 参数按列名（可含 %）反查含该列的表。
/// text 返回：头部状态行 + 每段 "# 段名 (行数)" + 表头 + TSV（与 db_query 同一编码）。
/// </summary>
[McpServerToolType]
public sealed class DbSchemaTool
{
    private static readonly Regex TableIdentifierPattern = new(
        @"^[A-Za-z][A-Za-z0-9_$#]*(\.[A-Za-z][A-Za-z0-9_$#]*)?$", RegexOptions.Compiled);

    // 模糊搜索模式值字符白名单（% 通配、_ 字面）：拒绝引号/空白/点，杜绝拼接与 schema 歧义
    private static readonly Regex LikePatternPattern = new(@"^[A-Za-z0-9_$#%]+$", RegexOptions.Compiled);

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
    /// table 可选——不传返回表清单(schema/表名/注释/行数)，传标识符返回该表列/索引/外键三段，含 % 为表名模糊搜索(%通配、_字面)；
    /// column 可选——按列名(可含%)反查含该列的表，与 table/sample 互斥；
    /// sample 可选(>0 时附 SELECT * 采样前 N 行,需配合 table 精确标识符)。
    /// text 返回：首行状态行，每段 "# 段名 (行数)" + 列名行 + TSV 数据(\N=NULL)。
    /// </summary>
    [McpServerTool(Name = "db_schema")]
    [Description("列出数据库元数据。project=项目名(必填)；environment 可选(缺省用项目 defaultEnvironment)；table 可选——不传返回表清单(schema/表名/注释/行数)，传标识符返回该表列/索引/外键三段，含 % 模糊搜表名；column 可选——按列名(可含%)反查含该列的表，与 table/sample 互斥；sample 可选(>0 附采样，需配合 table)。返回纯文本：首行状态+按 # 段名分块+列名+TSV。Oracle 对象名大写存储。可先用 db_list 查项目与环境。")]
    public async Task<string> GetSchema(
        string project,
        string? environment = null,
        string? table = null,
        string? column = null,
        int? sample = null,
        CancellationToken cancellationToken = default)
    {
        // 1/2. 解析项目与环境（与 db_query 同一矩阵）
        ResolvedConfig config = _configStore.GetResolved();
        if (!config.Projects.TryGetValue(project, out ResolvedProject? proj))
        {
            return QueryResult.Fail(project, "Unknown", $"项目不存在: {project}", "PROJECT_NOT_FOUND", environment: environment).Serialize();
        }
        string env = string.IsNullOrWhiteSpace(environment) ? (proj.DefaultEnvironment ?? string.Empty) : environment;
        if (string.IsNullOrWhiteSpace(env))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"未指定环境，且项目 {project} 未配置 defaultEnvironment。可用环境: {available}", "ENVIRONMENT_REQUIRED", environment: environment).Serialize();
        }
        if (!proj.Environments.TryGetValue(env, out ResolvedDatabase? db))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"环境不存在: {env}。项目 {project} 可用环境: {available}", "ENVIRONMENT_NOT_FOUND", environment: env).Serialize();
        }

        // 3. 参数校验与模式分流：column 反查 / table 模糊 / table 详情 / 表清单。
        //    模糊模式值字符白名单（拒绝引号/空白/点）+ 值一律参数化；sample 需 table 精确标识符（采样拼接表名无法参数化）
        bool hasColumn = !string.IsNullOrWhiteSpace(column);
        bool hasTable = !string.IsNullOrWhiteSpace(table);
        bool tableFuzzy = hasTable && table!.Contains('%');

        if (hasColumn && hasTable)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "column 与 table 参数互斥：反查列名用 column，查表结构用 table。", "PARAMETER_ERROR", environment: env).Serialize();
        }
        if (hasColumn && !LikePatternPattern.IsMatch(column!))
        {
            return QueryResult.Fail(project, db.Type.ToString(), $"column 参数含非法字符（仅支持字母/数字/下划线/$/#/%）: {column}", "PARAMETER_ERROR", environment: env).Serialize();
        }
        if (hasColumn && sample is > 0)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "column 反查不支持 sample。", "PARAMETER_ERROR", environment: env).Serialize();
        }
        if (tableFuzzy)
        {
            if (sample is > 0)
            {
                return QueryResult.Fail(project, db.Type.ToString(), "table 模糊搜索不支持 sample（采样需精确表名）。", "PARAMETER_ERROR", environment: env).Serialize();
            }
            if (!LikePatternPattern.IsMatch(table!))
            {
                return QueryResult.Fail(project, db.Type.ToString(), $"table 模糊模式含非法字符（仅支持字母/数字/下划线/$/#/%）: {table}", "PARAMETER_ERROR", environment: env).Serialize();
            }
        }
        else if (hasTable && !TableIdentifierPattern.IsMatch(table!))
        {
            return QueryResult.Fail(project, db.Type.ToString(), $"table 参数不是合法标识符（仅支持字母/数字/下划线/$/#，可带 schema 前缀）: {table}", "PARAMETER_ERROR", environment: env).Serialize();
        }
        int sampleRows = (sample is > 0) ? sample.Value : 0;
        if (sampleRows > 0 && !hasTable)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "sample 参数需配合 table 使用。", "PARAMETER_ERROR", environment: env).Serialize();
        }

        // 4/5. 执行元数据查询（同一并发闸门），按模式分流
        IDatabaseProvider provider = _providerFactory.Get(db.Type);
        string header;
        string auditLabel;
        List<SchemaSection> sections;
        SchemaSection? sampleSection = null;

        if (hasColumn)
        {
            string raw = column!.Trim();
            bool columnFuzzy = raw.Contains('%');
            string searchSql = SchemaDialects.ColumnSearchSql(db.Type, exact: !columnFuzzy);
            string searchValue = columnFuzzy ? SchemaDialects.EscapeLikePattern(raw) : raw;
            QueryResult r = await RunSearchAsync(provider, project, env, db, searchSql, searchValue, $"db_schema:column:{raw}", cancellationToken);
            if (!r.Success)
            {
                return r.Serialize();
            }
            header = $"column={raw}";
            auditLabel = $"db_schema:column:{raw}";
            sections = new List<SchemaSection> { new("matches", r) };
        }
        else if (tableFuzzy)
        {
            string searchSql = SchemaDialects.TablesLikeSql(db.Type);
            QueryResult r = await RunSearchAsync(provider, project, env, db, searchSql, SchemaDialects.EscapeLikePattern(table!), $"db_schema:{table}", cancellationToken);
            if (!r.Success)
            {
                return r.Serialize();
            }
            header = $"tables~{table}";
            auditLabel = $"db_schema:{table}";
            sections = new List<SchemaSection> { new("tables", r) };
        }
        else
        {
            IReadOnlyList<SchemaSection> loaded;
            try
            {
                await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, cancellationToken);
                loaded = await provider.GetSchemaAsync(project, db, hasTable ? table : null, cancellationToken);
            }
            catch (QueryRateLimitedException ex)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table ?? "(清单)"}", 0, 0, false, ex.Message));
                return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).Serialize();
            }
            catch (OperationCanceledException)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table ?? "(清单)"}", 0, 0, false, "查询被取消（客户端超时或中断）"));
                return QueryResult.Fail(project, db.Type.ToString(), "查询被取消", "QUERY_CANCELED", environment: env).Serialize();
            }
            catch (Exception ex)
            {
                // 非 DbException 逃逸异常（provider 仅包装 DbException/TimeoutException）：
                // 记审计后返回失败，防止单查询异常逃逸不记审计或崩到 SDK 层（与 DbQueryTool 阶段 3 同模式）
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table ?? "(清单)"}", 0, 0, false, $"未处理异常: {ex.GetType().Name}: {ex.Message}"));
                return QueryResult.Fail(project, db.Type.ToString(), $"未处理异常: {ex.Message}", "QUERY_UNHANDLED", environment: env).Serialize();
            }

            // 段失败短路透传（表不存在等由 provider 包装为 QUERY_ERROR）
            if (loaded.Count == 1 && !loaded[0].Result.Success)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table ?? "(清单)"}", 0, 0, false, loaded[0].Result.Error));
                return loaded[0].Result.Serialize();
            }
            sections = new List<SchemaSection>(loaded);
            header = hasTable ? $"table={table}" : "tables";
            auditLabel = $"db_schema:{table ?? "(清单)"}";

            // sample 采样（表名已过标识符校验；内部生成语句不进 SqlGuard 校验路径）
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
                    return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).Serialize();
                }
                if (!sampleResult.Success)
                {
                    _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_schema:{table} sample", 0, 0, false, sampleResult.Error));
                    return sampleResult.Serialize();
                }
                sampleSection = new SchemaSection("sample", sampleResult);
            }
        }

        // 7. 审计 + 组装返回（text：头部状态行 + 每段 "# 段名 (行数)" + 表头 + TSV，段体复用 QueryResult 同一编码）
        int totalRows = sections.Sum(s => s.Result.RowCount) + (sampleSection?.Result.RowCount ?? 0);
        _audit.Log(MakeEntry(project, env, db.Type.ToString(), auditLabel, totalRows, 0, true, null));

        var sb = new StringBuilder();
        sb.Append("OK ").Append(header)
            .Append(" @").Append(project).Append('/').Append(env)
            .Append(" (").Append(db.Type.ToString().ToLowerInvariant()).Append(')');
        foreach (SchemaSection section in sampleSection is null ? sections : sections.Append(sampleSection))
        {
            sb.Append("\n# ").Append(section.Name).Append(" (").Append(section.Result.RowCount).Append(')');
            if (section.Result.Truncated) sb.Append(" [truncated]");
            section.Result.AppendTextBody(sb);
        }
        return sb.ToString();
    }

    /// <summary>单条元数据搜索查询（模糊表/列名反查）：同一并发闸门，限流/取消/失败记审计；失败结果由调用方透传。</summary>
    private async Task<QueryResult> RunSearchAsync(
        IDatabaseProvider provider, string project, string env, ResolvedDatabase db,
        string sql, string value, string auditLabel, CancellationToken ct)
    {
        try
        {
            await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, ct);
            QueryResult r = await provider.ExecuteSchemaQueryAsync(project, db, sql, value, ct);
            if (!r.Success)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), auditLabel, 0, 0, false, r.Error));
            }
            return r;
        }
        catch (QueryRateLimitedException ex)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), auditLabel, 0, 0, false, ex.Message));
            return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env);
        }
        catch (OperationCanceledException)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), auditLabel, 0, 0, false, "查询被取消（客户端超时或中断）"));
            return QueryResult.Fail(project, db.Type.ToString(), "查询被取消", "QUERY_CANCELED", environment: env);
        }
        catch (Exception ex)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), auditLabel, 0, 0, false, $"未处理异常: {ex.GetType().Name}: {ex.Message}"));
            return QueryResult.Fail(project, db.Type.ToString(), $"未处理异常: {ex.Message}", "QUERY_UNHANDLED", environment: env);
        }
    }

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
