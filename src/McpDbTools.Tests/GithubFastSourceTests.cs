using McpDbTools.Server.Admin;
using Velopack;
using Velopack.Sources;

namespace McpDbTools.Tests;

/// <summary>
/// GithubFastSource 测试：检查 2 请求完成（官方为 1 API + 串行 10 个 feed 共 11 个）、prerelease 过滤、
/// 跳过无 feed asset 的 release、无可用 feed 抛异常（修复官方空 feed 误报"已是最新"）、下载按文件名反查 URL。
/// 夹具取自本仓库真实 GitHub API 响应与 releases.win.json（2026-09-03 抓取核对）。
/// </summary>
public class GithubFastSourceTests
{
    private const string RepoUrl = "https://github.com/lzm04521/MCP-DB-Tools";
    private const string ListUrl = "https://api.github.com/repos/lzm04521/MCP-DB-Tools/releases?per_page=10&page=1";
    private const string FeedUrlV6 = "https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/releases.win.json";

    // releases API 真实响应结构（2026-09-03，v0.11.6 为最新稳定版，节选关键字段）
    private const string ReleasesV6Stable = """
        [{"name":"v0.11.6","prerelease":false,"published_at":"2026-09-02T09:37:04Z",
          "assets":[{"name":"releases.win.json","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/releases.win.json",
                     "url":"https://api.github.com/repos/lzm04521/MCP-DB-Tools/releases/assets/1"}]}]
        """;

    // 更新的 prerelease 排在前面：应被过滤，取 stable v0.11.6
    private const string ReleasesPreNewer = """
        [{"name":"v0.11.7-pre","prerelease":true,"published_at":"2026-09-03T09:00:00Z",
          "assets":[{"name":"releases.win.json","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.7-pre/releases.win.json"}]},
         {"name":"v0.11.6","prerelease":false,"published_at":"2026-09-02T09:37:04Z",
          "assets":[{"name":"releases.win.json","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/releases.win.json"}]}]
        """;

    // 最新 release 不带 feed asset（如发布中断），次新带：应跳到次新
    private const string ReleasesLatestNoFeed = """
        [{"name":"v0.11.6","prerelease":false,"published_at":"2026-09-02T09:37:04Z",
          "assets":[{"name":"McpDbTools-win-Setup-0.11.6.exe","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/McpDbTools-win-Setup-0.11.6.exe"}]},
         {"name":"v0.11.5","prerelease":false,"published_at":"2026-09-02T04:06:17Z",
          "assets":[{"name":"releases.win.json","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.5/releases.win.json"}]}]
        """;

    // 所有 release 都没有 feed asset（发布配置异常）：应抛异常而非返回空 feed
    private const string ReleasesAllNoFeed = """
        [{"name":"v0.11.6","prerelease":false,"published_at":"2026-09-02T09:37:04Z",
          "assets":[{"name":"McpDbTools-win-Setup-0.11.6.exe","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/McpDbTools-win-Setup-0.11.6.exe"}]},
         {"name":"v0.11.5","prerelease":false,"published_at":"2026-09-02T04:06:17Z",
          "assets":[{"name":"McpDbTools-win-Setup-0.11.5.exe","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.5/McpDbTools-win-Setup-0.11.5.exe"}]}]
        """;

    // releases.win.json 真实内容（v0.11.6，单条 Full 记录）
    private const string FeedV6 = """
        {"Assets":[{"PackageId":"McpDbTools","Version":"0.11.6","Type":"Full","FileName":"McpDbTools-0.11.6-full.nupkg","SHA1":"BFFCBA20910D2C1C74FE5D83C54675CBE8353F8C","SHA256":"7CB1ED9F71F100383AC4E32D0F801A2DBAB9698007F9614D60622A560336AA6E","Size":87616908}]}
        """;

    [Fact]
    public async Task GetReleaseFeed_TwoRequests_ReturnsLatestFeedAsset()
    {
        var dl = new FakeDownloader();
        dl.Add(ListUrl, ReleasesV6Stable);
        dl.Add(FeedUrlV6, FeedV6);
        var source = new GithubFastSource(RepoUrl, prerelease: false, dl);

        var feed = await source.GetReleaseFeed(null!, "McpDbTools", "win");

        var asset = Assert.Single(feed.Assets);
        var fast = Assert.IsType<GithubFastAsset>(asset);
        Assert.Equal("0.11.6", fast.Version.ToString());
        Assert.Equal("McpDbTools-0.11.6-full.nupkg", fast.FileName);
        // 检查只发 2 个请求（列表 + 最新 feed）；官方 GithubSource 为 11 个（1 API + 10 feed 串行下载）
        Assert.Equal(2, dl.StringUrls.Count);
    }

