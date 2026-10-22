# ============================================================================
# GitBinder 一键构建脚本
# 用法：
#   .\build\build.ps1                        # 默认：还原 + 构建 + 测试
#   .\build\build.ps1 -Clean                 # 先清理再构建
#   .\build\build.ps1 -SkipTests             # 跳过测试
#   .\build\build.ps1 -Configuration Release # 指定配置
#   .\build\build.ps1 -Package               # 构建 + 测试 + 生成安装包
#   .\build\build.ps1 -Package -SkipTests    # 构建 + 安装包（跳过测试）
# ============================================================================
param(
    [string]$Configuration = "Debug",
    [string]$Version = "1.2.5",
    [string]$Runtime = "win-x64",
    [switch]$Clean,
    [switch]$SkipTests,
    [switch]$Package
)

$ErrorActionPreference = "Stop"

# 刷新 PATH，确保能找到 dotnet。
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")

$root = Split-Path -Parent $PSScriptRoot          # 仓库根目录
$buildDir = $PSScriptRoot                         # build\ 目录
$sln = Join-Path $root "GitBinder.slnx"

function Step([string]$title) {
    Write-Host ""
    Write-Host "========================================================" -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host "========================================================" -ForegroundColor DarkGray
}

# 0. 检查 dotnet 是否可用。
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "错误：未找到 dotnet 命令。" -ForegroundColor Red
    Write-Host "请先安装 .NET 10 SDK：https://aka.ms/dotnet/download" -ForegroundColor Yellow
    exit 1
}
Write-Host "dotnet 版本：$(dotnet --version)" -ForegroundColor DarkGray

# 1. 清理
if ($Clean) {
    Step "清理"
    dotnet clean $sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "清理失败" }
}

# 2. 还原
Step "还原依赖"
dotnet restore $sln
if ($LASTEXITCODE -ne 0) { throw "还原失败" }

# 3. 构建
Step "构建 ($Configuration)"
dotnet build $sln -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

# 4. 测试
if (-not $SkipTests) {
    Step "运行单元测试"
    dotnet test (Join-Path $root "tests\GitBinder.Tests\GitBinder.Tests.csproj") -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "测试失败" }
}
else {
    Write-Host "已跳过单元测试（-SkipTests）。" -ForegroundColor Yellow
}

# 5. 打包（可选）
if ($Package) {
    Step "生成安装包 (v$Version)"
    & (Join-Path $buildDir "build-installer.ps1") -Version $Version -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "打包失败" }
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  构建流程全部完成！" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  配置：$Configuration"
Write-Host "  测试：$(if ($SkipTests) { '跳过' } else { '通过' })"
if ($Package) {
    Write-Host "  安装包：$(Join-Path $root 'dist')"
}
Write-Host ""
