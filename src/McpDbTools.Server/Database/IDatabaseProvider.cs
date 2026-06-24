using System.Data;
using System.Data.Common;
using System.Diagnostics;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Database;

/// <summary>
/// 数据库提供者接口。每种数据库一个实现，负责执行只读查询并返回结构化结果。
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>数据库类型标识（与配置枚举对应）。</summary>
    DatabaseType DatabaseType { get; }

    /// <summary>执行查询。sql 已通过 SqlGuard 校验。</summary>
    Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct);
}

/// <summary>
/// 提供者共享的查询执行骨架：打开连接 → 创建命令 → 执行读取器 → 转换为 QueryResult。
/// 所有 ADO.NET 驱动的 Connection 均继承自 <see cref="DbConnection"/>，统一在此处理。
/// </summary>
public abstract class DatabaseProviderBase : IDatabaseProvider
{
    public abstract DatabaseType DatabaseType { get; }

    /// <summary>由子类创建对应驱动类型的连接对象。</summary>
    protected abstract DbConnection CreateConnection(string connectionString);

    public async Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using DbConnection conn = CreateConnection(db.ConnectionString);
            await conn.OpenAsync(ct);

            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = db.CommandTimeout;

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);

            var (columns, rows, truncated) = await ReadAsync(reader, maxRows, ct);
            sw.Stop();

            return QueryResult.Ok(project, DatabaseType.ToString(), columns, rows, maxRows, truncated, sw.ElapsedMilliseconds, db.Environment);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消向上传播，不包装
        }
        catch (TimeoutException ex)
        {
            return QueryResult.Fail(project, DatabaseType.ToString(), $"查询超时: {ex.Message}", "QUERY_TIMEOUT", sw.ElapsedMilliseconds, db.Environment);
        }
        catch (DbException ex)
        {
            return QueryResult.Fail(project, DatabaseType.ToString(), $"查询执行错误: {ex.Message}", "QUERY_ERROR", sw.ElapsedMilliseconds, db.Environment);
        }
    }

    /// <summary>从 DataReader 读取数据为 columns + rows，按 maxRows 截断。</summary>
    private static async Task<(List<string> columns, List<object?[]> rows, bool truncated)> ReadAsync(
        DbDataReader reader, int maxRows, CancellationToken ct)
    {
        var columns = new List<string>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        var rows = new List<object?[]>(Math.Min(maxRows, 64));
        bool truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }
            var row = new object?[reader.FieldCount];
            // GetValues 接受 object[]，DBNull 需转 null 以便 JSON 输出为 null
            object[] buffer = new object[reader.FieldCount];
            reader.GetValues(buffer);
            for (int i = 0; i < buffer.Length; i++)
            {
                row[i] = buffer[i] is DBNull ? null : buffer[i];
            }
            rows.Add(row);
        }
        return (columns, rows, truncated);
    }
}
