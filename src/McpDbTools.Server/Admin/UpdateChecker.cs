using Velopack;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 应用更新检查器：封装 Velopack <see cref="UpdateManager"/> 的检查/下载/应用。
/// <para>
/// 更新源 URL 由 DI 注入（环境变量 <c>UpdateSource</c> 或 appsettings <c>UpdateSource</c> 配置）。
/// 未配置时 <see cref="IsConfigured"/>=false，UI 显示"未配置更新源"，不绑死 GitHub。
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

    public UpdateChecker(string? updateSourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(updateSourceUrl))
        {
            _mgr = new UpdateManager(updateSourceUrl);
        }
    }

    /// <summary>是否配置了更新源 URL。</summary>
    public bool IsConfigured => _mgr is not null;

    /// <summary>当前是否以"已安装"方式运行（Velopack 安装）。开发态为 false。</summary>
    public bool IsInstalled => _mgr?.IsInstalled ?? false;

    /// <summary>检查更新。未配置/未安装/无更新/出错各自返回相应状态，不抛异常。</summary>
    public async Task<UpdateStatus> CheckAsync()
    {
        if (_mgr is null)
        {
            return new UpdateStatus { Configured = false };
        }
        if (!_mgr.IsInstalled)
        {
            return new UpdateStatus { Configured = true, Installed = false };
        }
        try
        {
            _lastInfo = await _mgr.CheckForUpdatesAsync();
            return new UpdateStatus
            {
                Configured = true,
                Installed = true,
                HasUpdate = _lastInfo is not null,
                TargetVersion = _lastInfo?.TargetFullRelease?.Version?.ToString()
            };
        }
        catch (Exception ex)
        {
            return new UpdateStatus { Configured = true, Installed = true, Error = ex.Message };
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
            return new UpdateStatus { Configured = true, Installed = true, HasUpdate = false };
        }
        await _mgr.DownloadUpdatesAsync(_lastInfo);
        return new UpdateStatus
        {
            Configured = true,
            Installed = true,
            HasUpdate = true,
            TargetVersion = _lastInfo.TargetFullRelease?.Version?.ToString(),
            Downloaded = true
        };
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
}

/// <summary>更新检查结果（端点返回用）。</summary>
public sealed class UpdateStatus
{
    /// <summary>是否配置了更新源。</summary>
    public bool Configured { get; set; }
    /// <summary>是否以已安装方式运行。</summary>
    public bool Installed { get; set; }
    /// <summary>是否有新版本。</summary>
    public bool HasUpdate { get; set; }
    /// <summary>新版本号。</summary>
    public string? TargetVersion { get; set; }
    /// <summary>是否已下载待应用。</summary>
    public bool Downloaded { get; set; }
    /// <summary>检查出错时的错误信息。</summary>
    public string? Error { get; set; }
}
