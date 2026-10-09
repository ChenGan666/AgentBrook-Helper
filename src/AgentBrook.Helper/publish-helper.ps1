# 发布 AgentBrook.Helper Windows 版本（win-x64 / win-arm64）
# 默认自动递增 version.txt 的 patch 版本号；可通过 VERSION 环境变量或 -VersionOverride 参数覆盖。
param(
    [string]$Configuration = "Release",
    [string]$VersionOverride = $env:VERSION
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Split-Path -Parent $projectDir
$publishBase = Join-Path $srcDir "publish"
$serviceDir = Join-Path $srcDir "AgentBrook.Service"
$versionFile = Join-Path $projectDir "version.txt"

$currentVersion = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() -replace '^\uFEFF' } else { "1.0.0" }
if ($VersionOverride) {
    $version = $VersionOverride
} else {
    $parts = $currentVersion -split '\.'
    if ($parts.Length -lt 3) { $parts = @("1","0","0") }
    [int]$patch = $parts[2]
    $version = "$($parts[0]).$($parts[1]).$($patch + 1)"
}
Set-Content -Path $versionFile -Value $version -NoNewline
Write-Host "==> 发布版本：$version"

$runtimes = @("win-x64", "win-arm64")
foreach ($rid in $runtimes) {
    $outDir = Join-Path $publishBase $rid
    Write-Host ""
    Write-Host "==> 正在发布 $rid -> $outDir"
    dotnet publish $projectDir `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:Version=$version `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # 平台分发包：版本号 + rid
    $distZipName = "AgentBrook.Helper-$version-$rid.zip"
    $distZipPath = Join-Path $publishBase $distZipName
    Remove-Item $distZipPath -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $distZipPath -Force
    Write-Host "  -> 分发包：$distZipPath"

    # 服务端更新目录：固定命名 AgentBrook.Helper-{version}.zip
    $updateDir = Join-Path $serviceDir "updates/$rid"
    New-Item -ItemType Directory -Force -Path $updateDir | Out-Null
    $serviceZipPath = Join-Path $updateDir "AgentBrook.Helper-$version.zip"
    Copy-Item $distZipPath $serviceZipPath -Force
    Copy-Item (Join-Path $projectDir "release-notes.txt") (Join-Path $updateDir "release-notes.txt") -Force
    Write-Host "  -> 服务端更新包：$serviceZipPath"
}

Write-Host ""
Write-Host "全部发布完成。"
