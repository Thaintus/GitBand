#Requires -Version 7.4
# Linux 实验包：可在 Windows/Linux 上交叉发布 self-contained .deb 与 tar.gz。
# 仅打包，不运行测试、不安装、不启动应用；不清理或覆盖历史产物。
# 用法：pwsh -File ./build/build-linux.ps1 -Version 1.2.0 [-Runtime linux-x64]
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version = '1.2.0',
    [ValidateSet('linux-x64', 'linux-arm64')]
    [ValidateNotNullOrEmpty()]
    [string[]]$Runtime = @('linux-x64', 'linux-arm64'),
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$templateDirectory = Join-Path $PSScriptRoot 'linux'
$distributionDirectory = Join-Path $repoRoot 'dist'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$archiveTimestamp = [DateTimeOffset]::FromUnixTimeSeconds([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())

function Write-Utf8File([string]$Path, [string]$Value) {
    $fileStream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $bytes = $utf8NoBom.GetBytes($Value.Replace("`r`n", "`n"))
        $fileStream.Write($bytes, 0, $bytes.Length)
    }
    finally { $fileStream.Dispose() }
}

function Export-PngIcon([string]$SourceIcon, [string]$DestinationIcon) {
    # 复用现有 ICO 中的 256 px PNG，不重新设计图标或依赖图像转换软件。
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
        if ([uint64]$offset + [uint64]$length -gt $bytes.Length) { throw 'ICO 图片范围非法。' }
        if ($bytes[$entryOffset] -eq 0 -and $bytes[$entryOffset + 1] -eq 0 -and
            $length -ge 8 -and [BitConverter]::ToString($bytes, $offset, 8) -eq '89-50-4E-47-0D-0A-1A-0A') {
            $iconStream = [IO.File]::Open($DestinationIcon, [IO.FileMode]::CreateNew)
            try { $iconStream.Write($bytes, $offset, $length) }
            finally { $iconStream.Dispose() }
            return
        }
    }
    throw 'ICO 中未找到 256 px PNG。'
}

function Get-ElfArchitecture([string]$Path) {
    $header = [byte[]]::new(20)
    $fileStream = [IO.File]::OpenRead($Path)
    try {
        if ($fileStream.Read($header, 0, $header.Length) -ne $header.Length -or
            [BitConverter]::ToString($header, 0, 4) -ne '7F-45-4C-46') { return $null }
        if ($header[4] -ne 2 -or $header[5] -ne 1) { throw "只允许 64 位 little-endian ELF：$Path" }
        return [int]($header[18] + $header[19] * 256)
    }
    finally { $fileStream.Dispose() }
}

function Assert-ElfArchitecture([string]$Path, [string]$Rid) {
    $expectedMachine = if ($Rid -eq 'linux-arm64') { 183 } else { 62 }
    if ((Get-ElfArchitecture $Path) -ne $expectedMachine) {
        throw "ELF 架构与 $Rid 不一致或缺少有效 ELF 文件：$Path"
    }
}

function Merge-PublishDirectory([string]$SourceDirectory, [string]$DestinationDirectory) {
    foreach ($item in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "发布树中出现未经处理的符号链接：$($item.FullName)"
        }
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($SourceDirectory, $item.FullName)
        $destination = Join-Path $DestinationDirectory $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
                throw "两个发布项目的共享文件不同，拒绝覆盖：$relative"
            }
            continue
        }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($item.FullName, $destination, $false)
    }
}

function New-TarGzip([string]$SourceDirectory, [string]$ArchivePath, [string]$Prefix = '.') {
    # Debian deb(5) 接受 Ustar；不使用可能包含不兼容扩展记录的默认 PAX 格式。
    # 无论宿主系统是什么，归档内都显式写入 root:root 和 POSIX 文件权限。
    $archiveStream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew)
    $gzip = [IO.Compression.GZipStream]::new($archiveStream, [IO.Compression.CompressionLevel]::Optimal, $true)
    $writer = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Ustar, $true)
    try {
        $rootEntry = [System.Formats.Tar.UstarTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$Prefix/")
        $rootEntry.Mode = [IO.UnixFileMode]493 # 0755
        $rootEntry.Uid = 0
        $rootEntry.Gid = 0
        $rootEntry.UserName = 'root'
        $rootEntry.GroupName = 'root'
        $rootEntry.ModificationTime = $archiveTimestamp
        $writer.WriteEntry($rootEntry)
        foreach ($item in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -Force | Sort-Object FullName) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "归档树中出现未经处理的符号链接：$($item.FullName)"
            }
            $relativeName = [IO.Path]::GetRelativePath($SourceDirectory, $item.FullName).Replace('\', '/')
            $entryName = "$Prefix/$relativeName"
            $entryType = if ($item.PSIsContainer) { [System.Formats.Tar.TarEntryType]::Directory } else { [System.Formats.Tar.TarEntryType]::RegularFile }
            if ($item.PSIsContainer) { $entryName += '/' }
            $entry = [System.Formats.Tar.UstarTarEntry]::new($entryType, $entryName)
            $executable = $item.PSIsContainer -or $item.Extension -eq '.sh' -or
                (-not $item.PSIsContainer -and $null -ne (Get-ElfArchitecture $item.FullName))
            $entry.Mode = [IO.UnixFileMode]$(if ($executable) { 493 } else { 420 }) # 0755 / 0644
            $entry.Uid = 0
            $entry.Gid = 0
            $entry.UserName = 'root'
            $entry.GroupName = 'root'
            $entry.ModificationTime = $archiveTimestamp
            if ($item.PSIsContainer) {
                $writer.WriteEntry($entry)
                continue
            }
            $sourceStream = [IO.File]::OpenRead($item.FullName)
            try {
                $entry.DataStream = $sourceStream
                $writer.WriteEntry($entry)
            }
            finally { $sourceStream.Dispose() }
        }
    }
    finally {
        $writer.Dispose()
        $gzip.Dispose()
        $archiveStream.Dispose()
    }
}

