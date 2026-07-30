using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

public class DatabaseProviderFactoryTests
{
    [Fact]
    public void Get_PostgreSql_ReturnsPostgreSqlProvider()
    {
        var factory = new DatabaseProviderFactory();

        IDatabaseProvider provider = factory.Get(DatabaseType.PostgreSql);

        Assert.Equal(DatabaseType.PostgreSql, provider.DatabaseType);
        Assert.IsType<PostgreSqlProvider>(provider);
    }

    [Fact]
    public void Get_UnknownType_Throws()
    {
        var factory = new DatabaseProviderFactory();

        Assert.Throws<NotSupportedException>(() => factory.Get((DatabaseType)999));
    }

    [Fact]
    public void Get_ReturnsCachedSingleton()
    {
        // provider 无状态，工厂缓存单例
        var factory = new DatabaseProviderFactory();

        IDatabaseProvider a = factory.Get(DatabaseType.PostgreSql);
        IDatabaseProvider b = factory.Get(DatabaseType.PostgreSql);

        Assert.Same(a, b);
    }
}
