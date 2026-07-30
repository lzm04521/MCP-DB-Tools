# PostgreSQL 支持 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 MCP-DB-Tools 新增第四种数据库类型 PostgreSQL，与现有 SqlServer/MySQL/Oracle 完全对等支持。

**Architecture:** 对称扩展现有三库模式——新增 `DatabaseType.PostgreSql` 枚举值 + 第四个 `IDatabaseProvider` 实现（`PostgreSqlProvider`，封装 `NpgsqlConnection`），在 `SqlGuard` 白名单、`DefaultDisabledKeywords` 按类型黑名单、`ResolvedConfig` 连接串拼接、JSON converter、AdminConfigService、Admin UI 下拉框各加一个对称分支。查询主流程（`DbQueryTool`/`DbListTool`）、`DatabaseProviderBase` 骨架、三层黑名单合并逻辑全部类型无关，不动。

**Tech Stack:** .NET 8（net8.0）、C# nullable enable、xUnit、`Npgsql`（ADO.NET 驱动）、静态 SPA（无 npm 构建链）。

## Global Constraints

- 目标框架 `net8.0`，nullable enable，ImplicitUsings enable。
- 新增驱动包：`Npgsql`（最新稳定，建议 8.x；用 `dotnet add package Npgsql` 取实际最新稳定版本号）。
- 代码标识符用英文，注释用简体中文（遵循项目惯例，见现有 provider/SqlGuard 注释风格）。
- 测试框架 xUnit，断言用 `Assert`，遵循现有 `SqlGuardTests`/`ConfigMergeTests` 风格（`[Theory]/[InlineData]`、helper 方法、连接串断言用 `ToUpperInvariant()` 兜底驱动输出大小写差异）。
- **提交授权：本项目 `git commit` 需用户明确通知后方可执行**（项目规则）。各 task 末尾的 commit step 是标准流程占位，实际执行时须等用户授权；未授权则保留未提交状态，不自动 commit。
- 不改单文件发布参数（`PublishSingleFile`+`SelfContained`+`IncludeNativeLibrariesForSelfExtract`），仅新增 Npgsql 依赖。
- 实施依据：`doc/20260730-PostgreSQL支持.md`（已确认实施方案）。

## 已确认的 PG 语义决策（来自实施方案）

- 白名单首关键字：`SELECT, WITH, CALL, EXPLAIN, SHOW, TABLE, VALUES`
- 按类型阻止关键字：`COPY, VACUUM, REINDEX, CLUSTER, REFRESH MATERIALIZED VIEW, ANALYZE, NOTIFY`
- 连接串键名：`Maximum Pool Size`（池上限）、`Timeout`（建连超时，注意**不是** `Connection Timeout`）
- DatabaseName 提取：`NpgsqlConnectionStringBuilder.Database`
- JSON 值：`"postgresql"` ↔ `DatabaseType.PostgreSql`

---

## Task 1: 配置层——Npgsql 包、枚举、JSON converter、连接串拼接、DatabaseName

**Files:**
- Modify: `src/McpDbTools.Server/McpDbTools.Server.csproj`（ItemGroup 加 Npgsql）
- Modify: `src/McpDbTools.Server/Configuration/DatabaseConfig.cs:9-14`（枚举）
- Modify: `src/McpDbTools.Server/Configuration/DatabaseTypeJsonConverter.cs:23-43`（converter）
- Modify: `src/McpDbTools.Server/Configuration/ResolvedConfig.cs`（using + `BuildConnectionString` + `ResolveDatabaseName`）
- Test: `src/McpDbTools.Tests/ConfigMergeTests.cs`（追加 3 个测试）

**Interfaces:**
- Consumes: 无（首个 task）
- Produces: `DatabaseType.PostgreSql` 枚举值；`"postgresql"` JSON 映射；`ResolvedConfigBuilder.BuildConnectionString`/`ResolveDatabaseName` 的 PG 分支。后续 task 全部依赖此枚举值。

- [ ] **Step 1: 加 Npgsql 包**

