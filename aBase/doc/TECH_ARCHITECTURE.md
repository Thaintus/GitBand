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
- `BindingService.BindAsync` 使用独立绑定副本和本次操作前的瞬时 `GitLocalConfigSnapshot`。配置部分写入或数据库落库失败时，先确认提交状态，再以不受用户取消影响的 token 补偿；已提交返回成功，无法确认提交或补偿失败时提示人工核对。瞬时快照由生产 Git 适配层完整保存本地凭据 helper 列表及空清空标记，避免补偿后意外继承其他账号；不覆盖最早的解绑持久快照，也不调整数据库 Schema。跨 Git 配置与 SQLite 的补偿不是分布式事务，无法承诺进程崩溃或外部 IDE 同时改配置时的原子性。
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

Project 表示：

> 本机已经存在的项目目录，可以尚未初始化 Git，也可以是有或没有 origin 的 Git Repository。

GitBinder 不复制 Repository 内容，普通目录登记/加载时不初始化 Git。`Project.IsLocalDirectory` 由 GitDir、origin、主机、分支均为空且协议为 Unknown 推导，不新增数据库列；已有仓库读取失败时保留缓存和仓库身份，不降级为普通目录。仅新登记的 Git 仓库尝试默认绑定；普通目录只有用户显式选择账号并完成初始化后才创建绑定与配置快照。

普通目录可分组、重新定位同一项目及移除，也可编辑仓库地址或选择账号；首次保存有效 HTTPS/SSH 地址或绑定时，界面确认后调用 `ProjectService.InitializeRepositoryAsync` 初始化当前目录自己的 Git，并同步 GitDir/origin/分支，保留原 ID、路径与分组。链接合法性校验先于初始化。仅查看或取消编辑/确认不写 Git；保留所有现有文件，不自动暂存、提交、联网或上传。未初始化时不展示已生效账号，测试/拉取仍禁用；Git 仓库无 origin 时允许绑定提交身份，绑定后跳过远程协议检查。

`IGitService.InitializeRepositoryAsync` 使用隔离 Git 环境和参数数组，对同目录既有 Git/工作树直接识别，对父级仓库下的普通目录只初始化自身；初始化后及写 origin 前必须确认根目录等于登记路径。失败保留磁盘内容并返回安全错误，不自动删除 `.git`。后续元数据、origin 及本地身份/认证配置读写统一清理仓库定位与环境配置注入变量，防止写入环境指定的其他仓库；普通元数据仍保留用户全局/系统配置读取语义，全局模式的全局配置动作不受影响。绑定界面先初始化再复用 `BindingService.BindAsync`，仍遵守全局账号优先和首次本地配置快照。保存新地址后按原绑定账号重应用配置；若失败，明确报告地址已保存但配置未同步，后续页面刷新失败也不能覆盖该提示。全局模式、账号重应用、后台测试与拉取不触发普通目录初始化。

### 克隆与快进拉取

