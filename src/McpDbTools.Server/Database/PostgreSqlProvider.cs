using System.Data.Common;
using McpDbTools.Server.Configuration;
using Npgsql;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>PostgreSQL 提供者。Npgsql 内置连接池。</summary>
public sealed class PostgreSqlProvider : DatabaseProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.PostgreSql;

    protected override DbConnection CreateConnection(string connectionString)
        => new NpgsqlConnection(connectionString);
}
