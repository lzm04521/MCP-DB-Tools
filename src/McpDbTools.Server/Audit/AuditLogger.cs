using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Audit;

/// <summary>单条审计记录。</summary>
public sealed record AuditEntry
{
    /// <summary>记录自增 id（仅查询结果返回时填充，写入时忽略）。</summary>
    public long Id { get; init; }

    /// <summary>UTC ISO 8601 时间戳。</summary>
    public string Time { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string DatabaseType { get; init; } = string.Empty;
    public string Sql { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public long ElapsedMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 审计日志器：基于本地 SQLite 记录全部 db_query 调用。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>全局开启，不再依赖开关配置；db 文件位于 config.json 同目录，文件名 audit.db。</item>
/// <item>WAL 模式 + busy_timeout：MCP 写入与 Admin 页读取同进程并发安全。</item>
/// <item>写入异步化：Log 入无界 Channel，单后台消费者串行落盘，彻底消除写锁竞争并避免线程池饥饿。</item>
/// <item>不自动清理：记录保留至用户在 Admin UI「审计日志」页手动清理为止。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AuditLogger : IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<AuditLogger> _logger;
    private int _initialized;

    // 异步写入队列：无界 Channel + 单消费者，保证写入串行，无 SQLite 写锁竞争。
    private readonly Channel<AuditEntry> _channel = Channel.CreateUnbounded<AuditEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private int _disposed;

    // 已入队 / 已落盘计数：仅供 Flush 同步等待用（测试场景），不参与业务逻辑
    private long _enqueuedCount;
    private long _processedCount;

    public AuditLogger(IOptions<ConfigStoreOptions> options, ILogger<AuditLogger> logger)
    {
        string configPath = Path.GetFullPath(options.Value.ConfigPath);
        string dir = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "audit.db");
        // SQLite 通过连接字符串里的数据源定位文件；WAL/busy_timeout 在每次连接初始化时设置
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _logger = logger;

        // 启动单后台消费者：串行处理队列，串行写库
        _consumerTask = Task.Run(() => ConsumeAsync(_consumerCts.Token));
    }

    /// <summary>记录一条查询审计。同步签名不变；实际行为为入队，失败仅记 Error，不抛出。</summary>
    public void Log(AuditEntry entry)
    {
        try
        {
            // 防御：调用方未设置时间时按当前 UTC 补齐，避免空串被清理逻辑误判为过期
            if (string.IsNullOrWhiteSpace(entry.Time))
            {
                entry = entry with { Time = NowUtcIso() };
            }

            // 已 Dispose（如 Host 退出后）则降级同步写入，保证至少一次
            if (_disposed == 1)
            {
                WriteEntryCore(entry);
                Interlocked.Increment(ref _enqueuedCount);
                Interlocked.Increment(ref _processedCount);
                return;
            }

            // 先递增入队计数再写入：保证 Flush 等待的 target >= 实际写入数
            Interlocked.Increment(ref _enqueuedCount);
            if (!_channel.Writer.TryWrite(entry))
            {
                // 入队失败（如已 Complete）：回退计数并降级同步写
                Interlocked.Decrement(ref _enqueuedCount);
                WriteEntryCore(entry);
                Interlocked.Increment(ref _enqueuedCount);
                Interlocked.Increment(ref _processedCount);
            }
        }
        catch (Exception ex)
        {
            // 审计写入失败不应影响主流程，但必须上报
            _logger.LogError(ex, "审计日志入队失败");
        }
    }

    /// <summary>后台消费者：从 Channel 读取并串行落盘。</summary>
    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (AuditEntry entry in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    WriteEntryCore(entry);
                }
                catch (Exception ex)
                {
                    // 单条写入失败不影响消费者继续运行
                    _logger.LogError(ex, "审计日志写入失败");
                }
                finally
                {
                    Interlocked.Increment(ref _processedCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "审计日志消费者异常退出");
        }
    }

