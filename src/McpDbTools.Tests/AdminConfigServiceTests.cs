using System.Text.Json;
using McpDbTools.Server.Admin;
using McpDbTools.Server.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

public class AdminConfigServiceTests : IDisposable
{
    private readonly string _tempDir;

    public AdminConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbadmin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (ConfigStore store, AdminConfigService service, string configPath) Create(string json)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, json);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var service = new AdminConfigService(store, options);
        return (store, service, configPath);
    }

    private (ConfigStore store, AdminConfigService service, string configPath) CreateMissing()
    {
        string configPath = Path.Combine(_tempDir, "config.json");

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var service = new AdminConfigService(store, options);
        return (store, service, configPath);
    }

    [Fact]
    public void GetConfig_ReturnsFullConnectionString()
    {
        var (store, service, _) = Create("""
        {
          "audit": { "enabled": false, "logPath": "logs/audit.log", "maxFileSizeMB": 10, "maxRetentionDays": 30 },
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=.;Database=db;User Id=sa;Password=secret;", "maxRows": 100, "commandTimeout": 30 }
              }
            }
          }
        }
        """);

        using (store)
        {
            AdminConfigResponse config = service.GetConfig();
            AdminEnvironmentDto env = config.Projects.Single().Environments.Single();

            Assert.Contains("DROP", config.DefaultDisabledKeywords);
            Assert.Contains("xp_cmdshell", config.DefaultDisabledKeywordsByType["sqlserver"]);
            Assert.Contains("LOAD DATA", config.DefaultDisabledKeywordsByType["mysql"]);
            Assert.Contains("FLASHBACK", config.DefaultDisabledKeywordsByType["oracle"]);
            Assert.Equal("Server=.;Database=db;User Id=sa;Password=secret;", env.ConnectionString);
            Assert.Equal(string.Empty, env.ConnectionStringMasked);
        }
    }

    [Fact]
    public async Task MissingConfig_StartsEmptyAndSaveCreatesJsonFile()
    {
        var (store, service, configPath) = CreateMissing();

        using (store)
        {
            AdminConfigResponse initial = service.GetConfig();
            Assert.Empty(initial.Projects);
            Assert.False(File.Exists(configPath));

            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
            {
                Audit = new AuditConfig { Enabled = false, LogPath = "logs/audit.log", MaxFileSizeMB = 10, MaxRetentionDays = 30 },
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "test",
                                Type = "sqlserver",
                                ConnectionString = "Server=.;Database=db;Trusted_Connection=True;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Success);
        }

        Assert.True(File.Exists(configPath));
        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        JsonElement root = doc.RootElement;

        Assert.Equal("Server=.;Database=db;Trusted_Connection=True;", root.GetProperty("databases").GetProperty("erp").GetProperty("environments").GetProperty("test").GetProperty("connectionString").GetString());
    }

    [Fact]
    public async Task SaveConfig_EmptyConnectionString_KeepsCurrentSecretAndGlobalKeywords()
    {
        var (store, service, configPath) = Create("""
        {
          "defaultDisabledKeywords": ["DROP"],
          "defaultDisabledKeywordsByType": { "sqlserver": ["xp_cmdshell"] },
          "audit": { "enabled": false, "logPath": "logs/audit.log", "maxFileSizeMB": 10, "maxRetentionDays": 30 },
          "databases": {
            "erp": {
              "displayName": "ERP",
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=.;Password=secret;", "maxRows": 100, "commandTimeout": 30, "disabledKeywords": ["extra"] }
              }
            }
          }
        }
        """);

        using (store)
        {
            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
            {
                Audit = new AuditConfig { Enabled = true, LogPath = "logs/audit.log", MaxFileSizeMB = 20, MaxRetentionDays = 15 },
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        OriginalName = "erp",
                        DisplayName = "ERP Updated",
                        DefaultEnvironment = "prod",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = null,
                                MaxRows = 200,
                                CommandTimeout = 60,
                                DisabledKeywords = new List<string> { "extra", "EXTRA", "read only" }
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Success);
        }

        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        JsonElement root = doc.RootElement;

        Assert.Equal("secret;", root.GetProperty("databases").GetProperty("erp").GetProperty("environments").GetProperty("prod").GetProperty("connectionString").GetString()!.Split("Password=")[1]);
        Assert.Equal("DROP", root.GetProperty("defaultDisabledKeywords")[0].GetString());
        Assert.Equal("xp_cmdshell", root.GetProperty("defaultDisabledKeywordsByType").GetProperty("sqlserver")[0].GetString());

        JsonElement disabled = root.GetProperty("databases").GetProperty("erp").GetProperty("environments").GetProperty("prod").GetProperty("disabledKeywords");
        Assert.Equal(2, disabled.GetArrayLength());
        Assert.Equal("extra", disabled[0].GetString());
        Assert.Equal("read only", disabled[1].GetString());
    }

    [Fact]
    public async Task SaveConfig_UpdatesDefaultDisabledKeywords()
    {
        var (store, service, configPath) = Create("""
        {
          "audit": { "enabled": false, "logPath": "logs/audit.log", "maxFileSizeMB": 10, "maxRetentionDays": 30 },
          "databases": {
            "erp": {
              "defaultEnvironment": "test",
              "environments": {
                "test": { "type": "sqlserver", "connectionString": "Server=.;", "maxRows": 100, "commandTimeout": 30 }
              }
            }
          }
        }
        """);

        using (store)
        {
            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
            {
                DefaultDisabledKeywords = new List<string> { " drop ", "DROP", "delete" },
                DefaultDisabledKeywordsByType = new Dictionary<string, List<string>>
                {
                    ["sqlserver"] = new() { " xp_cmdshell ", "XP_CMDSHELL" },
                    ["mysql"] = new() { "load data" },
                    ["oracle"] = new() { "flashback" }
                },
                Audit = new AuditConfig { Enabled = false, LogPath = "logs/audit.log", MaxFileSizeMB = 10, MaxRetentionDays = 30 },
                Projects = service.GetConfig().Projects
            }, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(new[] { "drop", "delete" }, result.Config!.DefaultDisabledKeywords);
            Assert.Equal(new[] { "xp_cmdshell" }, result.Config.DefaultDisabledKeywordsByType["sqlserver"]);
        }

        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        JsonElement root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("defaultDisabledKeywords").GetArrayLength());
        Assert.Equal("drop", root.GetProperty("defaultDisabledKeywords")[0].GetString());
        Assert.Equal("xp_cmdshell", root.GetProperty("defaultDisabledKeywordsByType").GetProperty("sqlserver")[0].GetString());
    }

    [Fact]
    public async Task SaveConfig_ProductionSensitiveChange_SavesWithoutProjectConfirmation()
    {
        var (store, service, _) = Create("""
        {
          "audit": { "enabled": false, "logPath": "logs/audit.log", "maxFileSizeMB": 10, "maxRetentionDays": 30 },
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "isProduction": true, "type": "sqlserver", "connectionString": "Server=old;", "maxRows": 100, "commandTimeout": 30 }
              }
            }
          }
        }
        """);

        using (store)
        {
            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
            {
                Audit = new AuditConfig { Enabled = false, LogPath = "logs/audit.log", MaxFileSizeMB = 10, MaxRetentionDays = 30 },
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        OriginalName = "erp",
                        DefaultEnvironment = "prod",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                OriginalName = "prod",
                                IsProduction = true,
                                Type = "mysql",
                                ConnectionString = null,
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Success);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
