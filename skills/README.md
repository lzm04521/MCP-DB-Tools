# Skills（mcp-db-tools Marketplace）

本目录是 `mcp-db-tools` marketplace 的 skill 源。每个子目录是一个可被 Claude Code 加载的 skill，随 plugin `db-tools` 一起安装。

## 目录规范

    skills/
    └── <skill-name>/        # kebab-case，与 SKILL.md frontmatter 的 name 一致
        ├── SKILL.md         # 必须，skill 主体
        └── references/      # 可选，skill 引用的辅助文件（按需）

## SKILL.md 格式

frontmatter 用 YAML 三横线包围，只需 `name` + `description`：

    ---
    name: <skill-name>           # 与目录名一致，kebab-case
    description: >
      <model-facing 触发说明：何时用、覆盖什么、触发词。
       Claude 靠这段决定是否调用本 skill，要写满触发措辞。>
    ---

    # <标题>

    ## 何时触发
    ## 执行流程
    ## 输出

正文按 skill 需要组织。MCP 工具类 skill 可参考：定位 → 易踩坑概念 → 工具路由表 → 安全约束。

## 新增 skill（3 步）

1. 建 `skills/<skill-name>/SKILL.md`（按上面的 frontmatter 模板填写）。
2. 在 `.claude-plugin/plugin.json` 的 `skills` 数组追加该 skill 的相对路径：

       "skills": ["./skills/<skill-name>"]

   多个 skill 用逗号分隔，例如 `["./skills/db-tools", "./skills/audit-log"]`。
3. commit + push。已安装的用户执行 `/plugin marketplace update mcp-db-tools` 后重装即可拿到新 skill。

## 安装与更新（使用者）

    # 添加 marketplace（本仓库）
    /plugin marketplace add lzm04521/MCP-DB-Tools

    # 安装 plugin（含其声明在 plugin.json 的全部 skills）
    /plugin install db-tools@mcp-db-tools

    # 仓库更新后，拉取最新 marketplace 清单
    /plugin marketplace update mcp-db-tools

> 当前 `plugin.json` 的 `skills` 为空 —— 尚未发布具体 skill。按上面「新增 skill」加入第一个 skill 并填好路径后即可正常安装。
