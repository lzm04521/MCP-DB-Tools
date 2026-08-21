using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

/// <summary>
/// Oracle EXPLAIN PLAN 双语句纯函数测试（真实会话行为见 T14 真库冒烟）。
/// </summary>
public class OracleExplainTests
{
    [Fact]
    public void BuildExplainStatements_PlanThenDisplay()
    {
        var (plan, read) = OracleProvider.BuildExplainStatements("SELECT * FROM t");
        Assert.Equal("EXPLAIN PLAN FOR SELECT * FROM t", plan);
        Assert.Equal("SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY())", read);
    }

    [Fact]
    public void BuildExplainStatements_StripsTrailingSemicolon()
    {
        var (plan, _) = OracleProvider.BuildExplainStatements("SELECT 1;");
        Assert.Equal("EXPLAIN PLAN FOR SELECT 1", plan);
    }
}
