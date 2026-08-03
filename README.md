# MCP Database Tools

为支持 MCP（Model Context Protocol）的 Agent（如 [Claude Code](https://docs.anthropic.com/claude-code)、[Codex](https://developers.openai.com/codex)）提供数据库只读访问能力的工具。基于 .NET 8 + 官方 `ModelContextProtocol` SDK，支持 SQL Server、MySQL、Oracle、PostgreSQL，内置 SQL 安全守卫、多环境配置、配置热重载、每环境并发限流、审计日志，以及本机 Admin UI 配置维护页面。

## 功能特性

- **四数据库支持**：SQL Server、MySQL、Oracle（兼容 11g R2+）、PostgreSQL
- **多环境配置**：同一项目可维护 `dev` / `test` / `prod` 等多环境，设置默认环境
- **SQL 安全守卫**：白名单（只读语句）+ 三层黑名单双重校验，拦截多语句注入
- **非生产环境 DB 写支持**：环境配置 `allowWrite=true` 时，允许 `INSERT` / `UPDATE` / `DELETE` / `CREATE` / `ALTER` / `DROP INDEX` / `MERGE` / `REPLACE` 等 DML/DDL 写操作并返回受影响行数 `affectedRows`；生产环境（`isProduction=true`）后端兜底强制只读，与 `allowWrite` 互斥（即便手改 `config.json` 也不生效）
- **全局关键字分只读池/写池**：阻止关键字按环境开关分别生效；Admin UI 关键字页额外展示代码内置固定关键字（只读，无法在页面修改）
- **配置热重载**：改 `config.json` 即时生效，无需重启
- **并发与连接池可控**：每个 `(project, env)` 独立并发闸门，避免高并发打满连接池
- **审计日志**：本地 SQLite 全局记录查询与阻止，支持自动/手动清理；可选记录查询结果（弹窗懒加载查看）
- **AI 友好返回**：columns 与 rows 分离，rows 用二维数组压缩 token
- **本机 Admin UI**：浏览器维护 `config.json`，含测试连接、备份管理、审计查看与全局设置

## 快速开始

**Windows 用户**：可直接从 [GitHub Release](../../releases) 下载 zip，解压后运行 `.\install.ps1` 一键部署（无需 .NET SDK，详见[发布版本安装](#发布版本安装推荐)）。

**源码部署 / 非 Windows**：

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
          // 非生产环境可开启写操作（生产环境即便设 true 也会被后端兜底强制只读）
          "allowWrite": true,
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

> 程序默认读取 `%ProgramData%\McpDbTools\config.json`（Windows 跨用户共享数据目录，与程序目录分离便于升级；LocalSystem 服务与当前用户进程共享同一份数据），可用环境变量 `ConfigStore__ConfigPath` 覆盖。**首次部署后，config.json 位于 `%ProgramData%\McpDbTools\`，不在 exe 同目录**——请通过 Admin UI 维护，或直接编辑该文件。文件不存在时空配置启动，可后续通过 Admin UI 补齐。开发时若用源码目录下的 config.json，需显式设置该环境变量。

### 接入 MCP 客户端

本工具通过 MCP Streamable HTTP 与 Agent 通信。服务须先启动（开发时 `dotnet run --project src/McpDbTools.Server`；生产环境用 NSSM 服务或登录计划任务），再让 MCP 客户端连接 `http://127.0.0.1:<port>/mcp`。下面给出 Claude Code 与 Codex 的配置示例，其它 MCP 客户端按各自文档以 HTTP URL 接入即可。

> 建议先用 Admin UI 测试连接、确认配置无误，再接入客户端：`dotnet run --project src/McpDbTools.Server`，浏览器打开 `http://127.0.0.1:5123/admin`。

#### Claude Code

Claude Code 在 `mcp.json`（项目级 `.mcp.json` 或用户级配置）中用 JSON 配置 `mcpServers`。HTTP 模式下只需指定 URL，服务须先单独启动（开发时 `dotnet run --project src/McpDbTools.Server`，可用 `ConfigStore__ConfigPath` 指向源码目录的 config.json；生产环境用 NSSM 服务或计划任务）：

```json
{
  "mcpServers": {
    "db-tools": {
      "type": "http",
      "url": "http://127.0.0.1:5123/mcp"
    }
  }
}
```

也可用 Claude Code CLI 一条命令添加（等效于上面配置；CLI 默认 scope 是 `local`，如要写用户级配置请加 `-s user`，要写项目级共享 `.mcp.json` 请加 `-s project`）：

```bash
claude mcp add --transport http db-tools http://127.0.0.1:5123/mcp
```

#### Codex

[Codex](https://developers.openai.com/codex) 在 `~/.codex/config.toml`（或项目级 `.codex/config.toml`）中用 TOML 配置，每个 server 一个 `[mcp_servers.<name>]` 表。Codex 通过是否存在 `url` 字段区分 stdio 与 streamable HTTP（无显式 `type` 字段），HTTP 模式只需写 `url`：

```toml
[mcp_servers.db-tools]
url = "http://127.0.0.1:5123/mcp"
```

> Codex 默认工具执行超时 `tool_timeout_sec = 60` 秒。如果数据库查询可能较慢，可在 `[mcp_servers.db-tools]` 下追加 `tool_timeout_sec = 120` 调大。

#### 验证接入

重启客户端后，在对话中让 Agent：

1. 先调用 `db_list`（不传参数）查看可用项目；
2. 再用 `db_list(project="xxx")` 查看该项目环境；
3. 最后调用 `db_query` 执行只读查询。

## 运行模式

默认即单一 Web 进程，同端口同时提供 Admin UI（`/admin`）与 MCP Streamable HTTP（`/mcp`）：

| 参数            | 说明                                                       |
| --------------- | ---------------------------------------------------------- |
| 无参数          | 默认。启动 Web 服务，同端口出 `/admin` + `/mcp`             |
| `--admin-port`  | 覆盖默认端口 `5123`（取值 1-65535）                        |

旧的 `--admin-only` / `--admin` 参数已移除。Admin UI 默认端口 `5123`（`--admin-port` 可改），只监听 `127.0.0.1`。首次访问 `/admin` 自动设置仅限该路径的 HttpOnly、SameSite=Strict 本机会话 cookie，secret 只存于进程内存。

服务须常驻（NSSM 服务或登录计划任务，见 [发布与部署](#发布与部署)）；不常驻则 MCP 客户端无法连接。

> **安全提示（本机信任模型）：** HTTP 合一后 `/admin` 与 `/mcp` 同进程同端口，均仅监听 `127.0.0.1`，且 `/mcp` 与 `/admin/api/*` **均不鉴权**——`/admin` 的会话 cookie 仅限浏览器 `/admin` 路径，不保护 API 调用。任何能访问 `127.0.0.1:<port>` 的本机进程或 Agent 都可调用 `/admin/api/*` 修改配置，或经 `/mcp` 查询数据库。远程访问、TLS、端点鉴权留作后续独立设计（本次不做）；多用户主机或不受信任环境暂不适用。

```bash
# 开发时
dotnet run --project src/McpDbTools.Server
ConfigStore__ConfigPath=D:/GitHub/mcp-db-tools/src/McpDbTools.Server/config.json \
  dotnet run --project src/McpDbTools.Server -- --admin-port 5123
```

## Admin UI

浏览器打开启动日志中的地址（如 `http://127.0.0.1:5123/admin`）即可维护配置。功能分五个页面：

- **项目配置**（`#/projects`）：增删项目和环境，**key 创建后不可修改**；维护连接字符串、数据库类型、`maxRows`、`commandTimeout`、环境级并发/连接池参数与阻止关键字；内置测试连接（不落盘）。
- **全局关键字**（`#/keywords`）：维护只读池 / 写池全局默认与按类型追加的阻止关键字；展示代码内置固定关键字（只读，无法在页面修改）。
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

在指定项目和环境上执行 SQL 查询。只读环境仅允许 `SELECT` 等只读语句；写环境（`allowWrite=true` 且 `isProduction=false`）支持 `INSERT` / `UPDATE` / `DELETE` / `CREATE` / `ALTER` / `DROP INDEX` / `MERGE` / `REPLACE` 等 DML/DDL 写操作，返回受影响行数 `affectedRows`。生产环境后端兜底强制只读。

| 参数          | 类型   | 必填 | 说明                                                                           |
| ------------- | ------ | ---- | ------------------------------------------------------------------------------ |
| `project`     | string | 是   | 项目名，对应 `config.json` 中 `databases` 的键                                 |
| `sql`         | string | 是   | SQL 语句；只读环境仅允许只读操作，写环境另支持 DML/DDL 写操作                  |
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
  // 写池：allowWrite=true 环境的全局阻止关键字，空时回退代码内置 BuiltInWrite
  "defaultWriteDisabledKeywords": ["DROP TABLE", "TRUNCATE"],
  "defaultDisabledKeywordsByType": {
    "sqlserver": ["BULK INSERT", "xp_cmdshell"],
    "mysql": ["LOAD DATA", "FLUSH"],
    "oracle": ["FLASHBACK", "PURGE"],
    "postgresql": ["COPY", "VACUUM", "ANALYZE"]
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
          "type": "sqlserver|mysql|oracle|postgresql",
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

> **只读池 / 写池**：`defaultDisabledKeywords` 是只读环境（`allowWrite` 缺省或 `false`）的全局阻止关键字，覆盖 `DROP` / `DELETE` / `UPDATE` / `INSERT` 等所有写动词；`defaultWriteDisabledKeywords` 是写环境（`allowWrite=true` 且非生产）的全局阻止关键字，默认放开业务写（`INSERT` / `UPDATE` / `DELETE` / `CREATE` / `ALTER` / `DROP INDEX` / `MERGE` / `REPLACE`），保留结构删除、系统级、账号权限、动态 SQL 等高危项。同一环境按 `allowWrite` 开关二选一，由后端解析时按"`isProduction=true` 强制只读，否则看环境 `allowWrite`"自动选池；`defaultDisabledKeywordsByType` 与环境级 `disabledKeywords` 在两种环境下都按上述三层规则追加。

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
- 清理策略由「全局设置」的 `maintenance` 节点控制：默认不清理，可开启按保留天数的自动清理（后台服务每小时检查，随 Web 进程常驻运行），也可手动按 10/20/30/50 天清理。
- 「全局设置」的「记录查询结果」开关（`maintenance.auditRecordResults`，默认关闭）开启后，成功的 `db_query` 会把完整查询结果（columns + rows）以 JSON 存入 `audit_log_result` 子表（1:1 关联主表）。结果集不限制大小，关闭开关或失败查询不入子表。审计日志列表不展示结果，点击 SQL 单元格弹窗时按需懒加载渲染为表格（含行号、NULL 灰字、滚动）。开关关闭前的老记录无结果数据，弹窗提示「该记录无查询结果」。开启后请关注 `audit.db` 体积，配合自动/手动清理使用。

## SQL 安全策略

**白名单（按数据库类型）**：

- 通用：`SELECT`、`WITH`（CTE）、`EXEC` / `EXECUTE`
- MySQL 额外：`CALL`、`SHOW`、`DESCRIBE` / `DESC`、`EXPLAIN`
- Oracle 额外：`CALL`、`DESCRIBE` / `DESC`
- SQL Server 额外：`sp_help`、`sp_tables`、`sp_columns` 等系统存储过程
- PostgreSQL 额外：`CALL`、`EXPLAIN`、`SHOW`、`TABLE`、`VALUES`（不含 `EXECUTE`：PG 中为执行 prepared statement，动态 SQL 语义，风险高）

**黑名单**：`DROP`、`DELETE`、`UPDATE`、`INSERT`、`ALTER`、`CREATE`、`TRUNCATE`、`MERGE`、`GRANT`、`REVOKE` 等，外加按类型和环境追加的关键字。

**写环境追加白名单**（`allowWrite=true` 且 `isProduction=false`）：在只读白名单基础上追加 `INSERT`、`UPDATE`、`DELETE`、`MERGE`、`REPLACE`、`CREATE`、`ALTER`、`DROP INDEX` 等写动词首关键字。这些动词在只读环境会被首关键字白名单拒绝；在写环境放行，但仍受写池黑名单（结构删除、系统级、动态 SQL 等）与多语句注入扫描约束。生产环境（`isProduction=true`）后端兜底强制只读，即使手改 `config.json` 设置 `allowWrite=true` 也不生效。

校验：去注释 → 规范化空白 → 首关键字白名单 → 全文黑名单扫描，可拦截 `SELECT 1; DROP TABLE x` 这类多语句注入。

## 发布与部署

### 目录结构

程序与用户数据物理分离，升级时安装目录可全量替换、用户数据不丢失：

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

数据目录选用 `%ProgramData%\McpDbTools`（Windows 跨用户共享数据目录），保证 **LocalSystem 服务**（NSSM / 计划任务承载的统一 Web 进程）与**当前用户进程**（开发时手动 `dotnet run`）读写同一份数据。部署脚本会自动给 Users 组授予 Modify 权限。

> 数据目录由 `DataDirectoryResolver` 集中解析，优先级：调用方传入 > 环境变量 `ConfigStore__ConfigPath` > `%ProgramData%\McpDbTools` > exe 同目录。多数情况下无需关心，默认值即可。

### 发布版本安装（推荐）

从 [GitHub Release](../../releases) 下载对应架构的 zip（`McpDbTools-vX.Y.Z-win-x64.zip` 或 `McpDbTools-vX.Y.Z-win-arm64.zip`），解压后里面已含 `McpDbTools.Server.exe`、`wwwroot\` 与 `install.ps1`：

```powershell
.\install.ps1
```

`install.ps1` 完成"确认 → 交互询问 → 提权 → 停服 → 迁移数据 → 替换文件 → 安装自启动 → 注册 MCP"全流程，**不编译代码**，无需 .NET SDK。脚本行为与下方「从源码构建并部署」一致，仅省去构建步骤；可用参数也相同（见参数表）。

> 发布包仅提供 Windows x64 / arm64。macOS / Linux 请走下方「手动发布」自行构建。

### 从源码构建并部署

仓库根目录的 [`build and install.ps1`](build and install.ps1) 完成"确认 → `dotnet publish` → 委托 [`install.ps1`](install.ps1) 完成安装"。编译产物输出到临时目录，确认通过后才构建；安装逻辑全部复用 `install.ps1`：

```powershell
.\build and install.ps1
```

脚本行为（`build and install.ps1` 在确认后多一步 `dotnet publish` 编译，随后委托 `install.ps1` 执行下列流程；`install.ps1` 直接从发布包执行）：

1. **提权前确认**：显示安装目录、数据目录、MCP 名称等部署计划，输入 `Y` 后才继续（源码版先编译再提权）
2. **交互式询问**（提权前完成，答案透传给提权进程）：Admin UI 端口（默认 `61123`）；未安装 [nssm](https://nssm.cc) 时是否用计划任务承载
3. **数据迁移**：把旧版数据（exe 同目录 或 `%USERPROFILE%\.mcpdbtools`）搬到 `%ProgramData%\McpDbTools`，幂等
4. **全量替换安装目录**：用户数据已分离，可无条件清空安装目录后复制新产物
5. **自启动安装**：有 nssm 则装 Windows 服务（`SERVICE_AUTO_START`），否则按选择装计划任务
6. **注册 MCP**：`claude mcp add` 把 Server 注册到 Claude Code（默认作用域 `user`）

常用参数：

| 参数 | 默认值 | 说明 |
| ---- | ------ | ---- |
| `-InstallDir` | `E:\Software\FreeInstall\Mcp-db-Tools` | 安装目录 |
| `-McpName` | `db-tools` | 注册到 Claude Code 的 MCP 名称 |
| `-McpScope` | `user` | MCP 作用域：`local` / `user` / `project` |
| `-AdminServiceName` | `McpDbTools.Admin` | NSSM 服务名 / 计划任务名 |
| `-PauseOnExit` | 关 | 结束时暂停等待回车（便于查看管理员窗口输出） |

示例：

```powershell
# 自定义安装目录与 MCP 作用域
.\build and install.ps1 -InstallDir D:\Tools\McpDbTools -McpScope local

# 当前已是管理员，跳过 UAC 直接部署
powershell -Verb RunAs -Command ".\build and install.ps1"
```

### 手动发布

如不走部署脚本（例如远程机器、便携部署、或非 Windows 平台）：

```bash
# Windows
dotnet publish src/McpDbTools.Server -c Release

# 指定目标架构（self-contained 单文件，免装运行时）
dotnet publish src/McpDbTools.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish src/McpDbTools.Server -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true

# 非 Windows（macOS / Linux）—— 官方不发布这些平台的包，需自行构建
dotnet publish src/McpDbTools.Server -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
dotnet publish src/McpDbTools.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

发布产物拷到目标目录后，首次运行会自动在 `%ProgramData%\McpDbTools` 创建数据目录与空配置。也可用环境变量 `ConfigStore__ConfigPath` 指定自定义路径。

> `install.ps1` 仅适用于 Windows。非 Windows 平台构建产物后需自行配置常驻进程（systemd 单元、launchd 等）与 MCP 客户端连接 `http://127.0.0.1:<port>/mcp`。

### 服务自启动

HTTP 模式下，MCP 客户端通过 `http://127.0.0.1:<port>/mcp` 连接服务，**服务必须常驻**。生产环境推荐用 NSSM 装成 Windows 服务（部署脚本已自动安装），或用登录计划任务承载：

```bash
# 前台运行（调试）
McpDbTools.Server.exe --admin-port 5123

# 后台服务（推荐，部署脚本已自动安装）
# 或手工用 nssm：
nssm install McpDbTools.Server "D:\Tools\McpDbTools\McpDbTools.Server.exe" --admin-port 61123
nssm start McpDbTools.Server
```

服务启动后同时暴露 Admin UI（`/admin`）与 MCP HTTP（`/mcp`）；MCP 客户端只需配置 URL，不感知承载方式。

## 开发

```bash
dotnet build
dotnet test                                    # 全部测试
dotnet test --filter "FullyQualifiedName~SqlGuardTests"   # 单个测试类
dotnet run --project src/McpDbTools.Server                                  # 启动 Web 服务（/admin + /mcp）
dotnet run --project src/McpDbTools.Server -- --admin-port 5123            # 指定端口
dotnet publish src/McpDbTools.Server -c Release
```

> 服务运行时 stdout 不再是协议通道（MCP 改用 HTTP），日志走标准 ASP.NET Core logging 管道。

### 项目结构

```text
src/McpDbTools.Server/
├── Admin/             # Admin API、配置读写、测试连接、备份管理、全局设置
├── Audit/             # 审计日志（SQLite + Channel 异步串行写入）
├── Configuration/     # 配置模型、热重载、三层关键字合并、连接串拼接、DataDirectoryResolver 数据目录解析
├── Database/          # 四种数据库 provider + 工厂 + 每环境并发限流器
├── Maintenance/       # 运维清理后台服务（审计日志/备份自动清理）
├── Security/          # SqlGuard SQL 安全守卫
├── Tools/             # db_list / db_query MCP 工具
├── wwwroot/admin/     # 静态 Admin UI（无 npm 构建链 SPA）
└── Program.cs         # 运行模式入口
```

### 技术栈

.NET 8、ASP.NET Core Minimal API、原生 HTML/CSS/JS、[ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) 1.4.0、SqlClient / MySqlConnector / Oracle.ManagedDataAccess.Core / Npgsql、Microsoft.Data.Sqlite、xUnit。

## 已知限制

- 不解析字符串字面量，字符串内的关键字可能被误判（安全工具宁可误拒）
- 不支持存储过程参数化传入，不支持跨环境/多连接 JOIN（同一连接内跨 schema 由数据库决定）
- Admin UI 仅设计为本机访问；远程访问需另行设计认证、授权、TLS 与审计
- 实际数据库连接需在目标环境用真实数据库验证（单测只覆盖纯逻辑层）
