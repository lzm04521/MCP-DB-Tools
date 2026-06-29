# MCP Database Tools — 并发健壮性加固

> 版本：v1.0 | 日期：2026-06-28 | 状态：已批准，实施中

---

## 1. 背景与目标

`db_query` 工具在高并发下存在多个"卡住"风险点。经端到端追踪确认，应用层此前**没有任何并发控制**，全部依赖下游驱动默认值：

| 风险点 | 现状 | 触发条件 |
|--------|------|---------|
| 连接池耗尽 | 走驱动默认（池上限 100、连接超时 15s），不可配置 | 并发查询 > 100，尤其含慢查询 |
| 慢查询占池 | `CommandTimeout` 默认 30s，期间独占一条连接 | 单条慢 SELECT 长时间占池 |
| 审计写锁竞争 + 线程池饥饿 | 每次查询后**同步**写 SQLite，阻塞返回线程 | 高并发写审计排队 |
| 无应用层限流 | 完全无闸门 | 压力无差别打到数据库 |
| 建连阶段无超时兜底 | `ExecuteQueryAsync.OpenAsync` 无超时，仅靠驱动超时 | 池耗尽时卡满 15s |

本次加固按三项已批准决策落地：
- **限流**：每环境独立 `SemaphoreSlim`，超载**排队等待**（默认上限 8，等待超时 5s）。
- **连接池**：新增**全局默认 + 环境级覆盖**字段（默认池 100、连接超时 15s）。
- **审计**：**Channel + 单后台消费者**异步化。

外加配套的 `ExecuteQueryAsync` 建连阶段超时兜底。

---

## 2. 配置设计

### 2.1 新增配置字段（config.json）

**全局默认**（`databases` 同级）：

| 字段 | 类型 | 内置默认 | 说明 |
|------|------|---------|------|
| `defaultMaxConcurrency` | int? | 8 | 每环境最大并发查询数 |
| `defaultMaxConcurrencyWaitSeconds` | int? | 5 | 超载排队最长等待秒数 |
| `defaultMaxPoolSize` | int? | 100 | 连接池上限 |
| `defaultConnectTimeoutSeconds` | int? | 15 | 建连超时秒数 |

**环境级覆盖**（`environments.<env>` 下）：

| 字段 | 类型 | 缺省行为 |
|------|------|---------|
| `maxConcurrency` | int | `<=0` 回退全局默认 |
| `maxPoolSize` | int | `<=0` 回退全局默认 |
| `connectTimeoutSeconds` | int | `<=0` 回退全局默认 |

### 2.2 配置样例

```jsonc
{
  "defaultMaxConcurrency": 8,            // 每环境默认最大并发查询数
  "defaultMaxConcurrencyWaitSeconds": 5, // 超载排队等待秒数
  "defaultMaxPoolSize": 100,             // 默认连接池上限
  "defaultConnectTimeoutSeconds": 15,    // 默认建连超时秒数
  "databases": {
    "erp": {
      "defaultEnvironment": "prod",
      "environments": {
        "prod": {
          "type": "sqlserver",
          "connectionString": "Server=.;Database=ERP;User Id=sa;Password=xxx;",
          "maxRows": 1000,
          "commandTimeout": 30,
          "maxConcurrency": 16,           // 高配库：允许更多并发
          "maxPoolSize": 50,              // 该环境专用池上限
          "connectTimeoutSeconds": 10
        }
      }
    }
  }
}
```

### 2.3 向后兼容

所有新字段缺省 = `0`（环境级）或 `null`（全局），自动走内置默认，**旧 `config.json` 零改动行为不变**。

---

## 3. 设计详情

### 3.1 应用层并发限流（`QueryConcurrencyLimiter`）

**作用域**：每 `(project, env)` 维度一个 `SemaphoreSlim`，存于 `ConcurrentDictionary`。

**流程**：
1. `db_query` 通过 SqlGuard 后，调用 `limiter.AcquireAsync(project, env, db, ct)`。
2. 内部 `SemaphoreSlim.WaitAsync(db.MaxConcurrencyWaitSeconds, ct)`：
   - 拿到槽位 → 返回 `IAsyncDisposable` 令牌（释放槽位）。
   - 超时 → 抛 `QueryRateLimitedException`，`DbQueryTool` 捕获后返回 `RATE_LIMITED` 错误码并记审计。
3. `await using` 保证槽位释放，即使查询抛异常。

**热重载**：`ResolvedDatabase` 每次由 `GetResolved()` 重建，`MaxConcurrency` 变更时按需重建对应环境的信号量（key 相同但上限不同 → 重建）。

**隔离性**：不同 `(project, env)` 互不阻塞，慢库不拖累其他库。

### 3.2 连接串拼接

在 `ResolvedConfigBuilder.Build` 中，按数据库类型用各驱动官方 `*ConnectionStringBuilder` 解析后写入池/超时参数：

