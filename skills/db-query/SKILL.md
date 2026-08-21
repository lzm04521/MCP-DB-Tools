---
name: db-query
description: >
  使用 db-tools MCP（mcp__db-tools__db_list / mcp__db-tools__db_query）查询多项目多环境数据库时的参考 skill。
  覆盖：项目/环境两级层级与实时核验；SQL Server/MySQL/Oracle/PostgreSQL 四 DBMS 的 schema（列）查询、
  索引查询、分页语法、字符串拼接差异；maxRows 静默截断与索引逐环境核验；写业务 SQL 前的 schema 验证；
  可写库谨慎；性能计数器列名特例；审批人/单据快照类 SQL 的范围确认。
  触发词：查数据库、连库、查表结构、查列、查索引、查 schema、写 SQL、拼 SQL、分页、SQL Server、
  MySQL、Oracle、PostgreSQL、性能计数器、审批人快照、单据快照、db_list、db_query、db-tools；
  以及"用 DB Prod/Test/UAT/Dev 环境验证"、"DB Prod 环境"、"Prod 环境"、"去数据库验证/确认"、
  "查生产数据/生产库"、"验证一下数据"、"确认数据"等带环境名或"验证/确认"措辞的查库请求。
  只要调用 mcp__db-tools__db_list 或 mcp__db-tools__db_query 就应加载本 skill。
---

# db-tools 数据库查询参考

本 skill 配套 db-tools MCP，给出安全查询多项目多环境数据库的参考。核心强制约束同时在全局 `CLAUDE.md` 常驻（实时核验、schema 验证、范围确认），本 skill 负责详细语法速查与 case。

## 工具与层级

- `mcp__db-tools__db_list()`：列出所有 project。
- `mcp__db-tools__db_list(project=...)`：列出该 project 下的 environment 与 DBMS type。
- `mcp__db-tools__db_query(project, sql, environment?, limit?, format?)`：执行 SQL（读或写环境）。`project` 必填；`environment` 不传走 `defaultEnvironment`（通常 Test）；`limit` 与环境 `maxRows` 取较小值；`format` 传 `"json"` 回退 rows 二维数组，缺省 TSV。

层级为 project → environment 两级，无扁平环境名，且环境常增删变化。

## 每次查询必做的强制约束

- **实时核验**：每次查询前先 `db_list` 列项目、`db_list(project=...)` 列环境与 DBMS type。不缓存、不假设、不写死环境名。
- **可写库谨慎**：环境返回标识区分可写/只读；可写库谨慎操作。
- **写前验 schema**：写业务 SQL 前强制 schema 验证（先查列名），避免列名错误。
- **写前定范围**：写审批人/单据快照类 SQL 前，先确认范围是"仅更新已有单据快照"还是"含设置表"，避免越界改设置表。

## 四 DBMS 语法速查

### schema（列）

| DBMS | 查列 |
|---|---|
| sqlserver | `INFORMATION_SCHEMA.COLUMNS` |
| oracle | `ALL_TAB_COLUMNS` / `ALL_COL_COMMENTS` |
| mysql | `information_schema.COLUMNS` |
| postgresql | `information_schema.columns` |

### 索引

| DBMS | 查索引 |
|---|---|
| sqlserver | `sys.indexes` / `sys.index_columns` / `sys.dm_db_index_usage_stats` |
| oracle | `ALL_IND_COLUMNS` / `USER_INDEXES` |
| mysql | `SHOW INDEX FROM <表>` / `information_schema.STATISTICS` |
| postgresql | `pg_indexes` / `pg_stat_user_indexes` |

### 分页

| DBMS | 分页 |
|---|---|
| sqlserver | `OFFSET ... FETCH NEXT` 或 `TOP n` |
| oracle | `ROWNUM` 或 `OFFSET-FETCH`（12c+） |
| mysql | `LIMIT n OFFSET m` |
| postgresql | `LIMIT n OFFSET m`（无 `TOP`） |

### 字符串拼接

| DBMS | 拼接 |
|---|---|
| sqlserver | `+` |
| oracle | `\|\|` |
| mysql | `CONCAT()`（默认 `\|\|` 为逻辑或，除非开 `PIPES_AS_CONCAT`） |
| postgresql | `\|\|`（与 MySQL 相反） |

PostgreSQL 标识符未加引号会折叠为小写；不区分大小写的模糊匹配用 `ILIKE`，不是 `LIKE`。

## 返回格式（db_query）

- 默认（TSV）：`columns` 列名数组 + `rowset` TSV 文本——制表符分列、`\n` 分行、`\N` 表示 NULL、空字段为空字符串；值内的 tab/换行/反斜杠转义为 `\t`/`\n`/`\r`/`\\`。
- `format="json"`：回退 `rows` 二维数组（需要精确结构时用）。
- 读结果带 `rowCount`/`truncated`；写结果带 `affectedRows`；失败带 `error`/`errorCode`/`executionTimeMs`。

## 截断与误判陷阱

- `maxRows` 默认 1000（以环境返回为准）。大结果必须限行或分页，否则被静默截断导致误判。
- 索引部署因环境而异——索引存在性、使用情况要逐环境核验，不能拿一个环境的结果推广到全部。

## 特殊 case

- **性能计数器**：列名是 `counter_name`（非 `Counter`）。写分析 SQL 前先快查验证 schema，别按记忆写列名。
- **审批人/单据快照**：见上文「写前定范围」——先确认是"仅更新已有单据快照"还是"含设置表"。

## 输出

给出可直接执行的 SQL 时，附上依据的 schema 查询结果（列名/类型来源），让结果可复核。涉及写操作时，明确写出已确认的范围边界。
