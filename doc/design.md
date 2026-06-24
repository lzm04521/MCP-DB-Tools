# MCP Database Tools — 设计文档

> 版本：v1.0 | 日期：2026-06-23 | 状态：待评审

---

## 1. 项目目标

为 Claude Code 提供一个 MCP (Model Context Protocol) 数据库访问工具，使 AI 能够安全、只读地查询 Oracle、SQL Server、MySQL 数据库，并以 AI 友好格式返回结果。

## 2. 技术选型

| 维度 | 选择 | 理由 |
|------|------|------|
| 运行时 | **.NET 8** | 低于 .NET 9 的最新 LTS，三方库兼容性最好 |
| MCP SDK | **ModelContextProtocol** | 微软官方 .NET MCP 包，与 ASP.NET Core 深度集成 |
| SQL Server | **Microsoft.Data.SqlClient** | 微软官方 ADO.NET 驱动 |
| MySQL | **MySqlConnector** | 纯 .NET 实现，性能优于 Oracle 官方驱动 |
| Oracle | **Oracle.ManagedDataAccess.Core 3.21.x** | 需兼容 Oracle 11g R2+，3.21.x 是 Oracle 21c 驱动分支（nuget 版本号格式 3.21.*），最后一个支持 11g 的分支 |
| 配置 | **System.Text.Json + FileSystemWatcher** | 轻量，无额外依赖 |
| 日志 | **Microsoft.Extensions.Logging** | 标准 .NET 日志抽象 |

## 3. 项目结构

```
mcp-db-tools/
├── doc/
│   └── design.md                        # 本文件
├── src/
│   └── McpDbTools.Server/               # 主项目（控制台应用）
│       ├── Program.cs                   # 入口，注册 MCP Server
│       ├── McpDbTools.Server.csproj
│       ├── appsettings.json             # 应用基础配置
│       ├── Configuration/
│       │   ├── DatabaseConfig.cs        # 配置 POCO 模型
│       │   ├── DatabasesConfig.cs       # 配置根模型
│       │   └── ConfigWatcher.cs         # 配置文件热重载
│       ├── Database/
│       │   ├── IDatabaseProvider.cs     # 数据库提供者接口
│       │   ├── SqlServerProvider.cs     # SQL Server 实现
│       │   ├── MySqlProvider.cs         # MySQL 实现
│       │   ├── OracleProvider.cs        # Oracle 实现
│       │   └── DatabaseProviderFactory.cs # 提供者工厂
│       ├── Security/
│       │   └── SqlGuard.cs              # SQL 安全守卫（白名单校验）
│       ├── Audit/
│       │   └── AuditLogger.cs           # 审计日志（JSONL 格式，支持轮转和过期清理）
│       └── Tools/
│           └── DbQueryTool.cs           # MCP Tool 定义与执行
├── config.json                          # 数据库连接配置（热重载目标）
└── README.md
```

## 4. 核心设计

### 4.1 配置文件设计 (`config.json`)

