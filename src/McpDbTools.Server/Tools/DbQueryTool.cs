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
    /// 在指定项目的指定环境上执行只读 SQL 查询。
    /// project 为项目名（对应 config.json 中 databases 配置）；
    /// environment 为环境名（如 dev/test/prod/xiqing-prod，可选；未传时使用项目 defaultEnvironment）；
    /// sql 为查询语句（仅允许只读操作），limit 为可选的最大返回行数。
    /// 返回包含 project、environment、columns 和 rows(二维数组) 的 JSON。
    /// 可用 db_list 工具列出所有项目及其环境。
    /// </summary>
    [McpServerTool(Name = "db_query")]
    [Description("在指定项目的指定环境上执行只读 SQL 查询。project 为项目名(对应 config.json 中 databases 配置)，environment 为环境名(如 dev/test/prod，可选；未传时使用项目的 defaultEnvironment)，sql 为查询语句(仅允许只读操作)，limit 为可选的最大返回行数。返回包含 project、environment、columns 和 rows(二维数组) 的 JSON。可先用 db_list() 获取项目列表，再 db_list(project=...) 获取环境详情。")]
    public async Task<string> ExecuteQuery(
        string project,
        string sql,
        string? environment = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        // 1. 解析项目配置（实时读取，支持热重载）
        ResolvedConfig config = _configStore.GetResolved();
        if (!config.Projects.TryGetValue(project, out ResolvedProject? proj))
        {
            return QueryResult.Fail(project, "Unknown", $"项目不存在: {project}", "PROJECT_NOT_FOUND", environment: environment).ToJson();
        }

        // 2. 解析环境：未指定则回退到项目 defaultEnvironment
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

        // 3. limit 覆盖 maxRows：取配置与入参的较小值（入参为空则用配置值）
        int maxRows = limit.HasValue ? Math.Min(limit.Value, db.MaxRows) : db.MaxRows;

        // 4. SQL 安全校验
        var guardResult = _sqlGuard.Validate(sql, db);
        if (!guardResult.Allowed)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, guardResult.Reason));
            _audit.Flush(); // 同步排空：保证审计先于返回落盘，防进程退出丢队列（诊断 20260722）
            return QueryResult.Fail(project, db.Type.ToString(), guardResult.Reason, guardResult.ErrorCode, environment: env).ToJson();
        }

        // 5. 执行查询（带每环境并发限流）
        IDatabaseProvider provider = _providerFactory.Get(db.Type);
        QueryResult result;
        try
        {
            // 申请并发槽位：超载排队，超过 MaxConcurrencyWaitSeconds 抛 QueryRateLimitedException
            await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, cancellationToken);
            result = await provider.ExecuteQueryAsync(project, db, sql, maxRows, cancellationToken);
        }
        catch (QueryRateLimitedException ex)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, ex.Message));
            _audit.Flush(); // 同步排空
            return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).ToJson();
        }
        catch (OperationCanceledException)
        {
            // 客户端取消/超时（provider 对外部 ct 取消会重新抛出 OperationCanceledException）：
            // 记审计后返回失败，避免逃逸异常不记审计（阶段 3，诊断 20260722）
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, "查询被取消（客户端超时或中断）"));
            _audit.Flush();
            return QueryResult.Fail(project, db.Type.ToString(), "查询被取消", "QUERY_CANCELED", environment: env).ToJson();
        }
        catch (Exception ex)
        {
            // 非 DbException 逃逸异常（provider 仅 catch DbException/TimeoutException）：
            // 记审计后返回失败，防止单查询异常逃逸不记审计或崩溃进程（阶段 3）
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, $"未处理异常: {ex.GetType().Name}: {ex.Message}"));
            _audit.Flush();
            return QueryResult.Fail(project, db.Type.ToString(), $"未处理异常: {ex.Message}", "QUERY_UNHANDLED", environment: env).ToJson();
        }

        // 6. 审计（开关开启且成功时，记录查询结果到子表）
        MaintenanceConfig maintenance = _configStore.Current.Maintenance ?? MaintenanceConfig.Default;
        string? resultJson = (maintenance.AuditRecordResults && result.Success)
            ? AuditLogger.SerializeResult(result)
            : null;
        _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, result.RowCount, result.ExecutionTimeMs, result.Success, result.Error, resultJson));
        _audit.Flush(); // 同步排空：查询返回前确保审计已落盘，不依赖后台消费者与进程退出方式

        return result.ToJson();
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
