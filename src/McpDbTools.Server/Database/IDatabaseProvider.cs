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

    /// <summary>执行写操作（INSERT/UPDATE/DELETE/DDL）。sql 已通过 SqlGuard 校验为写语句。返回受影响行数。</summary>
    Task<QueryResult> ExecuteNonQueryAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct);

    /// <summary>
    /// 测试连接是否可用。仅打开连接（用短超时，默认 5 秒），不执行任何 SQL。
    /// 返回 (success, elapsedMs, error)。
    /// </summary>
    Task<(bool Success, long ElapsedMs, string? Error)> TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct);

    /// <summary>
    /// 元数据查询。table=null（或空白）返回 [tables] 单段表清单；非空返回 [columns, indexes, foreignKeys] 三段。
    /// 任一查询失败即返回含失败 QueryResult 的单段列表（调用方直接透传错误）。
    /// </summary>
    Task<IReadOnlyList<SchemaSection>> GetSchemaAsync(string project, ResolvedDatabase db, string? table, CancellationToken ct);

    /// <summary>
    /// 执行单条元数据模板查询（sql 为 SchemaDialects 模板，paramValue 绑定 @table 参数）。
    /// 供 db_schema 模糊搜索（TablesLikeSql/ColumnSearchSql）与 db_query 错误自愈复用；
    /// 内部生成语句不经 SqlGuard 校验路径（值一律参数化，无拼接）。
    /// </summary>
    Task<QueryResult> ExecuteSchemaQueryAsync(string project, ResolvedDatabase db, string sql, string paramValue, CancellationToken ct);

    /// <summary>
    /// 执行计划查询。sql 为已通过只读校验的单条语句；基类默认 EXPLAIN 前缀拼接（MySQL/PG），
    /// SqlServer/Oracle 的会话式实现由子类 override。
    /// </summary>
    Task<QueryResult> ExplainAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct);
}

/// <summary>
/// EXPLAIN 前缀构造（纯函数）。仅 MySQL/PG；SqlServer/Oracle 走 provider override，误入即抛错暴露。
/// </summary>
internal static class ExplainSqlBuilder
{
    public static string Build(DatabaseType type, string sql)
        => type switch
        {
            DatabaseType.MySql or DatabaseType.PostgreSql
                => $"EXPLAIN {sql.Trim().TrimEnd(';')}",
            _ => throw new NotSupportedException($"{type} 的执行计划由 provider override 提供，不应走前缀拼接"),
        };
}

/// <summary>元数据查询段结果：Name 分段标识 + 单段查询结果。</summary>
public sealed record SchemaSection(string Name, QueryResult Result);

/// <summary>
/// 模板选择纯逻辑：table null/空白 → 表清单模板；非空 → 单表详情三段模板。
/// 与基类执行逻辑分离，便于单测（基类实际执行依赖真实连接）。
/// </summary>
internal static class SchemaExecutor
{
    public static IReadOnlyList<SchemaSectionTemplate> BuildSections(DatabaseType type, string? table)
        => string.IsNullOrWhiteSpace(table)
            ? SchemaDialects.Tables(type)
            : SchemaDialects.TableDetail(type);
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