```jsonc
{
  // ═══════════════════════════════════════════════
  // 第一层：全局通用阻止关键字（所有数据库类型生效）
  // ═══════════════════════════════════════════════
  "defaultDisabledKeywords": [
    "DROP", "DELETE", "UPDATE", "INSERT", "ALTER", "CREATE", "TRUNCATE",
    "MERGE", "GRANT", "REVOKE", "REPLACE",
    "BACKUP", "RESTORE", "KILL", "SHUTDOWN"
  ],

  // ═══════════════════════════════════════════════
  // 第二层：按数据库类型追加（可选，未配置时为空）
  // ═══════════════════════════════════════════════
  "defaultDisabledKeywordsByType": {
    "sqlserver": ["BULK INSERT", "OPENROWSET", "OPENDATASOURCE", "xp_cmdshell", "sp_configure"],
    "mysql":    ["LOAD DATA", "FLUSH", "OPTIMIZE", "REPAIR", "CHECKSUM", "HANDLER"],
    "oracle":   ["FLASHBACK", "PURGE", "ALTER SYSTEM", "ALTER DATABASE", "AUDIT", "NOAUDIT"]
  },

  // ═══════════════════════════════════════════════
  // 审计日志配置
  // ═══════════════════════════════════════════════
  "audit": {
    "enabled": true,
    "logPath": "logs/audit.log",
    "maxFileSizeMB": 10,
    "maxRetentionDays": 30
  },

  "databases": {
    // 项目名 → 数据库配置
    "erp-system": {
      "type": "sqlserver",
      "connectionString": "Server=.;Database=ERP;Trusted_Connection=true;TrustServerCertificate=true;",
      "maxRows": 1000,
      "commandTimeout": 30,
      "disabledKeywords": []          // 第三层：项目额外阻止关键字（叠加到前两层之上）
    },
    "crm-mysql": {
      "type": "mysql",
      "connectionString": "Server=localhost;Database=CRM;User=root;Password=xxx;",
      "maxRows": 500,
      "commandTimeout": 30
    },
    "finance-oracle": {
      "type": "oracle",
      "connectionString": "Data Source=ORCL;User Id=scott;Password=tiger;",
      "maxRows": 2000,
      "commandTimeout": 60
    }
  }
}
```

**配置项说明：**

**顶级配置：**

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `defaultDisabledKeywords` | string[] | 否 | 第一层：全局通用阻止关键字，所有项目生效。未提供时使用内置默认值 |
| `defaultDisabledKeywordsByType` | object | 否 | 第二层：按数据库类型（`sqlserver`/`mysql`/`oracle`）追加阻止关键字。未提供时为空 |
| `audit` | object | 否 | 审计日志配置，见下方"审计日志"节 |
| `databases` | object | 是 | 项目名 → 数据库配置的映射表 |

**审计日志配置（`audit`）：**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `audit.enabled` | bool | `false` | 是否启用审计日志 |
| `audit.logPath` | string | `logs/audit.log` | 日志文件路径（相对于程序运行目录） |
| `audit.maxFileSizeMB` | int | `10` | 单个日志文件最大大小（MB），超出后轮转 |
| `audit.maxRetentionDays` | int | `30` | 日志文件保留天数，超期自动清理 |

**项目级配置（`databases.<项目名>`）：**

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `type` | enum | 是 | `sqlserver` / `mysql` / `oracle` |
| `connectionString` | string | 是 | ADO.NET 连接字符串 |
| `maxRows` | int | 否 | 最大返回行数，默认 1000 |
| `commandTimeout` | int | 否 | 命令超时（秒），默认 30 |
| `disabledKeywords` | string[] | 否 | 第三层：项目额外阻止关键字，**叠加到全局默认 + 按类型默认之上** |

### 4.2 MCP Tool 参数设计

```
Tool Name: db_query
Description: 在指定数据库中执行只读 SQL 查询，返回 AI 友好的结构化结果
```

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `project` | string | 是 | 项目名，对应 config.json 中的数据库配置键 |
| `sql` | string | 是 | 要执行的 SQL 语句（仅允许只读操作） |
| `limit` | int | 否 | 覆盖配置中的 maxRows，临时限制返回行数 |

### 4.3 SQL 安全守卫 (SqlGuard)

**默认白名单（按数据库类型区分）：**

| 允许的操作 | SQL Server | MySQL | Oracle |
|-----------|------------|-------|--------|
| SELECT 查询 | ✅ | ✅ | ✅ |
| WITH (CTE) | ✅ | ✅ | ✅ |
| EXEC / EXECUTE (存储过程) | ✅ | ✅ | ✅ |
| SHOW (表/字段/状态) | ❌ | ✅ | ❌ |
| DESCRIBE / DESC | ❌ | ✅ | ✅ |
| EXPLAIN | ❌ | ✅ | ❌ |
| sp_help / sp_tables / sp_columns | ✅ | ❌ | ❌ |
| INFORMATION_SCHEMA 查询 | ✅ | ✅ | ✅ |
| ALL_TABLES / USER_TABLES 等数据字典 | ❌ | ❌ | ✅ |

