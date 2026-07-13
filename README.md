# MCP Database Tools

为支持 MCP（Model Context Protocol）的 Agent（如 [Claude Code](https://docs.anthropic.com/claude-code)、[Codex](https://developers.openai.com/codex)）提供数据库只读访问能力的工具。基于 .NET 8 + 官方 `ModelContextProtocol` SDK，支持 SQL Server、MySQL、Oracle，内置 SQL 安全守卫、多环境配置、配置热重载、每环境并发限流、审计日志，以及本机 Admin UI 配置维护页面。

## 功能特性

- **三数据库支持**：SQL Server、MySQL、Oracle（兼容 11g R2+）
- **多环境配置**：同一项目可维护 `dev` / `test` / `prod` 等多环境，设置默认环境
- **SQL 安全守卫**：白名单（只读语句）+ 三层黑名单双重校验，拦截多语句注入
- **配置热重载**：改 `config.json` 即时生效，无需重启
- **并发与连接池可控**：每个 `(project, env)` 独立并发闸门，避免高并发打满连接池
- **审计日志**：本地 SQLite 全局记录查询与阻止，支持自动/手动清理；可选记录查询结果（弹窗懒加载查看）
- **AI 友好返回**：columns 与 rows 分离，rows 用二维数组压缩 token
- **本机 Admin UI**：浏览器维护 `config.json`，含测试连接、备份管理、审计查看与全局设置

## 快速开始

```bash
git clone <repo>
cd mcp-db-tools
dotnet build
```

### 配置数据库

编辑 [src/McpDbTools.Server/config.json](src/McpDbTools.Server/config.json)，在 `databases` 下添加项目与环境：

```jsonc
{
  "databases": {
    "my-project": {
      "displayName": "示例项目",
      "defaultEnvironment": "test",
      "environments": {
        "test": {
          "displayName": "测试环境",
          "isProduction": false,
          "type": "sqlserver",
          "connectionString": "Server=.;Database=MyDb;Trusted_Connection=true;TrustServerCertificate=true;",
          "maxRows": 1000,
          "commandTimeout": 30,
          "disabledKeywords": []
        },
        "prod": {
          "displayName": "生产环境",
          "isProduction": true,
          "type": "sqlserver",
          "connectionString": "Server=prod;Database=MyDb;User Id=readonly;Password=***;TrustServerCertificate=true;",
          "maxRows": 500,
          "commandTimeout": 30,
          "disabledKeywords": []
        }
      }
    }
  }
}
```

> 程序默认读取 `%ProgramData%\McpDbTools\config.json`（Windows 跨用户共享数据目录，与程序目录分离便于升级；LocalSystem 服务与当前用户进程共享同一份数据），可用环境变量 `ConfigStore__ConfigPath` 覆盖。文件不存在时空配置启动，可后续通过 Admin UI 补齐。开发时若用源码目录下的 config.json，需显式设置该环境变量。

### 接入 MCP 客户端

本工具通过 MCP stdio 与 Agent 通信。下面给出 Claude Code 与 Codex 的配置示例，其它 MCP 客户端按各自文档以相同 command / args / env 接入即可。

> 建议先用 Admin UI 测试连接、确认配置无误，再接入客户端：`dotnet run --project src/McpDbTools.Server -- --admin-only`，打开日志中的 `http://127.0.0.1:5123/admin`。

#### Claude Code

Claude Code 在 `mcp.json`（项目级 `.mcp.json` 或用户级配置）中用 JSON 配置 `mcpServers`。

开发时直接用 `dotnet run`（指向源码 csproj，并指定配置文件）：

```json
{
  "mcpServers": {
    "db-tools": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "D:/GitHub/mcp-db-tools/src/McpDbTools.Server/McpDbTools.Server.csproj"
      ],
      "env": {
        "ConfigStore__ConfigPath": "D:/GitHub/mcp-db-tools/src/McpDbTools.Server/config.json"
      }
    }
  }
}
```

发布后推荐直接运行 exe（程序默认读取 `%ProgramData%\McpDbTools\config.json`，无需传 `ConfigStore__ConfigPath`）：

```json
{
  "mcpServers": {
    "db-tools": {
      "command": "D:/Tools/McpDbTools/McpDbTools.Server.exe",
      "args": []
    }
  }
}
```

#### Codex

[Codex](https://developers.openai.com/codex) 在 `~/.codex/config.toml`（或项目级 `.codex/config.toml`）中用 TOML 配置，每个 server 一个 `[mcp_servers.<name>]` 表，字段为 `command` / `args` / `env`。

开发时直接用 `dotnet run`：

```toml
[mcp_servers.db-tools]
command = "dotnet"
args = ["run", "--project", "D:/GitHub/mcp-db-tools/src/McpDbTools.Server/McpDbTools.Server.csproj"]

[mcp_servers.db-tools.env]
ConfigStore__ConfigPath = "D:/GitHub/mcp-db-tools/src/McpDbTools.Server/config.json"
```

发布后直接运行 exe（程序默认读取 `%ProgramData%\McpDbTools\config.json`）：

```toml
[mcp_servers.db-tools]
command = "D:/Tools/McpDbTools/McpDbTools.Server.exe"
args = []
```

也可以用 Codex CLI 一条命令添加（等效于上面发布后配置）：

```bash
codex mcp add db-tools -- D:/Tools/McpDbTools/McpDbTools.Server.exe
```

> Codex 默认工具执行超时 `tool_timeout_sec = 60` 秒。如果数据库查询可能较慢，可在 `[mcp_servers.db-tools]` 下追加 `tool_timeout_sec = 120` 调大。

#### 验证接入

重启客户端后，在对话中让 Agent：

1. 先调用 `db_list`（不传参数）查看可用项目；
2. 再用 `db_list(project="xxx")` 查看该项目环境；
3. 最后调用 `db_query` 执行只读查询。

## 运行模式

| 模式         | 参数           | 说明                                               |
| ------------ | -------------- | -------------------------------------------------- |
| MCP 模式     | 无参数         | 默认。启动 MCP stdio server，不启动 Admin UI       |
| Admin 模式   | `--admin-only` | 只启动本机 Admin Web 服务，不启动 MCP              |
| 调试混合模式 | `--admin`      | 同时启动两者，仅用于开发调试                       |

Admin UI 默认端口 `5123`（`--admin-port` 可改），只监听 `127.0.0.1`。首次访问 `/admin` 自动设置仅限该路径的 HttpOnly、SameSite=Strict 本机会话 cookie，secret 只存于进程内存。

```bash
ConfigStore__ConfigPath=D:/GitHub/mcp-db-tools/src/McpDbTools.Server/config.json \
  dotnet run --project src/McpDbTools.Server -- --admin-only --admin-port 5123
```

## Admin UI

浏览器打开启动日志中的地址（如 `http://127.0.0.1:5123/admin`）即可维护配置。功能分五个页面：

- **项目配置**（`#/projects`）：增删项目和环境，**key 创建后不可修改**；维护连接字符串、数据库类型、`maxRows`、`commandTimeout`、环境级并发/连接池参数与阻止关键字；内置测试连接（不落盘）。
- **全局关键字**（`#/keywords`）：维护全局默认与按类型追加的阻止关键字。
- **审计日志**（`#/audit-log`）：按项目/环境/类型/状态/时间/SQL 关键词筛选，分页查看，长文本点击弹窗复制。纯只读。
- **备份管理**（`#/backups`）：列出、下载、恢复（恢复前自动快照可撤销）、删除配置备份。
- **全局设置**（`#/settings`）：审计日志与备份文件的自动清理开关和保留天数；手动清理两者（按 10/20/30/50 天）。

写入安全：保存前自动备份当前 `config.json`，经临时文件校验后原子替换，避免 MCP 进程读到半写入文件。生产环境显示风险提示。保存会重写为标准 JSON，原注释与手工排版不保留。

## MCP 工具

### db_list

列出数据库项目与环境，**按需加载**避免环境多时返回数据量过大。建议查询前先调用。

| 参数          | 类型   | 必填 | 说明                                                                       |
| ------------- | ------ | ---- | -------------------------------------------------------------------------- |
| `project`     | string | 否   | 项目名。不传返回项目索引（轻量）；传则返回该项目环境详情                    |
| `environment` | string | 否   | 环境名，配合 `project` 缩小到单环境。单独传无意义                          |

空白字符串等同未传。行为矩阵：

| project    | environment | 返回 |
|------------|-------------|------|
| 不传       | —           | `{success:true, projects:[{name, defaultEnvironment}]}`（项目索引，不含环境） |
| 传（存在） | 不传        | 该项目全环境详情 |
| 传（存在） | 传（存在）  | 该单环境详情 |
| 传（存在） | 传（不存在）| `{success:false, errorCode:"ENVIRONMENT_NOT_FOUND", environments:[该项目全环境]}` |
| 传（不存在）| 任意        | `{success:false, errorCode:"PROJECT_NOT_FOUND", availableProjects:[项目名数组]}` |

环境详情含 `name`、`type`、`isProduction`、`maxRows` 及并发/连接池/超时配置，便于 Agent 按库类型组织 SQL、在生产环境谨慎操作。传错时响应直接回显可用项目或环境列表，可据此重试。

不传 project（首次发现项目）：

```json
{
  "success": true,
  "projects": [
    { "name": "my-project", "defaultEnvironment": "test" }
  ]
}
```

### db_query

在指定项目和环境上执行只读 SQL 查询。

| 参数          | 类型   | 必填 | 说明                                                                           |
| ------------- | ------ | ---- | ------------------------------------------------------------------------------ |
| `project`     | string | 是   | 项目名，对应 `config.json` 中 `databases` 的键                                 |
| `sql`         | string | 是   | SQL 语句，仅允许只读操作                                                       |
| `environment` | string | 否   | 环境名；未传时使用项目的 `defaultEnvironment`                                  |
| `limit`       | int    | 否   | 临时限制返回行数，必须为正整数；最终取 `min(limit, maxRows)`，不能突破配置上限 |

返回 JSON 示例：

```json
{
  "success": true,
  "project": "my-project",
  "environment": "test",
  "databaseType": "SqlServer",
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

错误以结构化 JSON 返回，不抛到协议层。常见错误码：

| 错误码                  | 说明                                 |
| ----------------------- | ------------------------------------ |
| `PROJECT_NOT_FOUND`     | 项目不存在                           |
| `ENVIRONMENT_REQUIRED`  | 未指定环境且无默认环境               |
| `ENVIRONMENT_NOT_FOUND` | 环境不存在                           |
| `SQL_BLOCKED`           | SQL 被安全守卫阻止                   |
| `SQL_PARSE_ERROR`       | SQL 为空或无法识别首关键字           |
| `RATE_LIMITED`          | 并发达上限，排队等待超时             |
| `QUERY_CONNECT_TIMEOUT` | 建立连接超时（连接池耗尽或网络不可达）|
| `QUERY_TIMEOUT`         | 查询执行超时（超过 `commandTimeout`）|
| `QUERY_ERROR`           | 数据库执行错误                       |

## 配置文件详解

完整配置见 [src/McpDbTools.Server/config.json](src/McpDbTools.Server/config.json)。核心结构：

```jsonc
{
  "defaultDisabledKeywords": ["DROP", "DELETE", "UPDATE"],
  "defaultDisabledKeywordsByType": {
    "sqlserver": ["BULK INSERT", "xp_cmdshell"],
    "mysql": ["LOAD DATA", "FLUSH"],
    "oracle": ["FLASHBACK", "PURGE"]
  },
  // 并发与连接池全局默认（缺省时用内置默认 10/5/100/60）
  "defaultMaxConcurrency": 10,
  "defaultMaxConcurrencyWaitSeconds": 5,
  "defaultMaxPoolSize": 100,
  "defaultConnectTimeoutSeconds": 60,
  // 运维清理（缺省时全部关闭，由 Admin UI「全局设置」维护）
  "maintenance": {
    "auditLogAutoCleanup": false,
    "auditLogRetentionDays": 30,
    "backupAutoCleanup": false,
    "backupRetentionDays": 30
  },
  "databases": {
    "<项目>": {
      "displayName": "项目显示名",
      "defaultEnvironment": "test",
      "environments": {
        "<环境>": {
          "displayName": "环境显示名",
          "isProduction": false,
          "type": "sqlserver|mysql|oracle",
          "connectionString": "...",
          "maxRows": 1000,
          "commandTimeout": 600,
          "maxConcurrency": 10,           // 可选，覆盖全局（<=0 回退全局）
          "maxPoolSize": 100,
          "connectTimeoutSeconds": 60,
          "disabledKeywords": []
        }
      }
    }
  }
}
```

> 残留的旧 `audit` 节点会被静默忽略；`maintenance` 缺省时全部关闭，向后兼容。

### 三层 SQL 阻止关键字

| 层级 | 字段                                                    | 作用域                         |
| ---- | ------------------------------------------------------- | ------------------------------ |
| 全局 | `defaultDisabledKeywords`                               | 所有数据库、所有项目、所有环境 |
| 类型 | `defaultDisabledKeywordsByType`                         | 按数据库类型追加               |
| 环境 | `databases.<项目>.environments.<环境>.disabledKeywords` | 单个环境追加                   |

最终阻止列表 = 全局 ∪ 按类型 ∪ 环境。全部转大写去重；下层只能追加，不能缩减上层。

### 并发与连接池

为避免高并发下 `db_query` 因连接池耗尽或线程池饥饿而卡死：

| 配置项 | 全局默认 key | 环境级覆盖 | 内置默认 |
| ------ | ------------ | ---------- | -------- |
| 每环境最大并发查询数 | `defaultMaxConcurrency` | `maxConcurrency` | 10 |
| 超载排队最长等待秒数 | `defaultMaxConcurrencyWaitSeconds` | —（仅全局） | 5 |
| 连接池上限 | `defaultMaxPoolSize` | `maxPoolSize` | 100 |
| 建立连接超时秒数 | `defaultConnectTimeoutSeconds` | `connectTimeoutSeconds` | 60 |

- 每个 `(project, environment)` 独立并发闸门，慢库不拖累其它环境；超限排队，等待超时返回 `RATE_LIMITED`。
- 连接池上限与建连超时按数据库类型拼接到连接串（如 SQL Server 的 `Max Pool Size` / `Connect Timeout`），并作为建连兜底超时。
- 环境级 `<=0` 或留空回退全局；全局未配置用内置默认。旧 config.json 不写这些字段时行为不变，且支持热重载。

### 审计日志

审计日志**全局开启**，记录到 `%ProgramData%\McpDbTools\audit.db`（SQLite，WAL 模式，与 config.json 同目录），MCP 写入与 Admin 读取可同进程并发。

- 每次成功解析到项目与环境的 `db_query` 都会记录一条（含被阻止与执行失败）；早期参数解析错误（项目/环境不存在）不入库。
- 写入经 Channel 入队、单消费者串行落盘，避免高并发下线程池饥饿与写锁竞争。
- 清理策略由「全局设置」的 `maintenance` 节点控制：默认不清理，可开启按保留天数的自动清理（后台服务每小时检查，仅在 Admin/混合模式运行时生效），也可手动按 10/20/30/50 天清理。
- 「全局设置」的「记录查询结果」开关（`maintenance.auditRecordResults`，默认关闭）开启后，成功的 `db_query` 会把完整查询结果（columns + rows）以 JSON 存入 `audit_log_result` 子表（1:1 关联主表）。结果集不限制大小，关闭开关或失败查询不入子表。审计日志列表不展示结果，点击 SQL 单元格弹窗时按需懒加载渲染为表格（含行号、NULL 灰字、滚动）。开关关闭前的老记录无结果数据，弹窗提示「该记录无查询结果」。开启后请关注 `audit.db` 体积，配合自动/手动清理使用。

## SQL 安全策略

**白名单（按数据库类型）**：

- 通用：`SELECT`、`WITH`（CTE）、`EXEC` / `EXECUTE`
- MySQL 额外：`CALL`、`SHOW`、`DESCRIBE` / `DESC`、`EXPLAIN`
- Oracle 额外：`CALL`、`DESCRIBE` / `DESC`
- SQL Server 额外：`sp_help`、`sp_tables`、`sp_columns` 等系统存储过程

**黑名单**：`DROP`、`DELETE`、`UPDATE`、`INSERT`、`ALTER`、`CREATE`、`TRUNCATE`、`MERGE`、`GRANT`、`REVOKE` 等，外加按类型和环境追加的关键字。

校验：去注释 → 规范化空白 → 首关键字白名单 → 全文黑名单扫描，可拦截 `SELECT 1; DROP TABLE x` 这类多语句注入。

## 发布与部署

发布到固定目录，程序与用户数据分离（MCP 与 Admin UI 共享同一份用户数据）：

```text
D:\Tools\McpDbTools\                # 安装目录（程序文件，升级时可全量替换）
├── McpDbTools.Server.exe
├── wwwroot\admin\                  # SPA 静态资源
└── ...

%ProgramData%\McpDbTools\            # 用户数据目录（跨用户共享，与程序目录分离）
├── config.json                     # 配置
├── audit.db                        # 审计日志（首次写入自动创建）
└── backups\                        # 配置备份（保存自动生成）
```

```bash
dotnet publish src/McpDbTools.Server -c Release
```

生产环境 MCP 客户端配置不加 Admin 参数，避免 Agent 通过 MCP 进程修改配置。Admin 服务单独运行：`McpDbTools.Server.exe --admin-only --admin-port 5123`。

## 开发

```bash
dotnet build
dotnet test                                    # 全部测试
dotnet test --filter "FullyQualifiedName~SqlGuardTests"   # 单个测试类
dotnet run --project src/McpDbTools.Server                                  # MCP 模式
dotnet run --project src/McpDbTools.Server -- --admin-only --admin-port 5123 # Admin 模式
dotnet publish src/McpDbTools.Server -c Release
```

> MCP 模式下 stdout 是协议通道，新增日志或调试输出必须走 stderr。

### 项目结构

```text
src/McpDbTools.Server/
├── Admin/             # Admin API、配置读写、测试连接、备份管理、全局设置
├── Audit/             # 审计日志（SQLite + Channel 异步串行写入）
├── Configuration/     # 配置模型、热重载、三层关键字合并、连接串拼接
├── Database/          # 三种数据库 provider + 工厂 + 每环境并发限流器
├── Maintenance/       # 运维清理后台服务（审计日志/备份自动清理）
├── Security/          # SqlGuard SQL 安全守卫
├── Tools/             # db_list / db_query MCP 工具
├── wwwroot/admin/     # 静态 Admin UI（无 npm 构建链 SPA）
└── Program.cs         # 运行模式入口
```

### 技术栈

.NET 8、ASP.NET Core Minimal API、原生 HTML/CSS/JS、[ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) 1.4.0、SqlClient / MySqlConnector / Oracle.ManagedDataAccess.Core、Microsoft.Data.Sqlite、xUnit。

## 已知限制

- 不解析字符串字面量，字符串内的关键字可能被误判（安全工具宁可误拒）
- 不支持存储过程参数化传入，不支持跨环境/多连接 JOIN（同一连接内跨 schema 由数据库决定）
- Admin UI 仅设计为本机访问；远程访问需另行设计认证、授权、TLS 与审计
- 实际数据库连接需在目标环境用真实数据库验证（单测只覆盖纯逻辑层）