Run:
```bash
cd src/McpDbTools.Server && dotnet add package Npgsql
```
Expected: `PackageReference for 'Npgsql' ... added` 之类成功信息，`McpDbTools.Server.csproj` 的 `<ItemGroup>` 多出一行 `<PackageReference Include="Npgsql" Version="..." />`。

- [ ] **Step 2: 枚举加 PostgreSql**

修改 `src/McpDbTools.Server/Configuration/DatabaseConfig.cs`，枚举改为：

```csharp
public enum DatabaseType
{
    SqlServer,
    MySql,
    Oracle,
    PostgreSql
}
```

- [ ] **Step 3: 写失败测试**

在 `src/McpDbTools.Tests/ConfigMergeTests.cs` 的 `ConfigMergeTests` 类内追加三个测试方法（复用现有 `Db(...)` helper）。测试仅引用 `DatabaseType.PostgreSql`（Step 2 已加）与 `ResolvedConfigBuilder`（已存在），可正常编译；运行时因 converter/ResolvedConfig 尚无 PG 分支而失败：

```csharp
[Fact]
public void DatabaseType_PostgreSql_JsonRoundtrip()
{
    // converter: "postgresql" ↔ DatabaseType.PostgreSql
    string json = JsonSerializer.Serialize(DatabaseType.PostgreSql);
    Assert.Equal("\"postgresql\"", json);
    Assert.Equal(DatabaseType.PostgreSql, JsonSerializer.Deserialize<DatabaseType>(json));
}

[Fact]
public void ConnectionString_AppendedWithPoolAndTimeout_PostgreSql()
{
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
                    ["pg"] = Db(DatabaseType.PostgreSql, cs: "Host=localhost;Database=db;Username=u;Password=p;")
                }
            }
        }
    };

    string pg = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["pg"].ConnectionString;

    // Npgsql 键名：Maximum Pool Size / Timeout（不同于 MySQL/Oracle 的 Connection Timeout）
    Assert.Contains("MAXIMUM POOL SIZE=80", pg.ToUpperInvariant());
    Assert.Contains("TIMEOUT=12", pg.ToUpperInvariant());
}

[Fact]
public void DatabaseName_Resolved_PostgreSql()
{
    var raw = new DatabasesConfig
    {
        Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["p"] = new ProjectConfig
            {
                Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pg"] = Db(DatabaseType.PostgreSql, cs: "Host=localhost;Database=pgdb;Username=u;Password=p;")
                }
            }
        }
    };

    Assert.Equal("pgdb", ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["pg"].DatabaseName);
}
```

注意：`ConfigMergeTests.cs` 顶部已有 `using McpDbTools.Server.Configuration;`，`JsonSerializer` 需确保 `using System.Text.Json;`（若文件未引用则在 using 区补一行；现有 `AdminConfigServiceTests.cs:1` 有此 using 可参照）。

- [ ] **Step 4: 跑测试确认失败**

Run:
```bash
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj --filter "FullyQualifiedName~ConfigMergeTests.DatabaseType_PostgreSql_JsonRoundtrip|FullyQualifiedName~ConfigMergeTests.ConnectionString_AppendedWithPoolAndTimeout_PostgreSql|FullyQualifiedName~ConfigMergeTests.DatabaseName_Resolved_PostgreSql"
```
Expected: 3 个测试 FAIL，原因各异——`DatabaseType_PostgreSql_JsonRoundtrip`：`ToJsonValue` 落到 `_ => throw JsonException`（converter 未加 PG 映射）；`ConnectionString_AppendedWithPoolAndTimeout_PostgreSql`：`BuildConnectionString` 落到 `_ => null!`，后续 `b[poolKey]=...` 抛 `NullReferenceException`；`DatabaseName_Resolved_PostgreSql`：`ResolveDatabaseName` 落到 `_ => null`，`Assert.Equal("pgdb", null)` 失败。

- [ ] **Step 5: JSON converter 加 postgresql 映射**

修改 `src/McpDbTools.Server/Configuration/DatabaseTypeJsonConverter.cs`。`Parse` 方法（约 :27-31）加一行：