**默认阻止（所有数据库通用，可通过配置文件 `defaultDisabledKeywords` 覆盖）：**

```
DROP, DELETE, UPDATE, INSERT, ALTER, CREATE, TRUNCATE, 
MERGE, GRANT, REVOKE, REPLACE, LOAD, IMPORT, 
BACKUP, RESTORE, KILL, SHUTDOWN, SET (部分)
```

**阻止关键字合并逻辑（三层叠加）：**

```
最终阻止列表 = 第一层: defaultDisabledKeywords（未配置则使用内置默认值）
               ∪ 第二层: defaultDisabledKeywordsByType[项目数据库类型]（未配置则为空）
               ∪ 第三层: 项目.disabledKeywords（可为空）
```

即：全局通用 → 按数据库类型追加 → 按项目追加，层层递进。下层不能缩减上层已定义的阻止关键字。

**校验流程：**

```
用户输入 SQL → 去除注释 → 规范化空白 → 提取首个关键字
  → 判断是否为 WITH (CTE) → 提取 CTE 后的第一个关键字
  → 判断是否为 EXEC → 允许（仅限存储过程/函数/包）
  → 判断是否为 SELECT → 允许
  → 判断是否为 SHOW/DESCRIBE/EXPLAIN → 检查数据库类型白名单
  → 其他 → 拒绝
```

### 4.4 数据库提供者接口

```csharp
public interface IDatabaseProvider
{
    /// <summary>数据库类型标识</summary>
    string DatabaseType { get; }

    /// <summary>执行查询并返回结果</summary>
    Task<QueryResult> ExecuteQueryAsync(string sql, int maxRows, int timeoutSec, CancellationToken ct);

    /// <summary>测试连接是否正常</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct);
}
```

### 4.5 返回格式设计（AI 友好）

**成功时：**

```json
{
  "success": true,
  "project": "erp-system",
  "databaseType": "sqlserver",
  "rowCount": 42,
  "maxRows": 1000,
  "truncated": false,
  "executionTimeMs": 125,
  "columns": ["Id", "Name", "CreatedAt"],
  "rows": [
    [1, "张三", "2024-01-15"],
    [2, "李四", "2024-03-22"]
  ]
}
```

**错误时：**

```json
{
  "success": false,
  "project": "erp-system",
  "error": "SQL 语句被拒绝: DROP 操作不允许",
  "errorCode": "SQL_BLOCKED"
}
```

**设计要点：**
- `columns` 和 `rows` 分离：columns 只传一次，避免每行重复键名，节省 token
- `rows` 使用二维数组而非对象数组：进一步压缩 token 消耗
- `truncated` 标记：明确告知 AI 结果被截断，避免基于不完整数据做判断
- `executionTimeMs`：方便 AI 判断查询性能

### 4.6 热重载机制

```
启动时加载 config.json
  → 启动 FileSystemWatcher 监听 config.json 变更
  → 文件变更时重新读取并原子替换内存配置
  → 日志记录配置变更事件（含时间戳）
  → 变更期间正在执行的查询不受影响（使用旧配置完成）
```

### 4.7 审计日志

启用审计日志后，每次 SQL 执行均记录到文件，格式如下：

```jsonl
{"time":"2026-06-23T10:30:01Z","project":"erp-system","type":"sqlserver","sql":"SELECT * FROM Users WHERE Id=1","rowCount":1,"elapsedMs":45,"success":true}
{"time":"2026-06-23T10:31:15Z","project":"erp-system","type":"sqlserver","sql":"DROP TABLE Users","error":"SQL_BLOCKED","elapsedMs":0,"success":false}
```

