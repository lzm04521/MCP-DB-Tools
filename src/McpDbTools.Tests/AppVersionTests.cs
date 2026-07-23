using McpDbTools.Server;

namespace McpDbTools.Tests;

/// <summary>
/// AppVersion 静态读取测试：非空 + SemVer 核心格式（build 时由 git tag 注入）。
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void Current_NotNullOrEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
    }

    [Fact]
    public void Current_StartsWithSemVerCore()
    {
        // 形如 X.Y.Z 起始（允许 build 环境注入后缀，但本机取干净 tag）
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Current);
    }

    [Fact]
    public void Current_HasNoSourceRevisionSuffix()
    {
        // 剥去 .NET SDK 自动追加的 +commit-sha，UI 显示干净版本
        Assert.DoesNotContain('+', AppVersion.Current);
    }
}