- 新增 Application 端口 `IGitTransfer` 与用例服务 `ProjectTransferService`，由 Infrastructure 的 `GitTransfer` 通过 `ICommandExecutor` 执行参数数组；不引入平台 API、提交、推送或冲突解决器。
- 克隆先验证 HTTPS/SSH 地址与目标目录、按 `EffectiveAccountResolver.ResolveForClone` 选择认证账号；完成后登记时跳过默认绑定，直接绑定用户所选账号。全局模式控制实际认证，所选绑定保留供关闭全局模式后使用。
- 拉取先刷新本地元数据，拒绝未初始化 Git，并重新核对真实根目录，再检查进行中的 Git 操作及当前分支 remote/merge 跟踪设置，用当前有效账号执行 `pull --ff-only --no-rebase --no-recurse-submodules`；支持非 origin 跟踪远程，不因 origin 缓存为空拦截。分组拉取复用同一逻辑，逐项反馈并继续其他项目。允许存在本地改动及未跟踪文件，由 Git 判断更新是否会覆盖它们，真实覆盖冲突映射为 `PULL_DIRTY`。禁用自动 stash，不重置或强推，不递归拉取子模块。
- 两项跟踪配置均缺失时，已有提交的分支只尝试 origin 同名分支；无提交的仓库通过当前账号 `ls-remote --symref` 查询 origin 默认分支，不猜测 main/master。验证目标引用后拉取至当前本地分支，成功后才保存 branch.remote/merge；写配置失败明确提示代码已拉取。分离 HEAD、跟踪配置不完整、没有明确默认分支或非法引用时停止，不自动切换/重命名分支。
- HTTPS 重置 helper 与额外认证头，在解析后的精确 URL 作用域指定带账号 ID 的凭据助手；SSH 指定单个私钥，保持严格主机校验，仅允许 Agent 为该密钥签名。传输 `TransferOptions` 保留全局/系统 Git 配置以兼容代理、证书、safe.directory 和 insteadOf；仍清理继承的仓库定位与环境配置注入，禁止交互和其他协议认证回退。初始化继续使用完全隔离的 `BaseOptions`。不修改开发机全局环境或配置文件。
- `CommandExecutionOptions` 提供子进程环境与 30 分钟超时；启动前检查取消，取消/超时请求终止进程树后等待退出和输出收尾。清理等待失败用 `CommandResult.TerminationUnconfirmed` 及专用异常保留诊断，传输层优先于普通取消反馈；不能仅根据父进程已退出宣称清理完成。错误不回显原始 stderr，未确认终止时要求用户先确认 Git/SSH 已退出再重试。失败不自动删除部分克隆目录，登记和绑定失败分别反馈。数据库 Schema 不变。

### Remote 协议切换

项目页面先显示数据库中已保存的项目和分组，再经 `ProjectService.RefreshMetadataAsync` 读取本地 origin，更新缓存协议和主机，不联网、不改 Git 配置。元数据每批最多并行刷新 4 个项目，SQLite/Git/同步账号解析在后台执行，卡片回填及项目占用释放回到 UI 上下文；快速往返页面复用尚未完成的加载，完成后再次进入仍读取最新元数据。读取失败时保留缓存并提示，单行刷新异常不阻止其他仓库更新；未配置 origin 与读取失败分别使用空字符串与 null 表示，禁止将含 HTTP 用户信息的地址自动写入缓存。项目仓储使用显式列名保持映射稳定。

普通目录后续初始化 Git 时，页面刷新补齐 GitDir、分支和 origin 等元数据，保留原 ID、分组且不自动绑定；自动识别要求仓库根目录等于已登记目录，不会将普通目录自动替换为后来出现的父级仓库。登记或明确修改路径时，Git 项目仍解析真实根目录并按平台路径规则去重；目录不存在与已登记仓库失效分别反馈。

项目卡片支持通过 `ProjectService.UpdatePathAsync` 重新定位移动后的同一项目：要求绝对路径，普通项目允许选择现存普通目录或其初始化后的 Git 仓库，已有 Git 仓库则仍要求有效 Git 路径。校验并去重后一次性保存路径、GitDir、origin、协议、主机和分支，清除旧测试结果。保留原项目 ID、名称、分组、绑定与配置快照，不改写 Git 配置，不搬移或删除文件；用户保存前确认是同一项目，不能用于替换成其他仓库或新克隆副本。

项目“修改地址”编辑区可在标准 Remote 地址间切换：`https://host/group/repo.git` 转为 `git@host:group/repo.git`，反向切换则生成 HTTPS 地址。切换只修改编辑框内容，用户仍须点击“保存地址”才会执行本地 `git remote set-url`。

带 HTTPS 用户信息、查询参数、片段或非默认端口的地址不自动转换，避免错误猜测 SSH/HTTPS 服务端口；此类地址由用户手动维护。

#### 账号变更与协议确认

