using McpDbTools.Server.Configuration;

namespace McpDbTools.Tests;

public class ConfigMergeTests
{
    /// <summary>构造单环境 DatabaseConfig 的便捷 helper。</summary>
    private static DatabaseConfig Db(DatabaseType type, string cs = "cs", List<string>? disabled = null, int maxRows = 0, int timeout = 0,
        int maxPoolSize = 0, int connectTimeout = 0, int maxConcurrency = 0)
        => new()
        {
            Type = type,
            ConnectionString = cs,
            DisabledKeywords = disabled ?? new List<string>(),
            MaxRows = maxRows,
            CommandTimeout = timeout,
            MaxPoolSize = maxPoolSize,
            ConnectTimeoutSeconds = connectTimeout,
            MaxConcurrency = maxConcurrency
        };

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
        Assert.Equal(600, db.CommandTimeout);
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

    [Fact]
    public void ConcurrencyAndPool_FallbackToBuiltin_WhenNotConfigured()
    {
        // 全局默认与环境级均未配置 → 回退内置默认（并发 10 / 等待 5 / 池 100 / 建连 60）
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.SqlServer)
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["prod"];

        Assert.Equal(10, db.MaxConcurrency);
        Assert.Equal(5, db.MaxConcurrencyWaitSeconds);
        Assert.Equal(100, db.MaxPoolSize);
        Assert.Equal(60, db.ConnectTimeoutSeconds);
    }

    [Fact]
    public void ConcurrencyAndPool_EnvironmentOverridesGlobal()
    {
        // 全局设并发 4 / 池 200；环境级覆盖为并发 16 / 池 50；建连用环境级 10
        var raw = new DatabasesConfig
        {
            DefaultMaxConcurrency = 4,
            DefaultMaxPoolSize = 200,
            DefaultConnectTimeoutSeconds = 20,
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.MySql, maxPoolSize: 50, connectTimeout: 10, maxConcurrency: 16)
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["prod"];

        Assert.Equal(16, db.MaxConcurrency);        // 环境级覆盖
        Assert.Equal(50, db.MaxPoolSize);           // 环境级覆盖
        Assert.Equal(10, db.ConnectTimeoutSeconds); // 环境级覆盖
        Assert.Equal(5, db.MaxConcurrencyWaitSeconds); // 仅有全局，未单独配 → 内置默认 5
    }

    [Fact]
    public void ConnectionString_AppendedWithPoolAndTimeout()
    {
        // 验证池/超时参数被拼接到连接串（按驱动键名）
        var raw = new DatabasesConfig
        {
            DefaultMaxPoolSize = 80,
            DefaultConnectTimeoutSeconds = 12,
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mssql"] = Db(DatabaseType.SqlServer, cs: "Server=.;Database=db;User Id=sa;Password=secret;"),
                        ["mysql"] = Db(DatabaseType.MySql, cs: "Server=localhost;Database=db;"),
                        ["oracle"] = Db(DatabaseType.Oracle, cs: "Data Source=ORCL;User Id=u;Password=p;")
                    }
                }
            }
        };

        ResolvedConfig resolved = ResolvedConfigBuilder.Build(raw);
        string mssql = resolved.Projects["p"].Environments["mssql"].ConnectionString;
        string mysql = resolved.Projects["p"].Environments["mysql"].ConnectionString;
        string oracle = resolved.Projects["p"].Environments["oracle"].ConnectionString;

        // 各驱动键名不同，且 SqlClient 输出全大写；统一转大写后断言存在
        Assert.Contains("MAX POOL SIZE=80", mssql.ToUpperInvariant());
        Assert.Contains("CONNECT TIMEOUT=12", mssql.ToUpperInvariant());
        Assert.Contains("MAXIMUM POOL SIZE=80", mysql.ToUpperInvariant());
        Assert.Contains("CONNECTION TIMEOUT=12", mysql.ToUpperInvariant());
        Assert.Contains("MAX POOL SIZE=80", oracle.ToUpperInvariant());
        Assert.Contains("CONNECTION TIMEOUT=12", oracle.ToUpperInvariant());
    }

    [Fact]
    public void ConnectionString_PreservedOnParseFailure()
    {
        // 畸形连接串：解析失败时保留原值，不阻断
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["prod"] = Db(DatabaseType.SqlServer, cs: "this is not a valid connection string $$$")
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["prod"];

        // 解析失败 → 保留原串（连接串不变，但 resolved 字段仍按默认计算）
        Assert.Equal("this is not a valid connection string $$$", db.ConnectionString);
        Assert.Equal(100, db.MaxPoolSize);
    }

    [Fact]
    public void DatabaseName_Resolved_ByType()
    {
        // 按类型从连接串提取默认库/schema 标识
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mssql"] = Db(DatabaseType.SqlServer, cs: "Server=.;Initial Catalog=OrderDb;User Id=sa;Password=p;"),
                        ["mssqlAlt"] = Db(DatabaseType.SqlServer, cs: "Server=.;Database=AltDb;User Id=sa;Password=p;"),
                        ["mysql"] = Db(DatabaseType.MySql, cs: "Server=localhost;Database=crm;"),
                        ["oracle"] = Db(DatabaseType.Oracle, cs: "Data Source=ORCL;User Id=APP;Password=p;")
                    }
                }
            }
        };

        ResolvedConfig resolved = ResolvedConfigBuilder.Build(raw);

        Assert.Equal("OrderDb", resolved.Projects["p"].Environments["mssql"].DatabaseName);
        Assert.Equal("AltDb", resolved.Projects["p"].Environments["mssqlAlt"].DatabaseName); // Database 键等价于 Initial Catalog
        Assert.Equal("crm", resolved.Projects["p"].Environments["mysql"].DatabaseName);
        Assert.Equal("APP", resolved.Projects["p"].Environments["oracle"].DatabaseName);
    }

    [Theory]
    [InlineData("Server=.;", DatabaseType.SqlServer)]                      // 缺 Initial Catalog/Database
    [InlineData("Server=.;Initial Catalog=  ;", DatabaseType.SqlServer)]   // 空白值
    [InlineData("Server=localhost;", DatabaseType.MySql)]                  // 缺 Database
    [InlineData("Data Source=ORCL;", DatabaseType.Oracle)]                 // 缺 User Id
    [InlineData("not a valid connection string $$$", DatabaseType.SqlServer)] // 畸形串
    public void DatabaseName_Null_WhenMissing_OrBlank_OrMalformed(string cs, DatabaseType type)
    {
        var raw = new DatabasesConfig
        {
            Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new ProjectConfig
                {
                    Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["e"] = Db(type, cs: cs)
                    }
                }
            }
        };

        ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["e"];

        Assert.Null(db.DatabaseName);
    }
}
