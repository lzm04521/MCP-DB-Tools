using System.Text.Json;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

/// <summary>
/// db_list 工具行为矩阵测试：验证按需加载（项目索引/单项目全环境/单环境）与错误兜底。
/// 覆盖 spec 第五节行为矩阵全部 5 种情形 + 空白字符串 + 项目无环境边界。
/// </summary>
public class DbListToolTests : IDisposable
{
    private readonly string _tempDir;

    public DbListToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdblist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private DbListTool CreateTool(string databasesJson)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string json = $$"""{"databases":{{databasesJson}}}""";
        File.WriteAllText(configPath, json);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(
            loggerFactory.CreateLogger<ConfigStore>(),
            options);
        return new DbListTool(store);
    }

    // ───────── 行为 1：不传 project → 项目索引（不带环境） ─────────

    [Fact]
    public async Task NoProject_ReturnsLightweightIndex_WithoutEnvironments()
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}},"crm-mysql":{"defaultEnvironment":"prod","environments":{"prod":{"type":"mysql","connectionString":"cs"}}}}""");
        string json = await tool.ListProjects();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        JsonElement projects = doc.RootElement.GetProperty("projects");
        Assert.Equal(2, projects.GetArrayLength());

        // 项目索引只含 name + defaultEnvironment，不含 environments
        JsonElement erp = projects[0];
        Assert.Equal("erp-system", erp.GetProperty("name").GetString());
        Assert.Equal("prod", erp.GetProperty("defaultEnvironment").GetString());
        Assert.False(erp.TryGetProperty("environments", out _)); // 关键：不带环境详情
    }

    // ───────── 行为 2：传 project（存在）、不传 environment → 该项目全环境详情 ─────────

    [Fact]
    public async Task ProjectOnly_ReturnsAllEnvironmentsWithDetails()
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"test":{"type":"mysql","connectionString":"cs","maxRows":500,"maxConcurrency":4,"maxPoolSize":50,"connectTimeoutSeconds":10,"commandTimeout":20},"prod":{"type":"sqlserver","isProduction":true,"connectionString":"cs"}}}}""");
        string json = await tool.ListProjects(project: "erp-system");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        JsonElement projects = doc.RootElement.GetProperty("projects");
        Assert.Equal(1, projects.GetArrayLength());

        JsonElement erp = projects[0];
        Assert.Equal("erp-system", erp.GetProperty("name").GetString());
        Assert.Equal("prod", erp.GetProperty("defaultEnvironment").GetString());

        JsonElement envs = erp.GetProperty("environments");
        Assert.Equal(2, envs.GetArrayLength());

        // test 环境：自定义覆盖值透传
        JsonElement testEnv = envs.EnumerateArray().First(e => e.GetProperty("name").GetString() == "test");
        Assert.Equal("mysql", testEnv.GetProperty("type").GetString());
        Assert.False(testEnv.GetProperty("isProduction").GetBoolean());
        Assert.Equal(500, testEnv.GetProperty("maxRows").GetInt32());
        Assert.Equal(4, testEnv.GetProperty("maxConcurrency").GetInt32());
        Assert.Equal(50, testEnv.GetProperty("maxPoolSize").GetInt32());
        Assert.Equal(10, testEnv.GetProperty("connectTimeoutSeconds").GetInt32());
        Assert.Equal(20, testEnv.GetProperty("commandTimeout").GetInt32());

        // prod 环境：默认值回退（maxConcurrency/maxPoolSize/connectTimeoutSeconds 未配置 → 全局内置默认 10/100/60）
        JsonElement prodEnv = envs.EnumerateArray().First(e => e.GetProperty("name").GetString() == "prod");
        Assert.Equal("sqlserver", prodEnv.GetProperty("type").GetString());
        Assert.True(prodEnv.GetProperty("isProduction").GetBoolean());
        Assert.Equal(10, prodEnv.GetProperty("maxConcurrency").GetInt32());
        Assert.Equal(100, prodEnv.GetProperty("maxPoolSize").GetInt32());
        Assert.Equal(60, prodEnv.GetProperty("connectTimeoutSeconds").GetInt32());
    }

    // ───────── 行为 3：传 project + environment（均存在）→ 单环境详情 ─────────

    [Fact]
    public async Task ProjectAndEnvironment_ReturnsSingleEnvironment()
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"test":{"type":"mysql","connectionString":"cs"},"prod":{"type":"sqlserver","isProduction":true,"connectionString":"cs"}}}}""");
        string json = await tool.ListProjects(project: "erp-system", environment: "prod");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        JsonElement envs = doc.RootElement.GetProperty("projects")[0].GetProperty("environments");
        Assert.Equal(1, envs.GetArrayLength()); // 关键：只返回单环境
        Assert.Equal("prod", envs[0].GetProperty("name").GetString());
        Assert.Equal("sqlserver", envs[0].GetProperty("type").GetString());
        Assert.True(envs[0].GetProperty("isProduction").GetBoolean());
    }

    // ───────── 行为 4：project 存在 + environment 不存在 → ENVIRONMENT_NOT_FOUND + 全环境详情 ─────────

    [Fact]
    public async Task EnvironmentNotFound_ReturnsAllEnvironmentDetails()
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"test":{"type":"mysql","connectionString":"cs"},"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ListProjects(project: "erp-system", environment: "staging");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ENVIRONMENT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("staging", doc.RootElement.GetProperty("error").GetString());

        // 兜底：返回该项目全环境详情（对象数组，非名字数组）
        Assert.True(doc.RootElement.TryGetProperty("environments", out JsonElement envs));
        Assert.Equal(2, envs.GetArrayLength());
        Assert.True(envs[0].TryGetProperty("name", out _));
        Assert.True(envs[0].TryGetProperty("type", out _)); // 详情对象，含运行参数
        // 不应有 availableProjects（那是项目兜底）
        Assert.False(doc.RootElement.TryGetProperty("availableProjects", out _));
    }

    // ───────── 行为 5：project 不存在 → PROJECT_NOT_FOUND + 项目名列表 ─────────

    [Fact]
    public async Task ProjectNotFound_ReturnsProjectNameArray()
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}},"crm-mysql":{"defaultEnvironment":"prod","environments":{"prod":{"type":"mysql","connectionString":"cs"}}}}""");
        string json = await tool.ListProjects(project: "nope");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("PROJECT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("nope", doc.RootElement.GetProperty("error").GetString());

        // 兜底：availableProjects 是字符串数组（非对象数组）
        Assert.True(doc.RootElement.TryGetProperty("availableProjects", out JsonElement available));
        Assert.Equal(JsonValueKind.String, available[0].ValueKind); // 关键：字符串而非对象
        Assert.Equal("erp-system", available[0].GetString());
        Assert.Equal("crm-mysql", available[1].GetString());
        // 不应有 environments 字段
        Assert.False(doc.RootElement.TryGetProperty("environments", out _));
    }

    // ───────── 边界：project 不存在时，environment 传了也忽略 ─────────

    [Fact]
    public async Task ProjectNotFound_EnvironmentIgnored_PrecedenceProjectFirst()
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        // project 不存在 + environment 也传了 → 应只返回 PROJECT_NOT_FOUND（判断顺序：先 project）
        string json = await tool.ListProjects(project: "nope", environment: "whatever");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("PROJECT_NOT_FOUND", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.True(doc.RootElement.TryGetProperty("availableProjects", out _));
        Assert.False(doc.RootElement.TryGetProperty("environments", out _)); // 不走环境兜底
    }

    // ───────── 边界：空白字符串等同未传 ─────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankProject_TreatedAsNotProvided(string? blankProject)
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"}}}}""");
        string json = await tool.ListProjects(project: blankProject);

        using var doc = JsonDocument.Parse(json);
        // 空白 project 等同未传 → 返回项目索引（success）
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("projects")[0].TryGetProperty("name", out _));
        Assert.False(doc.RootElement.GetProperty("projects")[0].TryGetProperty("environments", out _));
    }

    [Fact]
    public async Task BlankEnvironment_TreatedAsNotProvided()
    {
        var tool = CreateTool("""{"erp-system":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"cs"},"test":{"type":"mysql","connectionString":"cs"}}}}""");
        // project 传了，environment 为空白 → 等同只传 project → 返回该项目全环境
        string json = await tool.ListProjects(project: "erp-system", environment: "  ");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        JsonElement envs = doc.RootElement.GetProperty("projects")[0].GetProperty("environments");
        Assert.Equal(2, envs.GetArrayLength()); // 返回全部环境，而非单环境
    }

    // ───────── 边界：项目存在但无环境 ─────────

    [Fact]
    public async Task ProjectWithoutEnvironments_ReturnsEmptyEnvironmentsArray()
    {
        // environments 为空字典（理论合法边界）
        var tool = CreateTool("""{"empty-proj":{"defaultEnvironment":null,"environments":{}}}""");
        string json = await tool.ListProjects(project: "empty-proj");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        JsonElement envs = doc.RootElement.GetProperty("projects")[0].GetProperty("environments");
        Assert.Equal(0, envs.GetArrayLength()); // 空环境数组
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
