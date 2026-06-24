using System.Data.Common;
using McpDbTools.Server.Configuration;
using MySqlConnector;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>MySQL 提供者。MySqlConnector 内置连接池。</summary>
public sealed class MySqlProvider : DatabaseProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.MySql;

    protected override DbConnection CreateConnection(string connectionString)
        => new MySqlConnection(connectionString);
}
