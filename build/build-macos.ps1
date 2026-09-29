# macOS 实验包：在 Windows/macOS 上交叉发布 self-contained .app.zip。
# 此脚本不运行测试，不签名、不公证、不安装、不启动应用。
# 用法：.\build\build-macos.ps1 -Version 1.2.0 [-Runtime osx-arm64]
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version = '1.2.0',
    [ValidateSet('osx-arm64', 'osx-x64')]
    [ValidateNotNullOrEmpty()]
    [string[]]$Runtime = @('osx-arm64', 'osx-x64'),
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$templateDirectory = Join-Path $PSScriptRoot 'macos'
$distributionDirectory = Join-Path $repoRoot 'dist'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-Utf8File([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, $Value.Replace("`r`n", "`n"), $utf8NoBom)
}

function Write-BigEndianUInt32([IO.Stream]$Stream, [uint32]$Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function New-MacIcon([string]$SourceIcon, [string]$DestinationIcon) {
    # 当前 ICO 已包含无损 256 px PNG；直接封装为 ic08，无须系统图像工具。
    $bytes = [IO.File]::ReadAllBytes($SourceIcon)
    if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
        throw '应用图标不是受支持的 ICO 文件。'
    }
    $count = [BitConverter]::ToUInt16($bytes, 4)
    for ($index = 0; $index -lt $count; $index++) {
        $entryOffset = 6 + $index * 16
        if ($entryOffset + 16 -gt $bytes.Length) { throw 'ICO 目录不完整。' }
        $length = [BitConverter]::ToUInt32($bytes, $entryOffset + 8)
        $offset = [BitConverter]::ToUInt32($bytes, $entryOffset + 12)
        if ($offset + $length -gt $bytes.Length) { throw 'ICO 图片范围非法。' }
        if ($bytes[$entryOffset] -eq 0 -and $bytes[$entryOffset + 1] -eq 0 -and
            $length -ge 8 -and [BitConverter]::ToString($bytes, $offset, 8) -eq '89-50-4E-47-0D-0A-1A-0A') {
            $iconStream = [IO.File]::Open($DestinationIcon, [IO.FileMode]::CreateNew)
            try {
                $signature = [Text.Encoding]::ASCII.GetBytes('icns')
                $iconStream.Write($signature, 0, 4)
                Write-BigEndianUInt32 $iconStream ($length + 16)
                $chunkType = [Text.Encoding]::ASCII.GetBytes('ic08')
                $iconStream.Write($chunkType, 0, 4)
                Write-BigEndianUInt32 $iconStream ($length + 8)
                $iconStream.Write($bytes, $offset, $length)
            }
            finally { $iconStream.Dispose() }
            return
        }
    }
    throw 'ICO 中未找到 256 px PNG；请先为 Mac 提供 ICNS 图标。'
}

function Test-MachOFile([string]$Path) {
    $fileStream = [IO.File]::OpenRead($Path)
    try {
        $magic = New-Object byte[] 4
        if ($fileStream.Read($magic, 0, 4) -ne 4) { return $false }
        return [BitConverter]::ToString($magic) -in @(
            'CF-FA-ED-FE', 'CE-FA-ED-FE', 'FE-ED-FA-CF', 'FE-ED-FA-CE',
            'CA-FE-BA-BE', 'BE-BA-FE-CA', 'CA-FE-BA-BF', 'BF-BA-FE-CA')
    }
    finally { $fileStream.Dispose() }
}

function Assert-AppHostArchitecture([string]$Path, [string]$Rid) {
    $header = New-Object byte[] 8
    $fileStream = [IO.File]::OpenRead($Path)
    try {
        if ($fileStream.Read($header, 0, 8) -ne 8 -or
            [BitConverter]::ToString($header, 0, 4) -ne 'CF-FA-ED-FE') {
            throw "未找到有效的 64 位 Mach-O apphost：$Path"
        }
        $expectedCpu = if ($Rid -eq 'osx-arm64') { 0x0100000c } else { 0x01000007 }
        if ([BitConverter]::ToUInt32($header, 4) -ne $expectedCpu) {
            throw "apphost 架构与 $Rid 不一致：$Path"
        }
    }
    finally { $fileStream.Dispose() }
}

function Merge-PublishDirectory([string]$SourceDirectory, [string]$DestinationDirectory) {
    foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse) {
        $relative = $sourceFile.FullName.Substring($SourceDirectory.Length).TrimStart([char[]]'\/')
        $destination = Join-Path $DestinationDirectory $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
                throw "两个发布项目的共享文件不同，停止合并以防覆盖：$relative"
            }
            continue
        }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($sourceFile.FullName, $destination, $false)
    }
}

