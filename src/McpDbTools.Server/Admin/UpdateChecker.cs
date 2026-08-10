using System.Text.Json;
using McpDbTools.Server.Configuration;
using Velopack;
using Velopack.Sources;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 应用更新检查器：封装 Velopack <see cref="UpdateManager"/> 的检查/下载/应用。
/// <para>
/// 更新源 URL 由 DI 注入（环境变量 <c>UpdateSource</c> 或 appsettings <c>UpdateSource</c> 配置）。
/// 未配置时 <see cref="IsConfigured"/>=false，UI 显示"未配置更新源"，不绑死 GitHub。
/// </para>
/// <para>
/// 自动检查策略：每天首次启动后由 <see cref="UpdateCheckHostedService"/> 在 5 分钟后触发一次
/// <see cref="AutoCheckOnceAsync"/>；当天（自然日）一旦成功检查过，写入数据目录
/// <c>update-check.state.json</c>，后续启动直接跳过。手动「检查更新」按钮走 <see cref="CheckAsync"/>，
/// 不受当天去重限制。
/// </para>
/// <para>
/// 重要约束：<see cref="UpdateManager"/> 仅在"已安装"的应用（Velopack <c>Setup.exe</c> 安装）中工作；
/// 开发调试（dotnet run / 直接跑 bin exe）时 <see cref="IsInstalled"/>=false，<see cref="CheckAsync"/>
/// 返回未安装状态而非抛异常。
/// </para>
/// </summary>
public sealed class UpdateChecker
{
    private readonly UpdateManager? _mgr;
    private UpdateInfo? _lastInfo;
    // 进程内缓存的上次检查结果：供 GET /update/status 只读返回，避免每次进页面都打网络。
    private UpdateStatus? _lastStatus;
    // 持久化「最近一次成功检查的自然日」文件名（位于 DataDirectoryResolver 解析的数据目录）。
    private const string StateFileName = "update-check.state.json";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public UpdateChecker(string? githubRepoUrl)
    {
        // 更新源为 GitHub Releases：repoUrl 为空时不创建 UpdateManager（UI 显示"未配置"）
        if (!string.IsNullOrWhiteSpace(githubRepoUrl))
        {
            _mgr = new UpdateManager(new GithubSource(githubRepoUrl, null, false));
        }
    }

    /// <summary>是否配置了更新源 URL。</summary>
    public bool IsConfigured => _mgr is not null;

    /// <summary>当前是否以"已安装"方式运行（Velopack 安装）。开发态为 false。</summary>
    public bool IsInstalled => _mgr?.IsInstalled ?? false;

    /// <summary>
    /// 只读返回进程内缓存的上次检查结果。未检查过返回 null。
    /// 供 GET /update/status 使用：不触发网络请求，自动检查由后台服务驱动。
    /// </summary>
    public UpdateStatus? GetCachedStatus() => _lastStatus;

    /// <summary>
    /// 自动检查一次（后台服务调用）：今天已成功检查过则跳过（返回 null），否则发起网络检查。
    /// 检查成功（无 Error）后写「今天」到持久化文件。
    /// </summary>
    /// <returns>检查结果；当天已检查过则返回 null（表示本次跳过、未发请求）。</returns>
    public async Task<UpdateStatus?> AutoCheckOnceAsync()
    {
        if (TodayAlreadyChecked())
        {
            return null;
        }
        return await CheckAsync();
    }

    /// <summary>
    /// 检查更新（始终发起网络请求）。手动「检查更新」按钮与后台自动检查共用。
    /// 成功（无 Error）后：更新进程内缓存 _lastStatus，并写「今天」到持久化文件；
    /// 出错则只更新缓存（展示错误），不写持久化，下次启动/自动检查仍会重试。
    /// </summary>
    public async Task<UpdateStatus> CheckAsync()
    {
        if (_mgr is null)
        {
            return new UpdateStatus { Configured = false };
        }
        if (!_mgr.IsInstalled)
        {
            var notInstalled = new UpdateStatus { Configured = true, Installed = false };
            _lastStatus = notInstalled;
            return notInstalled;
        }
        try
        {
            _lastInfo = await _mgr.CheckForUpdatesAsync();
            var status = new UpdateStatus
            {
                Configured = true,
                Installed = true,
                Checked = true,
                HasUpdate = _lastInfo is not null,
                TargetVersion = _lastInfo?.TargetFullRelease?.Version?.ToString()
            };
            _lastStatus = status;
            MarkTodayChecked();
            return status;
        }
        catch (Exception ex)
        {
            // 出错：缓存错误态供 status 展示，但不写持久化（下次启动/自动检查仍会重试）
            var failed = new UpdateStatus { Configured = true, Installed = true, Checked = true, Error = ex.Message };
            _lastStatus = failed;
            return failed;
        }
    }

