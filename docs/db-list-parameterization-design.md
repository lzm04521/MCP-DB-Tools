# db_list 参数化设计

- **日期**：2026-06-30
- **状态**：待评审
- **背景**：上一轮把"参数解析失败兜底"加到了 `db_query` 上，经讨论确认该需求实际应作用于 `db_list`，需回退 `db_query` 的兜底改动，并对 `db_list` 做参数化改造。

## 一、问题陈述

当前 `db_list` 是无参工具，每次返回**所有项目 + 所有环境的完整运行参数**。当环境数量较多时，单次返回数据量大、token 占用高，而 Agent 多数情况下只需"该项目有哪些环境"或"某个具体环境的连接参数"。

同时，上一轮错误地把"参数错误时附带可用列表"加到了 `db_query` 上（`QueryResult.AvailableProjects`），这与本工具的职责划分不符——项目/环境发现应由 `db_list` 承担，`db_query` 应保持纯错误码返回。

## 二、设计目标

1. `db_list` 支持按需加载：能返回轻量项目索引、单项目全环境、或单项目单环境。
2. Agent 调用便捷：第一次调用无需知道任何项目名即可获取项目清单。
3. 参数错误时提供纠错兜底（轻量），避免 Agent 反复试错。
4. 回退 `db_query` 上一轮的兜底改动，保持职责单一。
5. 成功与错误响应统一带 `success` 字段，与 `db_query` 解析逻辑一致。

## 三、非目标

- **不**支持把 `project` 和 `environment` 拼成单字符串（如 `jinrong-西青正式`）传入并自动拆分。项目 key 可能本身含 `-`（如 `erp-system`），按字符盲目切分会歧义。`project` 与 `environment` 必须作为独立参数传入。
- **不**支持按 `displayName` 或中文别名匹配，仅按 config 中的 key 精确匹配（大小写不敏感）。
- **不**改变 `db_query` 的成功响应结构。

## 四、db_list 新签名

```csharp
[McpServerTool(Name = "db_list")]
[Description("...详见下文...")]
public Task<string> ListProjects(
    string? project = null,
    string? environment = null,
    CancellationToken cancellationToken = default)
```

两个参数均为可选。匹配走 `ResolvedConfig` 的字典（已用 `StringComparer.OrdinalIgnoreCase` 构造，即大小写不敏感的 key 精确匹配）。

## 五、行为矩阵

| project | environment | success | 返回结构 |
|---------|-------------|---------|----------|
| 不传 | — | true | `{success:true, projects:[{name, defaultEnvironment}]}` —— **项目索引，不带环境** |
| 传（存在） | 不传 | true | `{success:true, projects:[{name, defaultEnvironment, environments:[全环境详情]}]}` |
| 传（存在） | 传（存在） | true | `{success:true, projects:[{name, defaultEnvironment, environments:[单环境详情]}]}` |
| 传（存在） | 传（不存在） | false | `{success:false, errorCode:"ENVIRONMENT_NOT_FOUND", error, environments:[**该项目全环境详情**]}` |
| 传（不存在） | 任意 | false | `{success:false, errorCode:"PROJECT_NOT_FOUND", error, availableProjects:[**"p1","p2"**]}` —— 项目名字符串数组 |

> 上表 `error` 字段为人类可读的中文提示，含可用名列表。`availableProjects`/`environments` 字段为兜底数据，用于 Agent 纠错重试。
> 判断顺序：先校验 `project`，project 不存在时无论 `environment` 传什么都直接返回 `PROJECT_NOT_FOUND`（environment 无意义）。
> 字段命名：成功响应的 `projects` 是**对象数组**；错误兜底用独立字段 `availableProjects`（**字符串数组**）避免类型冲突；环境兜底复用 `environments`（详情对象数组），因成功响应无顶层 `environments` 字段，不冲突。

### 空白字符串处理（确定性行为）

`project` / `environment` 为 `null` **或空白字符串**（空格、制表符、空串等，经 `string.IsNullOrWhiteSpace` 判断）时，**一律等同未传**。此规则与 `db_query` 现有的环境解析逻辑一致（`DbQueryTool.cs:57`）。即：

- `db_list(project=" ")` → 等同 `db_list()` → 返回项目索引。
- `db_list(project="erp-system", environment="")` → 等同 `db_list(project="erp-system")` → 返回该项目全环境。

### 项目存在但无环境的边界

config 中某项目 `environments` 为空字典（`{}`，理论上合法）时：

- `db_list(project="empty-proj")` → **success:true**，返回 `{success:true, projects:[{name:"empty-proj", defaultEnvironment:null, environments:[]}]}`。
- `db_list(project="empty-proj", environment="any")` → **success:false**，返回 `ENVIRONMENT_NOT_FOUND` + `environments:[]`（空详情数组，与"该项目无环境"事实一致）。

