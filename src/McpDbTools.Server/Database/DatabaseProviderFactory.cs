using System.Runtime.CompilerServices;
using McpDbTools.Server.Configuration;

[assembly: InternalsVisibleTo("McpDbTools.Tests")]

namespace McpDbTools.Server.Database;

/// <summary>
/// 数据库提供者工厂。按 <see cref="DatabaseType"/> 创建对应实现。
/// 提供者无状态，缓存单例实例避免重复构造。
/// </summary>
public sealed class DatabaseProviderFactory
{
    private readonly IReadOnlyDictionary<DatabaseType, IDatabaseProvider> _providers;

    public DatabaseProviderFactory()
    {
        _providers = new Dictionary<DatabaseType, IDatabaseProvider>
        {
            [DatabaseType.SqlServer] = new SqlServerProvider(),
            [DatabaseType.MySql] = new MySqlProvider(),
            [DatabaseType.Oracle] = new OracleProvider()
        };
    }

    /// <summary>测试用：允许注入 stub provider，绕过真实数据库连接。</summary>
    internal DatabaseProviderFactory(IReadOnlyDictionary<DatabaseType, IDatabaseProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>按数据库类型获取提供者。未知类型抛异常（错误必须暴露）。</summary>
    public IDatabaseProvider Get(DatabaseType type)
        => _providers.TryGetValue(type, out IDatabaseProvider? provider)
            ? provider
            : throw new NotSupportedException($"不支持的数据库类型: {type}");
}
