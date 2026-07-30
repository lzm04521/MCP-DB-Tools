# 横向扩展支持 PostgreSQL

- 日期：2026-07-30
- 状态：待确认
- 范围：
  - `src/McpDbTools.Server/Configuration/DatabaseConfig.cs`
  - `src/McpDbTools.Server/Configuration/DatabaseTypeJsonConverter.cs`
  - `src/McpDbTools.Server/Configuration/DefaultDisabledKeywords.cs`
  - `src/McpDbTools.Server/Configuration/ResolvedConfig.cs`
  - `src/McpDbTools.Server/Database/DatabaseProviderFactory.cs`
  - `src/McpDbTools.Server/Database/PostgreSqlProvider.cs`（新增）
  - `src/McpDbTools.Server/Security/SqlGuard.cs`
  - `src/McpDbTools.Server/Admin/AdminConfigService.cs`
  - `src/McpDbTools.Server/wwwroot/admin/scripts/projects.js`
  - `src/McpDbTools.Server/McpDbTools.Server.csproj`
  - `src/McpDbTools.Tests`（新增测试）

## 目标

新增第四种数据库类型 PostgreSQL，与现有 SqlServer / MySQL / Oracle 完全对等：配置解析、连接串拼接、provider 查询、SqlGuard 白名单、按类型阻止关键字、Admin UI 可配置、测试连接、审计全链路支持。

## 背景：扩展面的对称性

数据库类型扩展点已被架构收敛到一处枚举 + 若干对称的 switch / dict。`DatabaseType.{SqlServer,MySql,Oracle}` 全仓 grep 命中点即全部扩展点，无散落在业务主流程的硬编码：

- 查询主流程 `Tools/DbQueryTool.cs`、`Tools/DbListTool.cs` 无任何 `DatabaseType` 分支（类型无关，不动）。
- `DatabaseProviderBase`（`IDatabaseProvider.cs:30-147`）是通用 ADO.NET 骨架：连接、命令、DataReader、超时兜底、并发、行截断全在基类。新 provider 只需重写 `CreateConnection` 一个方法。
- 三层阻止关键字合并（`ResolvedConfigBuilder`）、`QueryConcurrencyLimiter`、`AuditLogger`、测试连接（`DatabaseProviderBase.TestConnectionAsync`）全类型无关。
- 现有三个 provider 实现均 ~15 行、极度对称。

结论：本次扩展是机械式、对称、低风险的改动，唯一需要实际环境验证的是单文件发布兼容性。

## 决策点（已与需求方确认）

1. **驱动包**：`Npgsql`（官方、唯一主流选择，纯托管 + net8.0 兼容）。无公司层面依赖约束。
2. **白名单首关键字**：`SELECT, WITH, CALL, EXPLAIN, SHOW, TABLE, VALUES`。
   - 不加 `EXECUTE`：PG 里它是"执行 prepared statement"（动态 SQL 语义），与 MySQL/Oracle 的 `EXECUTE` 含义不同，风险高且不常用。
   - `WITH` 安全：即使 `WITH ... DELETE`，全局黑名单会拦 `DELETE`，与现有三库一致。
   - `CALL` 调用 PROCEDURE 可能写数据，但与现有 SqlServer 允许 `EXEC` 的姿态对称，靠黑名单 + `isProduction` 提示兜底。
3. **按类型阻止关键字**：`COPY, VACUUM, REINDEX, CLUSTER, REFRESH MATERIALIZED VIEW, ANALYZE, NOTIFY`。
   - 不加 `LISTEN`（仅注册通道，不写库）、`CHECKPOINT`（只读账号权限上本就拦得住，避免清单过长）。
4. **连接串键名**（Npgsql 技术事实，发布前以实测为准）：
   - 池上限键 `Maximum Pool Size`（默认 100）。
   - 连接超时键 **`Timeout`**（默认 15 秒），**不是** `Connection Timeout`，与 MySQL/Oracle 不同。
