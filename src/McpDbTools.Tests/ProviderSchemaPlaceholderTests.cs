using System.Collections;
using System.Data;
using System.Data.Common;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Tests;

/// <summary>
/// 元数据占位符方言适配的防回归测试（doc/20260902-bug报告-db_schema-Oracle元数据查询ORA-00936）：
/// ODP.NET 仅识别 : 前缀绑定变量，Oracle 路径最终 CommandText/参数名不得含 @table（否则真库报 ORA-00936）；
/// 默认前缀（SqlClient/MySqlConnector/Npgsql 兼容 @）保持原样零改动。
/// 用短路 fake ADO.NET 走基类真实执行路径（纯逻辑 stub 测不到绑定层，正是原 bug 的单测盲区）：
/// fake command 记录 CommandText 与参数后抛 DbException 短路，无需实现 reader。
/// </summary>
public class ProviderSchemaPlaceholderTests
{
    // ───────── 短路 fake ADO.NET ─────────

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeDbConnection : DbConnection
    {
        public List<FakeDbCommand> Commands { get; } = new();

        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            var cmd = new FakeDbCommand(this);
            Commands.Add(cmd);
            return cmd;
        }
    }

    private sealed class FakeDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new();
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (object v in values) _items.Add((DbParameter)v); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class FakeDbCommand(FakeDbConnection owner) : DbCommand
    {
        public List<FakeDbParameter> CapturedParameters { get; } = new();

        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeDbParameterCollection();

        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter()
        {
            var p = new FakeDbParameter();
            CapturedParameters.Add(p);
            return p;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => throw new FakeDbException("short-circuit: captured before reader");
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
    }

    private sealed class FakeDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override DataRowVersion SourceVersion { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    // ───────── stub provider：仅覆写占位符方言形式，连接返回短路 fake ─────────

    private sealed class OracleStubProvider : DatabaseProviderBase
    {
        public override DatabaseType DatabaseType => DatabaseType.Oracle;
        protected override (string Token, string ParamName) SchemaPlaceholder => (":tbl", ":tbl");
        private readonly FakeDbConnection _conn = new();
        public FakeDbConnection Connection => _conn;
        protected override DbConnection CreateConnection(string connectionString) => _conn;
    }

    private sealed class DefaultPrefixStubProvider : DatabaseProviderBase
    {
        public override DatabaseType DatabaseType => DatabaseType.SqlServer;
        private readonly FakeDbConnection _conn = new();
        public FakeDbConnection Connection => _conn;
        protected override DbConnection CreateConnection(string connectionString) => _conn;
    }

    private static ResolvedDatabase MakeDb(DatabaseType type) => new()
    {
        ProjectName = "erp",
        Environment = "prod",
        Type = type,
        ConnectionString = "cs",
        DatabaseName = null,
        IsProduction = true,
        AllowWrite = false,
        MaxRows = 1000,
        CommandTimeout = 30,
        MaxPoolSize = 100,
        ConnectTimeoutSeconds = 15,
        MaxConcurrency = 4,
        MaxConcurrencyWaitSeconds = 30,
        DisabledKeywords = new HashSet<string>()
    };

    // ───────── 纯函数 ─────────

    [Fact]
    public void AdaptSchemaTemplate_Oracle_ReplacesAtWithColon()
    {
        var provider = new OracleStubProvider();
        (string sql, string paramName) = provider.AdaptSchemaTemplate("WHERE t.table_name = UPPER(@table) ESCAPE '!'");

        Assert.Equal("WHERE t.table_name = UPPER(:tbl) ESCAPE '!'", sql);
        Assert.Equal(":tbl", paramName);
        Assert.DoesNotContain("@table", sql);
    }

    [Fact]
    public void AdaptSchemaTemplate_DefaultPrefix_ReturnsTemplateUnchanged()
    {
        var provider = new DefaultPrefixStubProvider();
        const string template = "WHERE c.table_name = UPPER(@table)";
        (string sql, string paramName) = provider.AdaptSchemaTemplate(template);

        Assert.Same(template, sql); // 默认前缀零替换，字节不变
        Assert.Equal("@table", paramName);
    }

    // ───────── 执行路径接线（GetSchemaAsync 表详情三段） ─────────

    [Fact]
    public async Task GetSchemaAsync_Oracle_CommandTextAndParamUseColonPlaceholder()
    {
        var provider = new OracleStubProvider();

        // 短路 fake 在 ExecuteReader 抛 DbException，GetSchemaAsync 包装为失败段（到达执行即证明接线生效）
        IReadOnlyList<SchemaSection> sections = await provider.GetSchemaAsync("erp", MakeDb(DatabaseType.Oracle), "ORDERS", CancellationToken.None);

        Assert.False(sections[0].Result.Success); // fake 短路，预期失败；这里只关心 captured
        FakeDbCommand cmd = Assert.Single(provider.Connection.Commands);
        Assert.Contains(":tbl", cmd.CommandText);
        Assert.DoesNotContain("@table", cmd.CommandText);
        FakeDbParameter p = Assert.Single(cmd.CapturedParameters);
        Assert.Equal(":tbl", p.ParameterName);
        Assert.Equal("ORDERS", p.Value);
    }

    [Fact]
    public async Task GetSchemaAsync_DefaultPrefix_KeepsAtPlaceholder()
    {
        var provider = new DefaultPrefixStubProvider();

        await provider.GetSchemaAsync("erp", MakeDb(DatabaseType.SqlServer), "ORDERS", CancellationToken.None);

        FakeDbCommand cmd = Assert.Single(provider.Connection.Commands);
        Assert.Contains("@table", cmd.CommandText);
        FakeDbParameter p = Assert.Single(cmd.CapturedParameters);
        Assert.Equal("@table", p.ParameterName);
    }

    // ───────── 执行路径接线（ExecuteSchemaQueryAsync：模糊搜索/列名反查/错误自愈共用） ─────────

    [Fact]
    public async Task ExecuteSchemaQueryAsync_Oracle_CommandTextAndParamUseColonPlaceholder()
    {
        var provider = new OracleStubProvider();
        string likeSql = SchemaDialects.TablesLikeSql(DatabaseType.Oracle);

        await provider.ExecuteSchemaQueryAsync("erp", MakeDb(DatabaseType.Oracle), likeSql, "ORD%", CancellationToken.None);

        FakeDbCommand cmd = Assert.Single(provider.Connection.Commands);
        Assert.Contains(":tbl", cmd.CommandText);
        Assert.DoesNotContain("@table", cmd.CommandText);
        Assert.DoesNotContain("@", cmd.CommandText); // 模糊模板含 ESCAPE '!'，全文不得残留任何 @
        FakeDbParameter p = Assert.Single(cmd.CapturedParameters);
        Assert.Equal(":tbl", p.ParameterName);
        Assert.Equal("ORD%", p.Value);
    }

    [Fact]
    public async Task ExecuteSchemaQueryAsync_DefaultPrefix_KeepsAtPlaceholder()
    {
        var provider = new DefaultPrefixStubProvider();
        string likeSql = SchemaDialects.TablesLikeSql(DatabaseType.SqlServer);

        await provider.ExecuteSchemaQueryAsync("erp", MakeDb(DatabaseType.SqlServer), likeSql, "ORD%", CancellationToken.None);

        FakeDbCommand cmd = Assert.Single(provider.Connection.Commands);
        Assert.Contains("@table", cmd.CommandText);
        FakeDbParameter p = Assert.Single(cmd.CapturedParameters);
        Assert.Equal("@table", p.ParameterName);
    }
}
