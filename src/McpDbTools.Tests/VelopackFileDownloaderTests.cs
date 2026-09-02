using McpDbTools.Server.Admin;

namespace McpDbTools.Tests;

/// <summary>
/// VelopackFileDownloader 行为测试：检查类请求超时 clamp（15s 上限）与失败后短路小写 URL 重试。
/// 连接池复用涉及真实网络 IO，不在单测覆盖，随安装版冒烟验证。
/// </summary>
public class VelopackFileDownloaderTests
{
    [Fact]
    public void ClampCheckTimeout_VelopackDefault30Min_ClampedTo15s()
    {
        // Velopack 默认超时 30 分钟：clamp 到 15 秒上限（网络半死不活时快速失败）
        Assert.Equal(0.25, VelopackFileDownloader.ClampCheckTimeout(30));
    }

    [Fact]
    public void ClampCheckTimeout_BelowCeiling_Kept()
    {
        // 低于 15 秒上限：保持调用方更短的值（只收紧、不放宽）
        Assert.Equal(0.1, VelopackFileDownloader.ClampCheckTimeout(0.1));
    }

    [Fact]
    public async Task TryDownloadThenLowercase_Failure_NoLowercaseRetry()
    {
        // 失败短路小写重试：downloadFunc 只调一次（不再用小写 URL 重试），且透传原始异常
        var downloader = new ExposedDownloader();
        int calls = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.InvokeTry<string>(async url =>
            {
                calls++;
                await Task.Yield();
                throw new InvalidOperationException($"boom {url}");
            }, "https://github.com/lzm04521/MCP-DB-Tools"));
        Assert.Equal(1, calls);
        Assert.Equal("boom https://github.com/lzm04521/MCP-DB-Tools", ex.Message);
    }

    /// <summary>暴露 protected TryDownloadThenLowercase 供直测短路行为。</summary>
    private sealed class ExposedDownloader : VelopackFileDownloader
    {
        public Task<T> InvokeTry<T>(Func<string, Task<T>> downloadFunc, string url)
            => TryDownloadThenLowercase(downloadFunc, url);
    }
}
