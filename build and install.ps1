[CmdletBinding()]
# 源码部署脚本：编译 McpDbTools.Server → 委托 install.ps1 完成确认/交互/提权/安装。
# 与 install.ps1 的关系：本脚本只多做一件事——dotnet publish 到临时目录；
# 其余（停服/迁移/替换/自启动/MCP 注册）全部由 install.ps1 完成，避免逻辑重复。
# 注意：本脚本的 param 默认值、Confirm-InstallDir 文案须与 install.ps1 保持一致。
param(
    [string]$InstallDir = "E:\Software\FreeInstall\Mcp-db-Tools",
    [string]$McpName = "db-tools",
    [ValidateSet("local", "user", "project")]
    [string]$McpScope = "user",
    [string]$AdminServiceName = "McpDbTools.Admin",
    [string]$AdminTaskName = "McpDbTools.Admin",
    [switch]$PauseOnExit,
    # -Confirmed：跳过部署计划确认门（已由调用方确认）；编译后委托 install.ps1 时强制传 -Confirmed。
    [switch]$Confirmed,
    [int]$AdminPortParam = 0,
    # UseScheduledTaskParam: "yes" / "no" / "ask"（ask 表示未决定，由 install.ps1 交互询问）
    [ValidateSet("yes", "no", "ask")]
    [string]$UseScheduledTaskParam = "ask"
)

$ErrorActionPreference = "Stop"

# 禁用控制台「快速编辑模式」：防止鼠标点击窗口进入标记模式，导致脚本输出/执行长时间暂停。
# 仅在真实控制台（ConsoleHost）执行；ISE/VSCode/无控制台环境跳过；失败静默，不阻断部署。
function Disable-ConsoleQuickEdit {
    if ($Host.Name -ne 'ConsoleHost') { return }
    try {
        $win32 = 'Win32.ConsoleMode' -as [type]
        if ($null -eq $win32) {
            $sig = @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
'@
            $win32 = Add-Type -Name 'ConsoleMode' -Namespace 'Win32' -MemberDefinition $sig -PassThru -ErrorAction Stop
        }
        # STD_OUTPUT_HANDLE = -11
        $handle = $win32::GetStdHandle([int]-11)
        $mode = [uint32]0
        if (-not $win32::GetConsoleMode($handle, [ref]$mode)) { return }
        # 必须先置位 ENABLE_EXTENDED_FLAGS(0x0080)，才能关闭 ENABLE_QUICK_EDIT_MODE(0x0040)
        $mode = $mode -bor [uint32]0x0080
        $mode = $mode -band (-bnot [uint32]0x0040)
        [void]$win32::SetConsoleMode($handle, $mode)
    } catch {
        # 静默：无法禁用快速编辑不影响部署流程
    }
}

Disable-ConsoleQuickEdit

# 部署计划确认门（前置）。本函数在 install.ps1 中有一份相同文案副本，修改需同步。
function Confirm-InstallDir {
    param([string]$Dir)

    Write-Host ""
    Write-Host "================ 部署计划 ================"
    Write-Host "  安装目录: $Dir"
    Write-Host "  数据目录: $(Join-Path $env:ProgramData 'McpDbTools')  (config.json / audit.db / backups)"
    Write-Host "  MCP 名称: $McpName (作用域: $McpScope, HTTP 模式)"
    if ($AdminServiceName) { Write-Host "  服务/任务名: $AdminServiceName" }
    Write-Host "  MCP 端点: http://127.0.0.1:<端口>/mcp (端口下一步询问)"
    Write-Host "=========================================="
    $inputValue = Read-Host "确认按以上计划编译并部署？[Y/n]"

    if ([string]::IsNullOrWhiteSpace($inputValue)) {
        return $true
    }

    switch ($inputValue.Trim().ToLowerInvariant()) {
        "y" { return $true }
        "yes" { return $true }
        "是" { return $true }
        "n" { return $false }
        "no" { return $false }
        "否" { return $false }
        default { throw "无效输入: $inputValue。请输入 Y 或 n。" }
    }
}

