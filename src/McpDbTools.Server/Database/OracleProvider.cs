using System.Data.Common;
using McpDbTools.Server.Configuration;
using Oracle.ManagedDataAccess.Client;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>Oracle 提供者。使用 ODP.NET 托管驱动（3.21.x，兼容 11g R2+）。</summary>
public sealed class OracleProvider : DatabaseProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.Oracle;

    protected override DbConnection CreateConnection(string connectionString)
        => new OracleConnection(connectionString);
}
