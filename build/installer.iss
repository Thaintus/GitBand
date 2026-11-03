; ============================================================================
; GitBinder Inno Setup 安装脚本
; 支持：自定义安装目录、添加开始菜单/桌面快捷方式、配置数据目录。
; 用法：ISCC.exe build\installer.iss  （或通过 build-installer.ps1 一键打包）
; ============================================================================

#define MyAppName "GitBinder"
#define MyAppNameCn "Git 仓库账号绑定管理工具"
#ifndef MyAppVersion
  #define MyAppVersion "1.2.14"
#endif
#define MyAppPublisher "GitBinder"
#define MyAppExeName "GitBinder.Desktop.exe"

[Setup]
AppId={{8A2E6F1C-4D2B-4E9C-8A11-2F3B7C5D9E0A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; 允许用户更改安装目录
DisableDirPage=no
; 安装不写入用户已有的旧数据，数据目录独立于安装目录
DirExistsWarning=no
; 使用 x64 体系结构
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 压缩设置
Compression=lzma2
SolidCompression=yes
; 权限
PrivilegesRequired=admin
; 语言
OutputDir=..\dist
OutputBaseFilename=GitBinder-{#MyAppVersion}-setup
SetupIconFile=..\src\GitBinder.Desktop\Assets\avalonia-logo.ico
UninstallDisplayIcon={app}\gitbinder.ico
WizardStyle=modern
; 版本信息
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppNameCn}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "desktopicon\user"; Description: "仅当前用户"; Flags: exclusive
Name: "desktopicon\common"; Description: "所有用户"; Flags: exclusive

[Files]
; 两个 apphost 与共享运行时、各自 deps/runtimeconfig 一并安装；不分发调试符号。
Source: "publish\app\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GitBinder"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\gitbinder.ico"
Name: "{group}\卸载 GitBinder"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GitBinder"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\gitbinder.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 GitBinder"; Flags: nowait postinstall skipifsilent
