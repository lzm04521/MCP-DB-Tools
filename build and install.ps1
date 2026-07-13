[CmdletBinding()]
param(
    [string]$InstallDir = "E:\Software\FreeInstall\Mcp-db-Tools",
    [string]$McpName = "db-tools",
    [ValidateSet("local", "user", "project")]
    [string]$McpScope = "user",
    [string]$AdminServiceName = "McpDbTools.Admin",
    [string]$AdminTaskName = "McpDbTools.Admin",
    [switch]$PauseOnExit,
    # 以下参数由提权前的交互式询问结果填充，提权后跳过对应询问
    [switch]$Confirmed,
    [int]$AdminPortParam = 0,
    # UseScheduledTaskParam: "yes" / "no" / "ask"（ask 表示未决定，需要交互询问）
    [ValidateSet("yes", "no", "ask")]
    [string]$UseScheduledTaskParam = "ask"
)

$ErrorActionPreference = "Stop"

function Resolve-RuntimeIdentifier {
    $arch = $env:PROCESSOR_ARCHITECTURE
    if ($arch -eq "ARM64") {
        return "win-arm64"
    }
    return "win-x64"
}

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-PowerShellLiteral {
    param([string]$Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

function Show-ExitPause {
    if (-not $PauseOnExit) {
        return
    }

    Write-Host ""
    [void](Read-Host "管理员窗口执行结束，按回车关闭")
}

function Resolve-PowerShellHostPath {
    $pwshCommand = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -ne $pwshCommand) {
        return $pwshCommand.Source
    }

    $currentProcessPath = (Get-Process -Id $PID).Path
    if (-not [string]::IsNullOrWhiteSpace($currentProcessPath)) {
        return $currentProcessPath
    }

    $powershellCommand = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($null -ne $powershellCommand) {
        return $powershellCommand.Source
    }

    throw "未找到可用的 PowerShell 宿主（pwsh 或 powershell.exe）。"
}

function Restart-AsAdministrator {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [hashtable]$Choices = @{}
    )

    if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
        throw "无法确定当前脚本路径，不能自动提权。请在管理员 PowerShell 中重新运行。"
    }

    $commandParts = @(
        "&",
        (ConvertTo-PowerShellLiteral $ScriptPath),
        "-InstallDir",
        (ConvertTo-PowerShellLiteral $InstallDir),
        "-McpName",
        (ConvertTo-PowerShellLiteral $McpName),
        "-McpScope",
        (ConvertTo-PowerShellLiteral $McpScope),
        "-AdminServiceName",
        (ConvertTo-PowerShellLiteral $AdminServiceName),
        "-AdminTaskName",
        (ConvertTo-PowerShellLiteral $AdminTaskName),
        "-Confirmed",
        "-PauseOnExit"
    )

    # 把提权前的交互式选择透传给提权后的进程，避免重复询问
    if ($Choices.ContainsKey("AdminPort") -and $Choices.AdminPort -gt 0) {
        $commandParts += @("-AdminPortParam", ($Choices.AdminPort.ToString()))
    }
    if ($Choices.ContainsKey("UseScheduledTask")) {
        $commandParts += @("-UseScheduledTaskParam", (ConvertTo-PowerShellLiteral $Choices.UseScheduledTask))
    }

    $encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes(($commandParts -join " ")))
    $powerShellPath = Resolve-PowerShellHostPath

    Write-Host "当前不是管理员模式，正在请求管理员权限重新运行脚本..."
    Write-Host "已转交管理员窗口继续执行，请在弹出的 UAC/管理员 PowerShell 窗口中查看部署过程。"
    $process = Start-Process `
        -FilePath $powerShellPath `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) `
        -WorkingDirectory $WorkingDirectory `
        -Verb RunAs `
        -PassThru `
        -ErrorAction Stop
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "管理员进程执行失败，退出码: $($process.ExitCode)"
    }

    return $true
}

