---
name: db-query
description: >
  使用 db-tools MCP（mcp__db-tools__db_list / mcp__db-tools__db_query / mcp__db-tools__db_schema /
  mcp__db-tools__db_explain）查询多项目多环境数据库时的参考 skill。
  覆盖：项目/环境两级层级与实时核验；db_schema 元数据探索（表清单/列/索引/外键，替代手写四方言
  information_schema 系 SQL）；db_query 的 offset 分页与 dryRun 写影响预估；db_explain 执行计划
  （慢查询分析）；SQL Server/MySQL/Oracle/PostgreSQL 四 DBMS 的 schema（列）查询、索引查询、
  分页语法、字符串拼接差异；maxRows 静默截断与索引逐环境核验；写业务 SQL 前的 schema 验证；
  可写库谨慎；可写 Prod 环境生产变更（索引创建/清理、批量 UPDATE/DELETE）强制四件套（执行前预检查、
  回滚备份、执行后数量对账、对账通过才可报完成）；性能计数器列名特例；审批人/单据快照类 SQL 的范围确认。
  触发词：查数据库、连库、查表结构、查列、查索引、查 schema、写 SQL、拼 SQL、分页、执行计划、
  慢查询、SQL 很慢、explain、影响行数、预估、dryRun、采样、表清单、元数据、加索引、生产库、
  生产变更、批量 UPDATE、批量 DELETE、四件套、回滚备份、数量对账、BLHProd、SQL Server、
  MySQL、Oracle、PostgreSQL、性能计数器、审批人快照、单据快照、db_list、db_query、db_schema、
  db_explain、db-tools；
  以及"用 DB Prod/Test/UAT/Dev 环境验证"、"DB Prod 环境"、"Prod 环境"、"去数据库验证/确认"、
  "查生产数据/生产库"、"验证一下数据"、"确认数据"等带环境名或"验证/确认"措辞的查库请求。
  只要调用 db-tools 任一工具（db_list / db_query / db_schema / db_explain）就应加载本 skill。
---

# db-tools 数据库查询参考

本 skill 配套 db-tools MCP，给出安全查询多项目多环境数据库的参考。核心强制约束（实时核验、schema 验证、范围确认、生产四件套）以本 skill 为准；已执行 `/init-project` 的项目，同一套约束也写在其 `AGENTS.md` 的「数据库」章节，两处内容一致。

## 工具与层级

- `mcp__db-tools__db_list()`：列出所有 project。
- `mcp__db-tools__db_list(project=...)`：列出该 project 下的 environment 与 DBMS type。
- `mcp__db-tools__db_schema(project, environment?, table?, sample?)`：元数据探索——不传 `table` 返回表清单；传 `table` 返回该表列/索引/外键三段（`sample>0` 附采样行）。**优先用它替代手写 information_schema/all_tab_columns 方言 SQL**。Oracle 对象名大写存储。
- `mcp__db-tools__db_explain(project, sql, environment?)`：执行计划（慢查询分析）——仅只读语句（写语句返回 SQL_BLOCKED）；返回计划行集（text：状态行 + 列名 + TSV，与 db_query 同构）。
- `mcp__db-tools__db_query(project, sql, environment?, limit?, format?, offset?, dryRun?)`：执行 SQL（读或写环境）。`project` 必填；`environment` 不传走 `defaultEnvironment`（通常 Test）；`limit` 与环境 `maxRows` 取较小值；`format` 三档 text（缺省纯文本）/tsv/json（见「返回格式」）；`offset` 分页跳行（见下文）；`dryRun` 写影响预估（见下文）。

层级为 project → environment 两级，无扁平环境名，且环境常增删变化。

## 每次查询必做的强制约束

- **实时核验**：每次查询前先 `db_list` 列项目、`db_list(project=...)` 列环境与 DBMS type。不缓存、不假设、不写死环境名。
- **可写库谨慎**：环境返回标识区分可写/只读；可写库谨慎操作。
- **写前验 schema**：写业务 SQL 前强制 schema 验证——优先 `db_schema(project, table=...)` 查列（方言无关），避免列名错误。
- **写前预估影响**：写环境执行 UPDATE/DELETE 前先 `db_query(..., dryRun=true)` 预估影响行数，超过预期先缩小 WHERE 范围。
- **写前定范围**：写审批人/单据快照类 SQL 前，先确认范围是"仅更新已有单据快照"还是"含设置表"，避免越界改设置表。
- **生产变更四件套**：可写 Prod 环境（如 BLHProd）执行索引创建/清理、批量 UPDATE/DELETE 等变更时强制——① 执行前预检查：阻塞会话、后台作业依赖（如 HangFire）逐一排除；② 脚本必须附带回滚备份：被删索引保留完整定义；③ 执行后数量对账：created/dropped/affected 与计划完全一致，不一致立即报告并给出补救 SQL；④ 对账通过才可报完成。

## 四 DBMS 语法速查

### schema（列）

优先用 `db_schema(project, table=...)`（列/索引/外键一次返回，方言无关）。手写方言查询仅作 fallback 参考：

