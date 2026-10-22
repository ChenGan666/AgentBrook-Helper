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
# 最终产物根目录：src/publish/updated/<平台>/（文件名仅含版本号，整文件夹可直接拷贝到更新服务器）
$finalRoot = Join-Path $publishBase "updated"
$versionFile = Join-Path $projectDir "version.txt"

# 版本号管理：自动递增 patch 并写回 version.txt（mac 侧脚本只读不改，两平台策略不同）
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
    # 删除旧 appsettings.json，确保 dotnet publish 用源文件覆盖
    $staleSettings = Join-Path $outDir "appsettings.json"
    if (Test-Path $staleSettings) { Remove-Item $staleSettings -Force }
    dotnet publish $projectDir `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:Version=$version `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # 清除输出目录 appsettings.json 中的 API Key 等敏感信息
    $outSettings = Join-Path $outDir "appsettings.json"
    if (Test-Path $outSettings) {
        $raw = Get-Content $outSettings -Raw -Encoding utf8
        $raw = $raw -replace '"ApiKey"\s*:\s*"[^"]*"', '"ApiKey": ""'
        $raw = $raw -replace '"GITHUB_PERSONAL_ACCESS_TOKEN"\s*:\s*"[^"]*"', '"GITHUB_PERSONAL_ACCESS_TOKEN": ""'
        [System.IO.File]::WriteAllText($outSettings, $raw, [System.Text.UTF8Encoding]::new($false))
        Write-Host "  -> 已清除 appsettings.json 中的敏感信息"
    }

    # 平台分发包：文件名仅含版本号，存于 updated/<平台>/ 文件夹内
    $finalDir = Join-Path $finalRoot $rid
    New-Item -ItemType Directory -Force -Path $finalDir | Out-Null
    $distZipName = "AgentBrook.Helper-$version.zip"
    $distZipPath = Join-Path $finalDir $distZipName
    Remove-Item $distZipPath -ErrorAction SilentlyContinue
    # Windows PowerShell 5.1 自带 Archive 模块的 Write-Progress 存在进度计算越界的已知 bug
    # （"索引超出了数组界限"），且进度渲染会显著拖慢打包——局部关闭进度规避
    $prevProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $distZipPath -Force
    }
    finally {
        $ProgressPreference = $prevProgress
    }
    Write-Host "  -> 分发包：$distZipPath"

    # 更新说明随平台文件夹分发（服务器按目录读取 release-notes.txt 作为该版本更新说明）
    Copy-Item (Join-Path $projectDir "release-notes.txt") (Join-Path $finalDir "release-notes.txt") -Force
}

# ── 构建安装程序（如果 Inno Setup 可用） ──
$iscc = @(
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
}

if ($iscc) {
    Write-Host ""
    Write-Host "==> 检测到 Inno Setup，开始构建安装程序..."
    $issFile = Join-Path $projectDir "installer\setup.iss"
    foreach ($rid in $runtimes) {
        $archTag = $rid -replace '^win-', ''
        $srcPublishDir = Join-Path $publishBase $rid
        $installerOutputDir = Join-Path $finalRoot $rid   # 安装包与 zip 同入 updated/<平台>/
        Write-Host "  -> 构建 $rid 安装包..."
        & $iscc `
            /DAppVersion=$version `
            /DArch=$archTag `
            "/DSourceDir=$srcPublishDir" `
            "/DOutputDir=$installerOutputDir" `
            /Q `
            $issFile
        if ($LASTEXITCODE -eq 0) {
            $exeName = "AgentBrook.Helper-$version.exe"
            $exePath = Join-Path $installerOutputDir $exeName
            if (Test-Path $exePath) {
                $sizeMB = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
                Write-Host "  -> 安装包：$exePath ($sizeMB MB)" -ForegroundColor Green
                # 复制到服务端更新目录
            }
        } else {
            Write-Host "  ⚠ $rid 安装包构建失败（zip 包不受影响）" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host ""
    Write-Host "提示：未检测到 Inno Setup，跳过安装程序构建。" -ForegroundColor DarkGray
    Write-Host "      如需构建安装程序，请安装 Inno Setup: https://jrsoftware.org/isdl.php" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "全部发布完成。"
