using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 自研 GitHub Releases 更新源，替换 Velopack 默认 <see cref="GithubSource"/>（用户"重写更新模块，组件太慢"的落地）。
/// 官方实现一次检查 = 1 次 releases API + 串行下载最近 10 个 release 的 releases.{channel}.json（共 11 个请求）；
/// 而每个 GitHub Release 的 feed 文件只含该版本一条 Full 记录（本仓库 CI 发布实测，2026-09-03 核对），聚合遍历是纯浪费。
/// 本实现只下载"第一个携带 channel feed 文件的 release"（即最新版）的 feed，检查从 11 个请求降到 2 个，
/// 并复用注入的 <see cref="VelopackFileDownloader"/>（连接池复用 / 检查类请求 15s 超时 / 短路小写重试）。
/// <para>
/// 语义修复：官方在"无任何可用 feed"时返回空 feed，上层 CheckForUpdatesAsync 返回 null，UI 误报"已是最新"；
/// 本实现改为抛出明确异常（检查失败、前端红色徽章）。
/// </para>
/// <para>其余更新链路保留 Velopack：UpdateManager 版本比较、DownloadUpdatesAsync 下载调度、ApplyUpdatesAndRestart 替换重启、Setup.exe 安装器。</para>
/// </summary>
public sealed class GithubFastSource : IUpdateSource
{
    private readonly Uri _repoUri;
    private readonly bool _prerelease;
    private readonly IFileDownloader _downloader;

    public GithubFastSource(string repoUrl, bool prerelease, IFileDownloader downloader)
    {
        _repoUri = new Uri(repoUrl.TrimEnd('/'));
        _prerelease = prerelease;
        _downloader = downloader;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        // 请求 1：最近 release 列表（与官方 GithubSource 相同端点，一次）
        string listUrl = new Uri(GetApiBase(), $"repos{_repoUri.AbsolutePath}/releases?per_page=10&page=1").ToString();
        string listJson = await _downloader.DownloadString(listUrl, Headers("application/vnd.github.v3+json")).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<GithubRelease>>(listJson) ?? new List<GithubRelease>();

        // 与官方一致的过滤：按构造过滤 prerelease，发布时间倒序
        var candidates = releases
            .Where(r => _prerelease || !r.Prerelease)
            .OrderByDescending(r => r.PublishedAt)
            .ToList();
        // channel feed 文件名（官方 CoreUtil.GetVeloReleaseIndexName 为 internal 不可用；Windows 默认 channel 为 "win"）
        string feedFileName = $"releases.{channel ?? "win"}.json";

        // 请求 2：只下载第一个携带 channel feed 文件的 release 的 feed（正常即最新版）。
        // feed 只含该版本的 Full/delta 记录，CheckForUpdatesAsync 取其中最大版本，结果与官方聚合 10 个完全一致
        foreach (var release in candidates)
        {
            var feedAsset = FindAsset(release, feedFileName);
            if (feedAsset?.BrowserDownloadUrl is null)
            {
                continue;
            }
            string feedJson = await _downloader.DownloadString(feedAsset.BrowserDownloadUrl, Headers("application/octet-stream")).ConfigureAwait(false);
            var feed = VelopackAssetFeed.FromJson(feedJson);
            return new VelopackAssetFeed
            {
                Assets = feed.Assets.Select(a => new GithubFastAsset(a, release)).ToArray(),
            };
        }

        // 官方此处返回空 feed（UI 误报"已是最新"）；改为明确报错
        throw new InvalidOperationException(
            $"GitHub 仓库 {_repoUri} 没有含更新元数据 {feedFileName} 的可用发布（候选 release {candidates.Count} 个）");
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        if (releaseEntry is not GithubFastAsset fast)
        {
            throw new ArgumentException($"Expected releaseEntry to be {nameof(GithubFastAsset)} but was {releaseEntry.GetType().Name}.");
        }
        var asset = FindAsset(fast.Release, releaseEntry.FileName)
            ?? throw new ArgumentException($"Could not find asset '{releaseEntry.FileName}' in GitHub Release '{fast.Release.Name}'.");
        return _downloader.DownloadFile(
            asset.BrowserDownloadUrl ?? asset.Url
                ?? throw new ArgumentException($"Asset '{releaseEntry.FileName}' has no available download url."),
            localFile, progress, Headers("application/octet-stream"), cancelToken: cancelToken);
    }

    private static GithubReleaseAsset? FindAsset(GithubRelease release, string assetName)
        => release.Assets?.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> Headers(string accept)
        => new() { ["Accept"] = accept };

    /// <summary>github.com 走公共 API；其他域名按 GitHub Enterprise 处理（逻辑同官方 GithubSource.GetApiBaseUrl）。</summary>
    private Uri GetApiBase()
        => _repoUri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)
            ? new Uri("https://api.github.com/")
            : new Uri($"{_repoUri.Scheme}{Uri.SchemeDelimiter}{_repoUri.Host}/api/v3/");
}

/// <summary>
/// 携带来源 release 的 <see cref="VelopackAsset"/> 包装（官方 GitBaseAsset 是 protected internal，项目侧不可复用），
/// 供 <see cref="GithubFastSource.DownloadReleaseEntry"/> 按文件名反查 GitHub asset 下载地址。
/// </summary>
public sealed record GithubFastAsset : VelopackAsset
{
    /// <summary>包含此更新包的 GitHub release。</summary>
    public GithubRelease Release { get; }

    public GithubFastAsset(VelopackAsset entry, GithubRelease release)
    {
        Release = release;
        PackageId = entry.PackageId;
        Version = entry.Version;
        Type = entry.Type;
        FileName = entry.FileName;
        SHA1 = entry.SHA1;
        SHA256 = entry.SHA256;
        Size = entry.Size;
        NotesMarkdown = entry.NotesMarkdown;
        NotesHTML = entry.NotesHTML;
    }
}
