using System.Text.Json;
using McpDbTools.Server.Admin;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
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
        var service = new AdminConfigService(store, new DatabaseProviderFactory(), options);
        return (store, service, configPath);
    }

    private (ConfigStore store, AdminConfigService service, string configPath) CreateMissing()
    {
        string configPath = Path.Combine(_tempDir, "config.json");

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var service = new AdminConfigService(store, new DatabaseProviderFactory(), options);
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

            // 空 config：编辑框字段为空（留空 = 用系统默认），内置全集通过 BuiltIn* 字段暴露
            Assert.Empty(config.DefaultDisabledKeywords);
            Assert.Empty(config.DefaultDisabledKeywordsByType["sqlserver"]);
            Assert.Empty(config.DefaultDisabledKeywordsByType["mysql"]);
            Assert.Empty(config.DefaultDisabledKeywordsByType["oracle"]);
            Assert.Contains("DROP", config.BuiltInReadOnlyKeywords);
            Assert.Contains("xp_cmdshell", config.BuiltInDisabledKeywordsByType["sqlserver"]);
            Assert.Contains("LOAD DATA", config.BuiltInDisabledKeywordsByType["mysql"]);
            Assert.Contains("FLASHBACK", config.BuiltInDisabledKeywordsByType["oracle"]);
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
    public async Task SaveConfig_EmptyKeywords_StaysEmptyOnReload_BuiltInStillExposed()
    {
        // 需求3：编辑框允许留空，空 = 使用系统默认。
        // 清空保存后重新加载，编辑框字段必须仍为空（不被内置列表填回）；
        // 内置全集仍通过 BuiltIn* 字段暴露，运行时由 ResolvedConfig.Build 回退。
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            AdminConfigRequest request = new()
            {
                DefaultDisabledKeywords = new List<string>(),
                DefaultWriteDisabledKeywords = new List<string>(),
                DefaultDisabledKeywordsByType = new Dictionary<string, List<string>>
                {
                    ["sqlserver"] = new(),
                    ["mysql"] = new(),
                    ["oracle"] = new(),
                    ["postgresql"] = new()
                },
                Projects = service.GetConfig().Projects
            };

            AdminSaveResult saved = await service.SaveConfigAsync(request, CancellationToken.None);
            Assert.True(saved.Success, string.Join("; ", saved.Errors));
            // 保存响应：编辑框字段为空，内置字段非空
            Assert.Empty(saved.Config!.DefaultDisabledKeywords);
            Assert.Empty(saved.Config!.DefaultWriteDisabledKeywords);
            Assert.NotEmpty(saved.Config!.BuiltInReadOnlyKeywords);
            Assert.NotEmpty(saved.Config!.BuiltInWriteKeywords);

            // 重新加载：编辑框字段仍为空（核心 — 不被内置填回）
            AdminConfigResponse reloaded = service.GetConfig();
            Assert.Empty(reloaded.DefaultDisabledKeywords);
            Assert.Empty(reloaded.DefaultWriteDisabledKeywords);
            Assert.Empty(reloaded.DefaultDisabledKeywordsByType["sqlserver"]);
            Assert.NotEmpty(reloaded.BuiltInReadOnlyKeywords);
            Assert.NotEmpty(reloaded.BuiltInWriteKeywords);
        }
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

    [Fact]
    public async Task SaveConfig_ProjectKeyChange_Rejected()
    {
        // 需求 1：项目 key 创建后不可修改。携带 originalName 但 name 与之不同应报错。
        var (store, service, _) = Create("""
        {
          "audit": { "enabled": false, "logPath": "logs/audit.log", "maxFileSizeMB": 10, "maxRetentionDays": 30 },
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=.;", "maxRows": 100, "commandTimeout": 30 }
              }
            }
          }
        }
        """);

        using (store)
        {
            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp-renamed",
                        OriginalName = "erp",
                        DefaultEnvironment = "prod",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = "Server=.;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("项目 key") && e.Contains("不可修改"));
        }
    }

    [Fact]
    public async Task SaveConfig_EnvironmentKeyChange_Rejected()
    {
        // 环境键同样不可修改
        var (store, service, _) = Create("""
        {
          "audit": { "enabled": false, "logPath": "logs/audit.log", "maxFileSizeMB": 10, "maxRetentionDays": 30 },
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=.;", "maxRows": 100, "commandTimeout": 30 }
              }
            }
          }
        }
        """);

        using (store)
        {
            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
            {
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
                                Name = "prod-renamed",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = "Server=.;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("环境 key") && e.Contains("不可修改"));
        }
    }

    [Fact]
    public async Task SaveConfig_NewProject_AllowsAnyKey()
    {
        // 新建项目（无 originalName）可自由命名
        var (store, service, _) = CreateMissing();

        using (store)
        {
            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "brand-new-project",
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "test",
                                Type = "sqlserver",
                                ConnectionString = "Server=.;",
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

    [Fact]
    public async Task SaveConfig_Rejects_IsProduction_And_AllowWrite_Both_True()
    {
        // 互斥校验：同一环境不能同时 IsProduction=true 与 AllowWrite=true
        var (store, service, _) = Create("""
        {
          "databases": {}
        }
        """);
        using (store)
        {
            var request = new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        DefaultEnvironment = "prod",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                IsProduction = true,
                                AllowWrite = true,
                                Type = "sqlserver",
                                ConnectionString = "Server=.;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            };

            AdminSaveResult result = await service.SaveConfigAsync(request, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("互斥"));
        }
    }

    [Fact]
    public void GetConfig_Exposes_BuiltIn_Keywords_And_WritePool()
    {
        // 内置关键字通过 API 暴露（单一真源 = 后端）；编辑框字段空 config 时为空（留空 = 用系统默认）
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            AdminConfigResponse resp = service.GetConfig();
            Assert.NotEmpty(resp.BuiltInReadOnlyKeywords);
            Assert.NotEmpty(resp.BuiltInWriteKeywords);
            Assert.NotEmpty(resp.BuiltInDisabledKeywordsByType);
            Assert.Empty(resp.DefaultDisabledKeywords);       // 空 config：编辑框不回填内置
            Assert.Empty(resp.DefaultWriteDisabledKeywords);  // 空 config：编辑框不回填内置
            // BuiltInByType 的 key 应是 lowerInvariant 字符串
            Assert.Contains("sqlserver", resp.BuiltInDisabledKeywordsByType.Keys);
            Assert.Contains("mysql", resp.BuiltInDisabledKeywordsByType.Keys);
            Assert.Contains("oracle", resp.BuiltInDisabledKeywordsByType.Keys);
            Assert.Contains("postgresql", resp.BuiltInDisabledKeywordsByType.Keys);
        }
    }

    [Fact]
    public async Task Save_PreservesPostgreSqlType()
    {
        var (store, service, configPath) = CreateMissing();
        using (store)
        {
            var result = await service.SaveConfigAsync(new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        DefaultEnvironment = "prod",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                Type = "postgresql",
                                ConnectionString = "Host=localhost;Database=db;Username=u;Password=p;"
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Contains("\"postgresql\"", File.ReadAllText(configPath));

            AdminEnvironmentDto env = result.Config!.Projects.Single().Environments.Single();
            Assert.Equal("postgresql", env.Type);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