```csharp
private static DatabaseType Parse(string? value)
{
    return value?.Trim().ToLowerInvariant() switch
    {
        "sqlserver" => DatabaseType.SqlServer,
        "mysql" => DatabaseType.MySql,
        "oracle" => DatabaseType.Oracle,
        "postgresql" => DatabaseType.PostgreSql,
        _ => throw new JsonException($"不支持的数据库类型: {value}")
    };
}
```

`ToJsonValue` 方法（约 :34-43）加一行：

```csharp
private static string ToJsonValue(DatabaseType value)
{
    return value switch
    {
        DatabaseType.SqlServer => "sqlserver",
        DatabaseType.MySql => "mysql",
        DatabaseType.Oracle => "oracle",
        DatabaseType.PostgreSql => "postgresql",
        _ => throw new JsonException($"不支持的数据库类型: {value}")
    };
}
```

- [ ] **Step 6: ResolvedConfig 连接串拼接与 DatabaseName 加 PG 分支**

修改 `src/McpDbTools.Server/Configuration/ResolvedConfig.cs`：

文件头 using 区加（与现有 `using MySqlConnector;` 等并列）：

```csharp
using Npgsql;
```

`BuildConnectionString` 的驱动 switch（约 :176-182）加 PG 分支：

```csharp
DbConnectionStringBuilder b = type switch
{
    DatabaseType.SqlServer => new SqlConnectionStringBuilder(raw),
    DatabaseType.MySql => new MySqlConnectionStringBuilder(raw),
    DatabaseType.Oracle => new OracleConnectionStringBuilder(raw),
    DatabaseType.PostgreSql => new NpgsqlConnectionStringBuilder(raw),
    _ => null! // 理论不可达：枚举已覆盖全部类型
};
```

`BuildConnectionString` 的键名 switch（约 :184-190）加 PG 分支：

```csharp
(string poolKey, string timeoutKey) = type switch
{
    DatabaseType.SqlServer => ("Max Pool Size", "Connect Timeout"),
    DatabaseType.MySql => ("Maximum Pool Size", "Connection Timeout"),
    DatabaseType.Oracle => ("Max Pool Size", "Connection Timeout"),
    DatabaseType.PostgreSql => ("Maximum Pool Size", "Timeout"),
    _ => (string.Empty, string.Empty)
};
```

`ResolveDatabaseName`（约 :212-217）加 PG 分支：

```csharp
string? v = type switch
{
    DatabaseType.SqlServer => new SqlConnectionStringBuilder(raw).InitialCatalog,
    DatabaseType.MySql => new MySqlConnectionStringBuilder(raw).Database,
    DatabaseType.Oracle => new OracleConnectionStringBuilder(raw).UserID,
    DatabaseType.PostgreSql => new NpgsqlConnectionStringBuilder(raw).Database,
    _ => null
};
```

- [ ] **Step 7: 跑测试确认通过**

Run 同 Step 4 命令。Expected: 3 个测试 PASS。

> 断言说明：连接串断言基于 Npgsql 规范化输出键名 `Maximum Pool Size`/`Timeout`。若实际 Npgsql 版本输出大小写或格式不同，按 `pg` 的实际字符串值调整断言（测试验证的是拼接行为，调整断言字符串不改变行为正确性）。

- [ ] **Step 8: 全量构建**

Run:
```bash
dotnet build src/McpDbTools.Server/McpDbTools.Server.csproj
```
Expected: Build succeeded，无错误。

- [ ] **Step 9: Commit（需用户授权）**

```bash
git add src/McpDbTools.Server/McpDbTools.Server.csproj \
  src/McpDbTools.Server/Configuration/DatabaseConfig.cs \
  src/McpDbTools.Server/Configuration/DatabaseTypeJsonConverter.cs \
  src/McpDbTools.Server/Configuration/ResolvedConfig.cs \
  src/McpDbTools.Tests/ConfigMergeTests.cs
git commit -m "feat(pg): 配置层支持 PostgreSQL（枚举+converter+连接串拼接+DatabaseName）"
```

---

## Task 2: Provider——PostgreSqlProvider + 工厂注册

