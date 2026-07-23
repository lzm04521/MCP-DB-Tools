# 版本号随 git tag 自动同步与 Admin UI 展示

## 目标

引入版本号机制：当前 `0.5.1`，**随 git tag 自动同步**（打 `vX.Y.Z` tag 后，build 产物版本即 tag），并在 Admin UI topbar 醒目展示。

现状：[McpDbTools.Server.csproj](../src/McpDbTools.Server/McpDbTools.Server.csproj) 无 `<Version>`（默认 1.0.0）；[release.yml](../.github/workflows/release.yml) tag push 触发但版本只进产物包名，未注入程序集/UI；Admin UI 无版本显示；git tag 格式 `vX.Y.Z`，最新 `v0.5.1`。

## 范围

- 新增 [Directory.Build.props](../Directory.Build.props)（仓库根）— MSBuild target 从 `git describe` 注入版本
- [.github/workflows/release.yml](../.github/workflows/release.yml) — checkout 补 `fetch-tags`
- [src/McpDbTools.Server](../src/McpDbTools.Server/) — 新增 `AppVersion` 静态、`GET /admin/api/version`
- [wwwroot/admin/index.html](../src/McpDbTools.Server/wwwroot/admin/index.html) + [scripts/shell.js](../src/McpDbTools.Server/wwwroot/admin/scripts/shell.js) + styles — topbar 版本展示
- 测试项目继承 Directory.Build.props，无害（不发布）

## 方案：build 时 `git describe` 自动注入（机制 B）

build 时取最近 tag（剥 `v` 前缀）写入 `AssemblyInformationalVersion` / `<Version>`；CI 打 tag 后产物即精确版本；本地 dev 得最近 tag；无 git/tag 时 fallback csproj 基线 `0.5.1`。

版本格式：纯 `0.5.1`（剥 v）。endpoint 返回 `{ version: "0.5.1" }`。UI 渲染 `v0.5.1`（拼 v 前缀）。

不取完整 `git describe`（`0.5.1-3-gabcdef`）—— UI 显示要简洁；dev 新 commit 未打 tag 时版本保持上个 tag，下个 tag 才升。

## 实施步骤

### 1. Directory.Build.props（仓库根，新建）

```xml
<Project>
  <PropertyGroup>
    <!-- 无 git/tag 时的 fallback 基线 -->
    <Version>0.5.1</Version>
    <AssemblyInformationalVersion>$(Version)</AssemblyInformationalVersion>
  </PropertyGroup>

  <!-- build 时从最近 git tag 注入版本；无 git/tag 时保留 fallback -->
  <Target Name="SetGitVersion" BeforeTargets="PrepareForBuild">
    <Exec Command="git describe --tags --abbrev=0"
          ConsoleToMSBuild="true"
          IgnoreExitCode="true"
          StandardOutputImportance="low">
      <Output TaskParameter="ConsoleOutput" PropertyName="GitDescribe" />
    </Exec>
    <PropertyGroup Condition="'$(GitDescribe)' != '' AND $(GitDescribe) != ''">
      <GitVersion>$(GitDescribe.TrimStart('v'))</GitVersion>
      <Version>$(GitVersion)</Version>
      <AssemblyInformationalVersion>$(GitVersion)</AssemblyInformationalVersion>
    </PropertyGroup>
  </Target>
</Project>
```

说明：
- `--abbrev=0` 取干净最近 tag（不带 commit 偏移）。
- `IgnoreExitCode=true`：无 tag/非仓库/无 git 时不 fail，回落基线。
- `TrimStart('v')`：剥小写 v 前缀（tag 格式固定 `v*`）。
- `BeforeTargets="PrepareForBuild"`：早于 `GenerateAssemblyInfo`，保证 `<Version>` 生效到 `AssemblyInformationalVersion`。
- 放仓库根自动被 Server/Tests 两项目继承（MSBuild 向上查找）；Tests 跑此 target 无害。

### 2. release.yml：checkout 补 fetch-tags（**关键**）

