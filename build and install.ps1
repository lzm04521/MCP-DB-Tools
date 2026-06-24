[CmdletBinding()]
param(
    [string]$InstallDir = "E:\Software\FreeInstall\Mcp-db-Tools",
    [string]$McpName = "db-tools",
    [ValidateSet("local", "user", "project")]
    [string]$McpScope = "user",
    [string]$AdminServiceName = "McpDbTools.Admin",
    [string]$AdminTaskName = "McpDbTools.Admin",
    [switch]$PauseOnExit
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
        [string]$WorkingDirectory
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
        "-PauseOnExit"
    )
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
if (-not (Test-IsAdministrator)) {
    $restarted = Restart-AsAdministrator -ScriptPath $PSCommandPath -WorkingDirectory $scriptRoot
    if ($restarted) {
        return
    }
}

try {
$projectPath = Join-Path $scriptRoot "src\McpDbTools.Server\McpDbTools.Server.csproj"
$installDirFull = [System.IO.Path]::GetFullPath($InstallDir)
$exePath = Join-Path $installDirFull "McpDbTools.Server.exe"
$configPath = Join-Path $installDirFull "config.json"
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
    $useScheduledTask = Read-UseScheduledTaskFallback
}
$adminPort = $null
if (-not $hasExistingNssmService -and ($useNssm -or $useScheduledTask)) {
    $adminPort = Read-AdminPort
}

Write-Host "发布目录: $installDirFull"
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

$configBackupPath = $null
if (Test-Path $configPath) {
    $configBackupPath = Join-Path ([System.IO.Path]::GetTempPath()) ("McpDbTools.config.{0}.json" -f [guid]::NewGuid())
    Copy-Item -Path $configPath -Destination $configBackupPath -Force
}

try {
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
        $installDirFull
    )

    if ($configBackupPath) {
        Copy-Item -Path $configBackupPath -Destination $configPath -Force
    }
}
finally {
    if ($configBackupPath -and (Test-Path $configBackupPath)) {
        Remove-Item -Path $configBackupPath -Force
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