- `BindingService.GetProtocolChangeAsync` 只读检查实际 origin 与当前有效账号（全局 > 绑定 > 默认），仅在缺少当前协议凭据但已配置另一协议凭据时生成 `BindingProtocolChange`；双协议账号不强制转换。复用 `GitTransferRemote` 与 `GitRemoteUrlConverter`，不猜测平台、端口或仓库路径。
- 项目改绑仍选择即保存；之后由 `ProjectsViewModel` 展示协议变更确认，取消保留已保存的绑定和原地址。已有错配项目在测试前同样确认，取消则不连接。单项与分组拉取都不预先转换 origin，因为实际跟踪远程可能不是 origin；按真实目标检查账号凭据，不匹配时返回安全错误，不改写远程地址。全局模式变化不批量改写地址。
- 确认记录包含项目、实际路径、当前有效账号和原/目标地址。`ChangeProtocolAsync` 执行前重新检查，过期则拒绝；仅修改 origin（不改独立 pushurl），按新协议重新应用同一个有效账号，并同步项目元数据，不覆盖已有解绑快照。
- 失败后尝试恢复本次地址与同一有效账号的认证配置。回滚前先读取实际 origin，仅当它仍是本次目标地址才回写原地址；不覆盖 IDE 并发改出的第三个地址。不能确认实际地址或恢复失败时明确提示，认证禁止回退到其他账号。
- 协议预览、确认和回滚通过 `IGitService.GetTransferOriginUrlAsync/SetTransferOriginUrlAsync` 使用与 `GitTransfer.TransferOptions` 一致的网络配置上下文，保留本机 insteadOf 并清理继承的 GIT_DIR，避免预览与真实测试指向不同协议/仓库。克隆通过只读 `ls-remote --get-url` 检查重写后协议并固定凭据，实际克隆仍用原地址；测试使用远程名称，避免二次应用地址重写。

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

`new DomainError(code, value)` 中第二个参数始终作为可显示的格式参数；诊断信息须显式传入 `technicalDetails` 和 `arguments`，避免可变参数重载吞掉路径。`LocalizationService` 对单个字符串参数集合先展开再格式化，避免出现 `System.String[]`；占位符不匹配时返回资源模板而非再次抛错。`TechnicalDetails` 不参与界面格式化，异常原文不得通过格式参数回显；私钥导入失败使用固定安全提示。

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

应用启动仅读取已保存的账号、绑定及全局模式状态，不重新应用仓库或全局 Git 配置，也不自动修复外部工具改动的绑定身份。配置重应用由用户明确保存绑定、切换全局模式或编辑账号等操作触发；旧配置不会在启动时自动升级补写。

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

Windows 发布使用自包含的共享目录布局：Desktop 与 Credential Helper 隔离发布后按同名文件 SHA256 合并到 app，分别保留 EXE/DLL/deps/runtimeconfig，只保留一套随包运行时；不向用户增加 .NET 安装要求。禁用自有调试符号输出，合并及安装文件规则均排除 PDB，并清理 deps 中原生 PDB 条目；保持不裁剪、不 AOT。1.2.12 已完成生产发布及产物核对，未作启动/安装验收，基线与核验规则见 [Windows 打包说明](WINDOWS_PACKAGING.md)。

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
- 侧栏“未分组”按完整 Items 的归属统计，数量为 0 时隐藏，若此前选中则回到“全部项目”；项目卡片 GroupChoices 独立保留未分组选项，自定义空分组仍显示。搜索不改变分组可见性或统计。页面底部由 HasPageProgress 控制整块折叠，操作进度、传输状态及克隆弹窗状态均通知该派生属性；认证文字和拉取取消入口按原流程保留。
- ProjectTransferService 使用短时锁保护占用集合，不在锁内等待网络操作：单项拉取按项目 ID 去重，不同项目可并行；克隆保持单实例但可与单项拉取共存。PullGroupAsync 在应用层获取完整分组快照并保持批量独占，与单项拉取和克隆双向互斥，再顺序调用共用 PullCoreAsync。继续处理单项失败，取消后停止剩余项目，返回 GroupPullReport；所有占用在 finally 释放，进度不暴露凭据或原始命令错误。
- 所有拉取仍由 EffectiveAccountResolver 决策实际账号，全局模式优先；保持工作区保护及仅快进策略。未增加平台 API 枚举、推送或自动合并功能。

## 桌面结果通知与 Windows 安装图标

