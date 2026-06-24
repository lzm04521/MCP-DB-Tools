using System.Text.Json;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Security;
using McpDbTools.Server.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

/// <summary>
/// db_query 的项目/环境解析逻辑测试（不连接真实数据库，覆盖 SQL 校验前的解析与错误码路径）。
/// </summary>
public class DbQueryToolTests : IDisposable
{
    private readonly string _tempDir;

    public DbQueryToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>写临时 config.json 并构造 ConfigStore + DbQueryTool（audit 关闭，避免写文件）。</summary>
    private DbQueryTool CreateTool(string databasesJson)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string json = $$"""{"audit":{"enabled":false},"databases":{{databasesJson}}}""";
        File.WriteAllText(configPath, json);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var store = new ConfigStore(
            loggerFactory.CreateLogger<ConfigStore>(),
            Options.Create(new ConfigStoreOptions { ConfigPath = configPath }));
        var audit = new AuditLogger(store, loggerFactory.CreateLogger<AuditLogger>());
        return new DbQueryTool(store, new SqlGuard(), new DatabaseProviderFactory(), audit);
    }

    [Fact]
    public async Task ProjectNotFound_ReturnsProjectNotFoundCode()
    {
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("nope", "SELECT 1");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PROJECT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task EnvironmentRequired_WhenNoDefaultAndNotSpecified()
    {
        // 无 defaultEnvironment，且未指定 environment
        var tool = CreateTool("""{"erp":{"environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "SELECT 1");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ENVIRONMENT_REQUIRED", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("prod", doc.RootElement.GetProperty("error").GetString()); // 提示可用环境
    }

    [Fact]
    public async Task EnvironmentNotFound_ReturnsCode_AndListsAvailable()
    {
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "SELECT 1", environment: "staging");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ENVIRONMENT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("prod", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DefaultEnvironment_Used_WhenNotSpecified_ThenSqlGuardRuns()
    {
        // 不传 environment → 走 defaultEnvironment=prod → 解析成功后进入 SQL 校验，DROP 被拦截
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "DROP TABLE x");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("SQL_BLOCKED", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("prod", doc.RootElement.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task ExplicitEnvironment_OverridesDefault()
    {
        // defaultEnvironment=test，但显式传 prod → 用 prod
        var tool = CreateTool("""{"erp":{"defaultEnvironment":"test","environments":{"test":{"type":"sqlserver","connectionString":"cs"},"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ExecuteQuery("erp", "DROP TABLE x", environment: "prod");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("prod", doc.RootElement.GetProperty("environment").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