function Read-AdminPort {
    $defaultPort = 61123
    $inputValue = Read-Host "请输入 Admin UI 端口号，直接回车使用默认 $defaultPort"
    if ([string]::IsNullOrWhiteSpace($inputValue)) {
        return $defaultPort
    }

    $port = 0
    if (-not [int]::TryParse($inputValue.Trim(), [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        throw "无效端口号: $inputValue。端口必须在 1-65535 之间。"
    }

    return $port
}

function Read-UseScheduledTaskFallback {
    $inputValue = Read-Host "未找到 nssm。是否使用计划任务在登录时启动 Admin UI？[Y/n]"
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

function Confirm-InstallDir {
    param([string]$Dir)

    # 显示关键部署信息，等用户确认后再提权，避免 UAC 弹窗后才发现路径不对
    Write-Host ""
    Write-Host "================ 部署计划 ================"
    Write-Host "  安装目录: $Dir"
    Write-Host "  数据目录: $(Join-Path $env:ProgramData 'McpDbTools')  (config.json / audit.db / backups)"
    Write-Host "  MCP 名称: $McpName (作用域: $McpScope)"
    if ($AdminServiceName) { Write-Host "  服务/任务名: $AdminServiceName" }
    Write-Host "=========================================="
    $inputValue = Read-Host "确认按以上计划部署？[Y/n]"

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

function Read-InteractiveChoices {
    # 提权前完成所有交互式询问，返回一个 hashtable 供提权命令拼装
    $choices = @{}

    # 1. nssm 探测 + 计划任务回退询问（只决定是否用计划任务；nssm 存在与否由管理员进程再次探测）
    $nssmCommand = Get-Command nssm -ErrorAction SilentlyContinue
    if ($null -eq $nssmCommand) {
        $useTask = Read-UseScheduledTaskFallback
        $choices.UseScheduledTask = if ($useTask) { "yes" } else { "no" }
    }
    else {
        # 有 nssm：不需要问计划任务，管理员进程走 nssm 分支
        $choices.UseScheduledTask = "no"
    }

    # 2. Admin 端口（nssm 或计划任务都用到；nssm 已存在服务的情况除外，管理员进程会判断）
    $choices.AdminPort = Read-AdminPort

    return $choices
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

function Test-WindowsServiceExists {
    param([string]$ServiceName)

    return $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
}

function Stop-NssmAdminService {
    param(
        [Parameter(Mandatory = $true)]
        [string]$NssmPath,
        [Parameter(Mandatory = $true)]
        [string]$ServiceName
    )

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "NSSM 服务已停止，跳过停止: $ServiceName"
        return
    }

    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
        Write-Host "NSSM 服务正在停止，等待退出: $ServiceName"
    }
    else {
        Write-Host "发布前停止 NSSM 服务: $ServiceName"
        Invoke-CheckedCommand -FilePath $NssmPath -Arguments @("stop", $ServiceName) -IgnoreExitCode
    }

    $service.Refresh()
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        try {
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }
        catch [System.ServiceProcess.TimeoutException] {
            throw "等待 NSSM 服务停止超时: $ServiceName。请手动停止后重试。"
        }
    }
}

function Stop-ExistingAdminTask {
    param([string]$TaskName)

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        return
    }

    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

function Stop-ProcessesByExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $fullPath = [System.IO.Path]::GetFullPath($ExecutablePath)
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'McpDbTools.Server.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and ([System.IO.Path]::GetFullPath($_.ExecutablePath) -eq $fullPath) })
    if ($processes.Count -eq 0) {
        return
    }

    Write-Host "发布前停止残留服务进程: $($processes.Count) 个"
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $remaining = @(Get-CimInstance Win32_Process -Filter "Name = 'McpDbTools.Server.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and ([System.IO.Path]::GetFullPath($_.ExecutablePath) -eq $fullPath) })
    } while ($remaining.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($remaining.Count -gt 0) {
        throw "等待残留服务进程退出超时，仍有 $($remaining.Count) 个进程占用: $fullPath"
    }
}

function Remove-ExistingAdminTask {
    param([string]$TaskName)

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        return
    }

    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

function Move-LegacyData {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDir,
        [Parameter(Mandatory = $true)]
        [string]$DataDir
    )

    # 若目标已存在 config.json 或 audit.db，视为已迁移过，直接跳过（幂等）
    $alreadyMigrated = (Test-Path (Join-Path $DataDir "config.json")) -or
                       (Test-Path (Join-Path $DataDir "audit.db"))
    if ($alreadyMigrated) {
        Write-Host "数据目录已存在用户数据，跳过迁移: $DataDir"
        return
    }

    # 旧数据可能来自两个位置：
    # 1. 旧版本：安装目录（exe 同目录）下的 config.json / audit.db* / backups
    # 2. 之前用 %USERPROFILE%\.mcpdbtools 的失败尝试残留（向前兼容）
    $legacySources = @($SourceDir)
    $userProfileDataDir = Join-Path $env:USERPROFILE ".mcpdbtools"
    if ((Test-Path $userProfileDataDir) -and ($userProfileDataDir -ne $SourceDir)) {
        $legacySources += $userProfileDataDir
    }

    $moved = $false
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

    foreach ($source in $legacySources) {
        $legacyConfig = Join-Path $source "config.json"
        $legacyDbFiles = @(Get-ChildItem -Path $source -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "audit.db*" -or $_.Name -like "*.db" })
        $legacyBackupsDir = Join-Path $source "backups"
        $hasLegacyBackups = Test-Path $legacyBackupsDir

        if (-not (Test-Path $legacyConfig) -and $legacyDbFiles.Count -eq 0 -and -not $hasLegacyBackups) {
            continue
        }

        if (-not $moved) {
            Write-Host "迁移旧版用户数据到: $DataDir"
        }

        if (Test-Path $legacyConfig) {
            # 若目标已有同名文件（来自更早的源），跳过
            $destConfig = Join-Path $DataDir "config.json"
            if (-not (Test-Path $destConfig)) {
                Move-Item -Path $legacyConfig -Destination $DataDir -Force
                Write-Host "  config.json ($source) -> $DataDir"
            }
        }

        foreach ($dbFile in $legacyDbFiles) {
            $destDb = Join-Path $DataDir $dbFile.Name
            if (-not (Test-Path $destDb)) {
                Move-Item -Path $dbFile.FullName -Destination $DataDir -Force
                Write-Host "  $($dbFile.Name) ($source) -> $DataDir"
            }
        }

        if ($hasLegacyBackups) {
            # backups 目录整个移动；若目标已有则合并
            $destBackups = Join-Path $DataDir "backups"
            if (-not (Test-Path $destBackups)) {
                Move-Item -Path $legacyBackupsDir -Destination $DataDir -Force
                Write-Host "  backups\ ($source) -> $DataDir"
            }
            else {
                # 合并：逐个复制不存在的备份文件
                Copy-Item -Path (Join-Path $legacyBackupsDir "*") -Destination $destBackups -Force -ErrorAction SilentlyContinue
                Remove-Item -Path $legacyBackupsDir -Recurse -Force -ErrorAction SilentlyContinue
                Write-Host "  backups\ ($source) -> $DataDir (合并)"
            }
        }
        $moved = $true
    }

    if (-not $moved) {
        Write-Host "未发现旧版用户数据，无需迁移。"
    }
}

