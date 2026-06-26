# MCP Database Tools

为 [Claude Code](https://docs.anthropic.com/claude-code) 提供数据库只读访问能力的 MCP (Model Context Protocol) 工具。基于 .NET 8 + 官方 `ModelContextProtocol` SDK，支持 SQL Server、MySQL、Oracle，内置 SQL 安全守卫、多环境配置、配置热重载、审计日志，以及本机 Admin UI 配置维护页面。

## 功能特性

- **三数据库支持**：Oracle（兼容 11g R2+）、SQL Server、MySQL
- **多环境项目配置**：同一项目可维护 `dev` / `test` / `prod` 等多个环境，并设置默认环境
- **SQL 安全守卫**：白名单（只读语句）+ 黑名单（三层合并关键字）双重校验，拦截多语句注入
- **配置热重载**：修改 `config.json` 即时生效，无需重启 MCP 进程
- **审计日志**：本地 SQLite（`audit.db`，WAL 模式）全局记录已解析到项目与环境后的查询、SQL 阻止与执行结果，不自动清理（用户不主动删除则一直保留），可在 Admin UI 按字段筛选、分页查看与手动清理
- **AI 友好返回**：columns 与 rows 分离，rows 用二维数组压缩 token 消耗
- **本机 Admin UI**：通过浏览器维护 `config.json`，支持直接编辑连接字符串、生产环境保护、测试连接、保存前备份与原子写入；另提供审计日志查看与备份管理

## 快速开始

### 构建

```bash
git clone <repo>
cd mcp-db-tools
dotnet build
```

### 配置数据库

编辑 [src/McpDbTools.Server/config.json](src/McpDbTools.Server/config.json)，在 `databases` 下添加项目与环境：

```jsonc
{
  // 审计日志已改为全局开启（本地 audit.db），无需在此配置
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
          "disabledKeywords": [],
        },
        "prod": {
          "displayName": "生产环境",
          "isProduction": true,
          "type": "sqlserver",
          "connectionString": "Server=prod;Database=MyDb;User Id=readonly;Password=***;TrustServerCertificate=true;",
          "maxRows": 500,
          "commandTimeout": 30,
          "disabledKeywords": [],
        },
      },
    },
  },
}
```

> 默认情况下程序读取**程序目录**下的 `config.json`。可通过环境变量 `ConfigStore__ConfigPath` 覆盖配置文件路径。配置文件不存在时，服务会以空配置启动，可后续通过 Admin UI 或创建配置文件补齐。
> 开发时如果直接使用源码目录下的 [src/McpDbTools.Server/config.json](src/McpDbTools.Server/config.json)，需要在启动环境中显式设置 `ConfigStore__ConfigPath`。

### 接入 Claude Code

开发时可直接通过 `dotnet run` 挂载：

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

发布后推荐直接运行 exe，并将 `config.json` 放在 exe 同目录：

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

重启 Claude Code 后，可先调用 `db_list` 查看可用项目与环境，再调用 `db_query` 执行只读查询。

## 运行模式

同一个程序支持 MCP 与 Admin 两类运行模式：

| 模式         | 参数           | 说明                                               |
| ------------ | -------------- | -------------------------------------------------- |
| MCP 模式     | 无参数         | 默认模式。启动 MCP stdio server，不启动 Admin UI   |
| Admin 模式   | `--admin-only` | 只启动本机 Admin Web 服务，不启动 MCP stdio server |
| 调试混合模式 | `--admin`      | 同时启动 MCP 与 Admin UI；仅建议开发调试使用       |

Admin UI 默认端口为 `5123`，可通过 `--admin-port` 修改。开发时如需使用源码目录下的配置文件，同样建议设置 `ConfigStore__ConfigPath`：

```bash
ConfigStore__ConfigPath=D:/GitHub/mcp-db-tools/src/McpDbTools.Server/config.json \
  dotnet run --project src/McpDbTools.Server -- --admin-only --admin-port 5123
```

启动后日志会输出本机访问地址，例如：

```text
Admin UI: http://127.0.0.1:5123/admin
```

Admin 服务只监听 `127.0.0.1`。首次访问 `/admin` 时服务端会自动设置仅限 `/admin` 路径的本机会话 cookie；Admin API 校验该 HttpOnly、SameSite=Strict 会话 cookie。会话 secret 只保存在当前进程内存中，不写入 `config.json`。

## Admin UI

直接访问启动日志中的地址即可打开配置页面：

```text
http://127.0.0.1:5123/admin
```

Admin UI 目前支持：

- 新增、编辑、删除项目；**项目 key 与环境 key 创建后不可修改**（前端置灰 + 后端校验双保险）
- 新增、编辑、删除环境；设置默认环境
- 输入 key 时若显示名为空，自动同步相同内容；手动改显示名后停止跟随
- 维护 `displayName`、`isProduction`、数据库类型、连接字符串、`maxRows`、`commandTimeout`、环境级 `disabledKeywords`
- 维护全局阻止关键字与按数据库类型追加的阻止关键字（`#/keywords`）
- **测试连接**：在项目配置页用当前编辑框的连接串即时验证（不落盘，成功/失败带耗时）
- **审计日志查看**（`#/audit-log`）：按项目/环境/类型/状态/时间/SQL 关键词筛选，项目环境联动下拉，分页（每页 50/100/500/1000/5000），SQL 与错误长文本点击弹窗查看并复制，手动清理 30/60/90 天前记录
- **备份管理**（`#/backups`）：列出备份、下载、恢复（恢复前自动快照可撤销）、删除
- 本机页面会直接加载并显示完整连接字符串，便于复制和维护
- 生产环境显示风险提示
- 保存前自动备份当前 `config.json`
- 使用临时文件验证 + 原子替换写入配置，避免 MCP 进程读到半写入文件
- 保存时会将 `config.json` 重写为标准 JSON；原文件中的注释与手工排版不会保留

备份文件默认写入 `config.json` 所在目录下的 `backups/`：

```text
backups/config.20260623-184500-123.json
```

## MCP 工具

### db_list

列出当前配置中所有项目及其环境，建议在查询前先调用。

返回 JSON 示例：

```json
{
  "projects": [
    {
      "name": "my-project",
      "defaultEnvironment": "test",
      "environments": ["test", "prod"]
    }
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

错误以结构化 JSON 返回，不抛到 MCP 协议层。常见错误码：

| 错误码                  | 说明                             |
| ----------------------- | -------------------------------- |
| `PROJECT_NOT_FOUND`     | 项目不存在                       |
| `ENVIRONMENT_REQUIRED`  | 未指定环境，且项目未配置默认环境 |
| `ENVIRONMENT_NOT_FOUND` | 环境不存在                       |
| `SQL_BLOCKED`           | SQL 被安全守卫阻止               |
| `SQL_PARSE_ERROR`       | SQL 为空或无法识别首关键字       |
| `QUERY_TIMEOUT`         | 查询超时                         |
| `QUERY_ERROR`           | 数据库执行错误                   |

## 配置文件详解

完整配置见 [src/McpDbTools.Server/config.json](src/McpDbTools.Server/config.json)。核心结构如下：

```jsonc
{
  "defaultDisabledKeywords": ["DROP", "DELETE", "UPDATE"],
  "defaultDisabledKeywordsByType": {
    "sqlserver": ["BULK INSERT", "OPENROWSET", "xp_cmdshell"],
    "mysql": ["LOAD DATA", "FLUSH"],
    "oracle": ["FLASHBACK", "PURGE"],
  },
  // 审计日志已改为全局开启（本地 audit.db），无需在此配置；残留的 audit 节点会被静默忽略
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
          "commandTimeout": 30,
          "disabledKeywords": [],
        },
      },
    },
  },
}
```

### 三层 SQL 阻止关键字

| 层级 | 字段                                                    | 作用域                         |
| ---- | ------------------------------------------------------- | ------------------------------ |
| 全局 | `defaultDisabledKeywords`                               | 所有数据库、所有项目、所有环境 |
| 类型 | `defaultDisabledKeywordsByType`                         | 按数据库类型追加               |
| 环境 | `databases.<项目>.environments.<环境>.disabledKeywords` | 单个环境追加                   |

最终阻止列表 = 全局 ∪ 按类型 ∪ 环境。全部转大写去重；下层只能追加，不能缩减上层。

### 审计日志

审计日志**全局开启**，记录到本地 SQLite 数据库 `audit.db`，位于 `config.json` 同目录。采用 WAL 模式，MCP 写入与 Admin 页读取可同进程并发。

- 每次成功解析到项目与环境的 `db_query` 调用都会记录一条（含 SQL 被安全守卫阻止、执行成功/失败）
- 不自动清理，用户不主动删除则一直保留；可在 Admin UI「审计日志」页按 30/60/90 天手动清理
- 项目不存在、环境缺失等早期参数解析错误不会写入审计日志

> 兼容说明：旧的 `config.json` 中若残留 `audit` 节点，反序列化时会被静默忽略；旧的 `logs/audit.log`（JSONL）不再写入，保留为只读归档。

## SQL 安全策略

**白名单（按数据库类型）：**

- 通用允许：`SELECT`、`WITH`（CTE）、`EXEC` / `EXECUTE`
- MySQL 额外：`CALL`、`SHOW`、`DESCRIBE` / `DESC`、`EXPLAIN`
- Oracle 额外：`CALL`、`DESCRIBE` / `DESC`
- SQL Server 额外：`sp_help`、`sp_tables`、`sp_columns` 等系统存储过程

**黑名单**：`DROP`、`DELETE`、`UPDATE`、`INSERT`、`ALTER`、`CREATE`、`TRUNCATE`、`MERGE`、`GRANT`、`REVOKE` 等，外加按数据库类型和环境追加的关键字。

校验流程：去注释 → 规范化空白 → 首句首关键字白名单判断 → 全文黑名单扫描。可拦截 `SELECT 1; DROP TABLE x` 这类多语句注入。

## 发布与部署建议

推荐发布到固定程序目录，让 MCP 与 Admin UI 共享同一个 `config.json`：

```text
D:\Tools\McpDbTools\
├── McpDbTools.Server.exe
├── config.json
├── audit.db                  # 审计日志数据库（首次写入时自动创建）
├── backups\
└── wwwroot\
    └── admin\                # SPA：index.html + styles/*.css + scripts/*.js
