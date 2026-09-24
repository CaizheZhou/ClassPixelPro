<#
.SYNOPSIS
    ClassPixelPro 一键打包脚本（非交互式）
.DESCRIPTION
    完成：PublishApp + PublishLauncher + 组装便携目录结构 + 打最终分发包 zip
    版本号自动从 git describe 获取，可用 -Version 覆盖。
.NOTES
    用法:
      pwsh -File ./pack.ps1                    # 全自动，版本号从 git tag 获取
      pwsh -File ./pack.ps1 -Version 2.0.0.0   # 指定版本号
      pwsh -File ./pack.ps1 -BuildType selfContained  # 免运行时版本
      pwsh -File ./pack.ps1 -SkipBuild         # 仅用已有 out/ 产物重新组装
#>
param(
    [string]$Version = "",
    [string]$OsName = "windows",
    [string]$Arch = "x64",
    [string]$BuildType = "full",
    [string]$DistDir = "ClassPixelPro-dist",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function C-Info($m)  { Write-Host "[INFO]  $m" -ForegroundColor Cyan }
function C-Ok($m)     { Write-Host "[OK]    $m" -ForegroundColor Green }
function C-Warn($m)   { Write-Host "[WARN]  $m" -ForegroundColor Yellow }
function C-Err($m)    { Write-Host "[ERR]   $m" -ForegroundColor Red }
function C-Step($n,$m){ Write-Host "`n=== [$n] $m ===" -ForegroundColor Magenta }

$RepoRoot = Split-Path $MyInvocation.MyCommand.Path -Parent
C-Info "仓库根目录: $RepoRoot"

# 版本号：参数传入 > git describe > 默认值
if ([string]::IsNullOrWhiteSpace($Version)) {
    try {
        Push-Location $RepoRoot
        $Version = (git describe --tags --abbrev=0 2>$null).Trim()
        Pop-Location
    } catch { Pop-Location 2>$null }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "2.0.0.0" }
}
C-Info "参数: Version=$Version  OsName=$OsName  Arch=$Arch  BuildType=$BuildType  DistDir=$DistDir  SkipBuild=$SkipBuild"

# ── 1. 环境准备 ──
C-Step "1/6" "环境准备"

$TempDir = Join-Path $RepoRoot "_packtmp"
if (-not (Test-Path $TempDir)) { New-Item -ItemType Directory -Path $TempDir -Force | Out-Null }
$env:TMP = $TempDir
$env:TEMP = $TempDir
C-Ok "TMP/TEMP -> $TempDir"

