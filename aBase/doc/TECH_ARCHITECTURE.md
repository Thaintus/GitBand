# GitBinder 技术架构设计

> 文档版本：v1.0  
> 适用范围：Windows / macOS / Ubuntu  
> 项目类型：跨平台本地桌面应用  
> 核心定位：Git 仓库与 Git 账号/Profile 的本地绑定管理工具  
> 技术路线：.NET 10 LTS + Avalonia 12 + MVVM + SQLite + System Git/OpenSSH

---

## 1. 架构目标

GitBinder 的目标是提供一个跨平台、纯本地、可离线运行的 Git 账号与仓库绑定管理工具。

核心能力包括：

- Git 账号/Profile 管理；
- Git 项目/仓库管理；
- Project → Account 一对一绑定；
- Git `user.name` / `user.email` 仓库级配置；
- SSH Private Key 仓库级绑定；
- HTTPS Credential 安全管理；
- 默认账号；
- Global Mode 全局覆盖模式；
- Git / SSH 配置检测；
- 用户主动触发的远程连接测试；
- Windows / macOS / Ubuntu 安装包构建；
- 完整 i18n 国际化架构；
- 不依赖任何云端服务。

---

# 2. 技术选型

## 2.1 核心技术栈

| 层级 | 技术 |
|---|---|
| 开发语言 | C# |
| Runtime | .NET 10 LTS |
| GUI | Avalonia 12 |
| UI 架构 | MVVM |
| MVVM Framework | CommunityToolkit.Mvvm |
| Dependency Injection | Microsoft.Extensions.DependencyInjection |
| Configuration | Microsoft.Extensions.Configuration |
| Database | SQLite |
| ORM / Data Access | EF Core 或 Dapper |
| Logging | Serilog |
| Git | 系统 Git CLI |
| SSH | 系统 OpenSSH |
| HTTPS Credential | 自定义 Git Credential Helper |
| Windows Secret Store | Windows Credential Manager / DPAPI |
| macOS Secret Store | Keychain |
| Linux Secret Store | Secret Service / libsecret |
| i18n | RESX / 自定义 Localization Service |
| Tests | xUnit |
| CI/CD | GitHub Actions 或自建多平台 Runner |

---

# 3. 为什么选择 Avalonia + .NET

本项目属于典型的桌面系统工具，核心功能涉及：

- 本地文件系统；
- Git CLI；
- SSH CLI；
- Git Config；
- SSH Key；
- 系统 Credential Store；
- 子进程调用；
- 路径处理；
- 跨平台安装包；
- 本地数据库；
- 系统托盘；
- 文件选择器；
- 权限及操作系统差异。

因此相比 Electron、Tauri 等方案，本项目优先选择：

```text
C#
+
.NET
+
Avalonia
```

优势：

1. UI、业务逻辑、基础设施全部使用 C#；
2. 无需维护 TypeScript ↔ Rust IPC；
3. 对系统 API、进程、文件系统、SQLite 集成更直接；
4. Windows / macOS / Linux 可共用绝大部分代码；
5. Avalonia 可以提供统一跨平台 GUI；
6. Domain/Application 层可以完全平台无关；
7. 适合持续使用 AI 编程工具进行维护和迭代。

---

# 4. 总体架构

```text
┌─────────────────────────────────────────────────────────┐
│                     GitBinder.Desktop                   │
│                                                         │
│        Avalonia Views / ViewModels / Components         │
└──────────────────────────┬──────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                  GitBinder.Application                  │
│                                                         │
│ Accounts / Projects / Bindings / GlobalMode / Testing   │
└──────────────────────────┬──────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                    GitBinder.Domain                     │
│                                                         │
│ Account / Project / Binding / EffectiveAccountResolver  │
└──────────────────────────┬──────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                GitBinder.Infrastructure                 │
│                                                         │
│ Git / SSH / SQLite / Credentials / Logging / Storage    │
└──────────────────────────┬──────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                  GitBinder.Platform                     │
│                                                         │
│        Windows        macOS             Linux           │
│        DPAPI          Keychain           SecretService  │
└─────────────────────────────────────────────────────────┘
```

核心原则：

> Domain 和 Application 层不得依赖 Windows、macOS、Linux 的任何具体实现。

---

# 5. Solution 结构

推荐目录：

```text
GitBinder/
│
├── GitBinder.sln
│
├── src/
│   │
│   ├── GitBinder.Desktop/
│   │   ├── App.axaml
│   │   ├── Program.cs
│   │   │
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   ├── Dialogs/
│   │   ├── Converters/
│   │   ├── Assets/
│   │   └── Localization/
│   │
│   ├── GitBinder.Application/
│   │   ├── Accounts/
│   │   ├── Projects/
│   │   ├── Bindings/
│   │   ├── GlobalMode/
│   │   ├── Testing/
│   │   ├── Localization/
│   │   └── Common/
│   │
│   ├── GitBinder.Domain/
│   │   ├── Accounts/
│   │   ├── Projects/
│   │   ├── Bindings/
│   │   ├── Credentials/
│   │   ├── GlobalMode/
│   │   └── Services/
│   │
│   ├── GitBinder.Infrastructure/
│   │   ├── Git/
│   │   ├── Ssh/
│   │   ├── Persistence/
│   │   ├── Credentials/
│   │   ├── Logging/
│   │   └── Configuration/
│   │
│   ├── GitBinder.Platform/
│   │   ├── Common/
│   │   ├── Windows/
│   │   ├── MacOS/
│   │   └── Linux/
│   │
│   └── GitBinder.CredentialHelper/
│       └── Program.cs
│
├── tests/
│   ├── GitBinder.Domain.Tests/
│   ├── GitBinder.Application.Tests/
│   ├── GitBinder.Infrastructure.Tests/
│   └── GitBinder.IntegrationTests/
│
├── build/
│   ├── windows/
│   ├── macos/
│   └── linux/
│
├── docs/
│
└── .github/
    └── workflows/
```