    /// <summary>实际的同步写入逻辑（参数化 INSERT）。仅在消费者或降级路径调用，串行执行。</summary>
    private void WriteEntryCore(AuditEntry entry)
    {
        try
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_log
                    (time, project, environment, database_type, sql, row_count, elapsed_ms, success, error)
                VALUES
                    (@time, @project, @environment, @databaseType, @sql, @rowCount, @elapsedMs, @success, @error)
                """;
            command.Parameters.AddWithValue("@time", entry.Time);
            command.Parameters.AddWithValue("@project", entry.Project);
            command.Parameters.AddWithValue("@environment", entry.Environment);
            command.Parameters.AddWithValue("@databaseType", entry.DatabaseType);
            command.Parameters.AddWithValue("@sql", entry.Sql);
            command.Parameters.AddWithValue("@rowCount", entry.RowCount);
            command.Parameters.AddWithValue("@elapsedMs", entry.ElapsedMs);
            command.Parameters.AddWithValue("@success", entry.Success ? 1 : 0);
            command.Parameters.AddWithValue("@error", (object?)entry.Error ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // 审计写入失败不应影响主流程，但必须上报
            _logger.LogError(ex, "审计日志写入失败");
        }
    }

    /// <summary>按条件分页查询审计日志（按时间倒序）。供 Admin 查看页使用。</summary>
    public AuditLogPage Query(AuditLogQuery query)
    {
        EnsureInitialized();
        query = NormalizeQuery(query);

        using var connection = OpenConnection();

        var (whereSql, parameters, total) = BuildWhere(query);

        List<AuditEntry> items = new(query.PageSize);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT id, time, project, environment, database_type, sql, row_count, elapsed_ms, success, error
                FROM audit_log
                {whereSql}
                ORDER BY time DESC, id DESC
                LIMIT @limit OFFSET @offset
                """;
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }
            cmd.Parameters.AddWithValue("@limit", query.PageSize);
            cmd.Parameters.AddWithValue("@offset", (query.Page - 1) * query.PageSize);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadEntry(reader));
            }
        }

        return new AuditLogPage
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>构造 WHERE 子句、参数列表，并返回总记录数。</summary>
    private (string Sql, IReadOnlyList<SqliteParameter> Parameters, long Total) BuildWhere(AuditLogQuery query)
    {
        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            conditions.Add("project = @project");
            parameters.Add(new SqliteParameter("@project", query.Project!.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.Environment))
        {
            conditions.Add("environment = @environment");
            parameters.Add(new SqliteParameter("@environment", query.Environment!.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.DatabaseType))
        {
            conditions.Add("database_type = @databaseType");
            parameters.Add(new SqliteParameter("@databaseType", query.DatabaseType!.Trim()));
        }
        if (query.Success.HasValue)
        {
            conditions.Add("success = @success");
            parameters.Add(new SqliteParameter("@success", query.Success.Value ? 1 : 0));
        }
        if (!string.IsNullOrWhiteSpace(query.FromTime))
        {
            conditions.Add("time >= @fromTime");
            parameters.Add(new SqliteParameter("@fromTime", query.FromTime!.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.ToTime))
        {
            conditions.Add("time <= @toTime");
            parameters.Add(new SqliteParameter("@toTime", query.ToTime!.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.SqlContains))
        {
            conditions.Add("sql LIKE @sqlContains ESCAPE '\\' COLLATE NOCASE");
            parameters.Add(new SqliteParameter("@sqlContains", "%" + EscapeLike(query.SqlContains!.Trim()) + "%"));
        }

        string sql = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : string.Empty;

        // 计算 total：用独立连接读取，避免与上面 reader 冲突
        long total;
        using (var countConn = OpenConnection())
        using (var countCmd = countConn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM audit_log {sql}";
            foreach (var p in parameters)
            {
                // SqliteParameter 不能复用到两个 command，新建同名参数
                countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            total = (long)countCmd.ExecuteScalar()!;
        }

        return (sql, parameters, total);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static AuditEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Time = reader.GetString(1),
        Project = reader.GetString(2),
        Environment = reader.GetString(3),
        DatabaseType = reader.GetString(4),
        Sql = reader.GetString(5),
        RowCount = reader.GetInt32(6),
        ElapsedMs = reader.GetInt64(7),
        Success = reader.GetInt32(8) == 1,
        Error = reader.IsDBNull(9) ? null : reader.GetString(9)
    };

    private static AuditLogQuery NormalizeQuery(AuditLogQuery query)
    {
        int page = query.Page <= 0 ? 1 : query.Page;
        // 允许的最大每页条数：与前端可选档位（50/100/500/1000/5000）一致
        int pageSize = query.PageSize is <= 0 or > 5000 ? 50 : query.PageSize;
        if (page == query.Page && pageSize == query.PageSize)
        {
            return query;
        }
        return query with { Page = page, PageSize = pageSize };
    }

    /// <summary>
    /// 手动删除早于指定天数的审计记录。供 Admin 页「清理」功能使用。
    /// <para>days 必须 &gt; 0。返回删除条数。失败抛出（由调用方包装为错误响应）。</para>
    /// </summary>
    public int DeleteOlderThan(int days)
    {
        if (days <= 0)
        {
            throw new ArgumentException("清理天数必须大于 0。", nameof(days));
        }
        EnsureInitialized();
        string cutoff = DateTime.UtcNow.AddDays(-days)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM audit_log WHERE time < @cutoff";
        command.Parameters.AddWithValue("@cutoff", cutoff);
        int deleted = command.ExecuteNonQuery();
        if (deleted > 0)
        {
            _logger.LogInformation("审计日志手动清理：删除 {Count} 条 {Days} 天前的记录", deleted, days);
        }
        return deleted;
    }

    /// <summary>首次使用时建表与开启 WAL（线程安全，仅执行一次）。</summary>
    private void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            try
            {
                using var connection = OpenConnection();
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();

                using var table = connection.CreateCommand();
                table.CommandText = """
                    CREATE TABLE IF NOT EXISTS audit_log (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        time TEXT NOT NULL,
                        project TEXT NOT NULL,
                        environment TEXT NOT NULL,
                        database_type TEXT NOT NULL,
                        sql TEXT NOT NULL,
                        row_count INTEGER NOT NULL DEFAULT 0,
                        elapsed_ms INTEGER NOT NULL DEFAULT 0,
                        success INTEGER NOT NULL DEFAULT 0,
                        error TEXT
                    )
                    """;
                table.ExecuteNonQuery();

                using var indexTime = connection.CreateCommand();
                indexTime.CommandText = "CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_log(time)";
                indexTime.ExecuteNonQuery();

                using var indexProject = connection.CreateCommand();
                indexProject.CommandText = "CREATE INDEX IF NOT EXISTS idx_audit_project ON audit_log(project)";
                indexProject.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // 初始化失败时重置标志，允许下次重试；但不吞掉写入路径的异常（Log 已捕获）
                _initialized = 0;
                _logger.LogError(ex, "审计日志初始化失败");
                throw;
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // 同进程多连接场景下设置忙等，避免短暂的写锁冲突
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=3000;";
        busy.ExecuteNonQuery();
        return connection;
    }

    /// <summary>构造 UTC ISO 8601 时间戳（避免在热路径反复构造格式化字符串）。</summary>
    public static string NowUtcIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// 关闭写入队列并等待消费者排空，保证 Dispose 后审计已落盘。带 5 秒软超时。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
        {
            return;
        }
        _channel.Writer.TryComplete();
        try
        {
            await Task.WhenAny(_consumerTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        catch
        {
            // 排空等待异常忽略，避免 Dispose 抛出
        }
        _consumerCts.Cancel();
        _consumerCts.Dispose();
    }

    /// <summary>同步版排空（供同步测试与 Host 同步释放路径）。</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
        {
            return;
        }
        _channel.Writer.TryComplete();
        try
        {
            _consumerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 排空等待异常忽略
        }
        _consumerCts.Cancel();
        _consumerCts.Dispose();
    }

    /// <summary>
    /// 等待当前已入队记录全部落盘，但不关闭消费者。
    /// 供测试在 Log 后、Query 前同步排空用；生产代码不需要调用。
    /// </summary>
    public void Flush()
    {
        if (_disposed == 1)
        {
            return;
        }
        long target = Interlocked.Read(ref _enqueuedCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Interlocked.Read(ref _processedCount) < target && sw.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
        }
    }
}