# 退出时清理临时目录的辅助函数（确保任何退出路径都清理 _packtmp）
function Exit-Script([int]$Code = 0) {
    Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    exit $Code
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { C-Err "未找到 dotnet"; Exit-Script 1 }
C-Ok ".NET SDK $(dotnet --version 2>$null)"

$pwshExe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { "pwsh" } else { "powershell" }
C-Ok "PowerShell: $pwshExe"

# ── 2. 构建 App ──
$AppPublishPath = Join-Path $RepoRoot "out\ClassPixelPro"

if (-not $SkipBuild) {
    C-Step "2/6" "构建应用本体 (PublishApp)"
    C-Info "build.ps1 PublishApp --OsName $OsName --Arch $Arch --Package folder --BuildType $BuildType --BuildName appBase --AppVersion $Version"
    & $pwshExe -File "$RepoRoot\build.ps1" PublishApp --OsName $OsName --Arch $Arch --Package folder --BuildType $BuildType --BuildName appBase --AppVersion $Version
    if ($LASTEXITCODE -ne 0) { C-Err "PublishApp 失败 (exit $LASTEXITCODE)"; Exit-Script 1 }
    C-Ok "应用本体构建完成"
} else {
    C-Step "2/6" "跳过构建（使用已有产物）"
    if (-not (Test-Path $AppPublishPath)) { C-Err "out\ClassPixelPro 不存在"; Exit-Script 1 }
    C-Ok "使用已有 $AppPublishPath"
}

# ── 3. 构建 Launcher ──
$LauncherExeName = if ($OsName -eq "windows") { "ClassPixelPro.exe" } else { "ClassPixelPro" }
$LauncherPublishPath = Join-Path $RepoRoot "out\Launcher"
$LauncherExePath = Join-Path $LauncherPublishPath $LauncherExeName

if (-not $SkipBuild) {
    C-Step "3/6" "构建启动器 (PublishLauncher, 原生 AOT)"
    C-Info "尝试原生 AOT 编译（需要 MSVC C++ 工具链）..."
    & $pwshExe -File "$RepoRoot\build.ps1" PublishLauncher --OsName $OsName --Arch $Arch --Package folder --BuildType $BuildType --BuildName appBase --AppVersion $Version 2>&1 | Tee-Object -Variable launcherOut
    if ($LASTEXITCODE -ne 0) {
        C-Warn "AOT 编译失败（可能缺 MSVC），回退为 self-contained 单文件..."
        switch ($OsName) { "windows" { $rid = "win" }; "linux" { $rid = "linux" }; "macos" { $rid = "osx" } }
        $rid = "$rid-$Arch"
        $launcherProj = Join-Path $RepoRoot "ClassPixelPro.Launcher\ClassPixelPro.Launcher.csproj"
        if (Test-Path $LauncherPublishPath) { Remove-Item $LauncherPublishPath -Recurse -Force }
        dotnet publish $launcherProj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:PublishAot=false -p:PublishBuilding=true -p:PublishPlatform=$OsName -p:ClassPixelPro_PlatformTarget=$Arch -o $LauncherPublishPath
        if ($LASTEXITCODE -ne 0) { C-Err "启动器回退编译也失败"; Exit-Script 1 }
        $srcExe = Join-Path $LauncherPublishPath "ClassPixelPro.Launcher$([System.IO.Path]::GetExtension($LauncherExeName))"
        if (Test-Path $srcExe) { Move-Item $srcExe $LauncherExePath -Force }
        C-Ok "启动器回退编译完成（self-contained 单文件）"
    } else {
        C-Ok "启动器 AOT 编译完成"
    }
} else {
    C-Step "3/6" "跳过启动器构建（使用已有产物）"
    if (-not (Test-Path $LauncherExePath)) { C-Err "out\Launcher\$LauncherExeName 不存在"; Exit-Script 1 }
    C-Ok "使用已有 $LauncherExePath"
}

# ── 4. 组装便携目录 ──
C-Step "4/6" "组装发布目录"

$DistPath = Join-Path (Split-Path $RepoRoot -Parent) $DistDir
$AppFolderName = "app-$Version-0"
$AppFolderPath = Join-Path $DistPath $AppFolderName

C-Info "清理旧目录（保留 data/）..."
if (Test-Path $DistPath) {
    Get-ChildItem $DistPath -Exclude "data" | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $DistPath -Force | Out-Null
}
New-Item -ItemType Directory -Path $AppFolderPath -Force | Out-Null

C-Info "复制启动器 -> $DistPath"
Copy-Item $LauncherExePath $DistPath -Force

C-Info "复制应用本体 -> $AppFolderPath"
Copy-Item -Path (Join-Path $AppPublishPath "*") -Destination $AppFolderPath -Recurse -Force

Get-ChildItem $DistPath -Filter "*.pdb" -Recurse | Remove-Item -Force
C-Ok "已清理 .pdb"

Set-Content -Value "folder" -Path (Join-Path $DistPath "PackageType") -Force -NoNewline
Set-Content -Value "folder" -Path (Join-Path $AppFolderPath "PackageType") -Force -NoNewline
C-Ok "已写入 PackageType=folder 标记（便携模式）"

$dataPath = Join-Path $DistPath "data"
if (-not (Test-Path $dataPath)) { New-Item -ItemType Directory -Path $dataPath -Force | Out-Null }
C-Ok "data/ 目录就绪"
C-Ok "发布目录组装完成: $DistPath"

# ── 5. 打 zip ──
C-Step "5/6" "生成分发包 zip"

$ZipName = "ClassPixelPro_${Version}_${OsName}-${Arch}.zip"
$ZipPath = Join-Path (Split-Path $RepoRoot -Parent) $ZipName

$packTmp = Join-Path $TempDir "pack_tmp"
if (Test-Path $packTmp) { Remove-Item $packTmp -Recurse -Force }
New-Item -ItemType Directory -Path $packTmp -Force | Out-Null
Get-ChildItem $DistPath -Exclude "data" | Copy-Item -Destination $packTmp -Recurse -Force

C-Info "压缩中... -> $ZipName"
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path $packTmp "*") -DestinationPath $ZipPath -Force
Remove-Item $packTmp -Recurse -Force

$zipSize = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
C-Ok "分发包已生成: $ZipPath ($zipSize MB)"

# ── 6. 完成 ──
C-Step "6/6" "完成"
Write-Host ""
Write-Host "  分发包     : $ZipPath" -ForegroundColor White
Write-Host "  可运行目录 : $DistPath" -ForegroundColor White
Write-Host ""
Write-Host "  目录结构:" -ForegroundColor White
Write-Host "    $LauncherExeName        (启动器)"
Write-Host "    PackageType             (便携标记)"
Write-Host "    $AppFolderName\          (应用本体)"
Write-Host "    data\                   (运行时生成)"
Write-Host ""
if ($BuildType -eq "full") {
    C-Warn "提示: full=框架依赖，目标机需 .NET 8 桌面运行时；选 selfContained 可免运行时"
}
Write-Host ""
C-Ok "打包完成。"

# ── 清理临时目录 ──
Exit-Script 0