5. **`DatabaseName` 解析**：取 `NpgsqlConnectionStringBuilder.Database`。

## 改动

### 1. 枚举与序列化

- `DatabaseConfig.cs:9-14`：`DatabaseType` 加 `PostgreSql`。
- `DatabaseTypeJsonConverter.cs`：`Parse`（`:27-29`）加 `"postgresql" => DatabaseType.PostgreSql`；`ToJsonValue`（`:38-40`）加反向映射。
- `Admin/AdminConfigService.cs`：`SupportedTypes` 列表（`:13-15`）加 `DatabaseType.PostgreSql`；`ParseType`（`:622-628`）、`ToJsonValue`（`:638-640`）加 PG 分支。

### 2. 新增 `Database/PostgreSqlProvider.cs`

仿 `SqlServerProvider.cs`，~15 行：

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

`Database/DatabaseProviderFactory.cs:18-23`：字典加 `[DatabaseType.PostgreSql] = new PostgreSqlProvider()`。

### 3. SqlGuard 白名单（`Security/SqlGuard.cs:50-68`）

加分支：

```csharp
[DatabaseType.PostgreSql] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "SELECT", "WITH", "CALL", "EXPLAIN", "SHOW", "TABLE", "VALUES"
}
```

DBCC 子命令二次校验（`ValidateDbcc`）仅对 SqlServer 触发，PG 不涉及，无需改动。

### 4. 按类型阻止关键字（`Configuration/DefaultDisabledKeywords.cs:35-64`）

加分支：

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

多词关键字 `REFRESH MATERIALIZED VIEW` 由 `SqlGuard.BuildKeywordPattern` 用词边界整体匹配，已有支持。

### 5. 连接串拼接（`Configuration/ResolvedConfig.cs`）

- `BuildConnectionString`（`:176-190`）：
  - 驱动 switch 加 `DatabaseType.PostgreSql => new NpgsqlConnectionStringBuilder(raw)`。
  - 键名 switch 加 `DatabaseType.PostgreSql => ("Maximum Pool Size", "Timeout")`。
- `ResolveDatabaseName`（`:212-217`）：加 `DatabaseType.PostgreSql => new NpgsqlConnectionStringBuilder(raw).Database`。
- 文件头 using 加 `using Npgsql;`。

### 6. 驱动包（`McpDbTools.Server.csproj:20-30`）

加：

```xml
<PackageReference Include="Npgsql" Version="<最新稳定>" />
```

建议 8.x（与 net8.0 对应）；实施时确认 NuGet 最新稳定版本号。

### 7. Admin 前端（`wwwroot/admin/scripts/projects.js:192-194`）

下拉框加 `<option value="postgresql">PostgreSQL</option>`。默认值 `'sqlserver'`（`:505`）不动。

### 8. 测试（`src/McpDbTools.Tests`）

- **SqlGuard**：
  - PG 白名单放行：`SELECT 1`、`CALL proc()`、`EXPLAIN SELECT 1`、`WITH t AS (...) SELECT * FROM t`、`SHOW server_version`、`TABLE t`、`VALUES (1)`。
  - PG 按类型黑名单拦截：`COPY t FROM '/x'`（拦 `COPY`）、`VACUUM t`、`REFRESH MATERIALIZED VIEW v`。
  - 全局黑名单仍拦：`DELETE`/`UPDATE`/`INSERT` 等。
- **连接串拼接**（`ResolvedConfigBuilder`）：PG 分支注入后连接串含 `Maximum Pool Size` 与 `Timeout` 键。
- **工厂**：`DatabaseProviderFactory.Get(DatabaseType.PostgreSql)` 返回 `PostgreSqlProvider` 实例。

## 端到端数据流（确认类型无关环节无需改）