function Set-DataDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DataDir
    )

    # ProgramData 子目录默认 ACL 有时只允许创建者修改。显式给 Users 组读写权限，
    # 保证 LocalSystem 服务与当前用户进程都能读写同一份数据。
    if (-not (Test-Path $DataDir)) {
        return
    }

    try {
        $acl = Get-Acl -Path $DataDir
        # 检查是否已有 Users 组的写权限，避免重复添加
        $existing = $acl.Access | Where-Object {
            $_.IdentityReference.Value -eq "BUILTIN\Users" -and
            ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Modify) -ne 0
        }
        if ($null -eq $existing) {
            $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
                "BUILTIN\Users",
                [System.Security.AccessControl.FileSystemRights]::Modify,
                [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
            Set-Acl -Path $DataDir -AclObject $acl
            Write-Host "已授权 Users 组对数据目录的读写权限: $DataDir"
        }
    }
    catch {
        Write-Host "警告：设置数据目录 ACL 失败（不影响 LocalSystem 访问，但可能影响普通用户进程写入）: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Install-ClaudeMcp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ClaudePath,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Scope,
        [Parameter(Mandatory = $true)]
        [string]$ServerExePath
    )

    Invoke-CheckedCommand -FilePath $ClaudePath -Arguments @(
        "mcp",
        "remove",
        "--scope",
        $Scope,
        $Name
    ) -IgnoreExitCode

    Invoke-CheckedCommand -FilePath $ClaudePath -Arguments @(
        "mcp",
        "add",
        "--scope",
        $Scope,
        "--transport",
        "stdio",
        $Name,
        "--",
        $ServerExePath
    )
}

