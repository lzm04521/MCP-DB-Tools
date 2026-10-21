using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

/// <summary>
/// DatabaseProviderBase.ConnectTimeoutMessage 纯函数测试：建连超时失败文案按项目级 requiresVpn 分流。
/// </summary>
public class DatabaseProviderBaseTests
{
    [Fact]
    public void ConnectTimeoutMessage_VpnProject_HintsVpn()
    {
        string msg = DatabaseProviderBase.ConnectTimeoutMessage(15, requiresVpn: true);

        Assert.Contains("15 秒", msg);
        Assert.Contains("VPN 未连接", msg);
        Assert.Contains("停止重试", msg);
    }

    [Fact]
    public void ConnectTimeoutMessage_NonVpnProject_HintsNetwork()
    {
        string msg = DatabaseProviderBase.ConnectTimeoutMessage(10, requiresVpn: false);

        Assert.Contains("10 秒", msg);
        Assert.Contains("网络不通", msg);
        Assert.Contains("停止重试", msg);
        Assert.DoesNotContain("VPN", msg);
    }
}
