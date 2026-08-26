using McpDbTools.Server.Admin;

namespace McpDbTools.Tests;

/// <summary>
/// UpdateChecker.ParseRepoPath 纯函数测试：GitHub 仓库 URL → "owner/repo" 相对路径。
/// 覆盖：null/空串、非 github URL、常规形式、尾斜杠、.git 后缀、多余路径段（取前两段）与不足两段。
/// </summary>
public class UpdateCheckerParseRepoPathTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    // 非 github.com 域 / 非绝对 URL / 缺 scheme 均不解析
    [InlineData("https://gitlab.com/lzm04521/MCP-DB-Tools", null)]
    [InlineData("github.com/lzm04521/MCP-DB-Tools", null)]
    [InlineData("https://github.com/lzm04521", null)]
    // 常规形式
    [InlineData("https://github.com/lzm04521/MCP-DB-Tools", "lzm04521/MCP-DB-Tools")]
    // 带尾斜杠
    [InlineData("https://github.com/lzm04521/MCP-DB-Tools/", "lzm04521/MCP-DB-Tools")]
    // .git 后缀（大小写均可）
    [InlineData("https://github.com/lzm04521/MCP-DB-Tools.git", "lzm04521/MCP-DB-Tools")]
    [InlineData("https://github.com/lzm04521/MCP-DB-Tools.GIT", "lzm04521/MCP-DB-Tools")]
    // 多余路径段（/tree/main）：取前两段
    [InlineData("https://github.com/lzm04521/MCP-DB-Tools/tree/main", "lzm04521/MCP-DB-Tools")]
    // 尾斜杠 + 多余段组合
    [InlineData("https://github.com/lzm04521/MCP-DB-Tools/releases/tag/v0.10.8", "lzm04521/MCP-DB-Tools")]
    public void ParseRepoPath_Variants(string? input, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.ParseRepoPath(input));
    }
}