**设计要点：**
- 每行一条 JSON（JSONL 格式），便于 `grep` / `jq` 分析
- 记录内容：时间、项目名、数据库类型、SQL 原文、返回行数、耗时、成功/失败
- 文件轮转：按 `maxFileSizeMB` 自动轮转，旧文件加 `.1` `.2` 后缀
- 过期清理：启动时和轮转时检查并删除超过 `maxRetentionDays` 天的日志文件
- 失败查询也记录：被阻止的 SQL、连接失败、超时等均有审计痕迹

### 4.8 连接池策略

ADO.NET 内置连接池默认开启，相关行为：

- **连接复用**：相同连接字符串的连接自动池化，查询结束后连接归还池中
- **池大小**：使用 ADO.NET 默认值（`Max Pool Size=100`），无需手动调整
- **MCP 场景适配**：每次 MCP 调用是独立的 stdio 进程，进程内连接池对单次调用（通常 1-3 条 SQL）已足够

结论：**不引入自定义连接池**，依赖 ADO.NET 内置池化即可。

## 5. 错误码设计

| 错误码 | 说明 |
|--------|------|
| `PROJECT_NOT_FOUND` | 指定的项目名在配置中不存在 |
| `SQL_BLOCKED` | SQL 语句被安全策略阻止 |
| `SQL_PARSE_ERROR` | SQL 语句语法解析失败 |
| `CONNECTION_FAILED` | 数据库连接失败 |
| `QUERY_TIMEOUT` | 查询超时 |
| `QUERY_ERROR` | 查询执行错误（通用） |
| `CONFIG_INVALID` | 配置文件格式错误 |
| `DATABASE_TYPE_UNSUPPORTED` | 不支持的数据库类型 |

## 6. 依赖项 (NuGet)

| 包名 | 版本 | 用途 |
|------|------|------|
| `ModelContextProtocol` | 0.2.* | MCP Server SDK |
| `Microsoft.Data.SqlClient` | 5.2.* | SQL Server 驱动 |
| `MySqlConnector` | 2.3.* | MySQL 驱动 |
| `Oracle.ManagedDataAccess.Core` | 3.21.* | Oracle 驱动（兼容 11g R2+，Oracle 21c 分支） |
| `Microsoft.Extensions.Logging.Console` | 8.0.* | 控制台日志 |

## 7. MCP 客户端配置（Claude Code 侧）

安装后，在 Claude Code 的 MCP 配置中添加：

```json
{
  "mcpServers": {
    "db-tools": {
      "command": "dotnet",
      "args": ["run", "--project", "path/to/McpDbTools.Server.csproj"],
      "env": {}
    }
  }
}
```

或编译后：

```json
{
  "mcpServers": {
    "db-tools": {
      "command": "path/to/McpDbTools.Server.exe",
      "args": [],
      "env": {}
    }
  }
}
```

## 8. 开发阶段

| 阶段 | 内容 | 产出 |
|------|------|------|
| Phase 1 | 配置模型 + 热重载 | `Configuration/` 模块 |
| Phase 2 | SQL 安全守卫 | `Security/SqlGuard.cs` |
| Phase 3 | 数据库提供者（3 种） | `Database/` 模块 |
| Phase 4 | MCP Tool 集成 | `Tools/DbQueryTool.cs` + `Program.cs` |
| Phase 5 | 测试与文档 | 集成测试 + README |

## 9. 已确认决策

| # | 事项 | 决策 | 说明 |
|---|------|------|------|
| 1 | Oracle 11g 兼容 | ✅ 需要 | 驱动降级到 Oracle.ManagedDataAccess.Core 21.x |
| 2 | 存储过程传参 | ❌ 暂不支持 | 用户在 SQL 文本中直接拼接参数 |
| 3 | 跨库 JOIN 查询 | ❌ 暂不支持 | 每个项目对应一个数据库，独立连接 |
| 4 | 审计日志 | ✅ 支持 | 见 4.7 节，JSONL 格式，支持开关、轮转、过期清理 |
| 5 | 连接池 | 不引入自定义 | 依赖 ADO.NET 内置连接池，见 4.8 节 |

---

> 以上设计已确认，可以开始开发。