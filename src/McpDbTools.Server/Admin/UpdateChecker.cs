using System.Globalization;
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
/// 自动检查策略：由 <see cref="UpdateCheckHostedService"/> 周期驱动（启动 1 分钟首查，常规每小时一次，
/// 失败 15 分钟后重试）。自动检查带节流窗：距上次成功检查不足 <see cref="MinAutoInterval"/> 时跳过
/// （防反复重启进程叠加请求），上次成功检查时间持久化到数据目录 <c>update-check.state.json</c>。
/// 手动「检查更新」按钮走 <see cref="CheckAsync"/>，不受节流限制；手动检查成功同样刷新时间戳。
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
    // 持久化「最近一次成功检查时间（UTC）」文件名（位于 DataDirectoryResolver 解析的数据目录）。
    private const string StateFileName = "update-check.state.json";
    // 自动检查节流窗：距上次成功检查不足该间隔时自动检查跳过（防反复重启进程叠加请求）
    private static readonly TimeSpan MinAutoInterval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // 下载进度（供 GET /update/status 轮询）：回调在 Velopack 下载线程写入，
    // int 写原子 + volatile 标志即可；读端仅做 UI 展示，允许微小竞态，不加锁。
    private volatile bool _downloadInProgress;
    private int _downloadPercent;

    // "owner/repo" 相对路径（供拉取 GitHub release notes）；非 github.com URL 或未配置时为 null
    private readonly string? _repoPath;

    // 拉 release notes 专用 HttpClient：GitHub API 要求 User-Agent；15s 超时防悬挂拖慢检查
    private static readonly HttpClient ReleaseHttp = CreateReleaseHttp();

    private static HttpClient CreateReleaseHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("McpDbTools-UpdateCheck");
        return client;
    }

    public UpdateChecker(string? githubRepoUrl)
    {
        // 更新源为 GitHub Releases：repoUrl 为空时不创建 UpdateManager（UI 显示"未配置"）
        if (!string.IsNullOrWhiteSpace(githubRepoUrl))
        {
            _mgr = new UpdateManager(new GithubSource(githubRepoUrl, null, false));
            _repoPath = ParseRepoPath(githubRepoUrl);
        }
    }

    /// <summary>是否配置了更新源 URL。</summary>
    public bool IsConfigured => _mgr is not null;

    /// <summary>当前是否以"已安装"方式运行（Velopack 安装）。开发态为 false。</summary>
    public bool IsInstalled => _mgr?.IsInstalled ?? false;

    /// <summary>是否正在下载更新（<see cref="DownloadAsync"/> 执行期间为 true）。</summary>
    public bool IsDownloadInProgress => _downloadInProgress;

    /// <summary>最近一次进度回调的下载百分比（0-100）。未开始下载时为 0。</summary>
    public int DownloadPercent => _downloadPercent;

    /// <summary>
    /// 只读返回进程内缓存的上次检查结果。未检查过返回 null。
    /// 供 GET /update/status 使用：不触发网络请求，自动检查由后台服务驱动。
    /// </summary>
    public UpdateStatus? GetCachedStatus() => _lastStatus;

    /// <summary>
    /// 自动检查一次（后台服务调用）：距上次成功检查不足节流窗（<see cref="MinAutoInterval"/>）则跳过
    /// （返回 null），否则发起网络检查。检查成功（无 Error）后写当前 UTC 时间到持久化文件。
    /// </summary>
    /// <returns>检查结果；节流跳过时返回 null（表示本次跳过、未发请求）。</returns>
    public async Task<UpdateStatus?> AutoCheckOnceAsync()
    {
        if (ShouldSkipAutoCheck(DateTime.UtcNow, GetLastCheckedAtUtc(), MinAutoInterval))
        {
            return null;
        }
        return await CheckAsync();
    }

    /// <summary>节流判断：距上次成功检查不足 minInterval 时跳过（未查过不跳过；时钟回拨视为窗内跳过）。纯函数便于单测。</summary>
    internal static bool ShouldSkipAutoCheck(DateTime utcNow, DateTime? lastCheckUtc, TimeSpan minInterval)
        => lastCheckUtc is { } last && utcNow - last < minInterval;

    /// <summary>
    /// 从 GitHub 仓库 URL 解析 <c>owner/repo</c> 相对路径（供拼接 releases API）。
    /// 仅接受 github.com 绝对 URL；容忍尾斜杠、<c>.git</c> 后缀与多余路径段（如 <c>/tree/main</c>，取前两段）。
    /// 其余情况（null/空/非绝对/非 github 域/不足两段）返回 null。纯函数便于单测。
    /// </summary>
    internal static string? ParseRepoPath(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            return null;
        }
        if (!Uri.TryCreate(repoUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }
        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        return owner.Length == 0 || repo.Length == 0 ? null : $"{owner}/{repo}";
    }

    /// <summary>
    /// 检查更新（始终发起网络请求）。手动「检查更新」按钮与后台自动检查共用。
    /// 成功（无 Error）后：更新进程内缓存 _lastStatus，并写当前 UTC 时间到持久化文件；
    /// 出错则只更新缓存（展示错误），不写持久化，下次自动检查仍会重试。
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
            // best-effort 拉取 release notes / 发布页（已是最新也展示说明）：失败置 null，不阻塞检查
            if (_repoPath is { } repo)
            {
                (status.Notes, status.ReleaseUrl) = await FetchReleaseMetaAsync(repo);
            }
            _lastStatus = status;
            MarkCheckedAtUtc();
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

    /// <summary>下载上次检查到的更新（若无检查记录，先自动检查）。下载期间进度经 <see cref="DownloadPercent"/> 暴露，供 /update/status 轮询。</summary>
    public async Task<UpdateStatus> DownloadAsync()
    {
        if (_mgr is null || !_mgr.IsInstalled)
        {
            return await CheckAsync();
        }
        try
        {
            _lastInfo ??= await _mgr.CheckForUpdatesAsync();
            if (_lastInfo is null)
            {
                var noUpdate = new UpdateStatus { Configured = true, Installed = true, Checked = true, HasUpdate = false };
                _lastStatus = noUpdate;
                return noUpdate;
            }
            _downloadInProgress = true;
            _downloadPercent = 0;
            await _mgr.DownloadUpdatesAsync(_lastInfo, p => _downloadPercent = Math.Clamp(p, 0, 100));
            var downloaded = new UpdateStatus
            {
                Configured = true,
                Installed = true,
                Checked = true,
                HasUpdate = true,
                TargetVersion = _lastInfo.TargetFullRelease?.Version?.ToString(),
                Downloaded = true,
                // 检查时拉取的更新说明/发布页随下载结果带上（前端 merge 亦会保留，后端兜底）
                Notes = _lastStatus?.Notes,
                ReleaseUrl = _lastStatus?.ReleaseUrl
            };
            _lastStatus = downloaded;
            return downloaded;
        }
        catch (Exception ex)
        {
            // 下载失败：返回错误态（与 CheckAsync 模式一致）而非抛出→500，UI 展示 error 并允许重试
            var failed = new UpdateStatus { Configured = true, Installed = true, Checked = true, Error = ex.Message };
            _lastStatus = failed;
            return failed;
        }
        finally
        {
            _downloadInProgress = false;
        }
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

    // ============ 自动检查节流持久化 ============

    /// <summary>
    /// 拉取 GitHub <c>releases/latest</c> 的更新说明（body）与发布页地址（html_url）。
    /// 任何失败（网络/非 2xx/解析）静默返回 (null, null)：notes 属展示增强，不阻塞检查、不改变异常语义。
    /// </summary>
    private static async Task<(string? Notes, string? ReleaseUrl)> FetchReleaseMetaAsync(string repoPath)
    {
        try
        {
            using var resp = await ReleaseHttp.GetAsync($"https://api.github.com/repos/{repoPath}/releases/latest");
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                ? body.GetString()
                : null;
            var url = root.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.ValueKind == JsonValueKind.String
                ? htmlUrl.GetString()
                : null;
            return (notes, url);
        }
        catch
        {
            // notes 拉取失败不影响检查主流程
            return (null, null);
        }
    }

    /// <summary>读取持久化的上次成功检查时间（UTC）。读取/解析失败返回 null（按"未检查"处理，宁可多查一次）。</summary>
    private static DateTime? GetLastCheckedAtUtc()
    {
        try
        {
            string path = Path.Combine(DataDirectoryResolver.EnsureExists(), StateFileName);
            if (!File.Exists(path))
            {
                return null;
            }
            using var fs = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<UpdateCheckState>(fs);
            // 仅接受带 Z 后缀（UTC）的 round-trip 格式，其余（含旧版 LastCheckDate 语义文件）按未检查处理
            return DateTime.TryParse(state?.LastCheckUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var utc) && utc.Kind == DateTimeKind.Utc
                ? utc
                : null;
        }
        catch
        {
            // 读取/解析失败按"未检查"处理，触发一次检查（保守：宁可多查一次）
            return null;
        }
    }

    /// <summary>写当前 UTC 时间到持久化文件。失败仅吞掉（不影响检查本身，最坏下次重复检查）。</summary>
    private static void MarkCheckedAtUtc()
    {
        try
        {
            string path = Path.Combine(DataDirectoryResolver.EnsureExists(), StateFileName);
            var state = new UpdateCheckState { LastCheckUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch
        {
            // 写入失败不影响检查本身
        }
    }

    /// <summary>持久化状态（最近一次成功检查的 UTC 时间）。旧版本的 LastCheckDate 字段读不到，按未检查处理。</summary>
    private sealed class UpdateCheckState
    {
        public string? LastCheckUtc { get; set; }
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
    /// <summary>最新版本的更新说明（GitHub Releases body 原文）。已是最新时为当前版本的说明。拉取失败为 null。</summary>
    public string? Notes { get; set; }
    /// <summary>最新版本发布页地址（GitHub Releases html_url）。拉取失败为 null；前端暂不展示（留存字段）。</summary>
    public string? ReleaseUrl { get; set; }
}