`db_query` → 解析项目/环境 → `ResolvedDatabase.Type=PostgreSql` → `SqlGuard`（PG 白名单 + 三层合并 PG 黑名单）→ `DatabaseProviderFactory.Get(PostgreSql)` → `PostgreSqlProvider.ExecuteQueryAsync`（走 `DatabaseProviderBase` 骨架）→ `NpgsqlConnection` → `AuditLogger` 写审计。`DbQueryTool` / `DbListTool` / `QueryConcurrencyLimiter` 不动。

## Dedupe Ticket：`PostgreSqlProvider`

- **Intent signature**：新增 PG 的 ADO.NET provider，继承 `DatabaseProviderBase`，封装 `NpgsqlConnection`，按 `DatabaseType.PostgreSql` 在工厂注册。
- **Queries**：`IDatabaseProvider` 实现、`DatabaseProviderBase` 子类、现有 provider 文件。
- **Top matches**：`SqlServerProvider.cs`、`MySqlProvider.cs`、`OracleProvider.cs`（均 ~15 行，各自绑定不同驱动的 `CreateConnection`）。
- **Decision**：不复用任一现有 provider（它们各自硬绑定不同驱动类型），新建 `PostgreSqlProvider` 遵循同一模式。
- **Rationale**：每种 DBMS 一个 provider 是既定架构（`DatabaseProviderFactory` 按 `DatabaseType` 分发）；无重复实现，不引入第二种风格。

## 不改动

- 查询主流程（`DbQueryTool` / `DbListTool`）。
- `DatabaseProviderBase` 骨架、`QueryConcurrencyLimiter`、`AuditLogger`、测试连接逻辑。
- 三层阻止关键字合并逻辑（`ResolvedConfigBuilder.Build`）。
- 现有 SqlServer / MySQL / Oracle 的 provider、白名单、黑名单。
- Admin UI 除类型下拉框外的全部页面。
- 配置路径、发布脚本（单文件发布参数不变，仅新增依赖）。

## 风险

- **单文件发布兼容性（唯一非确定性风险）**：项目用 `PublishSingleFile` + `SelfContained` + `IncludeNativeLibrariesForSelfExtract`（`csproj:13-17`）。Npgsql 在某些场景依赖 native 组件，需验证发布产物在干净机器上能跑。`IncludeNativeLibrariesForSelfExtract=true` 已开启，预期内嵌，但须实测。
- **Npgsql 连接串键名**：`Timeout` 与 `Maximum Pool Size` 基于 Npgsql 官方连接串参数文档，发布前以实际 `NpgsqlConnectionStringBuilder` 行为为准。
- **真实 PG 环境验证依赖外部**：单元测试只覆盖 guard / 工厂 / 连接串三层；真实 PG 查询冒烟依赖外部 PG 实例（符合 CLAUDE.md 关于"数据库连接类能力依赖真实环境"的声明）。

## 验证

- `dotnet build src/McpDbTools.Server/McpDbTools.Server.csproj`：构建通过（含新 Npgsql 引用）。
- `dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj`：新增测试通过，现有测试不回归。
- `dotnet publish src/McpDbTools.Server/McpDbTools.Server.csproj -c Release`：发布成功，产物含 Npgsql 相关程序集 / native 依赖。
- **冒烟（依赖外部 PG 实例）**：
  - Admin UI `/admin` → 项目管理：类型下拉可选 PostgreSQL；保存含 PG 环境的配置 → 重新加载配置正确显示。
  - 测试连接：对真实 PG 实例点「测试连接」返回成功与耗时。
  - MCP `db_query`：对真实 PG 执行 `SELECT version();`、`SELECT * FROM <表> LIMIT 10;` 返回正确结果；`COPY t FROM '/x'` 被 SqlGuard 拦截（`SQL_BLOCKED`）。
  - 审计日志页：PG 查询记录正确显示（类型 PostgreSQL）。
- 干净机器（无开发环境）运行发布产物，确认 Npgsql native 依赖正确加载。
