using System.ComponentModel;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Security;
using ModelContextProtocol.Server;

namespace McpDbTools.Server.Tools;

/// <summary>
/// db_explain 执行计划工具：对只读语句返回数据库执行计划（MySQL/PG 为 EXPLAIN 行集；
/// SQL Server 为 SHOWPLAN 计划行集；Oracle 为 DBMS_XPLAN 输出）。
/// 仅分析只读语句——写语句即使在写环境（allowWrite=true）也被拒绝（SqlGuard 会放行写语句，
/// 此处必须显式按 StatementKind.Write 拒绝，不依赖环境开关兜底）。
/// </summary>
[McpServerToolType]
public sealed class DbExplainTool
{
    private readonly ConfigStore _configStore;
    private readonly ISqlGuard _sqlGuard;
    private readonly DatabaseProviderFactory _providerFactory;
    private readonly AuditLogger _audit;
    private readonly IQueryConcurrencyLimiter _limiter;

    public DbExplainTool(ConfigStore configStore, ISqlGuard sqlGuard, DatabaseProviderFactory providerFactory, AuditLogger audit, IQueryConcurrencyLimiter limiter)
    {
        _configStore = configStore;
        _sqlGuard = sqlGuard;
        _providerFactory = providerFactory;
        _audit = audit;
        _limiter = limiter;
    }

    /// <summary>
    /// 查询执行计划。project=项目名(必填)；sql 必填(仅只读语句,写语句返回 SQL_BLOCKED)；
    /// environment 可选(缺省用项目 defaultEnvironment)。返回计划行集(与 db_query 读形状同构，text 缺省)：
    /// MySQL 传统 EXPLAIN 列(id/select_type/table/type/key/rows/Extra)；PG 默认文本格式；
    /// SQL Server SHOWPLAN_ALL 列(StmtText/PhysicalOp/EstimateRows/TotalSubtreeCost 等,不实际执行)；
    /// Oracle 经 DBMS_XPLAN 输出(依赖 PLAN_TABLE)。不支持 EXPLAIN ANALYZE(会实际执行)。
    /// </summary>
    [McpServerTool(Name = "db_explain")]
    [Description("查询执行计划(慢查询分析用)。project=项目名(必填)；sql 必填且仅只读语句(写语句返回 SQL_BLOCKED)；environment 可选(缺省用项目 defaultEnvironment)。返回计划行集(与 db_query 同构)。各方言输出形态不同：MySQL 传统 EXPLAIN 列 / PG 文本 / SQL Server SHOWPLAN_ALL(不实际执行) / Oracle DBMS_XPLAN。不支持 EXPLAIN ANALYZE。")]
    public async Task<string> Explain(
        string project,
        string sql,
        string? environment = null,
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

        // 3. SqlGuard 校验 + 显式 Kind 检查：写环境下 SqlGuard 对写语句返回 Allowed，
        //    必须按 Kind==Write 拒绝，保持"工具只碰只读语句"边界（不依赖环境开关兜底）
        var guardResult = _sqlGuard.Validate(sql, db);
        if (!guardResult.Allowed)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, guardResult.Reason));
            return QueryResult.Fail(project, db.Type.ToString(), guardResult.Reason, guardResult.ErrorCode, environment: env).Serialize();
        }
        if (guardResult.Kind == StatementKind.Write)
        {
            string reason = "db_explain 仅分析只读语句（写语句执行计划无意义且工具不碰写路径）";
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), sql, 0, 0, false, reason));
            return QueryResult.Fail(project, db.Type.ToString(), reason, "SQL_BLOCKED", environment: env).Serialize();
        }

        // 4. 执行（同一并发闸门）；EXPLAIN 前缀/会话语句由 provider 内部生成，不进 Agent 可控校验路径
        IDatabaseProvider provider = _providerFactory.Get(db.Type);
        QueryResult result;
        try
        {
            await using IAsyncDisposable slot = await _limiter.AcquireAsync(project, env, db, cancellationToken);
            result = await provider.ExplainAsync(project, db, sql, cancellationToken);
        }
        catch (QueryRateLimitedException ex)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_explain:{sql}", 0, 0, false, ex.Message));
            return QueryResult.Fail(project, db.Type.ToString(), ex.Message, "RATE_LIMITED", environment: env).Serialize();
        }
        catch (OperationCanceledException)
        {
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_explain:{sql}", 0, 0, false, "查询被取消（客户端超时或中断）"));
            return QueryResult.Fail(project, db.Type.ToString(), "查询被取消", "QUERY_CANCELED", environment: env).Serialize();
        }
        catch (Exception ex)
        {
            // 非 DbException 逃逸异常：记审计后返回失败，防止崩到 SDK 层（与 DbQueryTool 阶段 3 同模式）
            _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_explain:{sql}", 0, 0, false, $"未处理异常: {ex.GetType().Name}: {ex.Message}"));
            return QueryResult.Fail(project, db.Type.ToString(), $"未处理异常: {ex.Message}", "QUERY_UNHANDLED", environment: env).Serialize();
        }

        // 5. 审计 + 返回（text 缺省，db_explain 不开放 format 参数，与 db_schema 口径一致）
        _audit.Log(MakeEntry(project, env, db.Type.ToString(), $"db_explain:{sql}", result.RowCount, result.ExecutionTimeMs, result.Success, result.Error));
        return result.Serialize();
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