    /// <summary>下载上次检查到的更新（若无检查记录，先自动检查）。</summary>
    public async Task<UpdateStatus> DownloadAsync()
    {
        if (_mgr is null || !_mgr.IsInstalled)
        {
            return await CheckAsync();
        }
        _lastInfo ??= await _mgr.CheckForUpdatesAsync();
        if (_lastInfo is null)
        {
            var noUpdate = new UpdateStatus { Configured = true, Installed = true, Checked = true, HasUpdate = false };
            _lastStatus = noUpdate;
            return noUpdate;
        }
        await _mgr.DownloadUpdatesAsync(_lastInfo);
        var downloaded = new UpdateStatus
        {
            Configured = true,
            Installed = true,
            Checked = true,
            HasUpdate = true,
            TargetVersion = _lastInfo.TargetFullRelease?.Version?.ToString(),
            Downloaded = true
        };
        _lastStatus = downloaded;
        return downloaded;
    }

    /// <summary>应用已下载的更新并重启（Velopack 接管退出+替换+重启，本进程随即结束）。</summary>
    public void ApplyAndRestart()
    {
        if (_mgr is null || _lastInfo is null)
        {
            return;
        }
        _mgr.ApplyUpdatesAndRestart(_lastInfo);
    }

    // ============ 当天去重持久化 ============

    /// <summary>读取持久化文件，判断今天是否已成功检查过。</summary>
    private static bool TodayAlreadyChecked()
    {
        try
        {
            string path = Path.Combine(DataDirectoryResolver.EnsureExists(), StateFileName);
            if (!File.Exists(path))
            {
                return false;
            }
            using var fs = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<UpdateCheckState>(fs);
            return state?.LastCheckDate == DateTime.Today.ToString("yyyy-MM-dd");
        }
        catch
        {
            // 读取/解析失败按"未检查"处理，触发一次检查（保守：宁可多查一次）
            return false;
        }
    }

    /// <summary>写「今天」到持久化文件。失败仅吞掉（不影响检查本身，最坏下次重复检查）。</summary>
    private static void MarkTodayChecked()
    {
        try
        {
            string path = Path.Combine(DataDirectoryResolver.EnsureExists(), StateFileName);
            var state = new UpdateCheckState { LastCheckDate = DateTime.Today.ToString("yyyy-MM-dd") };
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch
        {
            // 写入失败不影响检查本身
        }
    }

    /// <summary>持久化状态（最近一次成功检查的自然日）。</summary>
    private sealed class UpdateCheckState
    {
        public string? LastCheckDate { get; set; }
    }
}

/// <summary>更新检查结果（端点返回用）。</summary>
public sealed class UpdateStatus
{
    /// <summary>是否配置了更新源。</summary>
    public bool Configured { get; set; }
    /// <summary>是否以已安装方式运行。</summary>
    public bool Installed { get; set; }
    /// <summary>是否已进行过一次检查（有结果，可能是无更新/有更新/失败）。进程重启后为 false 直到首次检查。</summary>
    public bool Checked { get; set; }
    /// <summary>是否有新版本。</summary>
    public bool HasUpdate { get; set; }
    /// <summary>新版本号。</summary>
    public string? TargetVersion { get; set; }
    /// <summary>是否已下载待应用。</summary>
    public bool Downloaded { get; set; }
    /// <summary>检查出错时的错误信息。</summary>
    public string? Error { get; set; }
}
