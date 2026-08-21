using System.Data.Common;
using System.Diagnostics;
using McpDbTools.Server.Configuration;
using Microsoft.Data.SqlClient;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>SQL Server 提供者。连接池由 SqlClient 内置管理。</summary>
public sealed class SqlServerProvider : DatabaseProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.SqlServer;

    protected override DbConnection CreateConnection(string connectionString)
        => new SqlConnection(connectionString);

    /// <summary>
    /// SQL Server 执行计划：同连接三步 SET SHOWPLAN_ALL ON → ExecuteReader(sql)（不实际执行，返回计划行集）
    /// → finally SET OFF。OFF 必须复位：连接池复用会话级 SET，不复位会污染池中连接（后续查询返回计划而非数据）。
    /// 双保险：SqlClient 归还池化连接时执行 sp_reset_connection 亦会重置 SET 选项；此处 finally 兜异常路径。
    /// </summary>
    public override async Task<QueryResult> ExplainAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new SqlConnection(db.ConnectionString);
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

        string[] seq = BuildShowPlanSequence(sql);
        await using (DbCommand on = conn.CreateCommand())
        {
            on.CommandText = seq[0];
            on.CommandTimeout = db.CommandTimeout;
            await on.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using DbCommand plan = conn.CreateCommand();
            plan.CommandText = seq[1];
            plan.CommandTimeout = db.CommandTimeout;
            await using DbDataReader reader = await plan.ExecuteReaderAsync(ct);
            var (columns, rows, truncated) = await ReadAsync(reader, db.MaxRows, ct);
            sw.Stop();
            return QueryResult.Ok(project, DatabaseType.ToString(), columns, rows, db.MaxRows, truncated, sw.ElapsedMilliseconds, db.Environment);
        }
        finally
        {
            try
            {
                await using DbCommand off = conn.CreateCommand();
                off.CommandText = seq[2];
                off.CommandTimeout = db.CommandTimeout;
                // 复位不用业务取消令牌：主查询已取消/失败也要尽力复位，避免污染池化连接
                await off.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                // 连接已死时复位无意义（连接销毁不归还池）；吞掉避免掩盖主异常——此处仅清理路径，非业务错误
            }
        }
    }

    /// <summary>SHOWPLAN 三步命令序列（纯函数，供测试与实现共用）。</summary>
    internal static string[] BuildShowPlanSequence(string sql)
        => new[] { "SET SHOWPLAN_ALL ON", sql.Trim().TrimEnd(';'), "SET SHOWPLAN_ALL OFF" };
}
