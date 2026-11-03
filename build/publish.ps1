# ============================================================================
# GitBinder 发布脚本
# 用法：.\build\publish.ps1 [-Configuration Release] [-Runtime win-x64] [-Version x.y.z]
# 说明：分别发布主程序与 CredentialHelper，再合并为共享运行时目录供 Inno Setup 打包。
# ============================================================================
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.2.14"
)

$ErrorActionPreference = "Stop"

# 刷新 PATH（若终端尚未合并 Machine 环境变量）。
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")

$root = Split-Path -Parent $PSScriptRoot          # 仓库根目录
$publishDir = Join-Path $root "build\publish"      # 统一输出目录
$desktopPublish = Join-Path $publishDir "desktop"
$helperPublish = Join-Path $publishDir "helper"
$applicationDirectory = Join-Path $publishDir "app"

function Remove-DebugAssetReferences([string]$Directory) {
    # NuGet 的原生 PDB 也可能进入 deps 的 native/runtimeTargets 清单。
    # 文件不分发时同步移除诊断条目，保留所有 DLL、运行时和资源依赖。
    foreach ($depsFile in Get-ChildItem -LiteralPath $Directory -Filter '*.deps.json' -File) {
        $deps = Get-Content -LiteralPath $depsFile.FullName -Raw | ConvertFrom-Json
        $changed = $false
        foreach ($target in $deps.targets.PSObject.Properties.Value) {
            foreach ($library in $target.PSObject.Properties.Value) {
                foreach ($kind in @('native', 'runtimeTargets')) {
                    $assets = $library.PSObject.Properties[$kind].Value
                    if ($null -eq $assets) { continue }
                    foreach ($asset in @($assets.PSObject.Properties)) {
                        if ([IO.Path]::GetExtension($asset.Name) -ieq '.pdb') {
                            $assets.PSObject.Properties.Remove($asset.Name)
                            $changed = $true
                        }
                    }
                }
            }
        }
        if ($changed) {
            $json = $deps | ConvertTo-Json -Depth 100
            [IO.File]::WriteAllText($depsFile.FullName, $json, [Text.UTF8Encoding]::new($false))
        }
    }
}

function Merge-PublishDirectory([string]$SourceDirectory, [string]$DestinationDirectory) {
    $sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "发布树包含重解析点，拒绝合并：$($item.FullName)"
        }
        if ($item.PSIsContainer -or $item.Extension -ieq '.pdb') { continue }
        $relative = $item.FullName.Substring($sourceRoot.Length + 1)
        $destination = Join-Path $DestinationDirectory $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
                throw "两个程序的共享文件不一致，拒绝覆盖：$relative"
            }
            continue
        }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($item.FullName, $destination, $false)
    }
}

Write-Host "==> 清理旧的发布目录..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishDir).Path
    $expectedPublish = [IO.Path]::GetFullPath((Join-Path $root "build\publish"))
    if ($resolvedPublish -ne $expectedPublish -or
        ((Get-Item -LiteralPath $resolvedPublish).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "发布目录不是预期的仓库内目录，停止清理：$resolvedPublish"
    }
    if (@(Get-ChildItem -LiteralPath $resolvedPublish -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "发布目录子项包含重解析点，停止清理。"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# 两个 apphost 共用随包 DLL/原生运行时，分别保留 deps/runtimeconfig；不裁剪反射依赖。
$commonArguments = @(
    '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
    '-p:UseAppHost=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
    '-p:PublishAot=false', '-p:PublishReadyToRun=false',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-p:CopyOutputSymbolsToPublishDirectory=false',
    "-p:Version=$Version"
)

Write-Host "==> 发布主程序 (GitBinder.Desktop)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\GitBinder.Desktop\GitBinder.Desktop.csproj") @commonArguments -o $desktopPublish
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败，停止打包。" }

Write-Host "==> 发布 CredentialHelper (gitbinder-credential)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\GitBinder.CredentialHelper\GitBinder.CredentialHelper.csproj") @commonArguments -o $helperPublish
if ($LASTEXITCODE -ne 0) { throw "Credential Helper 发布失败，停止打包。" }

Write-Host "==> 合并共享运行时并剔除调试符号..." -ForegroundColor Cyan
Remove-DebugAssetReferences $desktopPublish
Remove-DebugAssetReferences $helperPublish
[IO.Directory]::CreateDirectory($applicationDirectory) | Out-Null
Merge-PublishDirectory $desktopPublish $applicationDirectory
Merge-PublishDirectory $helperPublish $applicationDirectory
foreach ($requiredFile in @(
    'GitBinder.Desktop.exe', 'GitBinder.Desktop.dll', 'GitBinder.Desktop.deps.json', 'GitBinder.Desktop.runtimeconfig.json',
    'gitbinder-credential.exe', 'gitbinder-credential.dll', 'gitbinder-credential.deps.json', 'gitbinder-credential.runtimeconfig.json',
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll',
    'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'e_sqlite3.dll'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $applicationDirectory $requiredFile) -PathType Leaf)) {
        throw "合并后的发布文件不完整，缺少：$requiredFile"
    }
}

Write-Host "==> 复制应用图标..." -ForegroundColor Cyan
$iconSrc = Join-Path $root "src\GitBinder.Desktop\Assets\avalonia-logo.ico"
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc (Join-Path $applicationDirectory "gitbinder.ico") -Force
}

# 清单放在 app 之外，便于量化后续体积变化，不进入用户安装包。
$payloadFiles = @(Get-ChildItem -LiteralPath $applicationDirectory -Recurse -File)
if (@($payloadFiles | Where-Object Extension -eq '.pdb').Count) {
    throw "最终发布目录仍包含调试符号，停止打包。"
}
$applicationRoot = (Resolve-Path -LiteralPath $applicationDirectory).Path
$payloadManifest = [ordered]@{
    Version = $Version
    Runtime = $Runtime
    FileCount = $payloadFiles.Count
    TotalBytes = ($payloadFiles | Measure-Object Length -Sum).Sum
    Files = @($payloadFiles | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($applicationRoot.Length + 1); Bytes = $_.Length }
    })
}
$payloadManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $publishDir 'payload-manifest.json') -Encoding UTF8

Write-Host ""
Write-Host "==> 发布完成，产物目录：" -ForegroundColor Green
Write-Host "    $publishDir"
Write-Host ("    安装有效载荷：{0:N2} MiB / {1} 个文件（不含 PDB）" -f ($payloadManifest.TotalBytes / 1MB), $payloadManifest.FileCount)
Get-ChildItem $applicationDirectory | Select-Object Name, Length
