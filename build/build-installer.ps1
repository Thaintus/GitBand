# ============================================================================
# GitBinder 一键打包脚本
# 用法：.\build\build-installer.ps1 [-Version 1.2.14] [-Runtime win-x64]
# 步骤：发布 → 编译 Inno Setup 安装包 → 输出到 dist\
# ============================================================================
param(
    [string]$Version = "1.2.14",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")

$root = Split-Path -Parent $PSScriptRoot          # 仓库根目录
$buildDir = $PSScriptRoot                         # build\ 目录

# 查找 ISCC.exe
$iscc = $null
$candidates = @()
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
    $candidates += Join-Path $localAppData "Programs\Inno Setup 6\ISCC.exe"
}
foreach ($folder in @([Environment+SpecialFolder]::ProgramFilesX86, [Environment+SpecialFolder]::ProgramFiles)) {
    $programFiles = [Environment]::GetFolderPath($folder)
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
        $candidates += Join-Path $programFiles "Inno Setup 6\ISCC.exe"
    }
}
foreach ($c in $candidates) {
    if (Test-Path $c) { $iscc = $c; break }
}
if (-not $iscc) {
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { $iscc = $found.Source }
}
if (-not $iscc) {
    Write-Host "错误：未找到 Inno Setup 编译器 ISCC.exe。" -ForegroundColor Red
    Write-Host "请先安装：winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
    exit 1
}
Write-Host "==> Inno Setup 编译器：$iscc" -ForegroundColor Cyan

# 1. 发布
Write-Host "==> 步骤 1/2：发布应用..." -ForegroundColor Cyan
& (Join-Path $buildDir "publish.ps1") -Version $Version -Runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

# 2. 编译安装包
Write-Host "==> 步骤 2/2：编译安装包..." -ForegroundColor Cyan
& $iscc (Join-Path $buildDir "installer.iss") "/DMyAppVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw "安装包编译失败" }

$distDir = Join-Path $root "dist"
Write-Host ""
Write-Host "==> 打包完成！安装包输出：" -ForegroundColor Green
Get-ChildItem $distDir -Filter "*.exe" | Select-Object Name, Length, LastWriteTime
