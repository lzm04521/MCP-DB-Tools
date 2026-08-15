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
    public async Task SaveConfig_ProjectKeyRename_Succeeds()
    {
        // 项目 key 改名：携带 originalName 且 name 不同 = 重命名；空连接串按旧身份回填
        var (store, service, configPath) = Create("""
        {
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=old;", "maxRows": 100, "commandTimeout": 30 }
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
                        Name = "erp2",
                        OriginalName = "erp",
                        DefaultEnvironment = "prod",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = null,
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        JsonElement databases = doc.RootElement.GetProperty("databases");
        Assert.False(databases.TryGetProperty("erp", out _));
        Assert.Equal(
            "Server=old;",
            databases.GetProperty("erp2").GetProperty("environments").GetProperty("prod").GetProperty("connectionString").GetString());
    }

    [Fact]
    public async Task SaveConfig_ProjectKeyRename_CollisionWithExisting_Rejected()
    {
        // erp 改名为 crm，同时 crm 原样保留 → 请求内两个 crm，被既有查重拒绝
        var (store, service, _) = Create("""
        {
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=.;", "maxRows": 100, "commandTimeout": 30 }
              }
            },
            "crm": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=c;", "maxRows": 100, "commandTimeout": 30 }
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
                        Name = "crm",
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
                    },
                    new()
                    {
                        Name = "crm",
                        OriginalName = "crm",
                        DefaultEnvironment = "prod",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = "Server=c;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("项目 key 重复") && e.Contains("crm"));
        }
    }

    [Fact]
    public async Task SaveConfig_EnvironmentKeyRename_RemapsDefaultEnvironment()
    {
        // 环境 key 改名：defaultEnvironment 引用旧名时自动跟随为新名；空连接串按旧身份回填
        var (store, service, configPath) = Create("""
        {
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=old;", "maxRows": 100, "commandTimeout": 30 },
                "test": { "type": "sqlserver", "connectionString": "Server=t;", "maxRows": 100, "commandTimeout": 30 }
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
                                Name = "UAT",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = null,
                                MaxRows = 100,
                                CommandTimeout = 30
                            },
                            new()
                            {
                                Name = "test",
                                OriginalName = "test",
                                Type = "sqlserver",
                                ConnectionString = "Server=t;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        JsonElement project = doc.RootElement.GetProperty("databases").GetProperty("erp");
        JsonElement envs = project.GetProperty("environments");
        Assert.False(envs.TryGetProperty("prod", out _));
        Assert.Equal("Server=old;", envs.GetProperty("UAT").GetProperty("connectionString").GetString());
        Assert.Equal("UAT", project.GetProperty("defaultEnvironment").GetString());
    }

    [Fact]
    public async Task SaveConfig_EnvironmentNamesSwapped_DefaultEnvironmentFollowsIdentity()
    {
        // Test↔Prod 互换：默认环境按旧身份跟随（原 Test 环境现名 prod），连接串互不掉包
        var (store, service, configPath) = Create("""
        {
          "databases": {
            "erp": {
              "defaultEnvironment": "test",
              "environments": {
                "test": { "type": "sqlserver", "connectionString": "Server=t;", "maxRows": 100, "commandTimeout": 30 },
                "prod": { "type": "sqlserver", "connectionString": "Server=p;", "maxRows": 100, "commandTimeout": 30 }
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
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "prod",
                                OriginalName = "test",
                                Type = "sqlserver",
                                ConnectionString = null,
                                MaxRows = 100,
                                CommandTimeout = 30
                            },
                            new()
                            {
                                Name = "test",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = null,
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        JsonElement project = doc.RootElement.GetProperty("databases").GetProperty("erp");
        JsonElement envs = project.GetProperty("environments");
        Assert.Equal("prod", project.GetProperty("defaultEnvironment").GetString());
        Assert.Equal("Server=t;", envs.GetProperty("prod").GetProperty("connectionString").GetString());
        Assert.Equal("Server=p;", envs.GetProperty("test").GetProperty("connectionString").GetString());
    }

    [Fact]
    public async Task SaveConfig_EnvironmentKeyRename_CollisionWithSibling_Rejected()
    {
        // prod 改名为 test，同时 test 原样保留 → 请求内两个 test，环境级查重拒绝
        var (store, service, _) = Create("""
        {
          "databases": {
            "erp": {
              "defaultEnvironment": "prod",
              "environments": {
                "prod": { "type": "sqlserver", "connectionString": "Server=p;", "maxRows": 100, "commandTimeout": 30 },
                "test": { "type": "sqlserver", "connectionString": "Server=t;", "maxRows": 100, "commandTimeout": 30 }
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
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new()
                            {
                                Name = "test",
                                OriginalName = "prod",
                                Type = "sqlserver",
                                ConnectionString = "Server=p;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            },
                            new()
                            {
                                Name = "test",
                                OriginalName = "test",
                                Type = "sqlserver",
                                ConnectionString = "Server=t;",
                                MaxRows = 100,
                                CommandTimeout = 30
                            }
                        }
                    }
                }
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("环境 key 重复") && e.Contains("test"));
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

    // ============ 项目配置导入（环境级合并）============

    [Theory]
    [InlineData("""{"defaultDisabledKeywords":["DROP"],"databases":{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":100,"commandTimeout":30}}}}}""")]
    [InlineData("""{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":100,"commandTimeout":30}}}}""")]
    [InlineData("""{"crm":{"defaultEnvironment":"prod","environments":{"prod":{"type":"mysql","connectionString":"Server=localhost;","maxRows":500,"commandTimeout":30}}}}""")]
    public void ImportPreview_AcceptsThreeJsonShapes(string importedJson)
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            ImportPreviewResponse resp = service.GetImportPreview(importedJson);
            Assert.Empty(resp.Errors);
            Assert.Equal(1, resp.ParsedProjectCount);
            Assert.Single(resp.Plan.AddedProjects);
        }
    }

    [Fact]
    public void ImportPreview_NewProject_PlansAllAdded()
    {
        var (store, service, _) = Create("""{"databases":{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=old;","maxRows":100,"commandTimeout":30}}}}}""");
        using (store)
        {
            string imported = """{"crm":{"defaultEnvironment":"prod","environments":{"prod":{"type":"mysql","connectionString":"Server=l;","maxRows":500,"commandTimeout":30}}}}""";
            ImportPreviewResponse resp = service.GetImportPreview(imported);
            Assert.Empty(resp.Errors);
            Assert.Contains("crm", resp.Plan.AddedProjects);
            Assert.DoesNotContain("erp", resp.Plan.UpdatedProjects); // erp 未在导入文件，不出现在 plan
            Assert.Contains("crm/prod", resp.Plan.AddedEnvironments);
        }
    }

    [Fact]
    public void ImportPreview_ExistingProject_MergesAtEnvironmentLevel()
    {
        // current: erp 有 test + prod；imported: erp 有 test(新连接) + dev
        // 期望：test → updatedEnvironment；dev → addedEnvironment；prod 保留（不在 plan，但最终 next 保留）
        var (store, service, _) = Create("""{"databases":{"erp":{"defaultEnvironment":"prod","environments":{"test":{"type":"sqlserver","connectionString":"Server=old;","maxRows":100,"commandTimeout":30},"prod":{"type":"sqlserver","connectionString":"Server=p;","maxRows":100,"commandTimeout":30}}}}}""");
        using (store)
        {
            string imported = """{"erp":{"defaultEnvironment":"test","displayName":"ERP","environments":{"test":{"type":"sqlserver","connectionString":"Server=new;","maxRows":200,"commandTimeout":40},"dev":{"type":"sqlserver","connectionString":"Server=d;","maxRows":100,"commandTimeout":30}}}}""";
            ImportPreviewResponse resp = service.GetImportPreview(imported);
            Assert.Empty(resp.Errors);
            Assert.Contains("erp", resp.Plan.UpdatedProjects);
            Assert.Contains("erp/test", resp.Plan.UpdatedEnvironments);
            Assert.Contains("erp/dev", resp.Plan.AddedEnvironments);
            Assert.DoesNotContain("erp/prod", resp.Plan.UpdatedEnvironments);
        }
    }

    [Fact]
    public void ImportPreview_ProjectLevelFields_OverwrittenByImported()
    {
        // 项目级 displayName/defaultEnvironment 用 imported 覆盖
        var (store, service, _) = Create("""{"databases":{"erp":{"displayName":"Old","defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=p;","maxRows":100,"commandTimeout":30}}}}}""");
        using (store)
        {
            string imported = """{"erp":{"displayName":"New","defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=p;","maxRows":100,"commandTimeout":30}}}}""";
            ImportPreviewResponse resp = service.GetImportPreview(imported);
            Assert.Empty(resp.Errors);
            // 应用后（apply 测试在 T2 验证落盘）；preview 阶段先确认 plan 标记为 updated
            Assert.Contains("erp", resp.Plan.UpdatedProjects);
        }
    }

    [Fact]
    public void ImportPreview_InvalidMaxRows_CollectsError()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            string imported = """{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":0,"commandTimeout":30}}}}""";
            ImportPreviewResponse resp = service.GetImportPreview(imported);
            Assert.NotEmpty(resp.Errors);
            Assert.Contains(resp.Errors, e => e.Contains("maxRows"));
            Assert.Contains("erp", resp.Plan.AddedProjects); // plan 仍记录意图
        }
    }

    [Fact]
    public void ImportPreview_ProductionAndAllowWrite_Rejected()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            string imported = """{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"isProduction":true,"allowWrite":true,"type":"sqlserver","connectionString":"Server=.;","maxRows":100,"commandTimeout":30}}}}""";
            ImportPreviewResponse resp = service.GetImportPreview(imported);
            Assert.Contains(resp.Errors, e => e.Contains("互斥"));
        }
    }

    [Fact]
    public void ImportPreview_MultipleErrors_AllCollected()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            // 两项目都非法：a 的 maxRows=0，b 的连接串空
            string imported = """{"a":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":0,"commandTimeout":30}}},"b":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"","maxRows":100,"commandTimeout":30}}}}""";
            ImportPreviewResponse resp = service.GetImportPreview(imported);
            Assert.True(resp.Errors.Count >= 2);
        }
    }

    [Fact]
    public void ImportPreview_InvalidJson_ReturnsParseError()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            ImportPreviewResponse resp = service.GetImportPreview("not a json {");
            Assert.NotEmpty(resp.Errors);
            Assert.Contains("JSON 解析失败", resp.Errors[0]);
            Assert.Equal(0, resp.ParsedProjectCount);
        }
    }

    [Fact]
    public void ImportPreview_DoesNotMutateCurrentConfig()
    {
        var (store, service, configPath) = Create("""{"defaultDisabledKeywords":["DROP"],"databases":{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":100,"commandTimeout":30}}}}}""");
        string before = File.ReadAllText(configPath);
        using (store)
        {
            string imported = """{"crm":{"defaultEnvironment":"prod","environments":{"prod":{"type":"mysql","connectionString":"Server=l;","maxRows":500,"commandTimeout":30}}}}""";
            service.GetImportPreview(imported);
            // preview 不落盘、不改内存 current
            Assert.Equal(1, service.GetConfig().Projects.Count);
        }
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    // ============ apply（落盘）============

    [Fact]
    public async Task ApplyImport_NewProject_PersistsAndBacksUp()
    {
        var (store, service, configPath) = Create("{\"databases\":{}}");
        using (store)
        {
            string imported = """{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":100,"commandTimeout":30}}}}""";
            ImportApplyResult result = await service.ApplyImportAsync(imported, CancellationToken.None);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.False(string.IsNullOrEmpty(result.BackupName));
        }
        // 落盘后文件含 erp
        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        Assert.True(doc.RootElement.GetProperty("databases").TryGetProperty("erp", out _));
    }

    [Fact]
    public async Task ApplyImport_PreservesGlobalsAndMaintenance()
    {
        var (store, service, configPath) = Create("""{"defaultDisabledKeywords":["DROP"],"defaultWriteDisabledKeywords":["TRUNCATE"],"maintenance":{"auditLogAutoCleanup":true,"auditLogRetentionDays":12,"backupAutoCleanup":false,"backupRetentionDays":30,"auditRecordResults":true},"databases":{"old":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=o;","maxRows":100,"commandTimeout":30}}}}}""");
        using (store)
        {
            string imported = """{"new":{"defaultEnvironment":"prod","environments":{"prod":{"type":"mysql","connectionString":"Server=n;","maxRows":500,"commandTimeout":30}}}}""";
            ImportApplyResult result = await service.ApplyImportAsync(imported, CancellationToken.None);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }
        // 全局关键字 / 写池 / maintenance 必须原样保留；old 项目保留（未在导入文件）
        string saved = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(saved);
        JsonElement root = doc.RootElement;
        Assert.Equal("DROP", root.GetProperty("defaultDisabledKeywords")[0].GetString());
        Assert.Equal("TRUNCATE", root.GetProperty("defaultWriteDisabledKeywords")[0].GetString());
        Assert.Equal(12, root.GetProperty("maintenance").GetProperty("auditLogRetentionDays").GetInt32());
        Assert.True(root.GetProperty("databases").TryGetProperty("old", out _));
        Assert.True(root.GetProperty("databases").TryGetProperty("new", out _));
    }

    [Fact]
    public async Task ApplyImport_EnvironmentLevelMerge_PreservesExistingEnv()
    {
        // current: erp/test+prod；imported: erp/test(新)+dev → 落盘后 erp 含 test+prod+dev
        var (store, service, configPath) = Create("""{"databases":{"erp":{"defaultEnvironment":"prod","environments":{"test":{"type":"sqlserver","connectionString":"Server=old;","maxRows":100,"commandTimeout":30},"prod":{"type":"sqlserver","connectionString":"Server=p;","maxRows":100,"commandTimeout":30}}}}}""");
        using (store)
        {
            string imported = """{"erp":{"defaultEnvironment":"prod","environments":{"test":{"type":"sqlserver","connectionString":"Server=new;","maxRows":200,"commandTimeout":40},"dev":{"type":"sqlserver","connectionString":"Server=d;","maxRows":100,"commandTimeout":30}}}}""";
            ImportApplyResult result = await service.ApplyImportAsync(imported, CancellationToken.None);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement envs = doc.RootElement.GetProperty("databases").GetProperty("erp").GetProperty("environments");
        Assert.True(envs.TryGetProperty("test", out _));
        Assert.True(envs.TryGetProperty("prod", out _));
        Assert.True(envs.TryGetProperty("dev", out _));
        Assert.Equal("Server=new;", envs.GetProperty("test").GetProperty("connectionString").GetString());
    }

    [Fact]
    public async Task ApplyImport_ValidationFailed_DoesNotPersist()
    {
        var (store, service, configPath) = Create("""{"databases":{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":100,"commandTimeout":30}}}}}""");
        string before = File.ReadAllText(configPath);
        using (store)
        {
            string imported = """{"bad":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=.;","maxRows":0,"commandTimeout":30}}}}""";
            ImportApplyResult result = await service.ApplyImportAsync(imported, CancellationToken.None);
            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors);
        }
        // 校验失败不落盘：文件不变
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    // ============ T1 review pointers 补强测试 ===========

    [Fact]
    public async Task ApplyImport_ProjectLevelFields_OverwrittenByImported()
    {
        // pointer 1：项目级 displayName / defaultEnvironment 用 imported 值覆盖后落盘
        var (store, service, configPath) = Create("""{"databases":{"erp":{"displayName":"Old Name","defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=p;","maxRows":100,"commandTimeout":30},"test":{"type":"sqlserver","connectionString":"Server=t;","maxRows":100,"commandTimeout":30}}}}}""");
        using (store)
        {
            string imported = """{"erp":{"displayName":"New Name","defaultEnvironment":"test","environments":{"prod":{"type":"sqlserver","connectionString":"Server=p;","maxRows":100,"commandTimeout":30}}}}""";
            ImportApplyResult result = await service.ApplyImportAsync(imported, CancellationToken.None);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement erp = doc.RootElement.GetProperty("databases").GetProperty("erp");
        Assert.Equal("New Name", erp.GetProperty("displayName").GetString());
        Assert.Equal("test", erp.GetProperty("defaultEnvironment").GetString());
    }

    [Fact]
    public async Task ApplyImport_MixedCaseKey_MergesAsSameProject()
    {
        // pointer 2：current 含 ERP（大写）、imported 含 erp（小写）→ OrdinalIgnoreCase 合并为同一项目，不产生重复
        var (store, service, configPath) = Create("""{"databases":{"ERP":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=p;","maxRows":100,"commandTimeout":30}}}}}""");
        using (store)
        {
            string imported = """{"erp":{"defaultEnvironment":"prod","environments":{"dev":{"type":"sqlserver","connectionString":"Server=d;","maxRows":100,"commandTimeout":30}}}}""";
            ImportApplyResult result = await service.ApplyImportAsync(imported, CancellationToken.None);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            // 合并后 current 中 ERP 被 imported 的 erp 命中 → 标记为 updated，不产生 added
            Assert.Contains("ERP", result.Plan.UpdatedProjects);
            Assert.DoesNotContain("ERP", result.Plan.AddedProjects);
            Assert.DoesNotContain("erp", result.Plan.AddedProjects);
        }
        // 落盘后 databases 下只有一个项目 key（不因大小写产生重复）
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var keys = doc.RootElement.GetProperty("databases").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Single(keys);
        // dev 环境合并进来；原 prod 环境保留
        JsonElement envs = doc.RootElement.GetProperty("databases").GetProperty(keys[0]).GetProperty("environments");
        Assert.True(envs.TryGetProperty("prod", out _));
        Assert.True(envs.TryGetProperty("dev", out _));
    }

    [Fact]
    public async Task ApplyImport_ResultMatchesPreviewPlan()
    {
        // pointer 3：apply 复用 BuildMergedConfig，落盘内容应与 preview plan 描述一致。
        // 用同一 current 与 imported 分别跑 preview 和 apply，断言 plan 相同；
        // 再读 config.json 验证合并结果与 plan 描述（updated/added）一致。
        string current = """{"databases":{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=old;","maxRows":100,"commandTimeout":30}}}}}""";
        string imported = """{"erp":{"defaultEnvironment":"prod","environments":{"prod":{"type":"sqlserver","connectionString":"Server=new;","maxRows":200,"commandTimeout":40}}},"crm":{"defaultEnvironment":"prod","environments":{"prod":{"type":"mysql","connectionString":"Server=m;","maxRows":500,"commandTimeout":30}}}}""";

        // 先 preview，拿 plan
        var (previewStore, previewService, _) = Create(current);
        ImportPlan previewPlan;
        using (previewStore)
        {
            previewPlan = previewService.GetImportPreview(imported).Plan;
        }

        // 再 apply，对比 plan
        var (store, service, configPath) = Create(current);
        using (store)
        {
            ImportApplyResult result = await service.ApplyImportAsync(imported, CancellationToken.None);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            // plan 等价：updatedProjects / addedProjects / updatedEnvironments / addedEnvironments 相同
            Assert.Equal(previewPlan.UpdatedProjects.OrderBy(s => s), result.Plan.UpdatedProjects.OrderBy(s => s));
            Assert.Equal(previewPlan.AddedProjects.OrderBy(s => s), result.Plan.AddedProjects.OrderBy(s => s));
            Assert.Equal(previewPlan.UpdatedEnvironments.OrderBy(s => s), result.Plan.UpdatedEnvironments.OrderBy(s => s));
            Assert.Equal(previewPlan.AddedEnvironments.OrderBy(s => s), result.Plan.AddedEnvironments.OrderBy(s => s));
        }
        // 落盘结果验证：erp/prod 被 updated（连接串为新值），crm/prod 被 added
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement dbs = doc.RootElement.GetProperty("databases");
        Assert.True(dbs.TryGetProperty("erp", out _));
        Assert.True(dbs.TryGetProperty("crm", out _));
        Assert.Equal("Server=new;", dbs.GetProperty("erp").GetProperty("environments").GetProperty("prod").GetProperty("connectionString").GetString());
        Assert.Equal("Server=m;", dbs.GetProperty("crm").GetProperty("environments").GetProperty("prod").GetProperty("connectionString").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理，忽略 */ }
    }
}
