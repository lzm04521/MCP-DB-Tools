using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Tests;

public class AuditCounterTests : IDisposable
{
    private readonly string _tempDir;

    public AuditCounterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbcounter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (IOptions<ConfigStoreOptions> options, string dbPath) CreateOptions()
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, "{\"databases\":{}}");
        return (Options.Create(new ConfigStoreOptions { ConfigPath = configPath }),
                Path.Combine(_tempDir, "audit.db"));
    }

    private static string TodayKey() => DateTime.Today.ToString("yyyy-MM-dd");

    [Fact]
    public void Load_OnEmptyDb_ReturnsZero()
    {
        var (options, _) = CreateOptions();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var counter = new AuditCounter(options, loggerFactory.CreateLogger<AuditCounter>());

        counter.Load();

        Assert.Equal(0, counter.TotalCurrent);
        Assert.Equal(0, counter.TodayCount);
        Assert.Equal(TodayKey(), counter.TodayDateKey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理 */ }
    }
}