此边界按现有字典查找逻辑自然成立，无需特殊分支。

### 兜底字段格式不对称说明

| 错误码 | 兜底字段 | 内容 | 对象类型 |
|--------|----------|------|----------|
| `PROJECT_NOT_FOUND` | `availableProjects` | 项目名 | 字符串数组 `["p1","p2"]` |
| `ENVIRONMENT_NOT_FOUND` | `environments` | 环境 | 详情对象数组 |

两者**故意不对称**：项目名是简单标识，字符串数组足够且最省 token；环境详情含数据库类型、生产标识、连接/超时参数等 Agent 调用 `db_query` 前需要的运行信息，故返回详情而非纯名字（Agent 可从详情对象的 `name` 字段读出环境名）。这是明确的产品决策，不是实现疏漏。
兜底字段名 `availableProjects` 与成功响应的 `projects`（对象数组）区分，避免 Agent 用同一字段名解析两种类型。

### 环境详情对象结构（沿用上一轮已实现）

```
{
  name,                      // 环境 key
  type,                      // "sqlserver" | "mysql" | "oracle"
  isProduction,              // bool
  maxRows,                   // 最大返回行数
  maxConcurrency,            // 每环境并发上限
  maxPoolSize,               // 连接池上限
  connectTimeoutSeconds,     // 建连超时
  commandTimeout             // 命令执行超时
}
```

## 六、示例

### 1. 不传 project（首次发现）

请求：`db_list()`
响应：
```json
{
  "success": true,
  "projects": [
    { "name": "erp-system", "defaultEnvironment": "prod" },
    { "name": "crm-mysql", "defaultEnvironment": "prod" }
  ]
}
```

### 2. 传 project、不传 environment（该项目全环境）

请求：`db_list(project="erp-system")`
响应：
```json
{
  "success": true,
  "projects": [
    {
      "name": "erp-system",
      "defaultEnvironment": "prod",
      "environments": [
        { "name": "prod", "type": "sqlserver", "isProduction": false, "maxRows": 1000, "maxConcurrency": 8, "maxPoolSize": 100, "connectTimeoutSeconds": 15, "commandTimeout": 30 }
      ]
    }
  ]
}
```

### 3. 传 project + environment（单环境）

请求：`db_list(project="erp-system", environment="prod")`
响应：
```json
{
  "success": true,
  "projects": [
    {
      "name": "erp-system",
      "defaultEnvironment": "prod",
      "environments": [
        { "name": "prod", "type": "sqlserver", "isProduction": false, "maxRows": 1000, "maxConcurrency": 8, "maxPoolSize": 100, "connectTimeoutSeconds": 15, "commandTimeout": 30 }
      ]
    }
  ]
}
```

### 4. project 不存在（兜底项目名列表）

请求：`db_list(project="nope")`
响应：
```json
{
  "success": false,
  "errorCode": "PROJECT_NOT_FOUND",
  "error": "项目不存在: nope。可用项目: erp-system, crm-mysql",
  "availableProjects": ["erp-system", "crm-mysql"]
}
```

### 5. environment 不存在（兜底该项目全环境详情）

请求：`db_list(project="erp-system", environment="staging")`
响应：
```json
{
  "success": false,
  "errorCode": "ENVIRONMENT_NOT_FOUND",
  "error": "环境不存在: staging。项目 erp-system 可用环境: prod",
  "environments": [
    { "name": "prod", "type": "sqlserver", "isProduction": false, "maxRows": 1000, "maxConcurrency": 8, "maxPoolSize": 100, "connectTimeoutSeconds": 15, "commandTimeout": 30 }
  ]
}
```

## 七、回退 db_query 上一轮兜底改动

| 文件 | 动作 |
|------|------|
| `Database/QueryResult.cs` | 删除 `AvailableProjects` 字段；`Fail` 工厂去掉 `availableProjects` 参数 |
| `Tools/DbQueryTool.cs` | 三个参数解析失败分支去掉 `availableProjects` 传参，恢复纯错误码返回；`[Description]` 结尾从上一轮的"附带 availableProjects"改为新调用流程提示：「可先用 db_list() 获取项目列表，再 db_list(project=...) 获取环境列表」（与新 db_list 无参不返回 environments 的行为一致，避免误导 Agent） |
| `Tests/DbQueryToolTests.cs` | 去掉 `AssertHasAvailableProjects` 辅助方法与 `SqlBlocked_DoesNotIncludeAvailableProjects` 反向测试；三个错误测试恢复纯 errorCode 断言 |
| `README.md` | `db_query` 错误段去掉"参数解析失败附带 availableProjects"说明 |

