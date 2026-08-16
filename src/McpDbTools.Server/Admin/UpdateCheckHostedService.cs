using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 应用更新自动检查后台服务：启动后 1 分钟首查，此后按结果动态重排——
/// 检查成功（含无更新）1 小时后再查，失败 15 分钟后重试，节流跳过
/// （距上次成功检查不足 30 分钟）30 分钟后再试；跨重启节流由 <see cref="UpdateChecker"/> 持久化判断。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>单跳 + 重排模式（period=Infinite，tick 完成后按结果 Change 下一跳），天然无重叠回调。</item>
/// <item>常驻进程持续检查：不再依赖"每次启动触发一次"（旧策略在长驻进程上启动后永不复查）。</item>
/// <item>频率安全：GitHub 未认证限额 60 req/h，每次检查 1 次 API 请求，1 小时周期仅占 1/60。</item>
/// <item>所有输出走 <see cref="ILogger"/>（→ 文件日志），禁止 Console.Write*。</item>
/// <item>异常一律 catch：后台服务不能挂；异常路径同样重排，避免调度终止。</item>
/// </list>
/// </para>
/// </summary>
public sealed class UpdateCheckHostedService : IHostedService, IDisposable
{
    private readonly UpdateChecker _updateChecker;
    private readonly ILogger<UpdateCheckHostedService> _logger;
    private Timer? _timer;
    private int _disposed;

    // 启动后延迟 1 分钟首查，避免与启动初始化、网络就绪竞争（失败有短间隔重试兜底）
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    // 常规周期：每小时检查一次
    private static readonly TimeSpan RegularInterval = TimeSpan.FromHours(1);
    // 检查失败后的重试间隔（网络抖动/GitHub 限流时快速恢复）
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(15);
    // 节流跳过后的下次尝试间隔（跨 UpdateChecker 的 30 分钟节流窗后恢复常规周期）
    private static readonly TimeSpan ThrottleRetryInterval = TimeSpan.FromMinutes(30);

    public UpdateCheckHostedService(UpdateChecker updateChecker, ILogger<UpdateCheckHostedService> logger)
    {
        _updateChecker = updateChecker;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 单跳 + 重排：dueTime=首查延迟，period=Infinite，下一跳由 OnTick 按结果决定
        _timer = new Timer(OnTick, null, InitialDelay, Timeout.InfiniteTimeSpan);
        _logger.LogInformation("应用更新自动检查服务已启动：{Delay} 后首查，常规周期 {Interval}", InitialDelay, RegularInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("应用更新自动检查服务已停止");
        return Task.CompletedTask;
    }

    /// <summary>Timer 回调：执行一次自动检查并按结果安排下一跳。异常一律 catch，后台服务不能挂。</summary>
    private async void OnTick(object? state)
    {
        try
        {
            UpdateStatus? status = await _updateChecker.AutoCheckOnceAsync();
            if (status is null)
            {
                _logger.LogInformation("自动检查更新：距上次成功检查不足节流窗，本次跳过");
            }
            else if (status.Error is not null)
            {
                _logger.LogWarning("自动检查更新失败：{Error}", status.Error);
            }
            else if (status.HasUpdate)
            {
                _logger.LogInformation("自动检查更新：发现新版本 {Version}", status.TargetVersion);
            }
            else
            {
                _logger.LogInformation("自动检查更新：已是最新版本");
            }

            Reschedule(NextDelay(status));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动检查更新执行异常");
            // 异常路径同样重排（按失败间隔），否则调度终止
            Reschedule(FailureRetryInterval);
        }
    }

    /// <summary>按检查结果计算下一跳间隔：节流跳过→30 分钟；失败→15 分钟重试；其余（成功/未配置/未安装）→常规周期。纯函数便于单测。</summary>
    internal static TimeSpan NextDelay(UpdateStatus? status)
        => status is null ? ThrottleRetryInterval
         : status.Error is not null ? FailureRetryInterval
         : RegularInterval;

    /// <summary>安排下一跳。与 StopAsync/Dispose 竞态时 timer 已释放，吞掉即可（服务正在停止）。</summary>
    private void Reschedule(TimeSpan delay)
    {
        try
        {
            _timer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _timer?.Dispose();
        }
    }
}
