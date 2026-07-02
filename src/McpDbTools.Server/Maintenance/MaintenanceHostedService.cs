using McpDbTools.Server.Admin;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpDbTools.Server.Maintenance;

/// <summary>
/// 后台运维清理服务：按全局设置（maintenance 节点）定期删除过期审计日志与备份文件。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>仅在 Admin/混合模式注册（依赖 AdminConfigService）；MCP 纯 stdio 模式不运行，
/// 审计清理延后到下次 Admin/混合模式运行时执行。</item>
/// <item>周期 1 小时，启动后 30 秒首跑（避免与 ConfigStore/SQLite 初始化竞争）。</item>
/// <item>每次 tick 读取 ConfigStore.Current.Maintenance 最新值，天然跟随热重载。</item>
/// <item>重入保护：用 _running 标志防止上次清理未完成时重叠回调。</item>
/// <item>所有输出走 ILogger（→ stderr），禁止 Console.Write*，避免破坏 MCP stdio 协议。</item>
/// </list>
/// </para>
/// </summary>
public sealed class MaintenanceHostedService : IHostedService, IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly AuditLogger _audit;
    private readonly AdminConfigService _admin;
    private readonly ILogger<MaintenanceHostedService> _logger;
    private Timer? _timer;
    private int _running; // 0=空闲，1=清理中（Interlocked 保护，防重叠）
    private int _disposed;

    // 启动后延迟 30 秒首跑，避免与启动初始化竞争
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    // 周期 1 小时：本机工具，增长缓慢，足够及时
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    public MaintenanceHostedService(
        ConfigStore configStore,
        AuditLogger audit,
        AdminConfigService admin,
        ILogger<MaintenanceHostedService> logger)
    {
        _configStore = configStore;
        _audit = audit;
        _admin = admin;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(OnTick, null, InitialDelay, Period);
        _logger.LogInformation("运维清理服务已启动：首跑延迟 {Delay}，周期 {Period}", InitialDelay, Period);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("运维清理服务已停止");
        return Task.CompletedTask;
    }

    /// <summary>Timer 回调：重入保护 + 读最新配置 + 按开关执行清理。异常一律 catch，后台服务不能挂。</summary>
    private async void OnTick(object? state)
    {
        // 重入保护：上次清理未完成则跳过本次（Timer 回调可能在 ThreadPool 上重叠）
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            return;
        }

        try
        {
            await DoCleanupAsync();
        }
        catch (Exception ex)
        {
            // 后台服务异常不能终止进程，但必须上报
            _logger.LogError(ex, "运维清理执行异常");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>实际清理逻辑：按 maintenance 配置分别清理审计日志与备份。</summary>
    private async Task DoCleanupAsync()
    {
        MaintenanceConfig m = _configStore.Current.Maintenance ?? MaintenanceConfig.Default;

        if (m.AuditLogAutoCleanup)
        {
            if (m.AuditLogRetentionDays <= 0)
            {
                _logger.LogWarning("审计日志自动清理已启用但保留天数非法（{Days}），跳过", m.AuditLogRetentionDays);
            }
            else
            {
                try
                {
                    int deleted = _audit.DeleteOlderThan(m.AuditLogRetentionDays);
                    if (deleted > 0)
                    {
                        _logger.LogInformation("审计日志自动清理：删除 {Count} 条 {Days} 天前的记录", deleted, m.AuditLogRetentionDays);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "审计日志自动清理失败");
                }
            }
        }

        if (m.BackupAutoCleanup)
        {
            if (m.BackupRetentionDays <= 0)
            {
                _logger.LogWarning("备份自动清理已启用但保留天数非法（{Days}），跳过", m.BackupRetentionDays);
            }
            else
            {
                try
                {
                    int deleted = _admin.DeleteBackupsOlderThan(m.BackupRetentionDays);
                    if (deleted > 0)
                    {
                        _logger.LogInformation("备份自动清理：删除 {Count} 个 {Days} 天前的备份文件", deleted, m.BackupRetentionDays);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "备份自动清理失败");
                }
            }
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _timer?.Dispose();
        }
    }
}