function Set-ZipUnixCreator([string]$ArchivePath) {
    # .NET 在 Windows 上写出的 ZIP creator 为 DOS，只有 ExternalAttributes 不够。
    # 仅修正本脚本新建 ZIP 的 central directory creator 字节为 Unix(3)，不改文件内容。
    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        $minimumPosition = [Math]::Max(0, $stream.Length - 65557)
        $endPosition = $stream.Length - 22
        $found = $false
        for ($position = $endPosition; $position -ge $minimumPosition; $position--) {
            $stream.Position = $position
            if ($reader.ReadUInt32() -eq 0x06054b50) {
                $stream.Position = $position + 20
                if ($position + 22 + $reader.ReadUInt16() -eq $stream.Length) {
                    $found = $true
                    break
                }
            }
        }
        if (-not $found) { throw 'ZIP 缺少结束目录记录。' }
        $stream.Position = $position + 8
        $diskEntryCount = $reader.ReadUInt16()
        $entryCount = $reader.ReadUInt16()
        $directoryLength = $reader.ReadUInt32()
        $directoryPosition = $reader.ReadUInt32()
        if ($entryCount -eq 65535 -or $directoryPosition -eq [uint32]::MaxValue -or
            $entryCount -ne $diskEntryCount -or $directoryPosition + $directoryLength -gt $position) {
            throw '不支持 ZIP64/分卷归档；停止输出可能丢失权限的包。'
        }
        for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
            $stream.Position = $directoryPosition
            if ($reader.ReadUInt32() -ne 0x02014b50) { throw 'ZIP 中央目录格式错误。' }
            $stream.Position = $directoryPosition + 28
            $nameLength = $reader.ReadUInt16()
            $extraLength = $reader.ReadUInt16()
            $commentLength = $reader.ReadUInt16()
            $stream.Position = $directoryPosition + 5
            $stream.WriteByte(3)
            $directoryPosition += 46 + $nameLength + $extraLength + $commentLength
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function New-UnixZip([string]$SourceDirectory, [string]$ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $items = Get-ChildItem -LiteralPath $SourceDirectory -Recurse | Sort-Object FullName
        foreach ($item in $items) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "发布树中出现未经处理的符号链接：$($item.FullName)"
            }
            $relativeName = $item.FullName.Substring($SourceDirectory.Length).TrimStart([char[]]'\/').Replace('\', '/')
            if ($item.PSIsContainer) {
                $entry = $archive.CreateEntry($relativeName + '/')
                $entry.ExternalAttributes = (16877 -shl 16) -bor 16 # directory 0755
                continue
            }
            $executable = $item.Extension -eq '.sh' -or (Test-MachOFile $item.FullName)
            $mode = if ($executable) { 33261 } else { 33188 } # regular 0755 / 0644
            $entry = $archive.CreateEntry($relativeName, [IO.Compression.CompressionLevel]::Optimal)
            $entry.ExternalAttributes = $mode -shl 16
            $entryStream = $entry.Open()
            $sourceStream = [IO.File]::OpenRead($item.FullName)
            try { $sourceStream.CopyTo($entryStream) }
            finally {
                $sourceStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $archiveStream.Dispose()
    }
    Set-ZipUnixCreator $ArchivePath
}

$runtimeTargets = @($Runtime | Select-Object -Unique)
foreach ($rid in $runtimeTargets) {
    $packagePath = Join-Path $distributionDirectory "GitBinder-$Version-$rid-unsigned.zip"
    if ((Test-Path -LiteralPath $packagePath) -or (Test-Path -LiteralPath "$packagePath.sha256")) {
        throw "拒绝覆盖历史安装包。请先手动归档旧包或使用新版本号：$packagePath"
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand -and $env:ProgramFiles) {
    $dotnetCandidate = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $dotnetCandidate) { $dotnetCommand = Get-Command $dotnetCandidate }
}
if (-not $dotnetCommand) { throw '未找到 dotnet。请先安装 .NET 10 SDK。' }
$dotnetPath = $dotnetCommand.Source
$buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stagingRoot = Join-Path $repoRoot "artifacts/macos/$buildId"
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
[IO.Directory]::CreateDirectory($distributionDirectory) | Out-Null
Write-Host "macOS 打包暂存目录：$stagingRoot"
Write-Warning '仅生成未签名、未公证的实验包，不代表已在 macOS 上验证运行。'

foreach ($rid in $runtimeTargets) {
    $ridStaging = Join-Path $stagingRoot $rid
    $desktopPublish = Join-Path $ridStaging 'desktop'
    $helperPublish = Join-Path $ridStaging 'helper'
    $payload = Join-Path $ridStaging 'package'
    $bundle = Join-Path $payload 'GitBinder.app'
    $macosDirectory = Join-Path $bundle 'Contents/MacOS'
    $resourcesDirectory = Join-Path $bundle 'Contents/Resources'
    $artifactDirectory = Join-Path $ridStaging 'dotnet-artifacts'
    [IO.Directory]::CreateDirectory($macosDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($resourcesDirectory) | Out-Null

    $commonArguments = @(
        '-c', $Configuration, '-r', $rid, '--self-contained', 'true',
        '--artifacts-path', $artifactDirectory,
        '-p:UseAppHost=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false', '-p:PublishAot=false', '-p:DebugType=None',
        '-p:DebugSymbols=false', "-p:Version=$Version"
    )
    Write-Host "发布 Desktop ($rid)..." -ForegroundColor Cyan
    & $dotnetPath publish (Join-Path $repoRoot 'src/GitBinder.Desktop/GitBinder.Desktop.csproj') @commonArguments '-o' $desktopPublish
    if ($LASTEXITCODE -ne 0) { throw "Desktop 发布失败：$rid；诊断文件保留于 $ridStaging" }
    Write-Host "发布 Credential Helper ($rid)..." -ForegroundColor Cyan
    & $dotnetPath publish (Join-Path $repoRoot 'src/GitBinder.CredentialHelper/GitBinder.CredentialHelper.csproj') @commonArguments '-o' $helperPublish
    if ($LASTEXITCODE -ne 0) { throw "Credential Helper 发布失败：$rid；诊断文件保留于 $ridStaging" }

    Merge-PublishDirectory $desktopPublish $macosDirectory
    Merge-PublishDirectory $helperPublish $macosDirectory
    Assert-AppHostArchitecture (Join-Path $macosDirectory 'GitBinder.Desktop') $rid
    Assert-AppHostArchitecture (Join-Path $macosDirectory 'gitbinder-credential') $rid
    foreach ($requiredFile in @('GitBinder.Desktop.dll', 'gitbinder-credential.dll', 'libcoreclr.dylib', 'libhostfxr.dylib', 'libAvaloniaNative.dylib', 'libe_sqlite3.dylib')) {
        if (-not (Test-Path -LiteralPath (Join-Path $macosDirectory $requiredFile))) {
            throw "macOS 发布文件不完整，缺少：$requiredFile"
        }
    }

    $infoPlist = [IO.File]::ReadAllText((Join-Path $templateDirectory 'Info.plist.template')).Replace('__VERSION__', $Version)
    Write-Utf8File (Join-Path $bundle 'Contents/Info.plist') $infoPlist
    New-MacIcon (Join-Path $repoRoot 'src/GitBinder.Desktop/Assets/avalonia-logo.ico') (Join-Path $resourcesDirectory 'GitBinder.icns')
    [IO.File]::Copy((Join-Path $repoRoot 'LICENSE'), (Join-Path $resourcesDirectory 'LICENSE'), $false)
    foreach ($fileName in @('finalize-macos.sh', 'entitlements.plist')) {
        Write-Utf8File (Join-Path $payload $fileName) ([IO.File]::ReadAllText((Join-Path $templateDirectory $fileName)))
    }
    $instructions = [IO.File]::ReadAllText((Join-Path $templateDirectory 'README-macOS.txt')).Replace('__VERSION__', $Version).Replace('__RUNTIME__', $rid)
    Write-Utf8File (Join-Path $payload 'README-macOS.txt') $instructions

    $packageName = "GitBinder-$Version-$rid-unsigned.zip"
    $stagedArchive = Join-Path $ridStaging $packageName
    New-UnixZip $payload $stagedArchive
    $packagePath = Join-Path $distributionDirectory $packageName
    $hash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash
    # CreateNew 语义，打包期间如有同名产物出现也不会覆盖。
    [IO.File]::Copy($stagedArchive, $packagePath, $false)
    $checksumStream = [IO.File]::Open("$packagePath.sha256", [IO.FileMode]::CreateNew)
    try {
        $checksumBytes = $utf8NoBom.GetBytes("$hash  $packageName`n")
        $checksumStream.Write($checksumBytes, 0, $checksumBytes.Length)
    }
    finally { $checksumStream.Dispose() }
    Write-Host "已生成：$packagePath" -ForegroundColor Green
    Write-Host "SHA256：$hash"
}

Write-Warning '请在 Mac 上使用包内 finalize-macos.sh 完成本机签名/DMG，再进行实际功能验证；当前包未完成 Apple 公证。'
Write-Host '未运行测试、未启动应用，Windows 发布目录与历史安装包均保留。'