**保留不动**（这些正是本次 `db_list` 改造的基础）：
- `ResolvedDatabase.IsProduction` 字段
- `Tools/ProjectListBuilder.cs`（将重构，见下节）
- `DbListTool` 的新环境详情对象结构
- `DbListToolTests`（将重写）

## 八、ProjectListBuilder 重构

当前 `ProjectListBuilder` 只有一个 `BuildProjects`（总返回带环境详情的完整结构），不符合"按需加载"。重构为正交方法：

```
BuildProjectIndex(config)            → [{name, defaultEnvironment}]         // 不带环境，项目索引（无参）
BuildProjectWithEnvironments(proj)   → {name, defaultEnvironment, environments:[详情]}  // 单项目全环境
BuildSingleEnvironment(proj, envKey) → {name, defaultEnvironment, environments:[单详情]}  // 单项目单环境
BuildProjectNameList(config)         → ["p1","p2"]                          // 兜底：项目名字符串数组（用于 availableProjects）
BuildEnvironmentDetails(proj)        → [{详情},...]                         // 兜底：该项目全环境详情
SerializeSuccess(...)                → 统一序列化 {success:true, projects:[...]}
SerializeFail(...)                   → 统一序列化 {success:false, errorCode, error, availableProjects|environments}
```

`BuildEnvironment`（单个环境详情对象，已在上一轮实现）保留并被上述方法复用。

## 九、db_list 错误返回格式决策

- 成功与错误响应都带 `success`（true/false）。
- 错误响应复用与 `db_query` 一致的字段：`success`、`errorCode`、`error`。
- 兜底数据字段：项目兜底用 `availableProjects`（**字符串数组**，与成功响应的 `projects` 对象数组区分），环境兜底用 `environments`（详情对象数组，成功响应无此顶层字段，不冲突）。
- 错误响应通过 `ProjectListBuilder.SerializeFail` 统一构造，保证字段顺序与格式稳定。

## 十、契约变化与兼容性

- **db_list 成功响应**：从旧的 `{projects:[{name, defaultEnvironment, environments:[详情]}]}` 变为：
  - 无参调用：`{success:true, projects:[{name, defaultEnvironment}]}`（**不带环境**，旧客户端会拿不到 environments，属破坏性变化）。
  - 带参调用：`{success:true, projects:[{name, defaultEnvironment, environments:[详情]}]}`。
- **db_list 错误响应**：新增 `success:false` 形态（旧版无错误响应）。
- **db_query 错误响应**：上一轮加的 `availableProjects` 字段被移除，恢复纯 `success/error/errorCode`。
- 影响：仅影响 MCP 消费方（Claude Code / 其他 Agent），Admin UI（`logs.js`/`projects.js`）使用独立的 Admin HTTP API，零影响（已核实）。

## 十一、验证

- `dotnet build`：0 警告 0 错误。
- `dotnet test`：
  - 重写 `DbListToolTests` 覆盖行为矩阵全部 5 种情形。
  - 恢复 `DbQueryToolTests` 为纯 errorCode 断言。
  - 其余测试（SqlGuard、ConcurrencyLimiter 等）保持通过。

## 十二、Agent 使用流程与 Description 文案

### db_list 的 [Description] 完整文案（实现时直接采用）

```
"列出数据库项目与环境，按需加载避免返回过多数据。project 可选：不传则返回所有项目名索引(轻量，不含环境)；传项目名则返回该项目全部环境详情；同时传 environment 则返回单环境详情。environment 可选：配合 project 使用，单独传(不传 project)无意义。空白字符串等同未传。项目不存在时返回 PROJECT_NOT_FOUND 并附带 availableProjects(项目名数组)；环境不存在时返回 ENVIRONMENT_NOT_FOUND 并附带该项目 environments(环境详情数组)。返回 JSON 含 success 字段。"
```

### db_query 的 [Description] 结尾（回退后采用）

```
"...返回包含 project、environment、columns 和 rows(二维数组) 的 JSON。可先用 db_list() 获取项目列表，再 db_list(project=...) 获取环境详情。"
```

### 推荐 Agent 调用流程

1. 第一次：`db_list()` → 拿到所有项目名（轻量）。
2. 选定项目：`db_list(project="xxx")` → 拿到该项目全部环境详情（含类型、生产标识、连接/超时参数）。
3. 如已知环境：`db_list(project="xxx", environment="yyy")` → 拿到单环境详情（最轻）。
4. 任一步传错：错误响应直接回显 `availableProjects` 或该项目 `environments`，可直接据此重试，无需额外往返。