    [Fact]
    public async Task GetReleaseFeed_NewerPrerelease_FilteredOut()
    {
        var dl = new FakeDownloader();
        dl.Add(ListUrl, ReleasesPreNewer);
        dl.Add(FeedUrlV6, FeedV6);
        var source = new GithubFastSource(RepoUrl, prerelease: false, dl);

        var feed = await source.GetReleaseFeed(null!, "McpDbTools", "win");

        Assert.Equal("0.11.6", Assert.Single(feed.Assets).Version.ToString()); // 取 stable v0.11.6
        Assert.Equal(FeedUrlV6, dl.StringUrls[1]); // 第二个请求打向 stable 的 feed
    }

    [Fact]
    public async Task GetReleaseFeed_LatestWithoutFeedAsset_SkipsToNext()
    {
        var dl = new FakeDownloader();
        dl.Add(ListUrl, ReleasesLatestNoFeed);
        dl.Add("https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.5/releases.win.json", FeedV6);
        var source = new GithubFastSource(RepoUrl, prerelease: false, dl);

        var feed = await source.GetReleaseFeed(null!, "McpDbTools", "win");

        Assert.Single(feed.Assets);
        Assert.EndsWith("/v0.11.5/releases.win.json", dl.StringUrls[1]); // 跳过无 feed 的 v0.11.6
    }

    [Fact]
    public async Task GetReleaseFeed_NoReleaseWithFeed_ThrowsInsteadOfEmptyFeed()
    {
        // 官方此时返回空 feed → 上层返回 null → UI 误报"已是最新"；本实现抛明确异常走"检查失败"
        var dl = new FakeDownloader();
        dl.Add(ListUrl, ReleasesAllNoFeed);
        var source = new GithubFastSource(RepoUrl, prerelease: false, dl);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetReleaseFeed(null!, "McpDbTools", "win"));
    }

    [Fact]
    public async Task GetReleaseFeed_EmptyReleaseList_Throws()
    {
        var dl = new FakeDownloader();
        dl.Add(ListUrl, "[]");
        var source = new GithubFastSource(RepoUrl, prerelease: false, dl);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetReleaseFeed(null!, "McpDbTools", "win"));
    }

    [Fact]
    public async Task DownloadReleaseEntry_MatchesAssetByName_DownloadsBrowserUrl()
    {
        const string releasesWithNupkg = """
            [{"name":"v0.11.6","prerelease":false,"published_at":"2026-09-02T09:37:04Z",
              "assets":[
                {"name":"releases.win.json","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/releases.win.json"},
                {"name":"McpDbTools-0.11.6-full.nupkg","browser_download_url":"https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/McpDbTools-0.11.6-full.nupkg"}]}]
            """;
        var dl = new FakeDownloader();
        dl.Add(ListUrl, releasesWithNupkg);
        dl.Add(FeedUrlV6, FeedV6);
        var source = new GithubFastSource(RepoUrl, prerelease: false, dl);
        var feed = await source.GetReleaseFeed(null!, "McpDbTools", "win");

        await source.DownloadReleaseEntry(null!, Assert.IsType<GithubFastAsset>(Assert.Single(feed.Assets)), "local.nupkg", _ => { });

        // 按 FileName 反查到的浏览器直链（不占 API 限额）
        Assert.Equal("https://github.com/lzm04521/MCP-DB-Tools/releases/download/v0.11.6/McpDbTools-0.11.6-full.nupkg", Assert.Single(dl.FileUrls));
    }

    [Fact]
    public async Task DownloadReleaseEntry_PlainAsset_Throws()
    {
        var source = new GithubFastSource(RepoUrl, prerelease: false, new FakeDownloader());

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.DownloadReleaseEntry(null!, new VelopackAsset(), "local.nupkg", _ => { }));
    }

    /// <summary>按 URL 返回预设响应的假下载器：记录全部请求 URL；未预设 URL 抛 KeyNotFoundException 使测试失败。</summary>
    private sealed class FakeDownloader : IFileDownloader
    {
        private readonly Dictionary<string, string> _responses = new();

        public List<string> StringUrls { get; } = new();
        public List<string> FileUrls { get; } = new();

        public void Add(string url, string response) => _responses[url] = response;

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            StringUrls.Add(url);
            return Task.FromResult(_responses[url]);
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => throw new NotSupportedException("GithubFastSource 检查路径不应使用 DownloadBytes");

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null,
            double timeout = 30, CancellationToken cancelToken = default)
        {
            FileUrls.Add(url);
            return Task.CompletedTask;
        }
    }
}