**Files:**
- Create: `src/McpDbTools.Server/Database/PostgreSqlProvider.cs`
- Modify: `src/McpDbTools.Server/Database/DatabaseProviderFactory.cs:18-23`
- Test: `src/McpDbTools.Tests/DatabaseProviderFactoryTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 `DatabaseType.PostgreSql`；`DatabaseProviderBase`（基类骨架）。
- Produces: `PostgreSqlProvider` 类；`DatabaseProviderFactory.Get(DatabaseType.PostgreSql)` 可用。Task 3+ 不直接依赖，但运行时 `db_query` 经工厂取此 provider。

- [ ] **Step 1: 写失败测试**

新建 `src/McpDbTools.Tests/DatabaseProviderFactoryTests.cs`：

```csharp
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;

namespace McpDbTools.Tests;

public class DatabaseProviderFactoryTests
{
    [Fact]
    public void Get_PostgreSql_ReturnsPostgreSqlProvider()
    {
        var factory = new DatabaseProviderFactory();

        IDatabaseProvider provider = factory.Get(DatabaseType.PostgreSql);

        Assert.Equal(DatabaseType.PostgreSql, provider.DatabaseType);
        Assert.IsType<PostgreSqlProvider>(provider);
    }

    [Fact]
    public void Get_UnknownType_Throws()
    {
        var factory = new DatabaseProviderFactory();

        Assert.Throws<NotSupportedException>(() => factory.Get((DatabaseType)999));
    }

    [Fact]
    public void Get_ReturnsCachedSingleton()
    {
        // provider 无状态，工厂缓存单例
        var factory = new DatabaseProviderFactory();

        IDatabaseProvider a = factory.Get(DatabaseType.PostgreSql);
        IDatabaseProvider b = factory.Get(DatabaseType.PostgreSql);

        Assert.Same(a, b);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj --filter "FullyQualifiedName~DatabaseProviderFactoryTests"
```
Expected: FAIL（`PostgreSqlProvider` 未定义 / 工厂未注册 PG → `NotSupportedException`）。

- [ ] **Step 3: 新建 PostgreSqlProvider**

新建 `src/McpDbTools.Server/Database/PostgreSqlProvider.cs`（仿 `SqlServerProvider.cs`）：

```csharp
using System.Data.Common;
using McpDbTools.Server.Configuration;
using Npgsql;
using DatabaseType = McpDbTools.Server.Configuration.DatabaseType;

namespace McpDbTools.Server.Database;

/// <summary>PostgreSQL 提供者。Npgsql 内置连接池。</summary>
public sealed class PostgreSqlProvider : DatabaseProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.PostgreSql;

    protected override DbConnection CreateConnection(string connectionString)
        => new NpgsqlConnection(connectionString);
}
```

- [ ] **Step 4: 工厂注册 PG provider**

修改 `src/McpDbTools.Server/Database/DatabaseProviderFactory.cs:18-23`，字典加一行：

```csharp
_providers = new Dictionary<DatabaseType, IDatabaseProvider>
{
    [DatabaseType.SqlServer] = new SqlServerProvider(),
    [DatabaseType.MySql] = new MySqlProvider(),
    [DatabaseType.Oracle] = new OracleProvider(),
    [DatabaseType.PostgreSql] = new PostgreSqlProvider()
};
```

- [ ] **Step 5: 跑测试确认通过**

Run 同 Step 2 命令。Expected: 3 个测试 PASS。

- [ ] **Step 6: Commit（需用户授权）**

```bash
git add src/McpDbTools.Server/Database/PostgreSqlProvider.cs \
  src/McpDbTools.Server/Database/DatabaseProviderFactory.cs \
  src/McpDbTools.Tests/DatabaseProviderFactoryTests.cs
git commit -m "feat(pg): 新增 PostgreSqlProvider 与工厂注册"
```

---

## Task 3: AdminConfigService——PG 解析/序列化/SupportedTypes

**Files:**
- Modify: `src/McpDbTools.Server/Admin/AdminConfigService.cs:11-16`（SupportedDatabaseTypes）、`:617-634`（TryParseDatabaseType）、`:636-642`（ToConfigType）
- Test: `src/McpDbTools.Tests/AdminConfigServiceTests.cs`（追加保存往返测试）

**Interfaces:**
- Consumes: Task 1 的枚举与 converter（GetConfig 加载 PG 环境依赖 converter，已在 Task 1 就绪）。
- Produces: Admin UI 保存/校验 PG 环境类型的能力。

- [ ] **Step 1: 写失败测试**

在 `src/McpDbTools.Tests/AdminConfigServiceTests.cs` 的 `AdminConfigServiceTests` 类内追加（复用现有 `CreateMissing()` helper，参照文件内 `MissingConfig_StartsEmptyAndSaveCreatesJsonFile` 测试模式）：

```csharp
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
        });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains("\"postgresql\"", File.ReadAllText(configPath));

        AdminEnvironmentDto env = service.GetConfig().Projects.Single().Environments.Single();
        Assert.Equal("postgresql", env.Type);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj --filter "FullyQualifiedName~AdminConfigServiceTests.Save_PreservesPostgreSqlType"