```

发布命令：

```bash
dotnet publish src/McpDbTools.Server -c Release
```

生产推荐：

```text
Admin 服务：McpDbTools.Server.exe --admin-only --admin-port 5123
MCP 挂载：McpDbTools.Server.exe
```

MCP 客户端配置不需要 Admin 参数，避免 Claude 通过 MCP 修改配置。

## 开发

### 运行测试

```bash
dotnet test
```

MCP 模式下 stdout 是协议通道，新增日志或调试输出必须走 stderr，避免破坏 MCP stdio 协议。

### 常用命令

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~SqlGuardTests"
dotnet test --filter "MultiStatement_Injection_Blocked"
dotnet run --project src/McpDbTools.Server
dotnet run --project src/McpDbTools.Server -- --admin-only --admin-port 5123
dotnet publish src/McpDbTools.Server -c Release
```

### 项目结构

```text
src/McpDbTools.Server/
├── Admin/             # Admin API DTO、配置读写服务、测试连接、备份管理
├── Audit/             # 审计日志器（SQLite）+ 查询模型
├── Configuration/     # 配置模型、热重载、三层关键字合并
├── Database/          # 三种数据库提供者 + 工厂（含连接测试）
├── Security/          # SqlGuard SQL 安全守卫
├── Tools/             # db_query / db_list MCP 工具
├── wwwroot/admin/     # 静态 Admin UI（SPA：index.html + scripts/*.js + styles/*.css）
└── Program.cs         # MCP / Admin 运行模式入口
```

### 技术栈

- .NET 8
- ASP.NET Core Minimal API（Admin UI）
- 静态 HTML / CSS / JavaScript（无 npm 构建链，SPA 多文件组织）
- [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) 1.4.0（MCP C# SDK）
- Microsoft.Data.SqlClient 7.0.1 / MySqlConnector 2.6.0 / Oracle.ManagedDataAccess.Core 3.21.210
- Microsoft.Data.Sqlite（审计日志本地存储）
- xUnit

## 已知限制

- 不解析字符串字面量，字符串内的关键字可能被误判（安全工具宁可误拒）
- 不支持存储过程参数化传入（参数在 SQL 文本中直接拼接）
- 不支持跨环境 / 多连接 JOIN 查询（每个环境对应一个数据库连接；同一连接内数据库自身支持的跨 schema 查询由数据库决定）
- Admin UI 当前只设计为本机访问；如需远程访问，需要另行设计认证、授权、TLS 与审计
- 实际数据库连接需在目标环境用真实数据库验证（单元测试覆盖纯逻辑层）