function Install-NssmAdminService {
    param(
        [Parameter(Mandatory = $true)]
        [string]$NssmPath,
        [Parameter(Mandatory = $true)]
        [string]$ServiceName,
        [Parameter(Mandatory = $true)]
        [string]$ServerExePath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    if (Test-WindowsServiceExists -ServiceName $ServiceName) {
        Write-Host "检测到 NSSM 服务已存在，跳过安装并重启服务: $ServiceName"
        Stop-NssmAdminService -NssmPath $NssmPath -ServiceName $ServiceName
        Start-Sleep -Seconds 1
        Invoke-CheckedCommand -FilePath $NssmPath -Arguments @("start", $ServiceName)
        return
    }

    Invoke-CheckedCommand -FilePath $NssmPath -Arguments @(
        "install",
        $ServiceName,
        $ServerExePath,
        "--admin-only",
        "--admin-port",
        $Port.ToString()
    )
    Invoke-CheckedCommand -FilePath $NssmPath -Arguments @("set", $ServiceName, "AppDirectory", $WorkingDirectory)
    # 服务以默认的 LocalSystem 账户运行即可。数据目录在 %ProgramData%\McpDbTools（跨用户共享），
    # LocalSystem 与当前用户进程（MCP/Claude）都能读写同一份数据。
    # C# 端 DataDirectoryResolver 会自动解析到该目录，无需任何环境变量或账户配置。
    Invoke-CheckedCommand -FilePath $NssmPath -Arguments @("set", $ServiceName, "DisplayName", "McpDbTools Admin UI")
    Invoke-CheckedCommand -FilePath $NssmPath -Arguments @("set", $ServiceName, "Description", "McpDbTools Admin UI: http://127.0.0.1:$Port/admin")
    Invoke-CheckedCommand -FilePath $NssmPath -Arguments @("set", $ServiceName, "Start", "SERVICE_AUTO_START")
    Invoke-CheckedCommand -FilePath $NssmPath -Arguments @("start", $ServiceName)
}

function Install-AdminScheduledTask {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TaskName,
        [Parameter(Mandatory = $true)]
        [string]$ServerExePath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $existingTask) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }

    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $taskAction = New-ScheduledTaskAction -Execute $ServerExePath -Argument "--admin-only --admin-port $Port" -WorkingDirectory $WorkingDirectory
    $taskTrigger = New-ScheduledTaskTrigger -AtLogOn
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $taskAction `
        -Trigger $taskTrigger `
        -Principal $taskPrincipal `
        -Description "McpDbTools Admin UI: http://127.0.0.1:$Port/admin" `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
}

$scriptRoot = Split-Path -Parent $PSCommandPath

# 无论是否管理员，首次运行（未带 -Confirmed）都先确认安装目录，避免误部署
if (-not $Confirmed) {
    $ok = Confirm-InstallDir -Dir $InstallDir
    if (-not $ok) {
        Write-Host "已取消。请使用 -InstallDir 指定其它目录后重试。"
        return
    }
}

