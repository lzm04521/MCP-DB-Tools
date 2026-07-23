# db_list 环境详情新增 databaseName 字段

## 目标

`db_list` 返回的环境详情新增 `databaseName` 字段，让 Agent 一眼知道"当前连接默认落在哪个库/schema"，不必因信息不明而发 `USE` 切库语句。

现状：[ProjectListBuilder.BuildEnvironment](../src/McpDbTools.Server/Tools/ProjectListBuilder.cs#L80) 返回 8 个字段（name/type/isProduction/maxRows/maxConcurrency/maxPoolSize/connectTimeoutSeconds/commandTimeout），无库名/用户名标识。

## 范围

- [src/McpDbTools.Server/Configuration/ResolvedConfig.cs](../src/McpDbTools.Server/Configuration/ResolvedConfig.cs) — `ResolvedDatabase` 加字段、`ResolvedConfigBuilder` 解析填充、新增私有 helper
- [src/McpDbTools.Server/Tools/ProjectListBuilder.cs](../src/McpDbTools.Server/Tools/ProjectListBuilder.cs) — `BuildEnvironment` 输出新字段
- [src/McpDbTools.Tests](../src/McpDbTools.Tests/) — 新增/扩展解析与 db_list 用例
- **不动**：Admin UI（DTO/tab/卡片）、db_query 返回、config.json 模型

## 字段语义（按类型分流）

单一字段 `databaseName`，语义随 `type` 变：

| type | 取连接串键 | 含义 |
|---|---|---|
| sqlserver | `Initial Catalog`（缺则 `Database`） | 当前默认库 |
| mysql | `Database` | 当前默认 schema/库（与 sqlserver 同位，真正防 `USE`） |
| oracle | `User Id` | schema（Oracle 里 user 即 schema，Agent 查表默认落此） |

字段恒出现，值可为 `null`（键缺失/解析失败/空白）。

## 实施步骤

### 1. ResolvedDatabase 加字段

[ResolvedConfig.cs:38](../src/McpDbTools.Server/Configuration/ResolvedConfig.cs#L38) `ResolvedDatabase` record 新增：

```csharp
/// <summary>
/// 当前连接默认库/schema 标识，供 db_list 返回给 Agent，避免 USE 切库。
/// 按类型语义：SqlServer=Initial Catalog；MySQL=Database；Oracle=User Id。
/// 键缺失/解析失败/空白 → null。
/// </summary>
public required string? DatabaseName { get; init; }
```

### 2. ResolvedConfigBuilder.Build 解析填充

[ResolvedConfig.cs:128](../src/McpDbTools.Server/Configuration/ResolvedConfig.cs#L128) 构造 `ResolvedDatabase` 处加一行：

```csharp
DatabaseName = ResolveDatabaseName(db.ConnectionString, db.Type),
```

解析用 `db.ConnectionString`（用户原始配置串），与 `BuildConnectionString` 同阶段、同 try 风格。

新增私有 helper：

```csharp
/// <summary>
/// 从原始连接串提取默认库/schema 标识（按类型分流）。
/// 畸形串/键缺失/空白 → null，不抛、不阻断列表。
/// 只读单属性，绝不输出连接串整体或 Password 键。
/// </summary>
private static string? ResolveDatabaseName(string raw, DatabaseType type)
{
    try
    {
        string? v = type switch
        {
            DatabaseType.SqlServer => new SqlConnectionStringBuilder(raw).InitialCatalog,
            DatabaseType.MySql     => new MySqlConnectionStringBuilder(raw).Database,
            DatabaseType.Oracle    => new OracleConnectionStringBuilder(raw).UserID,
            _ => null
        };
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    catch
    {
        // 畸形连接串：保留 null，运行时由驱动报错；此处不阻断列表
        return null;
    }
}
```

> 实施时确认驱动属性名：`SqlConnectionStringBuilder.InitialCatalog`、`MySqlConnectionStringBuilder.Database`、`OracleConnectionStringBuilder.UserID`。前两者标准确定；Oracle 属性在 Oracle.ManagedDataAccess 为 `UserID`（接受 `User Id` 键）。实施时若编译器纠正以实际为准。

### 3. ProjectListBuilder.BuildEnvironment 输出字段

[ProjectListBuilder.cs:80](../src/McpDbTools.Server/Tools/ProjectListBuilder.cs#L80) 匿名对象追加：

```csharp
databaseName = e.Value.DatabaseName,
```

一处改全覆盖：成功响应（单环境/全环境）与 `ENVIRONMENT_NOT_FOUND` 兜底均经此构造。

## Dedupe Ticket

**Intent signature**：`从连接串按 DatabaseType 提取默认库/schema 名 → string?`。

**Queries**：
- 项目内是否已有"从连接串解析 catalog/user"的等价 helper？→ Grep `InitialCatalog|\.Database|UserID|Initial Catalog` 全项目。
- 是否已有 `ResolvedDatabase` 上的同类展示字段？→ 现有字段：Environment/IsProduction/Type/ConnectionString/MaxRows 等（[ResolvedConfig.cs:38-69](../src/McpDbTools.Server/Configuration/ResolvedConfig.cs#L38-L69)），无库名标识。

**Top matches**：
- `BuildConnectionString`（[ResolvedConfig.cs:163](../src/McpDbTools.Server/Configuration/ResolvedConfig.cs#L163)）—— 已用三驱动 `DbConnectionStringBuilder` 写入 pool/timeout，但不读取 catalog/user，职责不同。

**Decision**：新增私有 `ResolveDatabaseName`，不复用 `BuildConnectionString`（一写一读，职责正交）。

**Rationale**：无等价读取实现；helper 单一职责（按类型分流只读单键），与写连接串逻辑分离更清晰。

## 安全

- 只读 catalog/user/schema 专用属性，**绝不返回连接串整体或 `Password` 键**。
- Oracle `User Id` 即 schema 名，是 Agent 必需标识；非凭据。密码不碰。
- 字段值原样返回，不脱敏（库名/schema 名为元数据，非机密）。

## 错误与边界

| 情况 | 行为 |
|---|---|
| 键存在有值 | 返回原值 |
| 键缺失 | `null` |
| 空白值 | 归一为 `null` |
| 畸形连接串 | catch → `null`，不阻断列表、不影响查询 |
| 跨类型 | switch 单类型单键，无交叉污染 |

## 测试

[src/McpDbTools.Tests](../src/McpDbTools.Tests/) 新增/扩展：

| 用例 | 输入 | 期望 |
|---|---|---|
| SqlServer `Initial Catalog` | `Server=...;Initial Catalog=OrderDb;...` | `OrderDb` |
| SqlServer `Database` 键 | `Server=...;Database=OrderDb;...` | `OrderDb`（builder 归一） |
| Oracle `User Id` | `Data Source=ORCL;User Id=APP;...` | `APP` |
| MySQL `Database` | `Server=...;Database=crm;...` | `crm` |
| 键缺失 | `Server=...;`（无 catalog/database/user） | `null` |
| 空白值 | `Initial Catalog=  ;...` | `null` |
| 畸形串 | `not a connection string` | `null`，不抛 |
| db_list 输出 | 任一有效配置 | JSON 环境详情含 `databaseName` 字段 |

测试驱动属性名以实际编译为准；若属性名差异，spec 同步修正。

## 验证

```bash
dotnet build src/McpDbTools.Server/McpDbTools.Server.csproj
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj --filter "FullyQualifiedName~ResolvedConfigBuilder|~DbListTool"
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj
```

全绿 + 既有 db_list/config 合并用例不回归。

## 回滚

两源码文件 + 测试。`git revert` 本分支提交即可，无数据/配置迁移、无 schema 变更。

## 未覆盖

- Admin UI 不显示库名（连接串在 Admin 已可编辑，库名对管理员无新信息；YAGNI）。
- db_query 返回不加 `databaseName`（Agent 调用前已用 db_list 拿到）。
- config.json 不加手填覆盖字段（解析足够；YAGNI）。
- 真实外部数据库连通验证不在范围（单元测试覆盖解析层）。