```
Expected: FAIL（`TryParseDatabaseType` 不识别 `"postgresql"` → 校验失败或 type 未保留）。

> 说明：若 AdminConfigService 保存校验逻辑对未知 type 的处理与预期不同（例如直接报错 vs 静默丢弃），以实际行为为准调整断言；核心目标是验证 Task 3 改动后 PG type 能保留。

- [ ] **Step 3: AdminConfigService 加 PG**

修改 `src/McpDbTools.Server/Admin/AdminConfigService.cs`。

`SupportedDatabaseTypes` 数组（约 :11-16）加一项：

```csharp
private static readonly DatabaseType[] SupportedDatabaseTypes =
{
    DatabaseType.SqlServer,
    DatabaseType.MySql,
    DatabaseType.Oracle,
    DatabaseType.PostgreSql
};
```

`TryParseDatabaseType`（约 :617-634）加 case：

```csharp
switch (value.Trim().ToLowerInvariant())
{
    case "sqlserver":
        type = DatabaseType.SqlServer;
        return true;
    case "mysql":
        type = DatabaseType.MySql;
        return true;
    case "oracle":
        type = DatabaseType.Oracle;
        return true;
    case "postgresql":
        type = DatabaseType.PostgreSql;
        return true;
    default:
        type = default;
        return false;
}
```

`ToConfigType`（约 :636-642）加分支：

```csharp
private static string ToConfigType(DatabaseType type) => type switch
{
    DatabaseType.SqlServer => "sqlserver",
    DatabaseType.MySql => "mysql",
    DatabaseType.Oracle => "oracle",
    DatabaseType.PostgreSql => "postgresql",
    _ => throw new NotSupportedException($"不支持的数据库类型: {type}")
};
```

- [ ] **Step 4: 跑测试确认通过**

Run 同 Step 2 命令。Expected: PASS。

- [ ] **Step 5: Commit（需用户授权）**

```bash
git add src/McpDbTools.Server/Admin/AdminConfigService.cs \
  src/McpDbTools.Tests/AdminConfigServiceTests.cs