| DBMS | 查列 |
|---|---|
| sqlserver | `INFORMATION_SCHEMA.COLUMNS` |
| oracle | `ALL_TAB_COLUMNS` / `ALL_COL_COMMENTS` |
| mysql | `information_schema.COLUMNS` |
| postgresql | `information_schema.columns` |

### 索引

优先用 `db_schema(project, table=...)` 的 `indexes` 段（方言无关）。手写方言查询仅作 fallback 参考：

| DBMS | 查索引 |
|---|---|
| sqlserver | `sys.indexes` / `sys.index_columns` / `sys.dm_db_index_usage_stats` |
| oracle | `ALL_IND_COLUMNS` / `USER_INDEXES` |
| mysql | `SHOW INDEX FROM <表>` / `information_schema.STATISTICS` |
| postgresql | `pg_indexes` / `pg_stat_user_indexes` |

### 分页

**优先用 db_query 的 `offset` 参数**，工具按方言自动拼接，无需手写分页子句；截断时状态行标 `[truncated, nextOffset=N]` 直接续翻。约束：仅读语句；SQL Server/Oracle 的 SQL 必须带 `ORDER BY`；SQL 已自带 `LIMIT`/`OFFSET`/`FOR UPDATE` 或多语句时先去掉再传 offset。手写方言分页仅作参考：

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

## 返回格式

四工具缺省返回 **text 纯文本**（省 token，无 JSON 外壳）：

- **读成功（db_query / db_explain）**：首行状态行 `OK {行数} rows @项目/环境 (类型[, offset=N]) {耗时} [truncated, nextOffset=M]`（耗时如 `823ms`/`12.3s`），第 2 行列名，其后数据行 TSV——制表符分列、换行分行、`\N` 表示 NULL、空字段为空字符串；值内 tab/换行/反斜杠转义为 `\t`/`\n`/`\r`/`\\`；二进制列显示 `<binary NB>`（json 档保留 base64）。读语句 ≥5s 时输出末尾附 `提示: 慢查询 …，建议用 db_explain 分析执行计划`。
- **写成功**：`OK {N} affected @项目/环境 (类型) {耗时}` 单行。
- **dryRun 预估**：`OK ~{N} affected (estimated) @项目/环境 (类型) {耗时}` 单行。
- **失败**：`FAIL {错误码} @项目/环境: {错误消息}`（消息可能多行跟随）。
- **db_schema**：首行 `OK tables @项目/环境 (类型)`（表模式为 `table=名`），其后每段以 `# 段名 (行数)` 起始 + 列名行 + TSV 数据。
- **db_list**：项目索引每项目一行 `name (default env)`；环境详情每环境一行（`name→type→databaseName→prod=y|n→write=y|n→maxRows=N`，tab 分列）。

结构化回退：db_query 传 `format="tsv"`（JSON 壳 + rowset）或 `format="json"`（JSON 壳 + rows 二维数组，需精确结构时用）；db_list 传 `format="json"`。db_schema/db_explain 不开放 format 参数。

## 写影响预估（dryRun）

写环境执行 `UPDATE`/`DELETE` 前，先 `dryRun=true` 预估影响行数（工具变换为 `COUNT` 只读查询，不执行写），返回 `OK ~N affected (estimated)` 状态行；超过预期就缩小 WHERE 范围。限制：仅标准形态（无别名/单表/SET 无子查询）；INSERT/DDL/复杂形态返回 `DRYRUN_UNSUPPORTED`；不可与 `limit`/`offset` 同用。估算不含触发器影响，审计记 `[dryRun]` 前缀。

## 执行计划（db_explain，慢查询分析）

慢 SQL 分析工作流：`db_explain(project, sql)` 看计划（仅只读语句）→ 关注全表扫描/大行数估算节点 → 疑缺索引时用 `db_schema(project, table=...)` 的 `indexes` 段核对现状（注意索引逐环境差异）→ 需要加索引时给出 DDL 建议由人确认执行。SQL Server 返回 SHOWPLAN 计划列（不实际执行）；Oracle 经 DBMS_XPLAN 输出；不支持 EXPLAIN ANALYZE。

## 截断与误判陷阱

- `maxRows` 默认 1000（以环境返回为准）。大结果必须限行或分页，否则被静默截断导致误判；截断时状态行标 `[truncated, nextOffset=N]`，用 `offset` 参数（或该 nextOffset）续翻。
- 索引部署因环境而异——索引存在性、使用情况要逐环境核验，不能拿一个环境的结果推广到全部。

## 特殊 case

- **性能计数器**：列名是 `counter_name`（非 `Counter`）。写分析 SQL 前先快查验证 schema，别按记忆写列名。
- **审批人/单据快照**：见上文「写前定范围」——先确认是"仅更新已有单据快照"还是"含设置表"。

## 输出

给出可直接执行的 SQL 时，附上依据的 schema 查询结果（列名/类型来源），让结果可复核。涉及写操作时，明确写出已确认的范围边界。