[release.yml:36-37](../.github/workflows/release.yml#L36-L37) 改为：

```yaml
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-tags: true
```

**命门**：`actions/checkout@v4` 默认 `fetch-depth:1` 浅克隆**不带 tag**。不加 `fetch-tags: true`，CI build 时 `git describe` 取不到 tag → 回落基线 `0.5.1` → tag 永远不同步。此步漏则整个机制失效。

### 3. AppVersion 静态（新增）

新建 `src/McpDbTools.Server/AppVersion.cs`：

```csharp
using System.Reflection;

namespace McpDbTools.Server;

/// <summary>
/// 当前应用版本，从 AssemblyInformationalVersion 读取（build 时由 git tag 注入）。
/// 读取失败回落基线 0.5.1。
/// </summary>
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.5.1";
}
```

### 4. GET /admin/api/version endpoint

[Program.cs](../src/McpDbTools.Server/Program.cs) 的 `/admin/api` 路由组新增（与 `/config`、`/maintenance` 同级）：

```csharp
api.MapGet("/version", () => Results.Ok(new { version = AppVersion.Current }));
```

### 5. Admin UI topbar 展示（醒目）

- [index.html](../src/McpDbTools.Server/wwwroot/admin/index.html) topbar `eyebrow` span 旁加：

```html
<span id="appVersion" class="app-version">v?</span>
```

- [shell.js](../src/McpDbTools.Server/wwwroot/admin/scripts/shell.js) 启动时 fetch 填充（实施时读 shell.js 确认 init 钩子位置，与 configPath 同阶段）：

```javascript
const { version } = await window.adminApi.requestJson('/admin/api/version');
el.appVersion.textContent = `v${version}`;
```

- styles（components.css 或 tokens）加 `.app-version` 轻量 badge：主色调文字 + 淡背景 + 圆角 padding，比 `configPath` 更跳。例：

```css
.app-version {
  margin-left: .5rem;
  padding: .1rem .45rem;
  border-radius: .375rem;
  font-size: .75rem;
  font-weight: 600;
  color: var(--accent-fg, #fff);
  background: var(--accent, #2563eb);
}
```

实施时按 [tokens.css](../src/McpDbTools.Server/wwwroot/admin/styles/tokens.css) 既有变量对齐。

## Dedupe Ticket

**Intent signature**：`从 git tag 推导版本号注入程序集 + 暴露给 UI`。

**Queries**：
- 项目有无现成版本机制？→ csproj 无 `<Version>`；grep `Version|AssemblyInformationalVersion|AppVersion` 源码无版本常量。
- 有无现成 `/admin/api/version` 或 meta endpoint？→ [Program.cs](../src/McpDbTools.Server/Program.cs) 路由清单无。
- 有无 `Directory.Build.props`？→ 根目录无。

**Top matches**：无重复实现。

**Decision**：新增 `Directory.Build.props` + `AppVersion` + `/version` endpoint + UI span，不替换任何既有逻辑。

**Rationale**：项目首个版本机制，无冲突。

## 风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| CI checkout 不带 tag | tag 永不同步，停在 0.5.1 | §2 强制 `fetch-tags: true` |
| 本地无 git / 非 git 仓库 | fallback 0.5.1 | `IgnoreExitCode=true` + 基线 |
| 浅克隆源码包（zip 下载）build | 无 .git，fallback | 可接受，源码包本非 release 路径 |
| `git describe` 取到非预期 tag（如 `Release` tag） | 版本字符串异常 | tag 格式约定 `vX.Y.Z`；现有 `Release` tag 无 `v` 前缀，`describe --abbrev=0` 取最近 reachable tag——若 `Release` 在 HEAD 祖先且比 vX.Y.Z 更近会误取。实施时验证 `git describe` 实际输出 |
| Tests 项目继承 target | test build 多跑 git | 无害，不阻断 |

> 实施时第一步先在本机跑 `git describe --tags --abbrev=0` 确认输出 `v0.5.1`（而非 `Release`），再写 target。

## 测试

[src/McpDbTools.Tests](../src/McpDbTools.Tests/) 新增 `AppVersionTests`：

- `Current_NotNullOrEmpty` — 非空
- `Current_MatchesSemVer` — 匹配 `\d+\.\d+\.\d+` 起始（允许 build 时 git 注入偏移但本机取干净 tag）

endpoint 为 Program lambda，不单测；通过 `AppVersion.Current` 间接覆盖。

## 验证

```bash
# 1. 确认本机 git describe 输出
git describe --tags --abbrev=0    # 应为 v0.5.1

# 2. build 后版本注入
dotnet build src/McpDbTools.Server/McpDbTools.Server.csproj
# 检查产物 InformationalVersion
cat src/McpDbTools.Server/bin/Debug/net8.0/win-x64/McpDbTools.Server.dll 2>/dev/null | strings | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' || true

# 3. 起本机 admin，curl 验证
dotnet run --project src/McpDbTools.Server/McpDbTools.Server.csproj -- --admin-only --admin-port 5123 &
curl -s http://127.0.0.1:5123/admin/api/version   # 应 {"version":"0.5.1"}

# 4. 单元测试
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj --filter "FullyQualifiedName~AppVersion"
dotnet test src/McpDbTools.Tests/McpDbTools.Tests.csproj   # 全量不回归
```

## 回滚

- 删 `Directory.Build.props` → 版本回默认 1.0.0
- revert release.yml `fetch-tags` 行
- 删 `AppVersion.cs`、`/version` endpoint、index.html `#appVersion` span、shell.js fetch、styles 类

无数据/配置迁移，单分支 revert 即可。

## 未覆盖

- 不做 SemVer 高级特性（pre-release `-beta`、commit count、build metadata）—— 只取干净 tag。
- 不引入 MinVer/GitVersion 等 NuGet 包（零外部依赖原则）。
- MCP stdio 模式不暴露版本（仅 Admin UI；Agent 无版本需求）。
- 不在日志输出加版本（如需可后续在 Program 启动时 stderr 打一行）。
