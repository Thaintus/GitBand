# ============================================================================
# GitBinder 发布脚本
# 用法：.\build\publish.ps1 [-Configuration Release] [-Runtime win-x64] [-Version x.y.z]
# 说明：发布主程序与 CredentialHelper 到 build\publish\ 目录，供 Inno Setup 打包。
# ============================================================================
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.2.5"
)

$ErrorActionPreference = "Stop"

# 刷新 PATH（若终端尚未合并 Machine 环境变量）。
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")

$root = Split-Path -Parent $PSScriptRoot          # 仓库根目录
$publishDir = Join-Path $root "build\publish"      # 统一输出目录

Write-Host "==> 清理旧的发布目录..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishDir).Path
    $expectedPublish = [IO.Path]::GetFullPath((Join-Path $root "build\publish"))
    if ($resolvedPublish -ne $expectedPublish -or
        ((Get-Item -LiteralPath $resolvedPublish).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "发布目录不是预期的仓库内目录，停止清理：$resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "==> 发布主程序 (GitBinder.Desktop)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\GitBinder.Desktop\GitBinder.Desktop.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -o (Join-Path $publishDir "app")
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败，停止打包。" }

Write-Host "==> 发布 CredentialHelper (gitbinder-credential)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\GitBinder.CredentialHelper\GitBinder.CredentialHelper.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -o (Join-Path $publishDir "app")
if ($LASTEXITCODE -ne 0) { throw "Credential Helper 发布失败，停止打包。" }

Write-Host "==> 复制应用图标..." -ForegroundColor Cyan
$iconSrc = Join-Path $root "src\GitBinder.Desktop\Assets\avalonia-logo.ico"
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc (Join-Path $publishDir "app\gitbinder.ico") -Force
}

Write-Host ""
Write-Host "==> 发布完成，产物目录：" -ForegroundColor Green
Write-Host "    $publishDir"
Get-ChildItem (Join-Path $publishDir "app") | Select-Object Name, Length