if (-not (Test-IsAdministrator)) {
    # 提权前完成所有交互式询问（端口、承载方式），把答案随提权命令透传给管理员进程
    $choices = @{}
    if (-not $Confirmed) {
        $choices = Read-InteractiveChoices
    }
    else {
        # 已通过命令行参数提供，直接透传
        if ($AdminPortParam -gt 0) { $choices.AdminPort = $AdminPortParam }
        if ($UseScheduledTaskParam -ne "ask") { $choices.UseScheduledTask = $UseScheduledTaskParam }
    }

    $restarted = Restart-AsAdministrator -ScriptPath $PSCommandPath -WorkingDirectory $scriptRoot -Choices $choices
    if ($restarted) {
        return
    }
}
else {
    # 已是管理员（直接以管理员运行，或提权后的子进程）
    # 若未通过提权前的交互拿到端口/承载方式，此处补问
    if (-not $Confirmed) {
        $choices = Read-InteractiveChoices
        if ($choices.ContainsKey("AdminPort")) { $AdminPortParam = $choices.AdminPort }
        if ($choices.ContainsKey("UseScheduledTask")) { $UseScheduledTaskParam = $choices.UseScheduledTask }
    }
}

try {
$projectPath = Join-Path $scriptRoot "src\McpDbTools.Server\McpDbTools.Server.csproj"
$installDirFull = [System.IO.Path]::GetFullPath($InstallDir)
$exePath = Join-Path $installDirFull "McpDbTools.Server.exe"
# 用户数据目录（config.json / audit.db / backups）独立于程序目录，便于升级时全量替换安装目录。
# 使用 %ProgramData%\McpDbTools：Windows 跨用户共享数据目录，LocalSystem 服务与当前用户进程都能读写。
# C# 端 DataDirectoryResolver 以同优先级解析。
$dataDir = Join-Path $env:ProgramData "McpDbTools"
$runtimeIdentifier = Resolve-RuntimeIdentifier

if (-not (Test-Path $projectPath)) {
    throw "未找到项目文件: $projectPath"
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw "未找到 dotnet 命令，请先安装 .NET 8 SDK。"
}

$claudeCommand = Get-Command claude -ErrorAction SilentlyContinue
if ($null -eq $claudeCommand) {
    throw "未找到 claude 命令，请先安装 Claude Code CLI 并确认 claude 在 PATH 中。"
}

$nssmCommand = Get-Command nssm -ErrorAction SilentlyContinue
$useNssm = $null -ne $nssmCommand
$hasExistingNssmService = $useNssm -and (Test-WindowsServiceExists -ServiceName $AdminServiceName)
$useScheduledTask = $false
if (-not $useNssm) {
    # 优先用提权前传入的选择，缺省时才交互询问
    if ($UseScheduledTaskParam -eq "yes") {
        $useScheduledTask = $true
    }
    elseif ($UseScheduledTaskParam -eq "no") {
        $useScheduledTask = $false
    }
    else {
        $useScheduledTask = Read-UseScheduledTaskFallback
    }
}
$adminPort = $null
if (-not $hasExistingNssmService -and ($useNssm -or $useScheduledTask)) {
    # 优先用提权前传入的端口，缺省时才交互询问
    if ($AdminPortParam -gt 0) {
        $adminPort = $AdminPortParam
    }
    else {
        $adminPort = Read-AdminPort
    }
}

Write-Host "发布目录: $installDirFull"
Write-Host "数据目录: $dataDir"
Write-Host "Claude Code MCP: $McpName ($McpScope)"
if ($hasExistingNssmService) {
    Write-Host "检测到 Admin UI 已使用 NSSM 安装服务: $AdminServiceName"
}
elseif ($useNssm) {
    Write-Host "Admin UI: http://127.0.0.1:$adminPort/admin"
    Write-Host "Admin 承载方式: NSSM 服务 ($AdminServiceName)"
}
elseif ($useScheduledTask) {
    Write-Host "Admin UI: http://127.0.0.1:$adminPort/admin"
    Write-Host "Admin 承载方式: 计划任务 ($AdminTaskName)"
}
else {
    Write-Host "Admin 承载方式: 跳过"
}

New-Item -ItemType Directory -Path $installDirFull -Force | Out-Null
if ($useNssm -and $hasExistingNssmService) {
    Stop-NssmAdminService -NssmPath $nssmCommand.Source -ServiceName $AdminServiceName
}
elseif ($useNssm) {
    Remove-ExistingAdminTask -TaskName $AdminTaskName
}
if ($useScheduledTask) {
    Stop-ExistingAdminTask -TaskName $AdminTaskName
}
Stop-ProcessesByExecutablePath -ExecutablePath $exePath

# 升级迁移：把旧版（exe 同目录，或之前 %USERPROFILE%\.mcpdbtools）的用户数据搬到独立数据目录。
# 必须在服务停止、文件释放后执行。幂等：目标已有数据则跳过。
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
Move-LegacyData -SourceDir $installDirFull -DataDir $dataDir
# 确保数据目录 ACL 允许 LocalSystem 服务与普通用户进程都读写
Set-DataDirectoryAcl -DataDir $dataDir

# 阶段 1: 先 publish 到独立临时目录,确认构建成功后再动安装目录。
# 这样 dotnet publish 失败(编码、编译错误等)时,安装目录完全不受影响。
# 用户数据（config.json / audit.db / backups）已迁移到独立数据目录，不在安装目录下，
# 因此阶段 2 可以无条件全量替换安装目录，无需任何备份/还原。
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

    # 阶段 2: publish 已成功,全量替换安装目录。
    # 防御性清理：万一迁移漏了（如手动放进来的旧数据），删除残留的 audit.db*/backups，
    # 避免新版本读到安装目录下的旧数据（用户数据目录已分离）。
    Get-ChildItem -Path $installDirFull -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -like "audit.db*" -or $_.Name -like "*.db" -or $_.Name -eq "backups" -or $_.Name -eq "config.json") {
            Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    # 清空安装目录下所有内容
    Get-ChildItem -Path $installDirFull -Force -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

    # 将 publish 产物复制到安装目录
    Copy-Item -Path (Join-Path $publishStagingDir "*") -Destination $installDirFull -Recurse -Force
}
finally {
    # 清理 publish 临时目录(成功失败都清理)
    if ($publishStagingDir -and (Test-Path $publishStagingDir)) {
        Remove-Item -Path $publishStagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $exePath)) {
    throw "发布后未找到服务程序: $exePath"
}

