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
    /// format 为可选行编码（"json" 回退 rows 二维数组；缺省 TSV rowset，NULL 编码为 \N）。
    /// offset 为可选跳过行数（仅读语句生效；SQL Server/Oracle 要求 SQL 带 ORDER BY；
    /// SQL 已带 LIMIT/OFFSET/FOR UPDATE 时不可用；truncated=true 时返回 nextOffset 供续翻）。
    /// 读返回 rowCount/truncated/columns + rowset(或 rows)；写返回 affectedRows；失败返回 error/errorCode/executionTimeMs。
    /// 可用 db_list 工具列出所有项目及其环境。
    /// </summary>
    [McpServerTool(Name = "db_query")]
    [Description("在项目的指定环境执行 SQL。project=项目名(必填)；environment 可选(dev/test/prod 等，缺省用项目 defaultEnvironment)；sql 必填；limit 可选(读语句行数上限)；offset 可选(跳过行数,仅读语句;SQL Server/Oracle 需 SQL 带 ORDER BY;SQL 已带 LIMIT/OFFSET/FOR UPDATE 时不可用;truncated=true 时返回 nextOffset 续翻)；format 可选，传 \"json\" 回退 rows 二维数组，缺省 TSV。只读环境仅允许只读语句；写环境(allowWrite=true，非生产)支持 DML/DDL，返回 affectedRows。读结果含 columns 列名数组 + rowset TSV 文本(制表符分列、\\n 分行、\\N 表示 NULL)与 truncated 截断标记。先用 db_list() 查项目、db_list(project=...) 查环境。")]
    public async Task<string> ExecuteQuery(
        string project,
        string sql,
        string? environment = null,
        int? limit = null,
        string? format = null,
        int? offset = null,
        CancellationToken cancellationToken = default)
    {
        // 0. 行编码格式：宽容解析，仅 "json"（Trim+忽略大小写）回退二维数组，其他值一律 TSV
        RowFormat rowFormat = format?.Trim().Equals("json", StringComparison.OrdinalIgnoreCase) == true
            ? RowFormat.Json
            : RowFormat.Tsv;

        // 1. 解析项目配置（实时读取，支持热重载）
        ResolvedConfig config = _configStore.GetResolved();
        if (!config.Projects.TryGetValue(project, out ResolvedProject? proj))
        {
            return QueryResult.Fail(project, "Unknown", $"项目不存在: {project}", "PROJECT_NOT_FOUND", environment: environment).ToJson(rowFormat);
        }

        // 2. 解析环境：未指定则回退到项目 defaultEnvironment
        string env = string.IsNullOrWhiteSpace(environment) ? (proj.DefaultEnvironment ?? string.Empty) : environment;
        if (string.IsNullOrWhiteSpace(env))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"未指定环境，且项目 {project} 未配置 defaultEnvironment。可用环境: {available}", "ENVIRONMENT_REQUIRED", environment: environment).ToJson(rowFormat);
        }
        if (!proj.Environments.TryGetValue(env, out ResolvedDatabase? db))
        {
            string available = string.Join(", ", proj.Environments.Keys);
            return QueryResult.Fail(project, "Unknown", $"环境不存在: {env}。项目 {project} 可用环境: {available}", "ENVIRONMENT_NOT_FOUND", environment: env).ToJson(rowFormat);
        }

        // 3. limit 覆盖 maxRows：取配置与入参的较小值（入参为空则用配置值）
        int maxRows = limit.HasValue ? Math.Min(limit.Value, db.MaxRows) : db.MaxRows;

        // 3.5 offset 前置校验：负数拒绝（limit 既有校验行为保持不变）
        if (offset is int off && off < 0)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "offset 不能为负数。", "PARAMETER_ERROR", environment: env).ToJson(rowFormat);
        }

        // 4. SQL 安全校验
        var guardResult = _sqlGuard.Validate(sql, db);
        if (!guardResult.Allowed)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, guardResult.Reason));
            return QueryResult.Fail(project, db.Type.ToString(), guardResult.Reason, guardResult.ErrorCode, environment: env).ToJson(rowFormat);
        }

        // 4.5 offset 分页：仅对读语句生效，按方言改写 SQL（fetch = maxRows）
        int? resultOffset = null;
        if (offset is int pageOffset && guardResult.Kind == StatementKind.Write)
        {
            return QueryResult.Fail(project, db.Type.ToString(), "offset 参数仅对读语句生效，写语句不支持分页。", "PARAMETER_ERROR", environment: env).ToJson(rowFormat);
        }
        if (offset is int pageOff && guardResult.Kind == StatementKind.Read)
        {
            var outcome = SqlPaginator.TryAppend(db.Type, sql, pageOff, maxRows, out string paginated, out string? why);
            if (outcome == OffsetAppendOutcome.RequiresOrderBy)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, why!));
                return QueryResult.Fail(project, db.Type.ToString(), why!, "OFFSET_REQUIRES_ORDER_BY", environment: env).ToJson(rowFormat);
            }
            if (outcome == OffsetAppendOutcome.Conflict)
            {
                _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, why!));
                return QueryResult.Fail(project, db.Type.ToString(), why!, "PARAMETER_ERROR", environment: env).ToJson(rowFormat);
            }
            sql = paginated;
            resultOffset = pageOff;
        }

        // 5. 执行（读走 Reader，写走 NonQuery；带每环境并发限流）
        IDatabaseProvider provider = _providerFactory.Get(db.Type);
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
            return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).ToJson(rowFormat);
        }
        catch (OperationCanceledException)
        {
            // 客户端取消/超时（provider 对外部 ct 取消会重新抛出 OperationCanceledException）：
            // 记审计后返回失败，避免逃逸异常不记审计（阶段 3，诊断 20260722）
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, "查询被取消（客户端超时或中断）"));
            return QueryResult.Fail(project, db.Type.ToString(), "查询被取消", "QUERY_CANCELED", environment: env).ToJson(rowFormat);
        }
        catch (Exception ex)
        {
            // 非 DbException 逃逸异常（provider 仅 catch DbException/TimeoutException）：
            // 记审计后返回失败，防止单查询异常逃逸不记审计或崩溃进程（阶段 3）
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, $"未处理异常: {ex.GetType().Name}: {ex.Message}"));
            return QueryResult.Fail(project, db.Type.ToString(), $"未处理异常: {ex.Message}", "QUERY_UNHANDLED", environment: env).ToJson(rowFormat);
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

        return result.ToJson(rowFormat);
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