- 项目页的单项绑定、测试、解绑、地址/路径保存、分组归属、移除与拉取使用项目 ID 占用，直到目标项目刷新完成才释放。共享异步行命令允许跨项目并发，同项目仍互斥；每行独立展示忙碌、进度和可取消状态，单项任务不占用页面的批量进度与 CTS。
- 全量载入和操作后的目标刷新由同一门禁协调，保留既有 ProjectItemViewModel，跳过被其他操作占用的行；后台批量刷新保留每项目占用，直到所有 worker 收尾后才释放。目标刷新更新原行，不重建全部卡片，不清除其他行的地址/路径草稿。过滤后的条目引用及顺序相同时不替换 ItemsSource；仅页面忙碌状态真正变化时向全部行回填可用状态。账号及分组程序回填标记防止 SelectionChanged 再次发起写入。HasPendingOperations 聚合所有行占用与页面任务，不使用单个共享命令的 IsRunning 判断并发任务已全部结束。
- 项目卡片使用有界 `ProjectListBox` / `VirtualizingStackPanel`，仅创建可见区域及框架缓存范围内的控件。专用 ListBox 将 `NeedsContainerOverride` 的 recycleKey 设为 null，保留虚拟化而不跨行复用容器，避免旧 ComboBox 选项在 DataContext 切换过程中触发其他项目的账号/分组写入；项目 ViewModel 与编辑草稿仍由页面持有。
- 展示排序由 Application.Settings.ListOrderService 通过已有 ISettingsRepository 保存四组 GUID 数组（ui.order.accounts/platforms/projects/groups），无需 Schema 迁移；仅主列表/分组下拉框使用，不参与有效账号决策。加载时按 ID 合并历史排序，忽略已删除 ID，新条目依原查询顺序追加；损坏 JSON 回退查询顺序。SaveVisibleAsync 校验原可见顺序与全量子序、预览成员一致且无重复，将最终预览按原可见槽位投影到全量后单次 UPSERT；兼容 MoveAsync 复用该入口。
- Desktop.ListOrderPreview<T> 持有原始条目引用和最后预览顺序，拖动期间仅对 FilteredItems/Groups 执行 ObservableCollection.Move；松开通过 IListOrderHost 保存原快照和最终预览，成功后再同步 Items/分组下拉。取消或写入失败还原展示顺序；若集合已被外部筛选/加载替换则不恢复过期对象。会话检查源条目、加载/操作门禁、项目分组及搜索上下文。ListReorder 附加行为负责 500ms 长按、指针捕获、主色预览占位、边缘滚动及取消；命中在 Loaded 阶段等待布局后处理，并按实际鼠标/滚动位移采样，防止布局变动导致静止鼠标反复换位。松开只确认最后展示顺序；源卡片移动到鼠标下仍为有效落点。保留条目与列表实例，不新增 Git 查询或写入。
- `ProjectListBox.StyleKeyOverride` 必须返回 `typeof(ListBox)` 才能取得 Fluent 的列表主题及模板；只设置项目容器主题不能代替列表本身的主题。ViewLocator 按项目 ViewModel 弱引用缓存项目视图，切回页面时复用其控件树。用户明确触发的全局身份操作在后台执行，UI 保留门禁申请/释放和属性回填；操作后在门禁内只重读展示缓存，不再附带全仓库元数据扫描。启动只读历史记录，不执行身份重应用。
- 项目展示通过 `EffectiveAccountResolver.CaptureDisplaySnapshot` 复用本轮已读取账号及一次采集的全局状态，各行传入本次读取的绑定 ID，内部仍复用同一解析器规则；不逐行调用同步数据库适配器，快照不跨轮保留，实际认证/绑定/拉取继续使用实时解析入口。分组计数一次遍历累加，分组列表和下拉选项相同时保留原集合。账号列表条目不订阅与自身字段无关的语言事件，项目条目移除时解除其语言事件订阅。总览和账号页面的数据库读取在后台完成，UI 仅回填结果；各页面加载门禁继续防止旧请求覆盖新状态。
- `InitializeAsync` 只读取顶栏账号、通过 `LoadStoredAsync` 读取登记的项目与分组，再读取总览统计；不调用 `EnsureAppliedAsync`，不为启动取得身份写入外部门禁，不启动仓库 Git 扫描或配置重写。登记信息表示用户在软件中保存的状态，不保证与外部工具修改后的仓库配置实时一致。
- 项目名后的 RefreshConfigurationCommand 使用当前项目占用，后台读取元数据及 user.name/user.email，调用 BindingService.SynchronizeRecordedBindingAsync 将唯一匹配的启用账号同步到软件绑定记录，并只读重载目标行；不在卡片追加配置说明。匹配按姓名、邮箱和已知项目主机，零匹配/多匹配弹窗并保留记录；全局覆盖下只有已记录同一账号时可直接回填，否则提示而不反推原项目绑定。ProjectService.ReadCurrentIdentityAsync 核实真实根目录后调用 IGitService.GetCurrentIdentityAsync，两条 config --null --get 保留全局继承和缺失/空值，不读取原始凭据链。只同步软件记录、保留最早解绑快照，不调用 BindAsync 或任何 Git Apply/Restore，不执行远程认证；匹配不是 SSH/HTTPS 认证账号的验证。
- 绑定账号下拉框的 SelectedItem 显式使用 OneWay 展示，用户选择由 SelectionChanged 进入既有改绑用例；控件更换 ItemsSource 时产生的临时空值不能写回 VM。快照回填先通知 AvailableAccounts，再按账号 ID 从新候选列表选择同一实例并通知 SelectedBindingAccount，最后更新其他派生展示。失败/拒绝/进行中的选择恢复均强制通知，避免单向绑定下 VM 值相同但控件仍停留在错误选项。已绑定的停用账号仍纳入该项目的展示选项，其他停用账号不加入可选列表，实际账号可用性及有效账号回退仍由原服务规则决定。
- 分组结构修改、分组拉取和全局身份重应用保留跨项目冲突保护；全局模式入口与项目操作共享页面门禁，避免操作中切换有效账号。搜索、选择分组、导航及打开已登记目录不受单项忙碌状态影响；Escape 可取消其他空闲行的编辑，但不能关闭仍有后台操作的主窗口。
- 账号保存/设默认/删除通过相同外部门禁，覆盖持久化、显式身份同步及项目刷新；不能在项目传输中替换生效账号的凭据。账号列表刷新后发出只读通知，主窗口串行重读顶栏账号，不再次应用全局配置；禁止删除当前启用的全局账号。启动不参与身份重应用。
- ProjectService 的登记和路径更新在最终路径去重与写入之间共用短时异步门禁。耗时元数据读取位于锁外，锁内重新核对目标路径和原记录，避免两个项目同时登记同一路径；该门禁不串行化不同项目拉取。