---

# 6. 核心领域模型

## 6.1 Account

```text
Account
├── Id
├── Alias
├── Platform
├── Host
├── Username
├── GitName
├── GitEmail
├── AuthenticationType
├── SshPrivateKeyPath
├── SshPublicKeyPath
├── PassphraseSecretId
├── HttpsUsername
├── HttpsSecretId
├── IsDefault
├── Enabled
├── CreatedAt
└── UpdatedAt
```

账号对象同时描述：

```text
Commit Identity
+
Authentication Identity
```

Commit Identity：

```text
user.name
user.email
```

### 提交邮箱为空的语义

- `Account.GitEmail` 留空或纯空白表示“新提交不携带邮箱”，不是继承默认账号或系统/全局邮箱。`GitConfigApplier` 应用身份时，对当前作用域的 `user.email`、`author.email`、`committer.email` 都执行 `--replace-all` 并传入显式空字符串，不用 `--unset-all`；填写邮箱时三个配置项统一为该值。普通/全局模式遵循既有有效账号优先级。
- 依据 [Git 官方 ident.c（v2.51.0）](https://github.com/git/git/blob/v2.51.0/ident.c)：显式空 `user.email` 会标记为已配置，阻止默认邮箱推断，可生成 `姓名 <>`；独立非空作者/提交者邮箱优先于 `user.email`，故必须同时覆盖。该结论来自源码核对，本轮没有执行真实提交验证。
- `GitEmailConfigSnapshot(UserEmail, AuthorEmail, CommitterEmail)` 用 `null` 表示未配置、`""` 表示明确无邮箱。捕获仅裁剪命令输出行尾，缺失键（exit 1）与读取失败严格区分；恢复只有 null 才删除键，其他值原样写回。
- 仓库快照增加可空 `email_config_json` 列，旧 `user_email` 保留。首次升级应用绑定前，补采集此前未托管的 `author.email`/`committer.email`，但 `user.email` 仍保留历史快照值；旧仓库快照空值无法追溯缺失与显式空的区别，维持旧版空值删除语义。全局 JSON 快照可选 `EmailConfig`，升级同样只补新增配置，不覆盖原 UserEmail。
- 新建绑定、启动重新应用、全局切换、协议确认和账号编辑同步之前，都先确保快照完整。仓储显式列名查询，幂等升级保留旧行；损坏邮箱快照不能静默视为缺失后继续覆盖配置。
- `AccountIdentitySyncService` 在账号编辑持久化成功后协调同步：普通模式仅同步实际生效为该账号的绑定仓库身份，不改 origin/认证；编辑当前全局账号时重新应用全局配置与已绑定仓库。持久化成功但同步失败单独提示并留窗重试，不宣称事务整体失败。新建账号无现有绑定时不额外写入 Git。
- 隐私边界：不重写旧提交，也不改变 amend/rebase/cherry-pick 保留的作者；显式环境变量、命令行/IDE 作者覆盖、worktree 等更高优先级配置仍可覆盖管理配置。未登记仓库的本地配置仍优先于全局配置；远程平台是否接受空邮箱由服务器策略决定。同步失败时不能保证邮箱已清空，用户应先处理提示再提交。

Authentication Identity：

```text
SSH Key
或
HTTPS Username + Token/Password
```

---

## 6.2 Project

```text
Project
├── Id
├── Name
├── RepositoryPath
├── CanonicalPath
├── GitDir
├── OriginUrl
├── RemoteHost
├── RemoteProtocol
├── CurrentBranch
├── CreatedAt
└── UpdatedAt
```

Project 仅表示：

> 本机已经存在的 Git Repository。

GitBinder 不复制 Repository 内容。

### 克隆与快进拉取

- 新增 Application 端口 `IGitTransfer` 与用例服务 `ProjectTransferService`，由 Infrastructure 的 `GitTransfer` 通过 `ICommandExecutor` 执行参数数组；不引入平台 API、提交、推送或冲突解决器。
- 克隆先验证 HTTPS/SSH 地址与目标目录、按 `EffectiveAccountResolver.ResolveForClone` 选择认证账号；完成后登记时跳过默认绑定，直接绑定用户所选账号。全局模式控制实际认证，所选绑定保留供关闭全局模式后使用。
- 拉取先检查进行中的 Git 操作和 origin 跟踪分支，再用当前有效账号执行 `pull --ff-only --no-rebase --no-recurse-submodules`；允许存在本地改动及未跟踪文件，由 Git 判断更新是否会覆盖它们，真实覆盖冲突映射为 `PULL_DIRTY`。禁用自动 stash，不重置或强推，不递归拉取子模块。
- HTTPS 重置 helper 与额外认证头，在精确 URL 作用域指定带账号 ID 的凭据助手；SSH 指定单个私钥，保持严格主机校验，仅允许 Agent 为该密钥签名。子进程禁止交互，隔离全局/系统 Git 配置及继承的仓库路径环境，不更改开发机环境或配置文件。
- `CommandExecutionOptions` 提供子进程环境与 30 分钟超时；取消/超时终止进程树，错误不回显原始 stderr。失败不自动删除部分克隆目录，登记和绑定失败分别反馈。数据库 Schema 不变。

### Remote 协议切换

项目页面加载时经 `ProjectService.RefreshMetadataAsync` 读取本地 origin，更新缓存协议和主机，不联网、不改 Git 配置。读取失败时保留缓存并提示，未配置 origin 与读取失败分别使用空字符串与 null 表示；禁止将含 HTTP 用户信息的地址自动写入缓存。项目仓储使用显式列名保持映射稳定。

项目卡片支持通过 `ProjectService.UpdatePathAsync` 重新定位移动后的同一仓库：要求绝对路径，校验 Git 仓库并解析真实根目录后去重，再一次性保存路径、GitDir、origin、协议、主机和分支，清除旧测试结果。保留原项目 ID、名称、绑定与配置快照，不改写 Git 配置，不搬移或删除文件；用户保存前确认是同一仓库，不能用于替换成其他仓库或新克隆副本。

项目“修改地址”编辑区可在标准 Remote 地址间切换：`https://host/group/repo.git` 转为 `git@host:group/repo.git`，反向切换则生成 HTTPS 地址。切换只修改编辑框内容，用户仍须点击“保存地址”才会执行本地 `git remote set-url`。

带 HTTPS 用户信息、查询参数、片段或非默认端口的地址不自动转换，避免错误猜测 SSH/HTTPS 服务端口；此类地址由用户手动维护。

#### 账号变更与协议确认

- `BindingService.GetProtocolChangeAsync` 只读检查实际 origin 与当前有效账号（全局 > 绑定 > 默认），仅在缺少当前协议凭据但已配置另一协议凭据时生成 `BindingProtocolChange`；双协议账号不强制转换。复用 `GitTransferRemote` 与 `GitRemoteUrlConverter`，不猜测平台、端口或仓库路径。
- 项目改绑仍选择即保存；之后由 `ProjectsViewModel` 展示协议变更确认，取消保留已保存的绑定和原地址。已有错配项目在测试、单项拉取前同样确认，取消则不连接。全局模式变化不批量改写地址；分组批量拉取保留逐项失败报告。
- 确认记录包含项目、实际路径、当前有效账号和原/目标地址。`ChangeProtocolAsync` 执行前重新检查，过期则拒绝；仅修改 origin（不改独立 pushurl），按新协议重新应用同一个有效账号，并同步项目元数据，不覆盖已有解绑快照。
- 失败后尝试恢复本次地址与同一有效账号的认证配置。回滚前先读取实际 origin，仅当它仍是本次目标地址才回写原地址；不覆盖 IDE 并发改出的第三个地址。不能确认实际地址或恢复失败时明确提示，认证禁止回退到其他账号。
- 协议预览、确认和回滚通过 `IGitService.GetTransferOriginUrlAsync/SetTransferOriginUrlAsync` 使用与 `GitTransfer` 一致的隔离环境，避免全局 insteadOf 或继承的 GIT_DIR 令预览与真实测试指向不同协议/仓库；其他既有元数据读取接口行为保留。

#### 只读认证测试与凭据保留

- `BindingService.TestAsync` 不再先调用配置应用器；通过 `IGitTransfer.TestAsync` 读取实际远程地址、复用拉取的账号认证参数与非交互环境执行 ls-remote。测试默认 60 秒超时，拉取/克隆仍 30 分钟；SSH 临时空配置覆盖请求生命周期。
- HTTPS 缺凭据、SSH 缺私钥、无法启动助手、网络及权限问题使用结构化 DomainError；TestRemoteResult 只携带成功标记、错误与耗时，不回显原始 stderr、Token 或服务端输出。旧 `IGitService.TestRemoteAsync` 移除。
- 账号编辑保留原 SecretId 是否存在的状态，不读取或回填明文。HTTPS 用户名仍存在且已有 SecretId 时，空白凭据不降低认证能力；可恢复旧版误降级但仍有凭据引用的记录。新建和重新编辑均重置此状态，SSH 增删按现有表单重新判断组合类型；清空 HTTPS 用户名停用该能力，但不删除原秘密。

---

## 6.3 Binding

```text
Binding
├── Id
├── ProjectId
├── AccountId
├── Status
├── AppliedAt
├── VerifiedAt
└── LastTestResult
```

数据库必须约束：

```text
UNIQUE(project_id)
```

即：

> 一个 Project 只能绑定一个 Account。

一个 Account 可以绑定多个 Project。

---

# 7. Effective Account Resolver

任何 Git 身份相关操作都必须通过统一服务：

```text
EffectiveAccountResolver
```

规则：

```text
if GlobalMode.Enabled:
    return GlobalMode.Account

if Project.Binding exists:
    return Project.Binding.Account

return DefaultAccount
```

流程：

```text
             Global Mode?
                  │
          ┌───────┴───────┐
          │               │
         Yes              No
          │               │
          ▼               ▼
 Global Account     Project Binding?
                          │
                  ┌───────┴───────┐
                 Yes              No
                  │               │
                  ▼               ▼
           Bound Account    Default Account
```

禁止 ViewModel、GitService、SSHService 等模块自行实现账号选择逻辑。

---

# 8. Git 集成架构

## 8.1 使用系统 Git

V1 不使用 LibGit2Sharp / libgit2 作为核心 Git 引擎。

统一调用：

```text
git
```

理由：

- 与 Git CLI 行为一致；
- 与 IDEA / VS Code / SourceGit 等工具读取同一份 Git Config；
- 更容易验证配置是否真正生效；
- 避免重新实现 Git Credential / SSH 行为；
- 跨平台一致。

---

## 8.2 GitLocator

定义：

```csharp
public interface IGitLocator
{
    Task<string?> LocateAsync();
}
```

查找顺序：

1. 用户设置指定路径；
2. PATH；
3. 当前平台常见安装目录。

---

## 8.3 GitService

建议接口：

```text
IGitService
├── ValidateRepository
├── GetRepositoryRoot
├── GetRemotes
├── GetOrigin
├── GetCurrentBranch
├── ReadLocalIdentity
├── ApplyAccount
├── RestoreConfig
├── ReadEffectiveConfiguration
├── TestRemote
└── GetGitVersion
```

---

# 9. SSH 架构

## 9.1 使用系统 OpenSSH

优先调用：

```text
ssh
ssh-keygen
```

而不是自己实现 SSH 协议。

---

## 9.2 仓库级 SSH Key

SSH Repository 推荐将 Key 绑定写入：

```text
.git/config
```

通过：

```bash
git config --local core.sshCommand "ssh -i <key> -o IdentitiesOnly=yes"
```

例如：

```ini
[core]
    sshCommand = ssh -i "/Users/user/.ssh/company" -o IdentitiesOnly=yes
```

这样能够保证：

```text
Repo A → SSH Key A
Repo B → SSH Key B
```

即使两个仓库位于同一个 Host：

```text
github.com
```

也不会混用账号。

必须默认：

```text
IdentitiesOnly=yes
```

避免 ssh-agent 中其他 Key 被自动尝试。

当仓库选择的账号没有 SSH 私钥时，仍要写入使用 Windows 空配置/空身份文件（`-F NUL -i NUL`）且禁用 Agent、交互式认证的 `core.sshCommand`，而不是清除该项后回退到系统默认 Key。这样允许跨平台改绑，但远程认证只能使用当前选中账号提供的认证信息；未配置对应私钥时应认证失败。

---

# 10. HTTPS Credential 架构

HTTPS Account 包含：

```text
Username
Credential Secret
```

禁止：

```text
https://username:password@example.com/repo.git
```

禁止将 Token 写入 `.git/config`。

建议单独构建：

```text
GitBinder.CredentialHelper
```

输出：

```text
gitbinder-credential
```

Git：

```text
git
 │
 ▼
credential.helper
 │
 ▼
gitbinder-credential
 │
 ▼
SecretStore
 │
 ▼
username + password/token
```

GUI 不运行时 Credential Helper 仍可工作。

仓库级 `credential.helper` 必须先写入空值以重置继承的 Helper 链，再追加带 `--account-id <Guid>` 的 GitBinder Helper 绝对路径。该 Guid 固定到当前选中账号，Helper 不应再按 Host、默认账号或系统已有凭据回退；账号没有 HTTPS Secret 时只保留空 Helper，认证应失败。

---

# 11. Secret Store 架构

统一接口：

```csharp
public interface ISecretStore
{
    Task SetAsync(string key, string value);
    Task<string?> GetAsync(string key);
    Task DeleteAsync(string key);
}
```

不同平台：

```text
Windows
   ↓
Credential Manager / DPAPI

macOS
   ↓
Keychain

Ubuntu / Linux
   ↓
Secret Service / libsecret
```

数据库中只允许保存：

```text
SecretId
```

不得明文保存：

- Password；
- Token；
- SSH Passphrase；
- Private Key Content。

SSH Private Key 默认仅记录文件路径。

---

# 12. Platform Adapter

平台差异全部放入：

```text
GitBinder.Platform
```

建议接口：

```text
IPlatformService
ISecretStore
IApplicationDataPath
IPathNormalizer
IFilePermissionService
IGitLocator
ISshLocator
IShellService
```

启动时：

```csharp
if (OperatingSystem.IsWindows())
{
    services.AddWindowsPlatform();
}
else if (OperatingSystem.IsMacOS())
{
    services.AddMacOSPlatform();
}
else if (OperatingSystem.IsLinux())
{
    services.AddLinuxPlatform();
}
```

---

# 13. 路径架构

禁止：

```text
"C:\\Users\\..."
```

散落在业务代码中。

禁止简单使用：

```text
Replace("\\", "/")
```

路径统一经过：

```text
IPathNormalizer
```

处理：

```text
Normalize
Canonicalize
ResolveSymlink
Compare
```

尤其需要考虑：

- Windows 默认大小写不敏感；
- Linux 大小写敏感；
- macOS 文件系统可能大小写敏感或不敏感；
- 符号链接；
- 网络磁盘；
- UNC Path；
- Repository Worktree。

---

# 14. Command Executor

所有 Git / SSH 外部命令统一经过：

```text
ICommandExecutor
```

接口：

```csharp
Task<CommandResult> ExecuteAsync(
    string executable,
    IReadOnlyList<string> arguments,
    string? workingDirectory,
    CancellationToken cancellationToken);
```

必须统一处理：

- stdout；
- stderr；
- Exit Code；
- Timeout；
- Cancellation；
- Encoding；
- Environment Variables；
- Secret Masking；
- Process Kill；
- Command Logging。

禁止大量直接使用：

```csharp
Process.Start(...)
```

更禁止拼接：

```text
cmd.exe /c "..."
```

参数必须使用参数数组，降低 Shell Injection 风险。

---

# 15. 数据存储

数据库使用：

```text
SQLite
```

不同平台数据目录：

### Windows

```text
%LOCALAPPDATA%/GitBinder/
```

### macOS

```text
~/Library/Application Support/GitBinder/
```

### Linux

```text
~/.local/share/GitBinder/
```

目录结构：

```text
GitBinder/
├── gitbinder.db
├── config.json
├── logs/
└── backup/
```

---

# 16. GUI 架构

采用标准 MVVM：

```text
View
 │
 ▼
ViewModel
 │
 ▼
Application Service
 │
 ▼
Domain
 │
 ▼
Infrastructure
```

禁止：

```text
Button_Click
    ↓
Process.Start("git")
```

应为：

```text
Button
  ↓
ICommand
  ↓
ViewModel
  ↓
ApplicationService
  ↓
GitService
```

---

# 17. GUI 页面结构

```text
Dashboard

Accounts
Projects
Bindings

Global Mode

Logs
Settings
```

建议全局导航保持平台一致，不为不同 OS 维护不同业务页面。

系统级交互允许使用平台适配：

- 文件选择器；
- 系统托盘；
- 打开 Finder / Explorer / File Manager；
- 打开 Terminal；
- 系统 Credential Store。

---

# 18. i18n / l10n 国际化架构

国际化必须从项目第一版就作为基础架构能力实现，禁止后期再通过大量替换硬编码文本补救。

目标：

```text
同一套业务代码
      │
      ├── zh-CN
      ├── en-US
      └── 后续其他语言
```

V1 推荐至少提供：

```text
zh-CN
en-US
```

---

# 19. i18n 核心原则

## 19.1 UI 禁止硬编码用户可见文本

禁止：

```xml
<Button Content="保存" />
```

或者：

```csharp
MessageBox.Show("保存成功");
```

所有用户可见文本必须使用：

```text
Localization Key
```

例如：

```text
Common.Save
Common.Cancel

Accounts.Title
Accounts.Add
Accounts.Edit

Projects.Title

Binding.Test.Success

GlobalMode.EnabledWarning
```

---

# 20. Localization Service

推荐建立：

```csharp
public interface ILocalizationService
{
    CultureInfo CurrentCulture { get; }

    IReadOnlyList<CultureInfo> SupportedCultures { get; }

    string GetString(string key);

    string GetString(string key, params object[] args);

    Task SetCultureAsync(CultureInfo culture);
}
```

业务层不得直接读取 RESX。

所有动态语言切换统一经过：

```text
ILocalizationService
```

---

# 21. Resource 目录

推荐：

```text
src/GitBinder.Desktop/Localization/
│
├── Resources.resx
├── Resources.zh-CN.resx
├── Resources.en-US.resx
│
└── LocalizationKeys.cs
```

也可以按模块拆分：

```text
Localization/
├── Common/
│   ├── Common.zh-CN.resx
│   └── Common.en-US.resx
│
├── Accounts/
├── Projects/
├── Bindings/
├── GlobalMode/
└── Settings/
```

项目规模扩大后推荐模块化资源。

---

# 22. Localization Key 规范

禁止使用中文本身作为 Key：

```text
保存=Save
```

应该使用语义 Key：

```text
Common.Save
Common.Cancel
Common.Confirm

Account.Add.Title
Account.Delete.Confirm

Project.Binding.Success

GlobalMode.Enable.Warning
```

Key 必须：

- 与语言无关；
- 具有业务语义；
- 不包含 UI 布局位置；
- 不依赖具体中文文本。

---

# 23. 动态语言切换

Settings 页面提供：

```text
Language

○ Follow System
○ 简体中文
○ English
```

默认：

```text
Follow System
```

启动时：

```text
用户已配置语言
      ↓
使用用户语言

否则
      ↓
检测 CurrentUICulture

如果支持
      ↓
使用系统语言

否则
      ↓
fallback → en-US
```

语言切换原则上不要求重启应用。

Avalonia UI 通过绑定 Localization Service，在 Culture Changed 后通知相关 ViewModel / Resource Binding 更新。

---

# 24. Culture 与 Language 分离

必须区分：

```text
UI Language
```

和：

```text
Formatting Culture
```

例如用户可以：

```text
UI = English
System Region = China
```

因此：

- UI 文案可以是英文；
- 时间、数字等格式可以遵循系统区域设置。

不要假设：

```text
Language == Region
```

---

# 25. 日期、时间、数字格式

禁止：

```csharp
date.ToString("yyyy-MM-dd HH:mm:ss")
```

直接用于所有 GUI。

展示层应根据：

```text
CultureInfo
```

格式化。

例如：

```text
zh-CN:
2026/08/22 13:30

en-US:
8/22/2026 1:30 PM
```

但是：

- 数据库存储；
- 日志机器字段；
- JSON；
- API/Internal DTO；

统一使用：

```text
ISO 8601
```

例如：

```text
2026-08-22T13:30:00+08:00
```

---

# 26. 日志与 UI 文案分离

日志不要直接存：

```text
“项目绑定成功”
```

建议记录：

```text
EventId = BindingApplied
ProjectId = ...
AccountId = ...
```

UI 展示时：

```text
BindingApplied
      ↓
Localization
      ↓
项目绑定成功
```

英文：

```text
Binding applied successfully.
```

这样：

- 日志可稳定搜索；
- 切换 UI 语言不影响历史日志；
- 日志格式不依赖语言。

诊断日志可以同时记录英文技术信息，但不得使用本地化字符串作为唯一机器标识。

---

# 27. 错误模型国际化

Domain/Application 不应该抛出：

```text
throw new Exception("仓库不存在");
```

推荐：

```text
DomainError
├── Code
├── Arguments
└── TechnicalDetails
```

例如：

```text
Code:
PROJECT_REPOSITORY_NOT_FOUND

Arguments:
path
```

GUI：

```text
Error Code
    ↓
Localization Key
    ↓
Projects.Error.RepositoryNotFound
```

中文：

```text
未找到 Git 仓库：{0}
```

英文：

```text
Git repository not found: {0}
```

---

# 28. Git / SSH 原始错误处理

Git 与 SSH 返回的 stderr 不应尝试强行全文翻译。

例如：

```text
Permission denied (publickey).
```

应显示：

```text
认证失败

Git/SSH 原始信息：
Permission denied (publickey).
```

其中：

```text
认证失败
```

属于本地化业务解释。

原始 stderr：

```text
保持原样
```

方便用户排查和搜索资料。

---

# 29. 安装包国际化

安装包语言与应用语言分离。

推荐：

### Windows Installer

至少：

```text
English
Simplified Chinese
```

### macOS

`.app` 本身遵循系统语言。

### Ubuntu

安装包元数据至少提供英文描述，应用启动后使用自己的 i18n。

安装器不得成为业务语言配置的唯一来源。

应用首次启动仍然根据：

```text
CurrentUICulture
```

确定语言。

---

# 30. i18n 测试

必须建立测试：

```text
LocalizationTests
```

检查：

### Key 完整性

```text
zh-CN keys
==
en-US keys
```

防止某种语言缺失文本。

### 空值检测

禁止：

```text
key exists but value empty
```

### 占位符一致性

例如：

```text
zh-CN:
已绑定 {0} 个项目

en-US:
{0} projects bound
```

必须检查两边参数数量一致。

### UI Overflow

CI 无法完全解决，应在 GUI 测试中重点检查：

- 英文通常比中文长；
- 德语未来可能更长；
- Button 不应写死宽度；
- Dialog 应允许自适应；
- TextBlock 应支持 Wrap。

---

# 31. i18n 禁止事项

禁止：

```text
"账号：" + accountName
```

应该：

```text
Localization.Format("Account.Label", accountName)
```

禁止根据中文字符串做业务判断：

```csharp
if (status == "成功")
```

必须使用：

```text
enum / code
```

禁止：

```text
Enum.ToString()
```

直接显示给用户。

例如：

```text
AuthenticationType.Ssh
```

显示时：

```text
AuthenticationType.Ssh
      ↓
Authentication.Ssh
      ↓
SSH
```

---

# 32. Global Mode 架构

Global Mode 是 Overlay，不修改 Binding 数据关系。

普通模式：

```text
Project A → Account A
Project B → Account B
```

开启：

```text
Global Account = Account C
```

有效账号：

```text
Project A → Account C
Project B → Account C
```

数据库中的原 Binding：

```text
保持不变
```

Global Mode 开启时需要：

```text
Snapshot
→ Apply Global Account
→ Verify
```

除 `user.name`、`user.email` 外，还需以 Global Account 覆盖全局 `core.sshCommand` 与 `credential.helper`：SSH 禁止使用系统 Agent/默认 Key 回退，HTTPS 重置已有 Helper 后仅调用带账号 Id 的 GitBinder Helper。关闭时一并恢复进入全局模式前快照中的上述配置。

应用启动时，全局模式关闭也要重新应用所有已登记项目的 Binding；这是认证配置的升级补写步骤，确保旧绑定获得当前的 SSH/HTTPS 无回退规则，而不要求用户逐个重新保存绑定。

关闭：

```text
Drift Detection
→ Restore Snapshot
```

Global Mode 建议使用状态机：

```text
DISABLED
ENABLING
ENABLED
SWITCHING
RESTORING
ERROR
```

而不是只有 Boolean。

---

# 33. Repository Config Snapshot

修改 `.git/config` 前必须 Snapshot。

保存：

```text
user.name
user.email
core.sshCommand
credential.helper
credential.useHttpPath
```

流程：

```text
Read
 ↓
Snapshot
 ↓
Validate
 ↓
Write
 ↓
Read Back
 ↓
Verify
 ↓
Commit DB Transaction
```

失败：

```text
Rollback
```

---

# 34. 配置漂移检测

其他工具可能修改 `.git/config`：

```text
IDEA
VS Code
SourceGit
Git CLI
```

因此数据库 Binding 不能作为唯一真相。

Project 打开或测试时读取实际配置：

```text
user.name
user.email
core.sshCommand
credential.helper
```

状态：

```text
SYNCED
DRIFTED
UNBOUND
ERROR
```

GUI 支持：

```text
重新应用绑定
采用当前仓库配置
```

---

# 35. 数据库建议

核心表：

```text
accounts
projects
bindings
repository_snapshots
global_mode
global_mode_snapshots
operation_logs
settings
```

国际化不要把翻译文本写数据库。

数据库中存：

```text
status_code
error_code
event_code
```

GUI 通过 Localization Service 转换为用户语言。

---

# 36. Settings

Settings 建议包含：

```text
General
├── Language
├── Theme
├── Startup
└── Close to tray on window close

Git
├── Git Executable
└── Git Version

SSH
├── SSH Executable
└── SSH Version

Storage
├── Data Directory
└── Log Directory

Security
└── Secret Store Status
```

语言配置存：

```text
settings.language
```

关闭托盘配置存：

```text
settings.close_to_tray_on_close
```

Windows/macOS 缺省值为 `true`：用户关闭主窗口时仅隐藏窗口，托盘菜单提供“显示窗口”和“退出 GitBind”；用户在设置中关闭该选项后，关闭窗口即退出应用。Linux 缺省值为 `false`，避免没有托盘宿主的桌面隐藏窗口后无法找回；确认支持托盘后可手动开启。窗口关闭逻辑与设置页共享平台默认值，已保存的有效用户选项优先。

例如：

```text
system
zh-CN
en-US
```

---

# 37. 打包架构

统一使用：

```text
dotnet publish
+
平台原生打包脚本
```

发布采用：

```text
Self-contained
```

避免要求用户提前安装 .NET Runtime。

---

# 38. Windows 构建

Runtime Identifier：

```text
win-x64
win-arm64
```

推荐产物：

```text
GitBinder-x.y.z-win-x64-setup.exe
GitBinder-x.y.z-win-x64.msi

GitBinder-x.y.z-win-x64-portable.zip
```

Portable 版本对于开发工具用户建议保留。

---

# 39. macOS 构建

当前提供实验性双架构 `.app.zip`：`build/build-macos.ps1` 可在 Windows 交叉发布，Mac 本机通过包内脚本签名并生成未公证 DMG。平台路径、Keychain 与私钥权限适配的实际约定见 [macOS 打包与验收说明](MACOS_PACKAGING.md)。跨平台构建不等于 Mac 实机认证、窗口和安装验证；以下正式签名、公证要求仍然适用。

Runtime Identifier：

```text
osx-x64
osx-arm64
```

产物：

```text
GitBinder.app
GitBinder-x.y.z-macos.dmg
```

正式发布必须考虑：

```text
Code Signing
Notarization
```

推荐分别构建：

```text
Intel
Apple Silicon
```

未来再考虑 Universal Binary。

---

# 40. Ubuntu / Linux 构建

实验性实现使用 `build/build-linux.ps1` 生成双架构 `.deb` 与便携 `.tar.gz`，数据根遵循 XDG、凭据经 libsecret 访问已解锁的 Secret Service。发布布局、Linux 桌面依赖与实机验收边界见 [Linux 打包与验收说明](LINUX_PACKAGING.md)；不把 Windows 上交叉发布成功视为 Linux 安装/GUI/认证通过。

Runtime Identifier：

```text
linux-x64
linux-arm64
```

主要产物：

```text
gitbinder_x.y.z_amd64.deb
gitbinder_x.y.z_arm64.deb
```

可选：

```text
AppImage
```

优先保证 `.deb`。

---

# 41. CI/CD

Release Pipeline：

```text
Git Tag
   │
   ├──────── Windows Runner
   │           ↓
   │        Build/Test
   │           ↓
   │        EXE/MSI/ZIP
   │
   ├──────── macOS Runner
   │           ↓
   │        Build/Test
   │           ↓
   │        Sign/Notarize
   │           ↓
   │        APP/DMG
   │
   └──────── Ubuntu Runner
               ↓
            Build/Test
               ↓
              DEB
```

不要依赖一台 Windows 主机交叉构建所有正式发行包。

---

# 42. 测试架构

## Unit Tests

重点：

```text
Account
Project
Binding
EffectiveAccountResolver
GlobalMode
Localization
```

## Integration Tests

动态建立临时 Git Repository：

```text
git init
```

测试：

- local config；
- SSH Command 写入；
- Snapshot；
- Restore；
- Drift Detection；
- Global Mode。

## Platform Tests

分别在：

```text
Windows
macOS
Ubuntu
```

测试：

- Path；
- Secret Store；
- Git Locator；
- SSH Locator；
- Process Executor；
- Application Data Directory。

---

# 43. 纯离线要求

应用自身禁止：

- 自动检查更新；
- Telemetry；
- Crash Upload；
- Cloud Sync；
- CDN；
- 在线头像；
- 在线 License；
- SaaS Login。

允许用户主动触发：

```text
git clone
git fetch
git pull
git push
git ls-remote
ssh remote test
```

GUI 必须明确区分：

```text
本地配置测试
```

与：

```text
远程连接测试
```

---

# 44. 安全架构

必须遵守：

1. Private Key Content 不进入数据库；
2. Password / Token 不明文存储；
3. Passphrase 不明文存储；
4. 日志必须 Secret Masking；
5. 外部命令禁止 Shell 字符串拼接；
6. Git Config 修改必须 Snapshot；
7. Global Mode 必须可恢复；
8. HTTPS Token 不得写入 Remote URL；
9. Sensitive UI 默认遮罩；
10. Clipboard Copy Secret 后可考虑延迟清理。

---

# 45. 推荐 NuGet 依赖方向

建议优先使用成熟基础库：

```text
Avalonia
Avalonia.Desktop
CommunityToolkit.Mvvm

Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Configuration
Microsoft.Extensions.Logging

Microsoft.Data.Sqlite
或
Microsoft.EntityFrameworkCore.Sqlite

Serilog
Serilog.Sinks.File

xUnit
FluentAssertions（可选）
```

避免第一版引入过多 UI 框架和大型第三方依赖。

---

# 46. 开发约束

## Domain

不得依赖：

```text
Avalonia
SQLite
Windows API
macOS API
Linux API
```

## Application

不得：

```text
直接 Process.Start
直接访问 SQLite
直接访问 DPAPI / Keychain
```

## GUI

不得：

```text
直接执行 git
直接操作数据库
直接读取 SSH Key
```

## Infrastructure

负责：

```text
Git
SSH
Persistence
Logging
Credentials
```

## Platform

只负责操作系统差异。

---

# 47. 推荐 MVP

V1：

```text
Accounts
Projects
Bindings
Default Account
SSH
HTTPS
Repository Local Config
Binding Test
Global Mode
Snapshot / Restore
SQLite
Secret Store
zh-CN
en-US
Windows/macOS/Ubuntu Build
```

暂不实现：

```text
完整 Commit GUI
Diff GUI
Merge GUI
Rebase GUI
PR/MR
Issue
Cloud Sync
```

GitBinder 首先应该保持定位：

> Git Repository Account Binding Manager

而不是发展成另一个完整 Git Client。

---

# 48. 最终架构结论

推荐最终技术架构：

```text
                  GitBinder
                      │
          ┌───────────┴───────────┐
          │                       │
 Avalonia Desktop        Credential Helper
          │                       │
         MVVM                    CLI
          │                       │
          └───────────┬───────────┘
                      │
              Application Layer
                      │
          ┌───────────┼───────────┐
          │           │           │
       Account      Project     Binding
          │           │           │
          └───────────┼───────────┘
                      │
                 Domain Core
                      │
           EffectiveAccountResolver
                      │
       ┌──────────────┼──────────────┐
       │              │              │
   GitService      SSHService    SecretStore
       │              │              │
   System Git      OpenSSH           │
                                     │
                   ┌─────────────────┼─────────────────┐
                   │                 │                 │
                Windows            macOS             Linux
            CredentialMgr         Keychain       SecretService
```

UI 国际化：

```text
                 Avalonia UI
                      │
             LocalizationService
                      │
        ┌─────────────┴─────────────┐
        │                           │
      zh-CN                       en-US
        │                           │
        └─────────────┬─────────────┘
                      │
              Culture Changed
                      │
                Dynamic Refresh
```

发行：

```text
.NET 10 LTS
+
Avalonia 12
+
Self-contained

├── Windows
│   ├── x64 / arm64
│   ├── Installer
│   └── Portable
│
├── macOS
│   ├── x64 / arm64
│   └── DMG
│
└── Ubuntu
    ├── amd64 / arm64
    └── DEB
```

该架构的核心原则：

> **业务逻辑跨平台、平台能力通过 Adapter 隔离、Git 行为依赖系统标准 Git/OpenSSH、敏感数据使用操作系统 Secret Store、所有用户可见文本从第一版开始通过 i18n Resource 管理。**

## 项目分组与批量拉取

- 分组实体为 ProjectGroup，ProjectGroupService 验证名称并管理归属；IProjectGroupRepository 由 SQLite ProjectGroupRepository 实现。归属独立存表，不修改 Project 的路径、Remote、绑定或快照。
- project_groups 保存名称，project_group_members 以 project_id 为主键，支持一个项目一个分组；缺省无归属记录即未分组。Schema 与 DatabaseInitializer 均幂等创建新增表，历史项目原样保留。
- 删除分组和移除项目时，仓储通过显式事务清理归属关系；不依赖 SQLite 的 foreign_keys 开关，不级联删除用户项目或源码。
- ProjectsViewModel.Groups.cs 负责侧栏、新建/重命名/删除确认、即时归类及搜索叠加；新建/重命名由 DialogHelper 打开 GroupEditDialog，通过保存回调调用 ProjectGroupService，取消不写入、失败在弹窗内展示并允许重试。仅更改分组时不重新读取 Git 元数据，名称校验与归属持久化仍在原应用/仓储层。
- ProjectTransferService.PullGroupAsync 在应用层从仓储获取完整分组项目快照，持有与单项拉取/克隆相同的互斥锁，并顺序调用共用 PullCoreAsync。继续处理单项失败，取消后停止剩余项目，返回 GroupPullReport；进度不暴露凭据或原始命令错误。
- 所有拉取仍由 EffectiveAccountResolver 决策实际账号，全局模式优先；保持工作区保护及仅快进策略。未增加平台 API 枚举、推送或自动合并功能。

## 桌面结果通知与 Windows 安装图标

- ViewModelBase.SetNotice 统一发布操作结果，经 DialogHelper 的 UI 调度与串行队列显示 MessageDialog；不持久化消息，不改变业务错误码。账号编辑通知关联对应编辑窗口，应用退出期间的窗口异常不会向外传播。
- 项目 OperationProgress 与结果 Feedback 分离；批量汇总包含逐项结果，传输后的列表刷新错误合并到同一结果。MessageDialog 与 ConfirmationDialog 使用 SelectableTextBlock + ScrollViewer，提供原生文本选择及 Ctrl+C；不使用只读 TextBox、复制全部按钮或额外剪贴板写入逻辑。通知队列、消息来源及错误脱敏规则不变。
- Desktop 工程通过 ApplicationIcon 嵌入 Windows 原生图标；Inno Setup 的 UninstallDisplayIcon 显式指向安装目录的 gitbinder.ico。托盘、快捷方式和安装项复用已有图标，不变更 AppId 或用户数据路径。
- 发布脚本逐项检查 dotnet publish 退出码，避免主程序失败后继续生成不完整安装包；清理仅限已校验的仓库内 build/publish 临时产物。

## SSH 传输空配置与诊断

- 克隆与拉取通过 EmptySshConfig 为每次操作创建真实空配置文件，传给 SSH 的 -F 参数；不依赖不同 SSH 实现对 NUL 或 /dev/null 的解释。
- 文件用 CreateNew 唯一创建，关闭创建句柄后以只读句柄持有并允许共享读取，禁止外部写入；不使用 DeleteOnClose，也不持有写句柄，以兼容 Windows OpenSSH。操作结束或失败后 Dispose 清理唯一临时文件；异常退出时可能残留不含密钥或凭据的空文件。
- 保持指定私钥、IdentitiesOnly、非交互认证、严格主机校验及仅快进更新，不读取其他账号配置作为认证回退。
- GitTransferFailure 将 stderr 归类为固定的安全错误码，附带失败命令阶段和退出码，由中英文资源解释；涵盖空配置读取、主机密钥、认证、DNS、连接、超时、证书、目录所有权、快进分叉、锁和本地文件错误。未知诊断不回显原始文本，避免服务端输出或 URL 中夹带凭据。
- 本次不改变既有全局/系统 Git 配置隔离策略，HTTPS 证书、代理和 safe.directory 兼容性仍须按具体错误单独评估。
