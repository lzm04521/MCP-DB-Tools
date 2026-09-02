using System.Data.Common;
using System.Diagnostics;
using McpDbTools.Server.Configuration;
using Oracle.ManagedDataAccess.Client;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>Oracle 提供者。使用 ODP.NET 托管驱动（3.21.x，兼容 11g R2+）。</summary>
public sealed class OracleProvider : DatabaseProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.Oracle;

    protected override DbConnection CreateConnection(string connectionString)
        => new OracleConnection(connectionString);

    /// <summary>ODP.NET 绑定变量仅识别 : 前缀且 TABLE 为 Oracle 保留字（ORA-00936/ORA-01745），元数据占位符改写为 :tbl（见基类 SchemaPlaceholder）。</summary>
    protected override (string Token, string ParamName) SchemaPlaceholder => (":tbl", ":tbl");

    /// <summary>
    /// Oracle 执行计划：同连接两条语句——EXPLAIN PLAN FOR（仅硬解析，不执行）写入 PLAN_TABLE，
    /// 再经 DBMS_XPLAN.DISPLAY() 读计划行集。无会话级 SET 状态，无需复位。
    /// 依赖 PLAN_TABLE 存在（现代库默认有）；精简权限账号缺失时由下方 DbException 包装为 QUERY_ERROR，
    /// 错误信息含 ORA- 号与提示。
    /// </summary>
    public override async Task<QueryResult> ExplainAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new OracleConnection(db.ConnectionString);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(db.ConnectTimeoutSeconds));
        try
        {
            await conn.OpenAsync(connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return QueryResult.Fail(project, DatabaseType.ToString(), $"连接超时（{db.ConnectTimeoutSeconds} 秒）", "QUERY_CONNECT_TIMEOUT", sw.ElapsedMilliseconds, db.Environment);
        }
        catch (DbException ex)
        {
            // 建连失败（如 ORA-12154 TNS 解析失败）：与 ExecuteQueryAsync 骨架一致包装为 QUERY_ERROR，不逃逸
            return QueryResult.Fail(project, DatabaseType.ToString(), $"执行计划查询错误: {ex.Message}", "QUERY_ERROR", sw.ElapsedMilliseconds, db.Environment);
        }

        (string planSql, string readSql) = BuildExplainStatements(sql);
        try
        {
            await using (DbCommand plan = conn.CreateCommand())
            {
                plan.CommandText = planSql;
                plan.CommandTimeout = db.CommandTimeout;
                await plan.ExecuteNonQueryAsync(ct);
            }

            await using DbCommand read = conn.CreateCommand();
            read.CommandText = readSql;
            read.CommandTimeout = db.CommandTimeout;
            await using DbDataReader reader = await read.ExecuteReaderAsync(ct);
            var (columns, rows, truncated) = await ReadAsync(reader, db.MaxRows, ct);
            sw.Stop();
            return QueryResult.Ok(project, DatabaseType.ToString(), columns, rows, db.MaxRows, truncated, sw.ElapsedMilliseconds, db.Environment);
        }
        catch (DbException ex)
        {
            sw.Stop();
            return QueryResult.Fail(project, DatabaseType.ToString(), $"执行计划查询错误: {ex.Message}（精简权限账号可能缺少 PLAN_TABLE）", "QUERY_ERROR", sw.ElapsedMilliseconds, db.Environment);
        }
    }

    /// <summary>EXPLAIN PLAN 双语句（纯函数，供测试与实现共用）。</summary>
    internal static (string PlanSql, string ReadSql) BuildExplainStatements(string sql)
        => ($"EXPLAIN PLAN FOR {sql.Trim().TrimEnd(';')}", "SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY())");
}
