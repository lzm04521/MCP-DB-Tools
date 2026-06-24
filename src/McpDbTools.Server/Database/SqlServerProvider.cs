using System.Data.Common;
using McpDbTools.Server.Configuration;
using Microsoft.Data.SqlClient;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>SQL Server 提供者。连接池由 SqlClient 内置管理。</summary>
public sealed class SqlServerProvider : DatabaseProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.SqlServer;

    protected override DbConnection CreateConnection(string connectionString)
        => new SqlConnection(connectionString);
}
