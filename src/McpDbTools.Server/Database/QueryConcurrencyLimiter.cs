using System.Collections.Concurrent;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Database;

/// <summary>
/// 当并发查询数超过环境上限，且排队等待超时仍未获得执行槽位时抛出。
/// 由 DbQueryTool 捕获并转换为 RATE_LIMITED 错误码返回。
/// </summary>
public sealed class QueryRateLimitedException : Exception
{
    public string Project { get; }
    public string Environment { get; }
    public int WaitSeconds { get; }
    public int MaxConcurrency { get; }

    public QueryRateLimitedException(string project, string environment, int waitSeconds, int maxConcurrency)
        : base($"并发查询数已达上限 {maxConcurrency}，等待 {waitSeconds} 秒未获得执行槽位（project={project}, environment={environment}）")
    {
        Project = project;
        Environment = environment;
        WaitSeconds = waitSeconds;
        MaxConcurrency = maxConcurrency;
    }
}

/// <summary>
/// 每环境一个并发闸门（SemaphoreSlim），限制同一 (project, env) 的并发查询数。
/// <para>超载时排队等待，超过 MaxConcurrencyWaitSeconds 抛 QueryRateLimitedException。</para>
/// <para>不同环境互不阻塞，慢库不拖累其他库。</para>
/// <para>配置热重载：每次 AcquireAsync 携带最新 ResolvedDatabase；MaxConcurrency 变更时按需重建对应环境的信号量。</para>
/// </summary>
public interface IQueryConcurrencyLimiter
{
    /// <summary>
    /// 申请一个查询执行槽位。返回的令牌释放后归还槽位（用 await using）。
    /// 排队超时抛 QueryRateLimitedException。
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string project, string env, ResolvedDatabase db, CancellationToken ct);
}

/// <summary>每环境独立信号量的限流实现。</summary>
public sealed class QueryConcurrencyLimiter : IQueryConcurrencyLimiter
{
    // key = project + "|" + env（忽略大小写）；value = (maxConcurrency, semaphore)
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rebuildLock = new();

    public Task<IAsyncDisposable> AcquireAsync(string project, string env, ResolvedDatabase db, CancellationToken ct)
    {
        string key = Key(project, env);
        SemaphoreSlim sem = GetOrCreateSemaphore(key, db.MaxConcurrency);

        return AcquireCoreAsync(key, sem, db, ct);
    }

    private async Task<IAsyncDisposable> AcquireCoreAsync(string key, SemaphoreSlim sem, ResolvedDatabase db, CancellationToken ct)
    {
        bool acquired = await sem.WaitAsync(TimeSpan.FromSeconds(db.MaxConcurrencyWaitSeconds), ct).ConfigureAwait(false);
        if (!acquired)
        {
            throw new QueryRateLimitedException(db.ProjectName, db.Environment, db.MaxConcurrencyWaitSeconds, db.MaxConcurrency);
        }
        return new Slot(sem);
    }

    private SemaphoreSlim GetOrCreateSemaphore(string key, int maxConcurrency)
    {
        // 快速路径：已存在且上限未变
        if (_entries.TryGetValue(key, out Entry? existing) && existing.MaxConcurrency == maxConcurrency)
        {
            return existing.Semaphore;
        }

        // 慢路径：新建或上限变更 → 重建
        // 用锁避免同一 key 并发创建多个信号量；跨 key 无锁竞争
        lock (_rebuildLock)
        {
            if (_entries.TryGetValue(key, out Entry? current) && current.MaxConcurrency == maxConcurrency)
            {
                return current.Semaphore;
            }
            var fresh = new Entry(maxConcurrency, new SemaphoreSlim(maxConcurrency, maxConcurrency));
            _entries[key] = fresh;
            return fresh.Semaphore;
        }
    }

    private static string Key(string project, string env) => project + "|" + env;

    /// <summary>槽位令牌：Dispose 时释放信号量。</summary>
    private sealed class Slot(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            ReleaseOnce();
            return ValueTask.CompletedTask;
        }

        private void ReleaseOnce()
        {
            if (Interlocked.CompareExchange(ref _released, 1, 0) == 0)
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>记录某环境信号量及其配置上限，用于热重载时检测变更。</summary>
    private sealed record Entry(int MaxConcurrency, SemaphoreSlim Semaphore);
}
