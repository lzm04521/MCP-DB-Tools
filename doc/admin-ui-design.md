# MCP Database Tools — Admin UI 设计文档

> 版本：v1.0 | 日期：2026-06-23 | 状态：方案设计

---

## 1. 目标

为 MCP Database Tools 增加一个轻量本地 Admin UI，用于维护部署目录中的 `config.json`，降低手写 JSON 的成本，并降低误改生产连接的风险。

Admin UI 只负责配置维护，不参与 MCP 查询调用链。

## 2. 部署模型

推荐采用一个固定程序目录，Admin UI 与 MCP 共享同一个 `config.json`。

```text
D:\Tools\McpDbTools\
├── McpDbTools.Server.exe
├── config.json
├── backups\
├── logs\
└── wwwroot\
    └── admin\
        ├── index.html
        ├── admin.js
        └── admin.css
```

运行方式分为两个进程：

| 进程 | 启动方式 | 用途 |
|------|----------|------|
| Admin 服务 | `McpDbTools.Server.exe --admin-only --admin-port 5123` | 常驻本机服务，提供配置管理 UI |
| MCP 进程 | `McpDbTools.Server.exe` | 由 Claude Code/MCP 客户端按需启动，只提供 MCP tools |

MCP 客户端配置保持无 admin 参数：

```json
{
  "mcpServers": {
    "mcp-db-tools": {
      "command": "D:\\Tools\\McpDbTools\\McpDbTools.Server.exe"
    }
  }
}
```

Admin 服务访问地址：

```text
http://127.0.0.1:5123/admin
```

## 3. 运行模式

建议支持以下三种运行模式：

| 模式 | 参数 | 说明 |
|------|------|------|
| MCP 模式 | 无参数 | 默认模式。启动 MCP stdio server，不启动 Admin UI |
| Admin 模式 | `--admin-only` | 只启动本地 Admin Web 服务，不启动 MCP stdio server |
| 调试混合模式 | `--admin` | 开发调试用，同时启动 MCP 与 Admin UI；生产不推荐 |

生产推荐：

```text
Admin 服务：--admin-only
MCP 挂载：无参数
```

## 4. 边界与原则

### 4.1 Admin UI 的职责

Admin UI 负责：

- 管理项目
- 管理环境
- 设置默认环境
- 维护数据库类型
- 维护连接字符串
- 维护 `maxRows`
- 维护 `commandTimeout`
- 维护 `disabledKeywords`
- 维护审计配置 `audit`
- 测试连接
- 备份与回滚 `config.json`

### 4.2 Admin UI 不负责

Admin UI 不应负责：

- 执行业务 SQL 查询
- 暴露给远程网络访问
- 作为 MCP tool 被 Claude 调用
- 存储用户密码或额外账号体系
- 修改 MCP 客户端配置

### 4.3 MCP 的职责

MCP 进程继续只负责：

- `db_list`
- `db_query`

不新增配置修改类 MCP tool，避免 Claude 误改配置。

## 5. 配置结构

Admin UI 管理的核心配置结构如下：

```jsonc
{
  "audit": {
    "enabled": true,
    "logPath": "logs/audit.log",
    "maxFileSizeMB": 10,
    "maxRetentionDays": 30
  },
  "databases": {
    "津荣": {
      "defaultEnvironment": "test",
      "environments": {
        "test": {
          "type": "sqlserver",
          "connectionString": "Server=...;Database=...;User Id=...;Password=...;",
          "maxRows": 1000,
          "commandTimeout": 30,
          "disabledKeywords": []
        },
        "xiqing-prod": {
          "type": "sqlserver",
          "connectionString": "Server=...;Database=...;User Id=...;Password=...;",
          "maxRows": 1000,
          "commandTimeout": 30,
          "disabledKeywords": []
        }
      }
    }
  }
}
```

为提升 UI 可维护性与生产环境保护能力，建议新增可选字段：

