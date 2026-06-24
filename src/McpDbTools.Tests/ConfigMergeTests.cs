using McpDbTools.Server.Configuration;

namespace McpDbTools.Tests;

public class ConfigMergeTests
{
    /// <summary>构造单环境 DatabaseConfig 的便捷 helper。</summary>
    private static DatabaseConfig Db(DatabaseType type, string cs = "cs", List<string>? disabled = null, int maxRows = 0, int timeout = 0)
        => new() { Type = type, ConnectionString = cs, DisabledKeywords = disabled ?? new List<string>(), MaxRows = maxRows, CommandTimeout = timeout };

    [Fact]
    public void ThreeLayers_Merged_WhenAllProvided()
    {
        // 全局 DROP + 类型 BULK INSERT + 环境额外 EXTRA
        var raw = new DatabasesConfig
        {
            DefaultDisabledKeywords = new List<string> { "DROP" },
            DefaultDisabledKeywordsByType = new Dictionary<DatabaseType, List<string>>
            {
                [DatabaseType.SqlServer] = new() { "BULK INSERT" }
            },
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.SqlServer, disabled: new List<string> { "extra" })
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["prod"];

        Assert.Contains("DROP", db.DisabledKeywords);
        Assert.Contains("BULK INSERT", db.DisabledKeywords);
        Assert.Contains("EXTRA", db.DisabledKeywords); // 环境追加转大写
    }

    [Fact]
    public void FallsBackToBuiltin_WhenGlobalNotProvided()
    {
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.MySql)
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["prod"];

        // 全局默认回退到内置
        Assert.Contains("DROP", db.DisabledKeywords);
        Assert.Contains("DELETE", db.DisabledKeywords);
        // 类型默认回退到内置（MySQL 特有）
        Assert.Contains("FLUSH", db.DisabledKeywords);
        Assert.Contains("OPTIMIZE", db.DisabledKeywords);
    }

    [Fact]
    public void ProjectKeywords_AreAdditive_CannotReduceDefaults()
    {
        // 环境追加关键字，不应移除全局默认
        var raw = new DatabasesConfig
        {
            DefaultDisabledKeywords = new List<string> { "DROP", "DELETE" },
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.SqlServer, disabled: new List<string> { "my-extra" })
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["prod"];

        Assert.Contains("DROP", db.DisabledKeywords);       // 全局保留
        Assert.Contains("DELETE", db.DisabledKeywords);     // 全局保留
        Assert.Contains("MY-EXTRA", db.DisabledKeywords);   // 环境追加
    }

    [Fact]
    public void Defaults_Applied_WhenMaxRowsAndTimeoutInvalid()
    {
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.SqlServer, maxRows: 0, timeout: -1)  // 非法 → 默认
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["prod"];

        Assert.Equal(1000, db.MaxRows);
        Assert.Equal(30, db.CommandTimeout);
    }

    [Fact]
    public void ProjectName_Lookup_CaseInsensitive()
    {
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["ERP-System"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.SqlServer)
                    }
                }
            }
        };

        ResolvedConfig resolved = ResolvedConfigBuilder.Build(raw);

        Assert.True(resolved.Projects.ContainsKey("erp-system"));
        Assert.True(resolved.Projects.ContainsKey("ERP-SYSTEM"));
    }

    [Fact]
    public void EnvironmentName_Lookup_CaseInsensitive()
    {
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Prod"] = Db(DatabaseType.SqlServer)
                    }
                }
            }
        };

        ResolvedProject proj = ResolvedConfigBuilder.Build(raw).Projects["p"];

        Assert.True(proj.Environments.ContainsKey("prod"));
        Assert.True(proj.Environments.ContainsKey("PROD"));
    }

    [Fact]
    public void DefaultEnvironment_Preserved_FromConfig()
    {
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    DefaultEnvironment = "test",
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["test"] = Db(DatabaseType.SqlServer),
                        ["prod"] = Db(DatabaseType.SqlServer)
                    }
                }
            }
        };

        ResolvedProject proj = ResolvedConfigBuilder.Build(raw).Projects["p"];

        Assert.Equal("test", proj.DefaultEnvironment);
        Assert.Equal(2, proj.Environments.Count);
    }
}
