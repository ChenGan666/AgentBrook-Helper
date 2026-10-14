# ──────────────────────────────────────────────────────────────
# AgentBrook Helper — Windows 安装程序构建脚本
# 依赖：Inno Setup 6（需安装 ISCC.exe）
#
# 用法：
#   .\build-installer.ps1                       # 构建 x64 + arm64 安装包
#   .\build-installer.ps1 -Arch x64             # 只构建 x64
#   .\build-installer.ps1 -SkipPublish          # 跳过 dotnet publish（使用已有输出）
#   .\build-installer.ps1 -VersionOverride 2.0.0
# ──────────────────────────────────────────────────────────────
param(
    [string]$Configuration = "Release",
    [string]$VersionOverride = $env:VERSION,
    [string[]]$Arch = @("x64", "arm64"),
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

# ── 路径 ──
$projectDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir       = Split-Path -Parent $projectDir
$publishBase  = Join-Path $srcDir "publish"
$issFile      = Join-Path $projectDir "installer\setup.iss"
$versionFile  = Join-Path $projectDir "version.txt"

# ── 版本号 ──
$currentVersion = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() -replace '^\uFEFF' } else { "1.0.0" }
if ($VersionOverride) {
    $version = $VersionOverride
} else {
    $version = $currentVersion
}
Write-Host "==> 安装包版本：$version"

# ── 查找 Inno Setup 编译器 ──
$isccPaths = @(
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
# 也检查 PATH 中是否可用
$iscc = $null
foreach ($p in $isccPaths) {
    if (Test-Path $p) { $iscc = $p; break }
}
if (-not $iscc) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
}
if (-not $iscc) {
    Write-Host ""
    Write-Host "✖ 未找到 Inno Setup 编译器 (ISCC.exe)" -ForegroundColor Red
    Write-Host "  请从 https://jrsoftware.org/isdl.php 下载安装 Inno Setup" -ForegroundColor Yellow
    Write-Host "  安装后重新运行此脚本。" -ForegroundColor Yellow
    exit 1
}
Write-Host "==> Inno Setup: $iscc"

foreach ($rid in $Arch) {
    $runtimeId = "win-$rid"
    $outDir = Join-Path $publishBase $runtimeId
    $installerOutputDir = Join-Path $publishBase "installer"
    New-Item -ItemType Directory -Force -Path $installerOutputDir | Out-Null

    # ── 1. dotnet publish ──
    if (-not $SkipPublish) {
        Write-Host ""
        Write-Host "==> [1/3] dotnet publish ($runtimeId)"

        # 删除旧 appsettings.json，确保 publish 复制最新
        $staleSettings = Join-Path $outDir "appsettings.json"
        if (Test-Path $staleSettings) { Remove-Item $staleSettings -Force }

        dotnet publish $projectDir `
            -c $Configuration `
            -r $runtimeId `
            --self-contained true `
            -p:Version=$version `
            -o $outDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $runtimeId" }

        # 清除敏感信息
        $outSettings = Join-Path $outDir "appsettings.json"
        if (Test-Path $outSettings) {
            $raw = Get-Content $outSettings -Raw -Encoding utf8
            $raw = $raw -replace '"ApiKey"\s*:\s*"[^"]*"', '"ApiKey": ""'
            $raw = $raw -replace '"GITHUB_PERSONAL_ACCESS_TOKEN"\s*:\s*"[^"]*"', '"GITHUB_PERSONAL_ACCESS_TOKEN": ""'
            [System.IO.File]::WriteAllText($outSettings, $raw, [System.Text.UTF8Encoding]::new($false))
            Write-Host "  -> 已清除 appsettings.json 中的敏感信息"
        }
    } else {
        Write-Host ""
        Write-Host "==> [1/3] 跳过 dotnet publish（使用已有输出）"
        if (-not (Test-Path $outDir)) { throw "输出目录不存在：$outDir" }
    }

    # ── 2. 构建安装包 ──
    Write-Host "==> [2/3] 构建安装程序 ($runtimeId)"
    & $iscc `
        /DAppVersion=$version `
        /DArch=$rid `
        "/DSourceDir=$outDir" `
        "/DOutputDir=$installerOutputDir" `
        /Q `
        $issFile
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败 ($runtimeId)" }

    $installerFile = Join-Path $installerOutputDir "AgentBrook.Helper-$version-win-$rid-setup.exe"
    if (Test-Path $installerFile) {
        $sizeMB = [math]::Round((Get-Item $installerFile).Length / 1MB, 1)
        Write-Host "  -> 安装包：$installerFile ($sizeMB MB)" -ForegroundColor Green
    }

    # ── 3. 同步到服务端更新目录 ──
    $serviceDir = Join-Path $srcDir "AgentBrook.Service"
    $updateDir = Join-Path $serviceDir "updates/$runtimeId"
    New-Item -ItemType Directory -Force -Path $updateDir | Out-Null
    $serviceInstallerPath = Join-Path $updateDir "AgentBrook.Helper-$version-setup.exe"
    Copy-Item $installerFile $serviceInstallerPath -Force
    Write-Host "  -> 服务端安装包：$serviceInstallerPath"
}

Write-Host ""
Write-Host "=== 全部安装包构建完成 ===" -ForegroundColor Green