    public async Task<(bool Success, long ElapsedMs, string? Error)> TestConnectionAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int timeout = timeoutSeconds > 0 ? timeoutSeconds : 5;
        // 用 CTS 兜底超时：各驱动 ConnectionTimeout 多为只读，统一在调用层控制
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            await using DbConnection conn = CreateConnection(connectionString);
            await conn.OpenAsync(timeoutCts.Token);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 触发的是超时 CTS，不是外部取消
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, $"连接超时（{timeout} 秒）");
        }
        catch (OperationCanceledException)
        {
            throw; // 外部取消向上传播，不包装
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<QueryResult> ExecuteQueryAsync(string project, ResolvedDatabase db, string sql, int maxRows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using DbConnection conn = CreateConnection(db.ConnectionString);

            // 建连阶段超时兜底：与 TestConnectionAsync 一致，用 CTS 控制建连超时。
            // 连接池耗尽时 OpenAsync 会卡住，此处避免其卡满驱动默认超时。
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(db.ConnectTimeoutSeconds));
            try
            {
                await conn.OpenAsync(connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 触发的是建连超时 CTS，不是外部取消
                sw.Stop();
                return QueryResult.Fail(project, DatabaseType.ToString(), $"连接超时（{db.ConnectTimeoutSeconds} 秒）", "QUERY_CONNECT_TIMEOUT", sw.ElapsedMilliseconds, db.Environment);
            }

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

    /// <summary>
    /// 执行写操作（INSERT/UPDATE/DELETE/DDL）。建连/超时骨架与 <see cref="ExecuteQueryAsync"/> 一致，
    /// 命令执行用 <see cref="DbCommand.ExecuteNonQueryAsync(CancellationToken)"/>，返回受影响行数。
    /// </summary>
    public async Task<QueryResult> ExecuteNonQueryAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using DbConnection conn = CreateConnection(db.ConnectionString);

            // 建连阶段超时兜底：与 ExecuteQueryAsync 一致，用 CTS 控制建连超时。
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(db.ConnectTimeoutSeconds));
            try
            {
                await conn.OpenAsync(connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 触发的是建连超时 CTS，不是外部取消
                sw.Stop();
                return QueryResult.Fail(project, DatabaseType.ToString(), $"连接超时（{db.ConnectTimeoutSeconds} 秒）", "QUERY_CONNECT_TIMEOUT", sw.ElapsedMilliseconds, db.Environment);
            }

            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = db.CommandTimeout;

            int affected = await cmd.ExecuteNonQueryAsync(ct);
            sw.Stop();
            return QueryResult.OkWrite(project, DatabaseType.ToString(), affected, sw.ElapsedMilliseconds, db.Environment);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消向上传播，不包装
        }
        catch (TimeoutException ex)
        {
            return QueryResult.Fail(project, DatabaseType.ToString(), $"写操作超时: {ex.Message}", "QUERY_TIMEOUT", sw.ElapsedMilliseconds, db.Environment);
        }
        catch (DbException ex)
        {
            return QueryResult.Fail(project, DatabaseType.ToString(), $"写操作执行错误: {ex.Message}", "QUERY_ERROR", sw.ElapsedMilliseconds, db.Environment);
        }
    }

    /// <summary>
    /// 执行计划基类默认实现：前缀方言（MySQL/PG）经 ExplainSqlBuilder 拼接后走只读执行骨架。
    /// SqlServer/Oracle 的会话式实现由子类 override（见 SqlServerProvider/OracleProvider）。
    /// </summary>
    public virtual Task<QueryResult> ExplainAsync(string project, ResolvedDatabase db, string sql, CancellationToken ct)
    {
        string explainSql = ExplainSqlBuilder.Build(DatabaseType, sql);
        return ExecuteQueryAsync(project, db, explainSql, db.MaxRows, ct);
    }

    /// <summary>
    /// 元数据查询执行：按 SchemaExecutor 选模板，逐段执行（表名经 DbParameter 参数化），
    /// 任一段失败即短路返回失败段。建连/超时/异常矩阵与 ExecuteQueryAsync 一致：
    /// 建连抛出的 DbException（如 ORA-12154）同样包装为失败段，不逃逸到 MCP SDK 层。
    /// </summary>
    public async Task<IReadOnlyList<SchemaSection>> GetSchemaAsync(string project, ResolvedDatabase db, string? table, CancellationToken ct)
    {
        IReadOnlyList<SchemaSectionTemplate> sections = SchemaExecutor.BuildSections(db.Type, table);

        try
        {
            await using DbConnection conn = CreateConnection(db.ConnectionString);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(db.ConnectTimeoutSeconds));
            try
            {
                await conn.OpenAsync(connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new[] { new SchemaSection("tables", QueryResult.Fail(project, DatabaseType.ToString(), $"连接超时（{db.ConnectTimeoutSeconds} 秒）", "QUERY_CONNECT_TIMEOUT", 0, db.Environment)) };
            }

            var results = new List<SchemaSection>();
            foreach (SchemaSectionTemplate section in sections)
            {
                try
                {
                    await using DbCommand cmd = conn.CreateCommand();
                    cmd.CommandText = section.Sql;
                    cmd.CommandTimeout = db.CommandTimeout;
                    if (section.HasTableParam)
                    {
                        DbParameter p = cmd.CreateParameter();
                        p.ParameterName = SchemaDialects.TableParamName;
                        p.Value = table;
                        cmd.Parameters.Add(p);
                    }
                    await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
                    var (columns, rows, truncated) = await ReadAsync(reader, db.MaxRows, ct);
                    results.Add(new SchemaSection(section.Name, QueryResult.Ok(project, DatabaseType.ToString(), columns, rows, db.MaxRows, truncated, 0, db.Environment)));
                }
                catch (DbException ex)
                {
                    // 任一段失败（表不存在等）即短路返回，调用方透传错误
                    return new[] { new SchemaSection(section.Name, QueryResult.Fail(project, DatabaseType.ToString(), $"元数据查询错误: {ex.Message}", "QUERY_ERROR", 0, db.Environment)) };
                }
                catch (TimeoutException ex)
                {
                    return new[] { new SchemaSection(section.Name, QueryResult.Fail(project, DatabaseType.ToString(), $"元数据查询超时: {ex.Message}", "QUERY_TIMEOUT", 0, db.Environment)) };
                }
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            throw; // 取消向上传播，不包装
        }
        catch (DbException ex)
        {
            // 建连阶段失败（连接串不可达/TNS 解析失败等）与段循环未覆盖的驱动异常：包装为失败段，不逃逸
            return new[] { new SchemaSection("tables", QueryResult.Fail(project, DatabaseType.ToString(), $"元数据查询错误: {ex.Message}", "QUERY_ERROR", 0, db.Environment)) };
        }
    }

    /// <summary>
    /// 单条元数据模板查询：独立开连接（与 GetSchemaAsync 段循环的建连/超时/异常矩阵一致，
    /// 失败包装为 FAIL QueryResult 不逃逸）。paramValue 绑定 @table（LIKE 模式值需调用方先经
    /// QueryErrorAssist.EscapeLikePattern 转义）。
    /// </summary>
    public async Task<QueryResult> ExecuteSchemaQueryAsync(string project, ResolvedDatabase db, string sql, string paramValue, CancellationToken ct)
    {
        try
        {
            await using DbConnection conn = CreateConnection(db.ConnectionString);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(db.ConnectTimeoutSeconds));
            try
            {
                await conn.OpenAsync(connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return QueryResult.Fail(project, DatabaseType.ToString(), $"连接超时（{db.ConnectTimeoutSeconds} 秒）", "QUERY_CONNECT_TIMEOUT", 0, db.Environment);
            }

            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = db.CommandTimeout;
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = SchemaDialects.TableParamName;
            p.Value = paramValue;
            cmd.Parameters.Add(p);

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            var (columns, rows, truncated) = await ReadAsync(reader, db.MaxRows, ct);
            return QueryResult.Ok(project, DatabaseType.ToString(), columns, rows, db.MaxRows, truncated, 0, db.Environment);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消向上传播，不包装
        }
        catch (DbException ex)
        {
            return QueryResult.Fail(project, DatabaseType.ToString(), $"元数据查询错误: {ex.Message}", "QUERY_ERROR", 0, db.Environment);
        }
        catch (TimeoutException ex)
        {
            return QueryResult.Fail(project, DatabaseType.ToString(), $"元数据查询超时: {ex.Message}", "QUERY_TIMEOUT", 0, db.Environment);
        }
    }

    /// <summary>从 DataReader 读取数据为 columns + rows，按 maxRows 截断。</summary>
    internal static async Task<(List<string> columns, List<object?[]> rows, bool truncated)> ReadAsync(
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