- ViewModelBase.SetNotice 统一发布操作结果，经 DialogHelper 的 UI 调度与串行队列显示 MessageDialog；不持久化消息，不改变业务错误码。账号编辑通知关联对应编辑窗口，应用退出期间的窗口异常不会向外传播。
- 项目 OperationProgress 与结果 Feedback 分离；批量汇总包含逐项结果，传输后的列表刷新错误合并到同一结果。MessageDialog 与 ConfirmationDialog 使用 SelectableTextBlock + ScrollViewer，提供原生文本选择及 Ctrl+C；不使用只读 TextBox、复制全部按钮或额外剪贴板写入逻辑。通知队列、消息来源及错误脱敏规则不变。
- Desktop 工程通过 ApplicationIcon 嵌入 Windows 原生图标；Inno Setup 的 UninstallDisplayIcon 显式指向安装目录的 gitbinder.ico。托盘、快捷方式和安装项复用已有图标，不变更 AppId 或用户数据路径。
- 发布脚本逐项检查 dotnet publish 退出码，避免主程序失败后继续生成不完整安装包；清理仅限已校验的仓库内 build/publish 临时产物。
- 资源优化：账号/平台使用有界虚拟化 ListBox，按 ID 复用行并只通知变化字段；Main 导航合并进行中的相同页读取，首次总览读取统一由启动入口负责，显式写入后刷新保持独立。拖动预览改为事件驱动，仅边缘可滚动时保留 30ms 定时器；源卡片从已实现容器定位。PATH 成功定位缓存最多 5 秒且每次复核命中文件，GitDir 用一次 --absolute-git-dir 获取；Release 不启用框架 Trace。发布生成包外 payload-manifest.json；当前未测优化后内存/功耗，具体约束见 [性能说明](PERFORMANCE.md)。

