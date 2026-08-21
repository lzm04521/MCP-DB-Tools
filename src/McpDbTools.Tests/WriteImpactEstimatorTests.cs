using McpDbTools.Server.Security;

namespace McpDbTools.Tests;

/// <summary>
/// 写影响行数预估变换测试：标准形态 UPDATE/DELETE→COUNT、非标准形态宁拒不猜。
/// </summary>
public class WriteImpactEstimatorTests
{
    [Theory]
    [InlineData("UPDATE orders SET status = 2 WHERE id = 10",
                "SELECT COUNT(*) FROM orders WHERE id = 10")]
    [InlineData("DELETE FROM orders WHERE status = 9",
                "SELECT COUNT(*) FROM orders WHERE status = 9")]
    [InlineData("DELETE FROM orders",
                "SELECT COUNT(*) FROM orders")]                       // 无 WHERE = 全表
    [InlineData("DELETE FROM orders WHERE id IN (SELECT oid FROM items WHERE qty > 3)",
                "SELECT COUNT(*) FROM orders WHERE id IN (SELECT oid FROM items WHERE qty > 3)")] // WHERE 子查询整段保留
    [InlineData("update orders set status=2 where id=10 and memo='x;y'",   // 大小写/空白/字符串内分号，WHERE 段保留原文
                "SELECT COUNT(*) FROM orders where id=10 and memo='x;y'")]
    public void StandardForms_TransformToCount(string sql, string expected)
    {
        Assert.True(WriteImpactEstimator.TryBuildCountSql(sql, out string countSql, out _));
        Assert.Equal(expected, countSql);
    }

    [Theory]
    [InlineData("UPDATE t x SET a = 1 WHERE id = 1")]                 // Oracle 别名
    [InlineData("UPDATE t AS x SET a = 1 WHERE id = 1")]              // MySQL 别名
    [InlineData("UPDATE t SET a = 1")]                                // 无 WHERE（将更新全表）
    [InlineData("UPDATE t SET a = (SELECT MAX(x) FROM b) WHERE id = 1")] // SET 含子查询
    [InlineData("UPDATE t1 JOIN t2 ON t1.id = t2.id SET t1.a = 1")]   // 多表
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 LIMIT 5")]           // MySQL LIMIT 修饰
    [InlineData("UPDATE TOP (10) t SET a = 1")]                       // SQL Server TOP
    [InlineData("WITH c AS (SELECT 1 AS id) UPDATE t SET a = 1")]     // CTE 前缀
    [InlineData("INSERT INTO t (a) VALUES (1)")]
    [InlineData("CREATE TABLE t (a INT)")]
    [InlineData("DELETE t FROM t JOIN b ON t.id = b.id")]             // MySQL 多表 DELETE
    [InlineData("DELETE FROM t WHERE id = 1 LIMIT 5")]                // DELETE 带 LIMIT
    public void NonStandardForms_Rejected(string sql)
    {
        Assert.False(WriteImpactEstimator.TryBuildCountSql(sql, out _, out string reason));
        Assert.NotEmpty(reason);
    }
}