git commit -m "feat(pg): AdminConfigService 解析/序列化/校验 PostgreSQL 类型"
```

---

## Task 4: 黑名单——DefaultDisabledKeywords PG 分支

**Files:**
- Modify: `src/McpDbTools.Server/Configuration/DefaultDisabledKeywords.cs:35-64`
- Test: `src/McpDbTools.Tests/ConfigMergeTests.cs`（追加 PG 黑名单回退测试）

**Interfaces:**
- Consumes: Task 1 的 `DatabaseType.PostgreSql`。
- Produces: `DefaultDisabledKeywords.BuiltInByType[DatabaseType.PostgreSql]` 有值。Task 5（SqlGuard 测试的 `Db` helper）与 Admin GetConfig 的 `DefaultDisabledKeywordsByType["postgresql"]` 依赖此。

- [ ] **Step 1: 写失败测试**

在 `src/McpDbTools.Tests/ConfigMergeTests.cs` 追加：

```csharp
[Fact]
public void FallsBackToBuiltin_PostgreSqlKeywords()
{
    var raw = new DatabasesConfig
    {
        Projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["p"] = new ProjectConfig
            {
                Environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pg"] = Db(DatabaseType.PostgreSql)
                }
            }
        }
    };

    ResolvedDatabase db = ResolvedConfigBuilder.Build(raw).Projects["p"].Environments["pg"];

    // 全局通用默认仍在
    Assert.Contains("DROP", db.DisabledKeywords);
    Assert.Contains("DELETE", db.DisabledKeywords);
    // PG 特有按类型默认
    Assert.Contains("COPY", db.DisabledKeywords);
    Assert.Contains("VACUUM", db.DisabledKeywords);
    Assert.Contains("REINDEX", db.DisabledKeywords);
    Assert.Contains("CLUSTER", db.DisabledKeywords);
    Assert.Contains("REFRESH MATERIALIZED VIEW", db.DisabledKeywords);
    Assert.Contains("ANALYZE", db.DisabledKeywords);
    Assert.Contains("NOTIFY", db.DisabledKeywords);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj --filter "FullyQualifiedName~ConfigMergeTests.FallsBackToBuiltin_PostgreSqlKeywords"
```
Expected: FAIL。`ResolvedConfigBuilder.Build` 对 PG 类型走 `DefaultDisabledKeywords.BuiltInByType.TryGetValue(...)`，Task 4 前无 PG 键 → 返回 false → `byType` 为空数组 → 合并后的 `DisabledKeywords` 只含全局默认（`DROP`/`DELETE` 等），不含 `COPY`/`VACUUM` → `Assert.Contains("COPY", ...)` 失败。注意：`ConfigMergeTests.Db` helper 不读 `BuiltInByType`，`ResolvedConfigBuilder` 用 `TryGetValue`，二者均不抛 `KeyNotFoundException`，失败来自断言。

- [ ] **Step 3: DefaultDisabledKeywords 加 PG 分支**

修改 `src/McpDbTools.Server/Configuration/DefaultDisabledKeywords.cs`，`BuiltInByType`（约 :35-64）加分支：

```csharp
[DatabaseType.PostgreSql] = new[]
{
    "COPY",
    "VACUUM",
    "REINDEX",
    "CLUSTER",
    "REFRESH MATERIALIZED VIEW",
    "ANALYZE",
    "NOTIFY"
}
```

- [ ] **Step 4: 跑测试确认通过**

Run 同 Step 2 命令。Expected: PASS。

- [ ] **Step 5: Commit（需用户授权）**

```bash
git add src/McpDbTools.Server/Configuration/DefaultDisabledKeywords.cs \
  src/McpDbTools.Tests/ConfigMergeTests.cs