```jsonc
{
  "databases": {
    "津荣": {
      "displayName": "津荣项目",
      "defaultEnvironment": "test",
      "environments": {
        "xiqing-prod": {
          "displayName": "西青正式库",
          "isProduction": true,
          "type": "sqlserver",
          "connectionString": "...",
          "maxRows": 1000,
          "commandTimeout": 30,
          "disabledKeywords": []
        }
      }
    }
  }
}
```

| 字段 | 位置 | 类型 | 必填 | 说明 |
|------|------|------|------|------|
| `displayName` | project | string | 否 | 项目显示名，不影响 MCP 调用的项目 key |
| `displayName` | environment | string | 否 | 环境显示名，不影响 MCP 调用的环境 key |
| `isProduction` | environment | bool | 否 | 是否生产环境。用于 UI 风险提示 |

## 6. 页面设计

### 6.1 总体布局

```text
┌────────────────────────────────────────────────────────────┐
│ MCP DB Tools Config Admin                                  │
│ config: D:\Tools\McpDbTools\config.json  [重新加载] [保存] │
├──────────────────────┬─────────────────────────────────────┤
│ 项目                 │ 津荣                                │
│                      │ 默认环境: [test ▼]                  │
│ + 新增项目           │                                     │
│                      │ 环境                                │
│ 杰普特               │ ┌──────────────┬───────────────┐    │
│ 扬兴科技             │ │ test         │ 测试环境       │    │
│ 津荣  ●              │ │ xiqing-prod  │ 生产环境 ⚠     │    │
│                      │ └──────────────┴───────────────┘    │
│                      │                                     │
│                      │ 连接配置                            │
│                      │ Type: [SqlServer ▼]                 │
│                      │ ConnectionString: [************]    │
│                      │ MaxRows: [1000]                     │
│                      │ Timeout: [30]                       │
│                      │ DisabledKeywords: [DROP] [DELETE]   │
│                      │                                     │
│                      │ [测试连接] [保存环境] [删除环境]     │
└──────────────────────┴─────────────────────────────────────┘
```

### 6.2 首页/总览

展示所有项目及环境：

| 项目 | 默认环境 | 环境列表 | 操作 |
|------|----------|----------|------|
| 杰普特 | prod | prod | 编辑 |
| 扬兴科技 | prod | prod | 编辑 |
| 津荣 | test | test / xiqing-prod | 编辑 |

操作：

- 新增项目
- 编辑项目
- 删除项目
- 复制项目
- 导入配置
- 导出配置
- 查看备份

### 6.3 项目编辑

字段：

- 项目 key
- 项目显示名 `displayName`
- 默认环境 `defaultEnvironment`
- 环境列表

规则：

- 项目 key 不能为空
- 项目 key 不允许重复
- 修改项目 key 时需要提示会影响 MCP 调用参数
- 默认环境必须存在于 `environments`
- 删除默认环境前必须先切换默认环境

### 6.4 环境编辑

字段：

| 字段 | 控件 | 说明 |
|------|------|------|
| 环境 key | 文本框 | MCP 调用时使用的 environment |
| 显示名 | 文本框 | 仅用于 UI 显示 |
| 是否生产环境 | 开关 | `isProduction` |
| 数据库类型 | 下拉框 | `sqlserver` / `mysql` / `oracle` |
| 连接字符串 | 多行文本框 | 默认脱敏显示，编辑时需显式显示 |
| 最大行数 | 数字输入 | `maxRows`，必须大于 0 |
| 命令超时 | 数字输入 | `commandTimeout`，必须大于 0 |
| 阻止关键字 | Tag 输入 | `disabledKeywords` |

操作：

- 保存环境
- 复制环境
- 删除环境
- 测试连接
- 标记为默认环境

### 6.5 审计配置页

字段：

