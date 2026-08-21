using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

/// <summary>
/// SqlServer SHOWPLAN 命令序列纯函数测试（真实会话行为见 T14 真库冒烟：
/// ON→查询→OFF 三步与 finally 复位、连接池不污染）。
/// </summary>
public class SqlServerExplainTests
{
    [Fact]
    public void BuildShowPlanSequence_OnSqlOff_InOrder()
    {
        string[] seq = SqlServerProvider.BuildShowPlanSequence("SELECT * FROM t");
        Assert.Equal(new[] { "SET SHOWPLAN_ALL ON", "SELECT * FROM t", "SET SHOWPLAN_ALL OFF" }, seq);
    }

    [Fact]
    public void BuildShowPlanSequence_StripsTrailingSemicolon()
    {
        string[] seq = SqlServerProvider.BuildShowPlanSequence("SELECT 1;");
        Assert.Equal("SELECT 1", seq[1]);
    }
}
