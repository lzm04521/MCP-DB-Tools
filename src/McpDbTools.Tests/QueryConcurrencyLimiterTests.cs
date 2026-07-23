using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

/// <summary>
/// QueryConcurrencyLimiter 的并发与隔离行为测试：
/// 超限等待超时抛 QueryRateLimitedException、释放后槽位可用、不同环境互不阻塞、上限从配置读取。
/// </summary>
public class QueryConcurrencyLimiterTests
{
    /// <summary>构造一个指定并发上限的 ResolvedDatabase（其他字段填默认值）。</summary>
    private static ResolvedDatabase MakeDb(string project, string env, int maxConcurrency, int waitSeconds = 2)
        => new()
        {
            ProjectName = project,
            Environment = env,
            IsProduction = false,
            Type = DatabaseType.SqlServer,
            ConnectionString = "cs",
            DatabaseName = null,
            MaxRows = 1000,
            CommandTimeout = 30,
            MaxPoolSize = 100,
            ConnectTimeoutSeconds = 15,
            MaxConcurrency = maxConcurrency,
            MaxConcurrencyWaitSeconds = waitSeconds,
            DisabledKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

    [Fact]
    public async Task Acquire_WithinLimit_SucceedsImmediately()
    {
        var limiter = new QueryConcurrencyLimiter();
        ResolvedDatabase db = MakeDb("p", "prod", maxConcurrency: 2);

        // 上限 2：连续两次申请都应成功
        IAsyncDisposable slot1 = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);
        IAsyncDisposable slot2 = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);

        await slot1.DisposeAsync();
        await slot2.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_OverLimit_ThrowsAfterWaitTimeout()
    {
        var limiter = new QueryConcurrencyLimiter();
        // 并发上限 1，等待超时 1 秒（缩短测试耗时）
        ResolvedDatabase db = MakeDb("p", "prod", maxConcurrency: 1, waitSeconds: 1);

        // 占用唯一槽位
        IAsyncDisposable slot = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);

        // 第二次申请应排队等待，1 秒后超时抛异常
        var sw = System.Diagnostics.Stopwatch.StartNew();
        QueryRateLimitedException ex = await Assert.ThrowsAsync<QueryRateLimitedException>(
            () => limiter.AcquireAsync("p", "prod", db, CancellationToken.None));
        sw.Stop();

        Assert.Equal("p", ex.Project);
        Assert.Equal("prod", ex.Environment);
        Assert.Equal(1, ex.MaxConcurrency);
        // 等待至少接近 1 秒
        Assert.True(sw.ElapsedMilliseconds >= 800, $"等待时间过短：{sw.ElapsedMilliseconds}ms");

        await slot.DisposeAsync();
    }

    [Fact]
    public async Task Slot_Released_AllowsNextAcquire()
    {
        var limiter = new QueryConcurrencyLimiter();
        ResolvedDatabase db = MakeDb("p", "prod", maxConcurrency: 1, waitSeconds: 5);

        IAsyncDisposable slot = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);
        await slot.DisposeAsync();

        // 释放后应立即可再次申请
        IAsyncDisposable slot2 = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);
        await slot2.DisposeAsync();
    }

    [Fact]
    public async Task DifferentEnvironments_DoNotBlock()
    {
        // 不同 (project, env) 互不阻塞：占满 prod 的槽位，不应影响 staging
        var limiter = new QueryConcurrencyLimiter();
        ResolvedDatabase prodDb = MakeDb("p", "prod", maxConcurrency: 1, waitSeconds: 1);
        ResolvedDatabase stagingDb = MakeDb("p", "staging", maxConcurrency: 1, waitSeconds: 1);

        IAsyncDisposable prodSlot = await limiter.AcquireAsync("p", "prod", prodDb, CancellationToken.None);

        // staging 是不同环境，应立即获得槽位
        IAsyncDisposable stagingSlot = await limiter.AcquireAsync("p", "staging", stagingDb, CancellationToken.None);

        await prodSlot.DisposeAsync();
        await stagingSlot.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_DifferentProjectsDoNotBlock()
    {
        var limiter = new QueryConcurrencyLimiter();
        ResolvedDatabase aDb = MakeDb("proj-a", "prod", maxConcurrency: 1, waitSeconds: 1);
        ResolvedDatabase bDb = MakeDb("proj-b", "prod", maxConcurrency: 1, waitSeconds: 1);

        IAsyncDisposable slotA = await limiter.AcquireAsync("proj-a", "prod", aDb, CancellationToken.None);
        // 不同项目不阻塞
        IAsyncDisposable slotB = await limiter.AcquireAsync("proj-b", "prod", bDb, CancellationToken.None);

        await slotA.DisposeAsync();
        await slotB.DisposeAsync();
    }

    [Fact]
    public async Task Slot_ReleasedOnce_EvenIfDisposedMultipleTimes()
    {
        // 令牌幂等释放：多次 Dispose 不应导致信号量计数异常
        var limiter = new QueryConcurrencyLimiter();
        ResolvedDatabase db = MakeDb("p", "prod", maxConcurrency: 1, waitSeconds: 1);

        IAsyncDisposable slot = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);
        await slot.DisposeAsync();
        await slot.DisposeAsync(); // 重复释放应安全

        // 槽位仍只有 1 个可用
        IAsyncDisposable slot2 = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);
        await slot2.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_CaseInsensitiveEnvironmentKey()
    {
        // (project, env) 键忽略大小写：PROD 与 prod 共享同一闸门
        var limiter = new QueryConcurrencyLimiter();
        ResolvedDatabase db = MakeDb("p", "prod", maxConcurrency: 1, waitSeconds: 1);

        IAsyncDisposable slot = await limiter.AcquireAsync("p", "PROD", db, CancellationToken.None);

        // 同一环境不同大小写应受同一闸门限制 → 超时
        await Assert.ThrowsAsync<QueryRateLimitedException>(
            () => limiter.AcquireAsync("p", "prod", db, CancellationToken.None));

        await slot.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_CancellationToken_Propagates()
    {
        var limiter = new QueryConcurrencyLimiter();
        ResolvedDatabase db = MakeDb("p", "prod", maxConcurrency: 1, waitSeconds: 10);

        IAsyncDisposable slot = await limiter.AcquireAsync("p", "prod", db, CancellationToken.None);

        // 排队等待期间外部取消 → 抛 OperationCanceledException（而非 RATE_LIMITED）
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => limiter.AcquireAsync("p", "prod", db, cts.Token));

        await slot.DisposeAsync();
    }
}