function New-DebArchive([string]$ArchivePath, [string]$ControlArchive, [string]$DataArchive) {
    # 标准 Unix ar：固定顺序 debian-binary -> control.tar.gz -> data.tar.gz。
    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew)
    try {
        $signature = [Text.Encoding]::ASCII.GetBytes("!<arch>`n")
        $stream.Write($signature, 0, $signature.Length)
        $members = @(
            @{ Name = 'debian-binary'; Content = [Text.Encoding]::ASCII.GetBytes("2.0`n"); Path = $null },
            @{ Name = 'control.tar.gz'; Content = $null; Path = $ControlArchive },
            @{ Name = 'data.tar.gz'; Content = $null; Path = $DataArchive }
        )
        foreach ($member in $members) {
            $length = if ($null -ne $member.Content) { $member.Content.Length } else { (Get-Item -LiteralPath $member.Path).Length }
            $lengthText = $length.ToString([Globalization.CultureInfo]::InvariantCulture)
            if ($lengthText.Length -gt 10) { throw 'ar 成员大小超出格式限制。' }
            $timestampText = $archiveTimestamp.ToUnixTimeSeconds().ToString([Globalization.CultureInfo]::InvariantCulture)
            $header = $member.Name.PadRight(16) + $timestampText.PadRight(12) +
                '0'.PadRight(6) + '0'.PadRight(6) + '100644'.PadRight(8) +
                $lengthText.PadRight(10) + [char]0x60 + "`n"
            $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
            if ($headerBytes.Length -ne 60) { throw 'ar 成员头长度错误。' }
            $stream.Write($headerBytes, 0, $headerBytes.Length)
            if ($null -ne $member.Content) { $stream.Write($member.Content, 0, $member.Content.Length) }
            else {
                $sourceStream = [IO.File]::OpenRead($member.Path)
                try { $sourceStream.CopyTo($stream) }
                finally { $sourceStream.Dispose() }
            }
            if (($length % 2) -ne 0) { $stream.WriteByte(10) }
        }
    }
    finally { $stream.Dispose() }
}

function Export-Package([string]$StagedPath) {
    $packageName = [IO.Path]::GetFileName($StagedPath)
    $packagePath = Join-Path $distributionDirectory $packageName
    $hash = (Get-FileHash -LiteralPath $StagedPath -Algorithm SHA256).Hash
    # CreateNew 语义：并发任务生成同名包时也不覆盖。
    [IO.File]::Copy($StagedPath, $packagePath, $false)
    Write-Utf8File "$packagePath.sha256" "$hash  $packageName`n"
    Write-Host "已生成：$packagePath" -ForegroundColor Green
    Write-Host "SHA256：$hash"
}

$runtimeTargets = @($Runtime | Select-Object -Unique)
foreach ($rid in $runtimeTargets) {
    $architecture = if ($rid -eq 'linux-arm64') { 'arm64' } else { 'amd64' }
    foreach ($packageName in @("gitbinder_${Version}_${architecture}.deb", "GitBinder-$Version-$rid.tar.gz")) {
        $packagePath = Join-Path $distributionDirectory $packageName
        if ((Test-Path -LiteralPath $packagePath) -or (Test-Path -LiteralPath "$packagePath.sha256")) {
            throw "拒绝覆盖历史安装包，请使用新版本或先自行归档旧包：$packagePath"
        }
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand -and $env:ProgramFiles) {
    $dotnetCandidate = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $dotnetCandidate) { $dotnetCommand = Get-Command $dotnetCandidate }
}
if (-not $dotnetCommand) { throw '未找到 dotnet，请先安装 .NET 10 SDK。' }
$dotnetPath = $dotnetCommand.Source
if ($null -eq ('System.Formats.Tar.TarWriter' -as [type])) { throw '当前 PowerShell 缺少 System.Formats.Tar，请使用 PowerShell 7.4 或更新版。' }
$buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stagingRoot = Join-Path $repoRoot "artifacts/linux/$buildId"
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
[IO.Directory]::CreateDirectory($distributionDirectory) | Out-Null
Write-Host "Linux 打包暂存目录：$stagingRoot"
Write-Warning '仅生成 Linux 实验包，尚不代表已完成目标发行版的安装和桌面运行验收。'