| 类型 | 驱动 | 池上限键 | 建连超时键 |
|------|------|---------|-----------|
| SqlServer | `Microsoft.Data.SqlClient.SqlConnectionStringBuilder` | `Max Pool Size` | `Connect Timeout` |
| MySql | `MySqlConnector.MySqlConnectionStringBuilder` | `Maximum Pool Size` | `Connection Timeout` |
| Oracle | `Oracle.ManagedDataAccess.Core.OracleConnectionStringBuilder` | `Max Pool Size` | `Connection Timeout` |

**原则**：仅在用户配置值与驱动默认不同时写入，避免无谓覆盖。解析失败（畸形串）则保留原串并记 Warning，不阻断查询。

最终拼好的连接串放入 `ResolvedDatabase.ConnectionString`，供 `ExecuteQueryAsync` 直接使用；`DatabaseConfig`（Admin 读写）保留用户原始串。

### 3.3 建连阶段超时兜底

`ExecuteQueryAsync` 在 `OpenAsync` 阶段仿照同文件 `TestConnectionAsync` 已有写法，用 `CreateLinkedTokenSource(ct)` + `CancelAfter(db.ConnectTimeoutSeconds)` 包裹：

- 超时 → 返回 `QueryResult.Fail(..., "QUERY_CONNECT_TIMEOUT", ...)`。
- 外部取消 → 向上传播（不包装）。

`CommandTimeout`（执行阶段）保持不变。

### 3.4 审计异步化（`AuditLogger`）

- **写入路径**：`Log(entry)` 不再同步写库，而是补齐时间后 `_channel.Writer.TryWrite(entry)` 入无界队列。
- **后台消费者**：单 `Task` 循环 `await _channel.Reader.ReadAllAsync(ct)`，串行执行原 INSERT 逻辑（抽为 `WriteEntryCore`）。串行 → 彻底消除 SQLite 写锁竞争。
- **降级**：入队失败（理论上无界不会失败）→ 降级同步写 + 记 Error，保证至少一次。
- **生命周期**：
  - 实现 `IDisposable`：`Writer.Complete()` 后等待消费者完成（5s 软超时），供同步测试与 Host 退出排空。
  - 实现 `IAsyncDisposable`：异步版同上。
  - Host dispose 单例时触发，保证进程退出不丢日志。
- **可见性**：`Log` 同步签名不变，测试在 `Log` 后 `Dispose` 触发排空再查询。

---

## 4. 错误码

| 错误码 | 触发 | 返回信息 |
|--------|------|---------|
| `RATE_LIMITED` | 并发限流排队超时 | "并发过高，等待 N 秒未获得执行槽位，请稍后重试" |
| `QUERY_CONNECT_TIMEOUT` | 建连阶段超时 | "连接超时（N 秒）" |

---

## 5. 验证清单

1. `dotnet build src/McpDbTools.Server/McpDbTools.Server.csproj` — 编译通过。
2. `dotnet test` — 全部测试通过（含新增与适配）。
3. 向后兼容：`config.json` 不带新字段时行为与改动前完全一致。
4. Admin UI：保存含新字段配置后，`config.json` 与重新加载的 `ResolvedDatabase` 一致。

---

## 6. 风险与回滚

- **向后兼容**：所有新字段缺省走内置默认，旧 `config.json` 零改动行为不变。
- **连接串拼接**：用各驱动官方 builder 解析，避免重复/非法键；解析失败保留原串并记 Warning，不阻断。
- **审计异步**：进程异常崩溃（非 Host 正常退出）可能丢失队列内尚未落盘的少量审计；可接受（审计非业务关键路径，且原同步方案遇 SQLITE_BUSY 也会丢）。
- **回滚**：每阶段独立，可单独 revert。

---

## 7. 改动文件清单

| 文件 | 改动 |
|------|------|
| `doc/concurrency-hardening.md` | 新增（本文档） |
| `Configuration/DatabaseConfig.cs` | `DatabaseConfig` 加 3 环境级字段；`DatabasesConfig` 加 4 全局默认字段 |
| `Configuration/ResolvedConfig.cs` | `ResolvedDatabase` 加 resolved 字段；`ResolvedConfigBuilder` 合并默认值 + 连接串拼接 |
| `Database/QueryConcurrencyLimiter.cs` | 新增：限流器 + 异常类 |
| `Database/IDatabaseProvider.cs` | `ExecuteQueryAsync` 建连超时兜底 |
| `Tools/DbQueryTool.cs` | 注入限流器 + `RATE_LIMITED` 处理 |
| `Program.cs` | 注册 `IQueryConcurrencyLimiter` |
| `Audit/AuditLogger.cs` | Channel 异步化 + Dispose 排空 |
| `Admin/AdminConfigModels.cs` | DTO 加新字段 |
| `Admin/AdminConfigService.cs` | Get/Save 映射新字段 |
| `wwwroot/admin/scripts/projects.js` | 表单渲染新字段 |
| `config.json` | 注释样例 |
| `Tests/AuditLoggerTests.cs` | `Log` 后 `Dispose` 排空 |
| `Tests/DbQueryToolTests.cs` | 构造传入限流器 + `RATE_LIMITED` 用例 |
| `Tests/ConfigMergeTests.cs` | 新字段默认值/覆盖用例 |
| `Tests/QueryConcurrencyLimiterTests.cs` | 新增 |
