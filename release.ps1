[CmdletBinding()]
# 发版打包脚本：dotnet publish（self-contained win-x64）→ vpk pack 生成 Velopack 安装包。
# 产物在 Releases/：Setup.exe（安装器）+ 便携包 + 增量 delta + releases.win.json（更新元数据）。
# 取代旧 install.ps1：双击 Setup.exe 安装；应用内「系统设置」页检查更新。
# 依赖：dotnet SDK + vpk 全局工具（dotnet tool install -g vpk）。
param(
    [string]$PackId = "McpDbTools",
    [string]$MainExe = "McpDbTools.Server.exe",
    [string]$OutputDir = "Releases"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSCommandPath
$project = Join-Path $repoRoot "src\McpDbTools.Server\McpDbTools.Server.csproj"
$publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("McpDbTools.publish.{0}" -f [guid]::NewGuid())

# vpk 必须可用：优先 PATH，fallback 到 dotnet global tool 默认路径（装后 PATH 未刷新场景）
$vpkCmd = Get-Command vpk -ErrorAction SilentlyContinue
$vpkPath = if ($vpkCmd) { $vpkCmd.Source } else { Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe" }
if (-not (Test-Path $vpkPath)) {
    throw "未找到 vpk。请先安装：dotnet tool install -g vpk（装后可能需重开终端刷新 PATH）"
}

# 版本号取最近 git tag（去前缀 v）
$version = (git -C $repoRoot describe --tags --abbrev=0 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $version) {
    throw "未找到 git tag。请先打 tag（如 git tag v0.6.0）后再发版。"
}
$version = $version.TrimStart('v')
Write-Host "发版版本：$version"

try {
    Write-Host "1. dotnet publish（self-contained win-x64）-> $publishDir"
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

    Write-Host "2. vpk pack -> $OutputDir"
    & $vpkPath pack --packId $PackId --packVersion $version --packDir $publishDir --mainExe $MainExe --outputDir $OutputDir --icon (Join-Path $repoRoot "app.ico")
    if ($LASTEXITCODE -ne 0) { throw "vpk pack 失败（退出码 $LASTEXITCODE）" }

    # 给安装器与便携包追加版本号，便于 Release 页面下载识别
    # （应用内更新走 nupkg + releases.win.json，不依赖这两个文件名）
    $setupSrc = Join-Path $OutputDir "${PackId}-win-Setup.exe"
    $portableSrc = Join-Path $OutputDir "${PackId}-win-Portable.zip"
    if (Test-Path $setupSrc) {
        Rename-Item -Path $setupSrc -NewName "${PackId}-win-Setup-${version}.exe"
    }
    if (Test-Path $portableSrc) {
        Rename-Item -Path $portableSrc -NewName "${PackId}-win-Portable-${version}.zip"
    }

    Write-Host ""
    Write-Host "发布完成：$OutputDir"
    Write-Host "  安装包：$OutputDir\${PackId}-win-Setup-${version}.exe（双击安装）"
    Write-Host "  便携包：$OutputDir\${PackId}-win-Portable-${version}.zip（解压即用）"
    Write-Host "  更新元数据：$OutputDir\releases.win.json（上传到 UpdateSource 指向的 URL 目录）"
    Write-Host "应用内更新：系统设置页 → 应用更新 → 检查更新（更新源默认为 GitHub Releases）"
    Write-Host ""
    Write-Host "上传到 GitHub Releases（供应用内检查更新）："
    Write-Host "  vpk upload github -o $OutputDir --repoUrl https://github.com/lzm04521/MCP-DB-Tools --token <GITHUB_TOKEN> --publish"
}
finally {
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
}