foreach ($rid in $runtimeTargets) {
    $architecture = if ($rid -eq 'linux-arm64') { 'arm64' } else { 'amd64' }
    $ridStaging = Join-Path $stagingRoot $rid
    $desktopPublish = Join-Path $ridStaging 'desktop'
    $helperPublish = Join-Path $ridStaging 'helper'
    $dataRoot = Join-Path $ridStaging 'debian-data'
    $applicationDirectory = Join-Path $dataRoot 'opt/gitbinder'
    $controlDirectory = Join-Path $ridStaging 'debian-control'
    $artifactDirectory = Join-Path $ridStaging 'dotnet-artifacts'
    foreach ($directory in @($applicationDirectory, $controlDirectory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }

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

    Merge-PublishDirectory $desktopPublish $applicationDirectory
    Merge-PublishDirectory $helperPublish $applicationDirectory
    foreach ($nativeFile in @('GitBinder.Desktop', 'gitbinder-credential', 'libcoreclr.so', 'libhostfxr.so', 'libSkiaSharp.so', 'libHarfBuzzSharp.so', 'libe_sqlite3.so')) {
        Assert-ElfArchitecture (Join-Path $applicationDirectory $nativeFile) $rid
    }
    foreach ($managedFile in @('GitBinder.Desktop.dll', 'gitbinder-credential.dll', 'GitBinder.Desktop.runtimeconfig.json', 'gitbinder-credential.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $applicationDirectory $managedFile))) { throw "Linux 发布文件不完整，缺少：$managedFile" }
    }
    $instructions = [IO.File]::ReadAllText((Join-Path $templateDirectory 'README-Linux.txt')).
        Replace('__VERSION__', $Version).Replace('__RUNTIME__', $rid).Replace('__ARCHITECTURE__', $architecture)
    Write-Utf8File (Join-Path $applicationDirectory 'README-Linux.txt') $instructions
    [IO.File]::Copy((Join-Path $repoRoot 'LICENSE'), (Join-Path $applicationDirectory 'LICENSE'), $false)
    Export-PngIcon (Join-Path $repoRoot 'src/GitBinder.Desktop/Assets/avalonia-logo.ico') (Join-Path $applicationDirectory 'gitbinder.png')

    $desktopDirectory = Join-Path $dataRoot 'usr/share/applications'
    $iconDirectory = Join-Path $dataRoot 'usr/share/icons/hicolor/256x256/apps'
    $documentationDirectory = Join-Path $dataRoot 'usr/share/doc/gitbinder'
    foreach ($directory in @($desktopDirectory, $iconDirectory, $documentationDirectory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    Write-Utf8File (Join-Path $desktopDirectory 'gitbinder.desktop') ([IO.File]::ReadAllText((Join-Path $templateDirectory 'gitbinder.desktop')))
    [IO.File]::Copy((Join-Path $applicationDirectory 'gitbinder.png'), (Join-Path $iconDirectory 'gitbinder.png'), $false)
    [IO.File]::Copy((Join-Path $repoRoot 'LICENSE'), (Join-Path $documentationDirectory 'copyright'), $false)
    Write-Utf8File (Join-Path $documentationDirectory 'README-Linux.txt') $instructions

    $dataFiles = @(Get-ChildItem -LiteralPath $dataRoot -Recurse -File -Force)
    $installedSize = [long][Math]::Ceiling(($dataFiles | Measure-Object -Property Length -Sum).Sum / 1024.0)
    $control = [IO.File]::ReadAllText((Join-Path $templateDirectory 'control.template')).
        Replace('__VERSION__', $Version).Replace('__ARCHITECTURE__', $architecture).
        Replace('__INSTALLED_SIZE__', $installedSize.ToString([Globalization.CultureInfo]::InvariantCulture))
    Write-Utf8File (Join-Path $controlDirectory 'control') $control
    $md5Lines = foreach ($file in $dataFiles | Sort-Object FullName) {
        $relativeName = [IO.Path]::GetRelativePath($dataRoot, $file.FullName).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm MD5).Hash.ToLowerInvariant()
        "$hash  $relativeName"
    }
    Write-Utf8File (Join-Path $controlDirectory 'md5sums') (($md5Lines -join "`n") + "`n")

    $controlArchive = Join-Path $ridStaging 'control.tar.gz'
    $dataArchive = Join-Path $ridStaging 'data.tar.gz'
    New-TarGzip $controlDirectory $controlArchive
    New-TarGzip $dataRoot $dataArchive
    $debArchive = Join-Path $ridStaging "gitbinder_${Version}_${architecture}.deb"
    New-DebArchive $debArchive $controlArchive $dataArchive
    $portableArchive = Join-Path $ridStaging "GitBinder-$Version-$rid.tar.gz"
    New-TarGzip $applicationDirectory $portableArchive "GitBinder-$Version-$rid"
    Export-Package $debArchive
    Export-Package $portableArchive
}

Write-Host '未运行测试、未安装或启动应用，Windows/macOS 发布目录及历史安装包均保留。'
Write-Warning '请在对应 Linux 架构的桌面会话中验收依赖、密钥环、托盘、Git/SSH 和桌面交互。'
