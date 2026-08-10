using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 应用更新自动检查后台服务：启动后延迟 5 分钟触发一次 <see cref="UpdateChecker.AutoCheckOnceAsync"/>，
/// 非周期。当天（自然日）是否已检查过由 <see cref="UpdateChecker"/> 持久化去重。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>延迟 5 分钟首跑，避免与启动初始化、网络就绪竞争。</item>
/// <item>非周期（period=Infinite）：每天最多由"首次启动"触发一次，当天再次启动靠持久化记录跳过。</item>
/// <item>所有输出走 <see cref="ILogger"/>（→ 文件日志），禁止 Console.Write*。</item>
/// <item>异常一律 catch：后台服务不能挂。</item>
/// </list>
/// </para>
/// </summary>
public sealed class UpdateCheckHostedService : IHostedService, IDisposable
{
    private readonly UpdateChecker _updateChecker;
    private readonly ILogger<UpdateCheckHostedService> _logger;
    private Timer? _timer;
    private int _disposed;

    // 启动后延迟 5 分钟触发一次，避免与启动初始化、网络就绪竞争
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    public UpdateCheckHostedService(UpdateChecker updateChecker, ILogger<UpdateCheckHostedService> logger)
    {
        _updateChecker = updateChecker;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 非周期：dueTime=5 分钟，period=Infinite（只触发一次）
        _timer = new Timer(OnTick, null, InitialDelay, Timeout.InfiniteTimeSpan);
        _logger.LogInformation("应用更新自动检查服务已启动：启动后 {Delay} 检查一次", InitialDelay);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("应用更新自动检查服务已停止");
        return Task.CompletedTask;
    }

    /// <summary>Timer 回调：执行一次自动检查。异常一律 catch，后台服务不能挂。</summary>
    private async void OnTick(object? state)
    {
        try
        {
            UpdateStatus? status = await _updateChecker.AutoCheckOnceAsync();
            if (status is null)
            {
                _logger.LogInformation("自动检查更新：今天已检查过，本次启动跳过");
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动检查更新执行异常");
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