| 字段 | 控件 | 说明 |
|------|------|------|
| enabled | 开关 | 是否启用审计日志 |
| logPath | 文本框 | 日志路径，支持相对路径 |
| maxFileSizeMB | 数字输入 | 单文件最大 MB |
| maxRetentionDays | 数字输入 | 保留天数 |

## 7. API 设计

所有 Admin API 只在 Admin 模式下启用。

### 7.1 获取配置

```http
GET /admin/api/config
```

返回脱敏后的配置和元信息：

```json
{
  "configPath": "D:\\Tools\\McpDbTools\\config.json",
  "projects": [
    {
      "name": "津荣",
      "displayName": "津荣项目",
      "defaultEnvironment": "test",
      "environments": [
        {
          "name": "test",
          "displayName": "测试环境",
          "isProduction": false,
          "type": "sqlserver",
          "connectionStringMasked": "Server=...;Password=******;",
          "maxRows": 1000,
          "commandTimeout": 30,
          "disabledKeywords": []
        }
      ]
    }
  ],
  "audit": {
    "enabled": true,
    "logPath": "logs/audit.log",
    "maxFileSizeMB": 10,
    "maxRetentionDays": 30
  }
}
```

### 7.2 保存配置

```http
PUT /admin/api/config
```

保存前必须执行：

1. 模型验证
2. 业务规则验证
3. 业务规则验证
4. 写入前备份
5. 原子替换 `config.json`

### 7.3 测试连接

```http
POST /admin/api/test-connection
```

请求：

```json
{
  "type": "sqlserver",
  "connectionString": "Server=...;Database=...;User Id=...;Password=...;",
  "commandTimeout": 5
}
```

响应：

```json
{
  "success": true,
  "elapsedMs": 123
}
```

测试连接只验证连接可用性，不执行用户 SQL。

### 7.4 测试只读查询（可选）

```http
POST /admin/api/test-query
```

必须复用现有 `SqlGuard`，不能绕过只读限制。

### 7.5 列出备份

```http
GET /admin/api/backups
```

响应：

```json
{
  "backups": [
    {
      "name": "config.20260623-184500.json",
      "createdAt": "2026-06-23T18:45:00Z",
      "size": 4429
    }
  ]
}
```

### 7.6 回滚备份

```http
POST /admin/api/backups/{name}/restore
```

回滚前仍需备份当前配置。

## 8. 校验规则

### 8.1 项目校验

- `databases` 可以为空，但 UI 应提示当前没有项目
- 项目 key 不能为空
- 项目 key 去除首尾空白后不能重复
- 项目 key 不建议包含换行、路径分隔符等控制字符
- `defaultEnvironment` 为空时，调用 `db_query` 必须显式指定 environment
- `defaultEnvironment` 非空时必须存在于 `environments`

### 8.2 环境校验

- 环境 key 不能为空
- 同一项目下环境 key 不能重复
- `type` 必须是支持的数据库类型：`sqlserver` / `mysql` / `oracle`
- `connectionString` 不能为空
- `maxRows` 必须大于 0
- `commandTimeout` 必须大于 0
- `disabledKeywords` 去除空白后去重保存

### 8.3 审计配置校验

- `logPath` 不能为空
- `maxFileSizeMB` 必须大于 0
- `maxRetentionDays` 必须大于 0

## 9. 安全设计

### 9.1 监听地址

Admin 服务必须默认只监听本机：

```text
127.0.0.1
```

禁止默认监听：

```text
0.0.0.0
```

如未来需要远程访问，必须另行设计认证、授权、TLS 与审计，不在本阶段范围内。

### 9.2 默认关闭

无参数启动时只进入 MCP 模式，不启用 Admin UI。

只有显式参数才启用：

```bash
--admin-only
```

或开发调试：

```bash
--admin
```

### 9.3 Admin Token

建议 Admin 服务启动时生成一次性 token：

```text
Admin UI: http://127.0.0.1:5123/admin?token=xxxx
```