git commit -m "feat(pg): DefaultDisabledKeywords 增 PostgreSQL 按类型阻止关键字"
```

---

## Task 5: 白名单——SqlGuard PG 白名单

**Files:**
- Modify: `src/McpDbTools.Server/Security/SqlGuard.cs:50-68`
- Test: `src/McpDbTools.Tests/SqlGuardTests.cs`（追加 PG 白名单/黑名单测试）

**Interfaces:**
- Consumes: Task 1 枚举；Task 4 的 `DefaultDisabledKeywords.BuiltInByType[PostgreSql]`（`SqlGuardTests.Db` helper 依赖）。
- Produces: `SqlGuard.WhitelistByType[PostgreSql]`。运行时 `db_query` 的 PG 校验依赖此。

- [ ] **Step 1: 写失败测试**

在 `src/McpDbTools.Tests/SqlGuardTests.cs` 的 `SqlGuardTests` 类内追加（复用现有 `Db(DatabaseType)` helper）：

```csharp
[Theory]
[InlineData("SELECT * FROM Users", true)]
[InlineData("select id from t where x = 1", true)]            // 小写也放行
[InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte", true)]
[InlineData("CALL my_proc()", true)]
[InlineData("EXPLAIN SELECT * FROM Users", true)]
[InlineData("SHOW server_version", true)]
[InlineData("TABLE Users", true)]
[InlineData("VALUES (1), (2)", true)]
[InlineData("COPY t FROM '/etc/passwd'", false)]              // PG 黑名单拦 COPY
[InlineData("VACUUM Users", false)]                            // PG 黑名单拦 VACUUM
[InlineData("REFRESH MATERIALIZED VIEW v", false)]            // 多词关键字
[InlineData("DELETE FROM Users", false)]                      // 全局黑名单
[InlineData("EXEC prepared_stmt", false)]                     // PG 白名单不含 EXECUTE/EXEC
public void PostgreSql_DialectSpecific(string sql, bool expected)
{
    Assert.Equal(expected, _guard.Validate(sql, Db(DatabaseType.PostgreSql)).Allowed);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj --filter "FullyQualifiedName~SqlGuardTests.PostgreSql_DialectSpecific"
```
Expected: FAIL。Task 4 已为 PG 加 `BuiltInByType` 键，故 `SqlGuardTests.Db(PostgreSql)` helper 可正常构造（不抛）；但 `SqlGuard.WhitelistByType` 尚无 PG 键 → `Validate` 中 `WhitelistByType.TryGetValue(...)` 返回 false → 进入 Deny 分支，所有语句被拒。放行类（`SELECT`/`CALL`/`EXPLAIN` 等期望 true）实际为 false → FAIL；拦截类（`COPY`/`DELETE` 等期望 false）碰巧 PASS。Theory 整体 FAIL。

- [ ] **Step 3: SqlGuard 加 PG 白名单分支**

修改 `src/McpDbTools.Server/Security/SqlGuard.cs`，`WhitelistByType`（约 :50-68）加分支：

```csharp
[DatabaseType.PostgreSql] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "SELECT", "WITH", "CALL", "EXPLAIN", "SHOW", "TABLE", "VALUES"
}
```

- [ ] **Step 4: 跑测试确认通过**

Run 同 Step 2 命令。Expected: 13 条 InlineData 全部 PASS。

- [ ] **Step 5: Commit（需用户授权）**

```bash
git add src/McpDbTools.Server/Security/SqlGuard.cs \
  src/McpDbTools.Tests/SqlGuardTests.cs
git commit -m "feat(pg): SqlGuard 增 PostgreSQL 白名单（SELECT/WITH/CALL/EXPLAIN/SHOW/TABLE/VALUES）"
```

---

## Task 6: Admin 前端——类型下拉框加 PostgreSQL 选项

**Files:**
- Modify: `src/McpDbTools.Server/wwwroot/admin/scripts/projects.js:192-194`
- 验证: 手动浏览器（无前端自动化测试框架）

**Interfaces:**
- Consumes: Task 3 的 AdminConfigService（保存 PG type）。前端通过下拉框 `value="postgresql"` 与后端交互。
- Produces: Admin UI 可视化选择 PostgreSQL 类型。

- [ ] **Step 1: 加 option**

修改 `src/McpDbTools.Server/wwwroot/admin/scripts/projects.js`，在现有三个 `<option>`（约 :192-194）后加一行：

```html
<option value="sqlserver">SqlServer</option>
<option value="mysql">MySQL</option>
<option value="oracle">Oracle</option>
<option value="postgresql">PostgreSQL</option>
```

默认值 `'sqlserver'`（约 :438、:505）不动。

- [ ] **Step 2: 静态确认**

Run:
```bash
grep -n 'value="postgresql"' src/McpDbTools.Server/wwwroot/admin/scripts/projects.js
```
Expected: 输出含 `value="postgresql"` 的行。

- [ ] **Step 3: 手动验证（合并到 Task 7 冒烟，或本地启动 Admin UI）**

启动服务（见 Task 7 Step 2），打开 `http://127.0.0.1:5123/admin` → 项目管理 → 新增/编辑环境 → 类型下拉框出现「PostgreSQL」选项 → 选择后填写 PG 连接串保存 → 重新加载页面，类型仍显示 PostgreSQL。

- [ ] **Step 4: Commit（需用户授权）**

```bash
git add src/McpDbTools.Server/wwwroot/admin/scripts/projects.js
git commit -m "feat(pg): Admin UI 类型下拉框增 PostgreSQL 选项"
```

---

## Task 7: 全量构建、测试与发布验证

**Files:** 无代码改动（验证 task）

**Interfaces:** 消费 Task 1-6 全部产出。

- [ ] **Step 1: 全量测试**

Run:
```bash
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj
```
Expected: 全部测试 PASS，无回归。重点确认新增测试：`ConfigMergeTests`（3+1 个 PG）、`DatabaseProviderFactoryTests`（3 个）、`AdminConfigServiceTests.Save_PreservesPostgreSqlType`、`SqlGuardTests.PostgreSql_DialectSpecific`（13 条）。

- [ ] **Step 2: 本地启动 + 手动冒烟（依赖外部 PG 实例）**

启动：
```bash
dotnet run --project src/McpDbTools.Server/McpDbTools.Server.csproj -- --admin-port 5123
```

浏览器 `http://127.0.0.1:5123/admin` 验证：
- 类型下拉可选 PostgreSQL（Task 6）。
- 配置一个 PG 环境（真实连接串）→ 保存 → 测试连接返回成功与耗时。
- 审计日志页查询 PG 时类型显示「PostgreSQL」。

MCP 客户端连 `http://127.0.0.1:5123/mcp`，调 `db_query` 对真实 PG 执行：
- `SELECT version();` → 返回正确结果。
- `COPY t FROM '/x'` → 返回 `SQL_BLOCKED`（Task 4/5 生效）。

- [ ] **Step 3: 单文件发布验证**

Run:
```bash
dotnet publish src/McpDbTools.Server/McpDbTools.Server.csproj -c Release
```
Expected: 发布成功，发布目录含 Npgsql 相关程序集（与 native 依赖，若有）。

- [ ] **Step 4: 干净环境冒烟（验证 native 依赖内嵌）**

在无开发环境的机器上运行发布产物，连接真实 PG 跑一条 `SELECT`，确认 Npgsql native 依赖（若有）被 `IncludeNativeLibrariesForSelfExtract` 正确内嵌、可加载。

> 此步是实施方案中标注的唯一非确定性风险点。若失败，排查 Npgsql 是否需要额外 native 依赖或发布参数调整。

- [ ] **Step 5: 更新实施方案状态**

将 `doc/20260730-PostgreSQL支持.md` 顶部「状态：待确认」改为「已完成」，补充实际 Npgsql 版本号与冒烟结果。

- [ ] **Step 6: Commit（需用户授权）**

```bash
git add doc/20260730-PostgreSQL支持.md
git commit -m "docs(pg): PostgreSQL 支持实施完成，更新状态"
```

---

## 验证矩阵

| spec 要求 | 覆盖 task | 自动化测试 |
|-----------|-----------|------------|
| 枚举 + JSON converter | Task 1 | `DatabaseType_PostgreSql_JsonRoundtrip` |
| 连接串拼接（Maximum Pool Size/Timeout） | Task 1 | `ConnectionString_AppendedWithPoolAndTimeout_PostgreSql` |
| DatabaseName 提取 | Task 1 | `DatabaseName_Resolved_PostgreSql` |
| PostgreSqlProvider + 工厂 | Task 2 | `DatabaseProviderFactoryTests`（3 个） |
| AdminConfigService 解析/序列化/校验 | Task 3 | `Save_PreservesPostgreSqlType` |
| 按类型阻止关键字（7 个） | Task 4 | `FallsBackToBuiltin_PostgreSqlKeywords` |
| 白名单（7 个关键字） | Task 5 | `PostgreSql_DialectSpecific`（13 条） |
| Admin UI 下拉框 | Task 6 | 手动（Step 2 grep + Step 3 浏览器） |
| 单文件发布兼容性 | Task 7 | 手动冒烟（Step 3-4） |
| 真实 PG 查询/守卫 | Task 7 | 手动冒烟（Step 2，依赖外部 PG） |
