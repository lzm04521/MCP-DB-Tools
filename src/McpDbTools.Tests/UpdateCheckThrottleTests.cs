using McpDbTools.Server.Admin;

namespace McpDbTools.Tests;

/// <summary>
/// 更新检查节流与调度纯函数测试：
/// UpdateChecker.ShouldSkipAutoCheck（30 分钟节流窗边界）与 UpdateCheckHostedService.NextDelay（下一跳间隔分支）。
/// </summary>
public class UpdateCheckThrottleTests
{
    [Fact]
    public void ShouldSkip_NoLastCheck_NotSkipped()
    {
        // 从未成功检查过：不跳过
        Assert.False(UpdateChecker.ShouldSkipAutoCheck(
            new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc), null, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ShouldSkip_WithinWindow_Skipped()
    {
        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddMinutes(-29);
        Assert.True(UpdateChecker.ShouldSkipAutoCheck(now, last, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ShouldSkip_AtWindowBoundary_NotSkipped()
    {
        // 恰好 30 分钟：窗已过，不跳过（>= 间隔即放行）
        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddMinutes(-30);
        Assert.False(UpdateChecker.ShouldSkipAutoCheck(now, last, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ShouldSkip_PastWindow_NotSkipped()
    {
        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddMinutes(-31);
        Assert.False(UpdateChecker.ShouldSkipAutoCheck(now, last, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ShouldSkip_ClockRolledBack_Skipped()
    {
        // 时钟回拨（last 在"未来"）：差值为负 < 间隔，视为窗内跳过（宁可少查一次）
        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddMinutes(5);
        Assert.True(UpdateChecker.ShouldSkipAutoCheck(now, last, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void NextDelay_Throttled_ThrottleRetry()
    {
        // null = 节流跳过：30 分钟后再试（节流窗过后恢复常规周期）
        Assert.Equal(TimeSpan.FromMinutes(30), UpdateCheckHostedService.NextDelay(null));
    }

    [Fact]
    public void NextDelay_Failed_FailureRetry()
    {
        var failed = new UpdateStatus { Checked = true, Error = "boom" };
        Assert.Equal(TimeSpan.FromMinutes(15), UpdateCheckHostedService.NextDelay(failed));
    }

    [Fact]
    public void NextDelay_NoUpdate_RegularInterval()
    {
        var ok = new UpdateStatus { Checked = true, HasUpdate = false };
        Assert.Equal(TimeSpan.FromHours(1), UpdateCheckHostedService.NextDelay(ok));
    }

    [Fact]
    public void NextDelay_HasUpdate_RegularInterval()
    {
        // 发现新版本不暂停周期：请求量无压力，且 UI 目标版本号可随新发版刷新
        var found = new UpdateStatus { Checked = true, HasUpdate = true, TargetVersion = "0.9.5" };
        Assert.Equal(TimeSpan.FromHours(1), UpdateCheckHostedService.NextDelay(found));
    }
}
