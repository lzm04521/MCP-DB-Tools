using System.ComponentModel;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Security;
using ModelContextProtocol.Server;

namespace McpDbTools.Server.Tools;

/// <summary>
/// MCP 数据库查询工具。向 Claude Code 暴露 db_query 工具，按项目+环境执行只读 SQL 并返回 AI 友好结构化结果。
/// <para>依赖通过构造函数注入（实例工具类，由 MCP SDK 经 DI 容器实例化）。</para>
/// </summary>
[McpServerToolType]
public sealed class DbQueryTool
{
    private readonly ConfigStore _configStore;
    private readonly ISqlGuard _sqlGuard;
    private readonly DatabaseProviderFactory _providerFactory;
    private readonly AuditLogger _audit;
    private readonly IQueryConcurrencyLimiter _limiter;

    public DbQueryTool(ConfigStore configStore, ISqlGuard sqlGuard, DatabaseProviderFactory providerFactory, AuditLogger audit, IQueryConcurrencyLimiter limiter)
    {
        _configStore = configStore;
        _sqlGuard = sqlGuard;
        _providerFactory = providerFactory;
        _audit = audit;
        _limiter = limiter;
    }

    /// <summary>
    /// 在指定项目的指定环境上执行 SQL 查询。
    /// project 为项目名（对应 config.json 中 databases 配置）；
    /// environment 为环境名（如 dev/test/prod/xiqing-prod，可选；未传时使用项目 defaultEnvironment）；
    /// sql 为查询语句，只读环境仅允许只读操作；写环境（allowWrite=true，非生产）支持 DML/DDL 写操作，返回受影响行数（affectedRows）；
    /// limit 为可选的最大返回行数（仅对读语句生效）；
    /// format 为可选行编码三档：text（缺省，纯文本）/ tsv（JSON 壳 + rowset）/ json（JSON 壳 + rows 二维数组）。
    /// offset 为可选跳过行数（仅读语句生效；SQL Server/Oracle 要求 SQL 带 ORDER BY；
    /// SQL 已带 LIMIT/OFFSET/FOR UPDATE 时不可用；截断时状态行标注 nextOffset 供续翻）。
    /// dryRun 为可选写影响预估（标准形态 UPDATE/DELETE 变换为 COUNT 只读查询返回 "~N affected (estimated)"，
    /// 不执行写操作；估算不含触发器影响；INSERT/DDL/复杂形态返回 DRYRUN_UNSUPPORTED；不可与 limit/offset 同用）。
    /// text 返回：首行状态行（OK 行数 @项目/环境 (类型[,offset=N]) [truncated,nextOffset=N]，或 FAIL 错误码: 消息），
    /// 第 2 行列名，其后数据行 TSV（tab 分列、\N=NULL、二进制列显示 &lt;binary NB&gt;）。
    /// 可用 db_list 工具列出所有项目及其环境。
    /// </summary>
    [McpServerTool(Name = "db_query")]
    [Description("在项目的指定环境执行 SQL。project=项目名(必填)；environment 可选(缺省用项目 defaultEnvironment)；sql 必填。limit/offset 可选(仅读语句分页；SQL Server/Oracle 需 ORDER BY；SQL 已带 LIMIT/OFFSET/FOR UPDATE 时不可用)；dryRun 可选(仅标准形态 UPDATE/DELETE 返回估算行数不执行，不可与 limit/offset 同用)；format 可选 text(缺省)/tsv/json。只读环境仅允许只读语句；写环境(allowWrite 且非生产)支持 DML/DDL。返回纯文本：首行状态(OK/FAIL+错误码)+列名+TSV 数据。先用 db_list 查项目与环境。")]
    public async Task<string> ExecuteQuery(
        string project,
        string sql,
        string? environment = null,
        int? limit = null,
        string? format = null,
        int? offset = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        // 0. 行编码格式三档（宽容解析：Trim+忽略大小写）：text=纯文本(缺省) / tsv=JSON壳+rowset / json=JSON壳+rows；其他值回落 text
        RowFormat rowFormat = ParseRowFormat(format);

        // 1. 解析项目配置（实时读取，支持热重载）
        ResolvedConfig config = _configStore.GetResolved();
        if (!config.Projects.TryGetValue(project, out ResolvedProject? proj))
        {
            return QueryResult.Fail(project, "Unknown", $"项目不存在: {project}", "PROJECT_NOT_FOUND", environment: environment).Serialize(rowFormat);
        }

        // 2. 解析环境：未指定则回退到项目 defaultEnvironment
        string env = string.IsNullOrWhiteSpace(environment) ? (proj.DefaultEnvironment ?? string.Empty) : environment;
        if (string.IsNullOrWhiteSpace(env))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"未指定环境，且项目 {project} 未配置 defaultEnvironment。可用环境: {available}", "ENVIRONMENT_REQUIRED", environment: environment).Serialize(rowFormat);
        }
        if (!proj.Environments.TryGetValue(env, out ResolvedDatabase? db))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"环境不存在: {env}。项目 {project} 可用环境: {available}", "ENVIRONMENT_NOT_FOUND", environment: env).Serialize(rowFormat);
        }

        // 3. limit 覆盖 maxRows：取配置与入参的较小值（入参为空则用配置值）
        int maxRows = limit.HasValue ? Math.Min(limit.Value, db.MaxRows) : db.MaxRows;

        // 3.5 offset 前置校验：负数拒绝（limit 既有校验行为保持不变）
        if (offset is int off && off < 0)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "offset 不能为负数。", "PARAMETER_ERROR", environment: env).Serialize(rowFormat);
        }

        // 3.6 dryRun 参数互斥：预估恒为 COUNT 单行，limit/offset 无意义，显式拒绝优于静默忽略
        if (dryRun && (limit.HasValue || offset.HasValue))
        {
            return QueryResult.Fail(project, db.Type.ToString(), "dryRun=true 时不可同时指定 limit/offset。", "PARAMETER_ERROR", environment: env).Serialize(rowFormat);
        }

        // 4. SQL 安全校验
        var guardResult = _sqlGuard.Validate(sql, db);
        if (!guardResult.Allowed)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, guardResult.Reason));
            return QueryResult.Fail(project, db.Type.ToString(), guardResult.Reason, guardResult.ErrorCode, environment: env).Serialize(rowFormat);
        }

        // 4.4 dryRun：写语句影响行数预估——不执行写，变换为 COUNT 只读查询。
        //     provider 获取上移至此（dryRun 与正常执行路径均需要）。
        IDatabaseProvider provider = _providerFactory.Get(db.Type);
        if (dryRun)
        {
            if (guardResult.Kind != StatementKind.Write)
            {
                return QueryResult.Fail(project, db.Type.ToString(), "dryRun 仅对写语句生效，读语句无需预估。", "PARAMETER_ERROR", environment: env).Serialize(rowFormat);
            }
            if (!WriteImpactEstimator.TryBuildCountSql(sql, out string countSql, out string estReason))
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, $"[dryRun不支持] {estReason}"));
                return QueryResult.Fail(project, db.Type.ToString(), estReason, "DRYRUN_UNSUPPORTED", environment: env).Serialize(rowFormat);
            }
            // 走只读执行路径（内部生成的 COUNT 语句不进 SqlGuard 校验路径，入参原 SQL 已过守卫）
            QueryResult estResult;
            try
            {
                await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, cancellationToken);
                estResult = await provider.ExecuteQueryAsync(project, db, countSql, 1, cancellationToken);
            }
            catch (QueryRateLimitedException ex)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"[dryRun] {sql}", 0, 0, false, ex.Message));
                return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).Serialize(rowFormat);
            }
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"[dryRun] {sql}", estResult.RowCount, estResult.ExecutionTimeMs, estResult.Success, estResult.Error));
            if (!estResult.Success)
            {
                return estResult.Serialize(rowFormat);
            }
            return QueryResult.Ok(estResult.Project, estResult.DatabaseType, estResult.Columns, estResult.Rows,
                estResult.MaxRows, estResult.Truncated, estResult.ExecutionTimeMs, estResult.Environment, estimated: true).Serialize(rowFormat);
        }

        // 4.5 offset 分页：仅对读语句生效，按方言改写 SQL（fetch = maxRows）
        int? resultOffset = null;
        if (offset is int pageOffset && guardResult.Kind == StatementKind.Write)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "offset 参数仅对读语句生效，写语句不支持分页。", "PARAMETER_ERROR", environment: env).Serialize(rowFormat);
        }
        if (offset is int pageOff && guardResult.Kind == StatementKind.Read)
        {
            var outcome = SqlPaginator.TryAppend(db.Type, sql, pageOff, maxRows, out string paginated, out string? why);
            if (outcome == OffsetAppendOutcome.RequiresOrderBy)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, why!));
                return QueryResult.Fail(project, db.Type.ToString(), why!, "OFFSET_REQUIRES_ORDER_BY", environment: env).Serialize(rowFormat);
            }
            if (outcome == OffsetAppendOutcome.Conflict)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, why!));
                return QueryResult.Fail(project, db.Type.ToString(), why!, "PARAMETER_ERROR", environment: env).Serialize(rowFormat);
            }
            sql = paginated;
            resultOffset = pageOff;
        }

        // 5. 执行（读走 Reader，写走 NonQuery；带每环境并发限流）
        QueryResult result;
        try
        {
            // 申请并发槽位：超载排队，超过 MaxConcurrencyWaitSeconds 抛 QueryRateLimitedException
            await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, cancellationToken);
            // 按 SqlGuard 判定的 StatementKind 分流：写语句（DML/DDL，allowWrite=true 环境）→ NonQuery 返回受影响行数；
            // 读语句 → 原 Reader 路径返回 columns/rows
            result = guardResult.Kind == StatementKind.Write
                ? await provider.ExecuteNonQueryAsync(project, db, sql, cancellationToken)
                : await provider.ExecuteQueryAsync(project, db, sql, maxRows, cancellationToken);
        }
        catch (QueryRateLimitedException ex)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, ex.Message));
            return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).Serialize(rowFormat);
        }
        catch (OperationCanceledException)
        {
            // 客户端取消/超时（provider 对外部 ct 取消会重新抛出 OperationCanceledException）：
            // 记审计后返回失败，避免逃逸异常不记审计（阶段 3，诊断 20260722）
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, "查询被取消（客户端超时或中断）"));
            return QueryResult.Fail(project, db.Type.ToString(), "查询被取消", "QUERY_CANCELED", environment: env).Serialize(rowFormat);
        }
        catch (Exception ex)
        {
            // 非 DbException 逃逸异常（provider 仅 catch DbException/TimeoutException）：
            // 记审计后返回失败，防止单查询异常逃逸不记审计或崩溃进程（阶段 3）
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, $"未处理异常: {ex.GetType().Name}: {ex.Message}"));
            return QueryResult.Fail(project, db.Type.ToString(), $"未处理异常: {ex.Message}", "QUERY_UNHANDLED", environment: env).Serialize(rowFormat);
        }

        // 5.5 分页请求标注：回显 offset；truncated 时附 nextOffset 供续翻
        //     （QueryResult 为 init-only class，经 Ok 工厂重组携带分页字段）
        if (resultOffset is int ro && result.Success && !result.IsWrite)
        {
            int? nextOff = result.Truncated ? ro + result.RowCount : null;
            result = QueryResult.Ok(result.Project, result.DatabaseType, result.Columns, result.Rows,
                result.MaxRows, result.Truncated, result.ExecutionTimeMs, result.Environment, ro, nextOff);
        }

        // 6. 审计（开关开启且成功时，记录查询结果到子表）
        MaintenanceConfig maintenance = _configStore.Current.Maintenance ?? MaintenanceConfig.Default;
        string? resultJson = (maintenance.AuditRecordResults && result.Success)
            ? AuditLogger.SerializeResult(result)
            : null;
        _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, result.RowCount, result.ExecutionTimeMs, result.Success, result.Error, resultJson));

        // 6.5 错误自愈（doc/20260901 P1）：猜列名/表名失败自动附真实列清单/相近表，省一轮无效往返。
        //     审计主条目已记原始错误（上一行），附加文本只进返回消息；仅读路径，辅助限流/异常一律降级返回原错误。
        if (!result.Success && guardResult.Kind == StatementKind.Read && !cancellationToken.IsCancellationRequested)
        {
            string? assist = await TryBuildErrorAssistAsync(project, env, db, provider, sql, result, cancellationToken);
            if (assist is not null)
            {
                result = QueryResult.Fail(
                    result.Project ?? project,
                    result.DatabaseType ?? db.Type.ToString(),
                    result.Error + "\n" + assist,
                    result.ErrorCode ?? "QUERY_ERROR",
                    result.ExecutionTimeMs,
                    result.Environment);
            }
        }

        return result.Serialize(rowFormat);
    }

    /// <summary>
    /// 错误辅助编排（doc/20260901 P1）：分类失败消息 → 列名错误附相关表真实列清单（SQL 提取表名，逐表查），
    /// 表名错误附相近表（模糊搜）。辅助查询经 ExecuteSchemaQueryAsync（值参数化，内部生成不经 SqlGuard），
    /// 逐条走并发闸门并记 [assist] 前缀审计；限流/取消/异常返回 null 静默降级（主失败已记审计，辅助永不放大失败）。
    /// </summary>
    private async Task<string?> TryBuildErrorAssistAsync(
        string project, string env, ResolvedDatabase db, IDatabaseProvider provider, string sql, QueryResult failed, CancellationToken ct)
    {
        QueryErrorAssist.ErrorSignal signal = QueryErrorAssist.Classify(db.Type, failed.Error ?? string.Empty);
        if (signal.Kind == QueryErrorAssist.AssistKind.None)
        {
            return null;
        }

        if (signal.Kind == QueryErrorAssist.AssistKind.InvalidTable)
        {
            // 坏名优先取错误消息（ORA-00942 除外），回退 SQL 提取；去 schema 前缀后须为纯标识符（防消息值混入特殊字符）
            string? name = signal.BadName is not null
                ? QueryErrorAssist.StripSchema(signal.BadName)
                : QueryErrorAssist.ExtractTableNames(sql).Select(QueryErrorAssist.StripSchema).FirstOrDefault();
            if (!QueryErrorAssist.IsPlainIdentifier(name))
            {
                return null;
            }

            QueryResult? search = await RunAssistQueryAsync(project, env, db, provider,
                SchemaDialects.TablesLikeSql(db.Type),
                "%" + SchemaDialects.EscapeLikePattern(name!) + "%",
                $"[assist] 相近表 %{name}%", ct);
            if (search is null || !search.Success)
            {
                return null;
            }

            // TablesLikeSql 列序：schema_name, table_name, ...——候选取表名去重（同表名多 schema 只展示一次）
            List<string> candidates = search.Rows
                .Select(row => row.Length > 1 ? row[1]?.ToString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return QueryErrorAssist.FormatTableAssist(candidates);
        }

        // InvalidColumn：SQL 提取相关表（≤3），逐表附真实列清单；某表也不存在（查列失败/空）自然跳过
        IReadOnlyList<string> tables = QueryErrorAssist.ExtractTableNames(sql);
        if (tables.Count == 0)
        {
            return null;
        }

        var found = new List<(string Table, IReadOnlyList<string> Columns)>();
        foreach (string t in tables)
        {
            QueryResult? cols = await RunAssistQueryAsync(project, env, db, provider,
                SchemaDialects.ColumnsSql(db.Type), t, $"[assist] {t} 列清单", ct);
            if (cols is null)
            {
                break; // 限流/取消：已有部分结果也可用
            }
            if (!cols.Success || cols.RowCount == 0)
            {
                continue; // 该表不存在（表名也猜错）或列清单为空：跳过
            }
            found.Add((t, cols.Rows.Select(row => row[0]?.ToString() ?? "").Where(s => s.Length > 0).ToList()));
        }
        return found.Count > 0 ? QueryErrorAssist.FormatColumnAssist(found) : null;
    }

    /// <summary>单条辅助元数据查询：同一并发闸门 + [assist] 审计；限流/取消/异常返回 null（辅助永不放大失败）。</summary>
    private async Task<QueryResult?> RunAssistQueryAsync(
        string project, string env, ResolvedDatabase db, IDatabaseProvider provider,
        string sql, string value, string auditLabel, CancellationToken ct)
    {
        try
        {
            await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, ct);
            QueryResult r = await provider.ExecuteSchemaQueryAsync(project, db, sql, value, ct);
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), auditLabel, r.RowCount, r.ExecutionTimeMs, r.Success, r.Error));
            return r;
        }
        catch (QueryRateLimitedException)
        {
            return null; // 辅助不与主查询争抢：限流即放弃
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), auditLabel, 0, 0, false, $"辅助查询异常: {ex.GetType().Name}: {ex.Message}"));
            return null;
        }
    }

    /// <summary>format 参数宽容解析：tsv/json 显式匹配，text 或任何其他值回落 text（与旧版"其他值回落"同哲学，仅回落点变为缺省档）。</summary>
    private static RowFormat ParseRowFormat(string? format)
    {
        switch (format?.Trim().ToLowerInvariant())
        {
            case "tsv": return RowFormat.Tsv;
            case "json": return RowFormat.Json;
            default: return RowFormat.Text;
        }
    }

    private static AuditEntry MakeEntry(string project, string environment, string dbType, string sql, int rowCount, long elapsedMs, bool success, string? error, string? resultJson = null) => new()
    {
        Time = AuditLogger.NowUtcIso(),
        Project = project,
        Environment = environment,
        DatabaseType = dbType,
        Sql = sql,
        RowCount = rowCount,
        ElapsedMs = elapsedMs,
        Success = success,
        Error = success ? null : error,
        ResultJson = resultJson
    };
}