function Resolve-RuntimeIdentifier {
    $arch = $env:PROCESSOR_ARCHITECTURE
    if ($arch -eq "ARM64") {
        return "win-arm64"
    }
    return "win-x64"
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$IgnoreExitCode
    )

    $nativeCommandPreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativeCommandPreference = $null
    $previousErrorActionPreference = $ErrorActionPreference
    if ($null -ne $nativeCommandPreference) {
        $previousNativeCommandPreference = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
    }

    try {
        $ErrorActionPreference = "Continue"
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($null -ne $nativeCommandPreference) {
            $PSNativeCommandUseErrorActionPreference = $previousNativeCommandPreference
        }
    }

    if (-not $IgnoreExitCode -and $exitCode -ne 0) {
        throw "命令执行失败，退出码 ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

$scriptRoot = Split-Path -Parent $PSCommandPath
$installScriptPath = Join-Path $scriptRoot "install.ps1"
$projectPath = Join-Path $scriptRoot "src\McpDbTools.Server\McpDbTools.Server.csproj"

# 安装目录：未通过 -InstallDir 显式传入且非自动确认模式时交互询问；回车保留默认值
if (-not $Confirmed -and -not $PSBoundParameters.ContainsKey('InstallDir')) {
    $inputDir = Read-Host "请输入安装目录（直接回车使用默认: $InstallDir）"
    if (-not [string]::IsNullOrWhiteSpace($inputDir)) {
        $InstallDir = $inputDir.Trim()
    }
}

# 1. 确认门（前置）：未带 -Confirmed 则先确认部署计划，取消则直接退出，不编译
if (-not $Confirmed) {
    $ok = Confirm-InstallDir -Dir $InstallDir
    if (-not $ok) {
        Write-Host "已取消。请使用 -InstallDir 指定其它目录后重试。"
        return
    }
}

# 2. 预检：install.ps1、项目文件、dotnet 必须存在
if (-not (Test-Path $installScriptPath)) {
    throw "未找到 install.ps1: $installScriptPath"
}
if (-not (Test-Path $projectPath)) {
    throw "未找到项目文件: $projectPath"
}
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw "未找到 dotnet 命令，请先安装 .NET 8 SDK。"
}

$runtimeIdentifier = Resolve-RuntimeIdentifier

# 临时发布目录：跨提权存活（同用户 token 的 admin 进程能读用户 temp）；本脚本 finally 负责清理。
$publishStagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("McpDbTools.publish.{0}" -f [guid]::NewGuid())
try {
    Write-Host "构建到临时目录: $publishStagingDir"
    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -Arguments @(
        "publish",
        $projectPath,
        "-c",
        "Release",
        "-r",
        $runtimeIdentifier,
        "--self-contained",
        "true",
        "-o",
        $publishStagingDir
    )

    $stagedExePath = Join-Path $publishStagingDir "McpDbTools.Server.exe"
    if (-not (Test-Path $stagedExePath)) {
        throw "发布产物中未找到服务程序: $stagedExePath"
    }

    # 3. 委托 install.ps1：本脚本已确认过 → 强制 -Confirmed（跳过其 Confirm-InstallDir，避免二次询问）。
    #    端口/承载交互由 install.ps1 在提权前完成（依据 -AdminPortParam / -UseScheduledTaskParam 是否传值决定是否提示）。
    $delegateParams = @{} + $PSBoundParameters
    $delegateParams['Confirmed'] = $true
    $delegateParams['SourceDir'] = $publishStagingDir
    # 交互输入的目录不在 PSBoundParameters 中，显式透传当前值，避免 install.ps1 回退默认
    $delegateParams['InstallDir'] = $InstallDir
    & $installScriptPath @delegateParams
}
finally {
    # 清理发布临时目录（成功失败都清理）
    if ($publishStagingDir -and (Test-Path $publishStagingDir)) {
        Remove-Item -Path $publishStagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