Write-Host "安装 Claude Code MCP: $McpName ($McpScope)"
Install-ClaudeMcp `
    -ClaudePath $claudeCommand.Source `
    -Name $McpName `
    -Scope $McpScope `
    -ServerExePath $exePath

if ($useNssm) {
    if ($hasExistingNssmService) {
        Invoke-CheckedCommand -FilePath $nssmCommand.Source -Arguments @("start", $AdminServiceName)
    }
    else {
        Install-NssmAdminService `
            -NssmPath $nssmCommand.Source `
            -ServiceName $AdminServiceName `
            -ServerExePath $exePath `
            -WorkingDirectory $installDirFull `
            -Port $adminPort
    }
}
elseif ($useScheduledTask) {
    Install-AdminScheduledTask `
        -TaskName $AdminTaskName `
        -ServerExePath $exePath `
        -WorkingDirectory $installDirFull `
        -Port $adminPort
}
else {
    Write-Host "已跳过 Admin UI 自启动安装。"
}

Write-Host ""
Write-Host "部署完成。"
Write-Host "数据目录: $dataDir (config.json / audit.db / backups)"
if (-not $hasExistingNssmService -and ($useNssm -or $useScheduledTask)) {
    Write-Host "Admin UI: http://127.0.0.1:$adminPort/admin"
}
Write-Host "MCP 服务已安装到 Claude Code: $McpName ($McpScope)"
if ($useNssm) {
    if ($hasExistingNssmService) {
        Write-Host "Admin UI 已使用 NSSM 安装服务: $AdminServiceName"
    }
    else {
        Write-Host "Admin UI 已通过 NSSM 服务承载: $AdminServiceName"
    }
}
elseif ($useScheduledTask) {
    Write-Host "Admin UI 已通过计划任务承载: $AdminTaskName"
}
else {
    Write-Host "Admin UI 自启动未安装。"
}
Write-Host "如需调整安装目录、MCP 作用域或服务名，可运行: .\deploy.ps1 -InstallDir D:\Tools\McpDbTools -McpScope local -AdminServiceName McpDbTools.Admin"
}
finally {
    Show-ExitPause
}