## SSH 连接配置兼容与诊断

- `GitSshExecutableResolver` 在 Windows 根据本次定位的绝对 `git.exe` 路径识别 `cmd`、`bin`、`mingw64/bin`、`mingw32/bin` 安装布局，优先选择同安装目录下存在的 `usr/bin/ssh.exe`；不从仓库或任意父目录搜索。缺少配套程序时回退既有 `SshLocator`，非 Windows 保持原选择。测试、克隆、拉取传递同一 Git 路径，避免 IDE 的 Git 配套 SSH 与软件的 Windows OpenSSH 混用。
- SshConnectionConfig 用同一定位后的 SSH 程序执行 `ssh -G` 解析本机配置，按允许列表复制目标主机连接设置，再通过 EmptySshConfig 创建真实的只读配置文件供 `-F` 使用；不依赖不同 SSH 实现对空设备的解释。保留 HostName、用户、端口、ProxyCommand/ProxyJump、主机密钥文件与连接算法，不复制目标主机其他 IdentityFile、CertificateFile 或不安全的主机校验选项。
- 单值连接参数在包含空格、引号、反斜线或注释符时按 SSH 配置词法引用和转义；ProxyCommand 保留命令尾部语法，known_hosts 列表不整体引用。已知限制：`ssh -G` 的 knownhosts 多路径输出会丢失含空格路径的原始边界，本版不猜测拆分、不回退默认文件或降低严格主机校验；因此此类配置不属于已验证兼容范围。
- 目标 Host 之后用负匹配的 Host 条件 Include 用户/系统配置，仅供 ProxyJump 子进程读取跳板自身设置，目标仓库不叠加这些配置中的其他私钥。SSH 解析失败时在连接前停止并显示固定错误码，不静默改用空配置或其他账号。
- 文件用 CreateNew 唯一创建，写入后关闭写句柄，以只读句柄持有并允许共享读取；非 Windows 创建权限为 0600。不使用 DeleteOnClose，以兼容 Windows OpenSSH。结束或失败后 Dispose 清理本实例临时文件；异常退出可能残留连接配置，不能上传其原文，代理命令可能包含用户自己的敏感设置。
- 保持指定私钥、IdentitiesOnly、非交互认证、严格主机校验及仅快进更新，不读取其他账号配置作为认证回退。
- `GitTransferFailure` 在通用 publickey 认证错误之前识别明确的本地私钥权限、格式、口令和 Agent 拒绝签名信息；检查对应密钥/签名上下文并排除 `remote:` 服务端行，防止把远程文件权限误当私钥权限。错误只返回固定双语文案、阶段和退出码，不回显原始 stderr、路径或凭据，不自动修复用户 ACL。
- GitTransferFailure 将 stderr 归类为固定的安全错误码，附带失败命令阶段和退出码，由中英文资源解释；涵盖空配置读取、主机密钥、认证、DNS、连接、超时、证书、目录所有权、快进分叉、锁和本地文件错误。未知诊断不回显原始文本，避免服务端输出或 URL 中夹带凭据。
- Git 网络配置兼容不等于复用 IDEA 的密码库或交互会话；仍需提供当前账号的 HTTPS 凭据，或提前由 Agent 解锁同一 SSH 私钥。不自动信任未知主机、关闭证书校验、信任所有目录或执行真实项目操作来验证。

## 项目路径打开

- ProjectsViewModel 的 OpenDirectoryCommand 校验已登记路径为存在的绝对目录后调用既有 IShellService；普通目录同样可打开，不初始化 Git、不修改路径。路径缺失与文件管理器启动失败使用双语安全提示。
- 路径前的文件夹图标使用可键盘聚焦的按钮，提供悬停提示与无障碍名称。WindowsShellService 以参数数组把完整目录传给 explorer.exe，含空格、尾部分隔符等路径不手工拼接引号；macOS/Linux 复用既有系统服务。
