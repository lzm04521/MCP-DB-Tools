using System.Text.Json;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

public class AuditLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public AuditLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>在临时目录构造一个指向给定 audit 配置的 ConfigStore + AuditLogger。</summary>
    private (ConfigStore store, AuditLogger logger, string logPath) Create(bool enabled, int maxMB = 10)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string logPath = Path.Combine(_tempDir, "audit.log");

        string json = $$"""
        {
          "audit": { "enabled": {{enabled.ToString().ToLower()}}, "logPath": "{{logPath.Replace("\\", "/")}}", "maxFileSizeMB": {{maxMB}}, "maxRetentionDays": 30 },
          "databases": {}
        }
        """;
        File.WriteAllText(configPath, json);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var store = new ConfigStore(
            loggerFactory.CreateLogger<ConfigStore>(),
            Options.Create(new ConfigStoreOptions { ConfigPath = configPath }));
        var logger = new AuditLogger(store, loggerFactory.CreateLogger<AuditLogger>());
        return (store, logger, logPath);
    }

    [Fact]
    public void Disabled_DoesNotWriteFile()
    {
        var (store, logger, logPath) = Create(enabled: false);
        using (store)
        {
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 1" });
        }
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public void Enabled_WritesValidJsonlLine()
    {
        var (store, logger, logPath) = Create(enabled: true);
        using (store)
        {
            logger.Log(new AuditEntry
            {
                Project = "erp",
                DatabaseType = "SqlServer",
                Sql = "SELECT 1",
                RowCount = 5,
                ElapsedMs = 12,
                Success = true
            });
        }

        Assert.True(File.Exists(logPath));
        string line = File.ReadAllText(logPath).Trim();
        using JsonDocument doc = JsonDocument.Parse(line);

        Assert.Equal("erp", doc.RootElement.GetProperty("project").GetString());
        Assert.Equal("SqlServer", doc.RootElement.GetProperty("databaseType").GetString());
        Assert.Equal("SELECT 1", doc.RootElement.GetProperty("sql").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("rowCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.NotNull(doc.RootElement.GetProperty("time").GetString());
    }

    [Fact]
    public void MultipleEntries_AppendedAsSeparateLines()
    {
        var (store, logger, logPath) = Create(enabled: true);
        using (store)
        {
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 1", Success = true });
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 2", Success = true });
            logger.Log(new AuditEntry { Project = "p", Sql = "DROP x", Success = false, Error = "blocked" });
        }

        string[] lines = File.ReadAllLines(logPath);
        Assert.Equal(3, lines.Length);
        foreach (string line in lines)
        {
            JsonDocument.Parse(line);
        }
        using JsonDocument doc = JsonDocument.Parse(lines[2]);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("blocked", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Rotates_WhenFileSizeExceedsLimit()
    {
        var (store, logger, logPath) = Create(enabled: true, maxMB: 1);
        using (store)
        {
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 1", Success = true });
            File.AppendAllText(logPath, new string('x', 1024 * 1024 + 1));
            logger.Log(new AuditEntry { Project = "p", Sql = "SELECT 2", Success = true });
        }

        Assert.True(File.Exists(logPath + ".1"), "超限后原文件应轮转为 .1");
        Assert.True(File.Exists(logPath), "轮转后应新建当前日志文件");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}