API 请求要求：

```http
X-Admin-Token: xxxx
```

Token 只保存在进程内存，不写入 `config.json`。

### 9.4 连接字符串脱敏

UI 默认不展示完整连接字符串。

显示方式：

```text
Server=...;Database=...;User Id=sa;Password=******;
```

用户点击“显示完整连接字符串”前提示：

```text
连接字符串包含敏感信息，请勿截图或共享。
```

### 9.5 生产环境保护

若环境 `isProduction = true`，UI 应展示醒目的生产环境风险提示。保存配置不再要求输入项目名确认，用户需在修改以下内容前自行核对：

- 连接字符串
- 数据库类型
- 删除环境
- 回滚到会影响该环境的备份
- 项目 key 或环境 key

### 9.6 原子写入

保存配置必须避免 MCP 读到半写入文件。

流程：

```text
1. 序列化新配置到内存
2. 写入 config.tmp.json
3. 重新读取 config.tmp.json 并反序列化验证
4. 复制当前 config.json 到 backups/config.yyyyMMdd-HHmmss.json
5. 使用文件替换方式替换 config.json
6. 删除临时文件
```

### 9.7 备份策略

每次保存前自动备份：

```text
backups/config.20260623-184500.json
```

UI 提供：

- 查看备份列表
- 下载备份
- 回滚备份
- 删除旧备份（可选）

## 10. 技术选型

推荐：ASP.NET Core Minimal API + 静态 HTML/CSS/JS。

理由：

- 与现有 .NET 8 项目一致
- 改动最小
- 不引入 npm 前端构建链
- 发布仍然是一个程序目录
- 足够支撑配置表单和备份管理

不推荐首期使用：

| 方案 | 原因 |
|------|------|
| Blazor | 对当前配置管理场景偏重，结构和发布复杂度更高 |
| Electron | 打包和维护成本高，不符合轻量目标 |
| WPF/WinForms | 与 MCP server 部署形态割裂，后续远程/浏览器访问不方便 |

## 11. 实施阶段

### Phase 1：最小可用 Admin UI

目标：能安全维护 `config.json`。

内容：

- 支持 `--admin-only`
- 只监听 `127.0.0.1`
- 静态 `/admin` 页面
- `GET /admin/api/config`
- `PUT /admin/api/config`
- 项目/环境增删改
- 默认环境维护
- 基础字段校验
- 保存前备份
- 原子写入

### Phase 2：安全增强

内容：

- `displayName`
- `isProduction`
- 生产环境风险提示
- 连接字符串脱敏显示
- Admin Token
- 配置差异预览

### Phase 3：运维增强

内容：

- 测试连接
- 测试只读查询
- 备份列表
- 一键回滚
- 导入/导出配置

## 12. 验收标准

### 功能验收

- Admin 服务可通过 `--admin-only` 启动
- MCP 无参数启动不暴露 Admin UI
- UI 能新增/编辑/删除项目
- UI 能新增/编辑/删除环境
- UI 能设置默认环境
- UI 能编辑审计配置
- 保存后 MCP 进程读取同一个 `config.json`
- 保存前自动生成备份
- 配置非法时拒绝保存并提示原因

### 安全验收

- Admin UI 默认只监听 `127.0.0.1`
- 无参数启动不会启用 Admin UI
- 连接字符串默认脱敏
- 生产环境修改仅展示风险提示，不要求二次确认
- 保存使用临时文件 + 原子替换
- MCP 不新增配置修改工具

### 运维验收

- 可将 Admin 模式注册为 Windows Service
- 可通过浏览器访问本地管理页面
- 可从备份恢复配置
- 配置保存后不需要手动修改 MCP 客户端配置

---

> 结论：采用“一个固定程序目录 + Admin 本机服务常驻 + MCP 无参数挂载同一 exe + 共享同一个 config.json”的方式，实现成本低、安全边界清晰，也便于后续扩展。