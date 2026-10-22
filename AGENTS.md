# GitBinder 协作规则

## 1. 项目范围与基本原则

- GitBinder 是以 Windows 为正式发行目标、提供 macOS / Linux 实验包的本地 Git 身份与仓库账号管理工具，不是 Git 图形客户端；不要扩展提交、分支、合并等 Git GUI 功能。跨平台打包不代表已完成目标系统实机验收。
- 技术栈为 .NET 10、Avalonia、CommunityToolkit.Mvvm、SQLite（Microsoft.Data.Sqlite）和系统 Git/OpenSSH。
- 默认使用中文沟通与代码注释；用户明确要求英文时例外。
- 先从最具体的类、页面、错误码或目录开始查找，只读取完成当前判断所需的上下文。
- 工作区可能含有用户未提交的改动。只修改任务相关文件，不还原、覆盖或清理无关内容。
- 每次完成需求、功能调整或缺陷修复后，必须按 `aBase/README.md` 的规则同步更新对应的 `aBase/iteration/` 迭代文档；涉及设计、Schema 或第三方资料变更时，再同步维护 `aBase/doc/`、`aBase/sql/` 中的对应资料。
- 禁止 `git push`；未经用户明确授权，不执行重置、递归删除、数据库重建或删除用户数据。

## 2. 目录与产物规则

| 路径 | 用途 | 修改规则 |
| --- | --- | --- |
| `src/GitBinder.Domain/` | 实体、枚举、领域规则、`EffectiveAccountResolver`、`Result`/`DomainError` | 保持纯领域层；不得依赖 UI、SQLite、Git CLI 或 Avalonia。 |
| `src/GitBinder.Application/` | 用例服务及端口接口（账号、项目、绑定、全局模式） | 只依赖 Domain 和抽象接口；外部能力通过接口注入。 |
| `src/GitBinder.Infrastructure/` | SQLite 仓储、Git CLI、命令执行、日志 | 实现 Application 端口；数据库映射显式列顺序，避免依赖 SQLite 物理列顺序。 |
| `src/GitBinder.Platform/` | Windows DPAPI、路径、Shell 等平台适配 | 平台相关代码集中在此，不向 Domain/Application 泄漏 Windows API。 |
| `src/GitBinder.Desktop/` | Avalonia View、ViewModel、样式、本地化、组合根 | 保持 MVVM；View 不直接访问仓储或执行 Git 命令。DI 注册放在 `CompositionRoot.cs`。 |
| `src/GitBinder.CredentialHelper/` | `gitbinder-credential` 可执行程序 | 只处理 Git Credential Helper 协议；发布时必须与桌面程序一同输出。 |
| `tests/GitBinder.Tests/` | xUnit 测试与内存 Fake | 优先复用 `Fakes/FakeRepositories.cs`；涉及 Git 全局配置的测试必须隔离到临时配置文件。 |
| `build/` | 构建、发布、Inno Setup 打包脚本 | 脚本和 `installer.iss` 是源文件；`build/publish/` 是临时发布产物，不手工编辑。 |
| `dist/` | 版本化安装包 | 仅由打包脚本生成；不覆盖或删除历史安装包，除非用户明确指定。 |
| `aBase/doc/`、`aBase/iteration/`、`aBase/sql/` | 设计资料、迭代记录、参考 SQL | 作为参考资料；运行时 Schema 以代码和迁移逻辑为准。 |
| `aBase/trash/` | 逻辑回收站说明 | 不把它当作可随意清空的临时目录。 |
| `bin/`、`obj/`、`.idea/` | 本地生成或 IDE 状态 | 不手工编辑，不纳入功能修改。 |

### aBase 文档维护规则

- `aBase/` 是项目长期维护资料的统一目录。处理需求分析、方案设计、功能开发、代码修改、数据库调整或问题排查前，应先查阅与任务相关的资料，不能只根据代码推测历史约定。
- `aBase/doc/` 用于原始需求、产品/技术设计、第三方 API/SDK/协议、测试方案等长期参考资料；不记录过程性的代码迭代。需求或设计资料有变化时同步维护该目录。
- `aBase/iteration/` 用于记录实际开发、调整和演进过程。每次完成需求、功能调整或缺陷修复后，都必须在对应模块文档追加本次背景、变更内容、实现方案、涉及文件、接口/数据库变更及注意事项；不存在对应文档时新建。
- `aBase/sql/` 用于运行时 Schema 参考和数据库变更 SQL。新增或调整表、字段、索引时，必须同步维护表结构与必要的增量 SQL，并先核对历史 SQL。
- `aBase/trash/` 是逻辑回收站；不再需要但暂不物理删除的文件应移入此目录，不能将其视作可随意清空的临时目录。
- 文档与代码冲突时，结合最新代码和迭代记录判断，并在需要时同步修正文档。目录选择遵循：开发依据看 `doc`，已做与本次变更记 `iteration`，Schema/数据变更查并改 `sql`。

## 3. 分层与实现约束

- 依赖方向固定为 `Desktop → Application → Domain`；`Infrastructure`、`Platform` 通过 DI 提供实现。禁止 Domain 反向引用上层。
- 新建功能时，先确认是否已有服务、仓储接口和错误码；避免在 ViewModel 中复制业务规则。
- 新增或调整服务依赖时，同步检查 `Application/DependencyInjection.cs`、`Infrastructure/DependencyInjection.cs` 与 `Desktop/CompositionRoot.cs`。
- 外部进程只能通过 `ICommandExecutor` 使用参数数组调用；禁止拼接 Shell 命令字符串，禁止在日志或异常中暴露 Token、密码、私钥内容。
- 新增可本地化的 UI 文案时，同时维护 `StringsZhCn.cs` 与 `StringsEnUs.cs`；不要把用户可见文案硬编码在 ViewModel 中。
- 新增页面或移除页面时，同步维护导航、ViewModel DI、View 定位、所有引用及本地化键，不能留下不可访问的页面功能。

## 4. 账号、项目、绑定与全局模式规则

- `EffectiveAccountResolver` 是唯一的有效账号决策入口，优先级固定：**Global Mode > Project Binding > Default Account**。任何 Git 身份选择不得自行绕过该规则。
- 项目与账号绑定在“项目”页面的项目卡片内完成：选择账号、保存绑定/改绑、测试、解绑均在项目中操作；不新增独立“绑定”导航页。
- 普通本地目录可登记、分组、修改路径和移除，登记/加载不初始化 Git。用户显式保存有效仓库地址或选择绑定账号时，确认后可初始化该目录自身的 Git 仓库，保留现有文件，不提交、拉取或上传；不能接管父级仓库。已是 Git 仓库但未配置 origin 时允许绑定提交身份，远程测试与拉取须先配置地址。
- 新登记的 Git 仓库可自动绑定默认账号；普通目录后来初始化为 Git 仓库时只刷新元数据，不自动绑定。改绑在常规模式下必须立即把新选账号应用到该仓库本地 Git 配置。全局模式开启时，实际应用身份仍以全局账号为准。
- 移除项目仅移除 GitBinder 的项目、绑定与快照记录，绝不删除本地源码或 Git 仓库。
- 修改本地路径仅用于重新定位同一项目，保留 ID、分组、绑定与快照，不搬移文件；普通目录可仍为普通目录，已有 Git 仓库不能改到未初始化 Git 的目录。
- 项目的 Remote 元数据在登记、进入项目页或通过“修改地址”保存时同步更新；元数据读取不联网，“测试”才会实际验证当前生效账号的远程认证。
- 协议展示统一使用大写：`SSH`、`HTTPS`。
- 全局模式必须通过 `git config --global` 作用于未登记仓库，同时对已绑定仓库处理本地配置覆盖；启用前保存用户原全局 `user.name`、`user.email`、`core.sshCommand` 快照，关闭后恢复。不得把 Secret 写入 Git 配置。

## 5. 数据与安全规则

- 用户数据库默认位于 `%LOCALAPPDATA%\GitBinder\gitbinder.db`。已有用户库兼容优先，修改表结构必须在 `DatabaseInitializer` 增加可重复执行的迁移，并保留旧数据。
- SQLite 查询使用明确列名和稳定映射，不使用 `SELECT *`；新增列时考虑旧库物理列顺序和默认值。
- HTTPS 凭据、SSH 私钥口令等机密只通过 Secret Store 保存；Windows 使用 CurrentUser DPAPI，macOS 使用登录钥匙串，不允许明文回退或把秘密放进进程参数。macOS 数据目录为 `~/Library/Application Support/GitBinder/`，桌面端与 Credential Helper 必须共享同一平台路径工厂；导入私钥权限为 `0600`、目录为 `0700`。数据库、日志、测试输出、截图和提交中不得出现明文机密。
- Linux 使用 libsecret / Secret Service 和已解锁的用户会话密钥环；缺库、会话服务不可用、密钥环锁定时不得退回明文或其他账号。数据根遵守绝对 `XDG_DATA_HOME`，否则使用 `~/.local/share/GitBinder/`。桌面端与 Helper 共用平台路径，导入私钥权限同样为 `0600`/`0700`；不修改用户密钥环/桌面会话配置来完成构建。
- 绑定首次应用本地 Git 身份前保留 `RepositorySnapshot`；解绑时恢复该快照后再删除绑定/快照记录。
- 除用户明确要求外，不清除 `%LOCALAPPDATA%\GitBinder`、密钥目录、SQLite 文件或安装目录；任何数据清理都要先核实精确路径并说明可恢复性。

## 6. UI 与交互规则

- 左侧导航使用深色高对比度配色，文字必须可读；新增主题样式优先放入 `Styles/Theme.axaml`，不要用分散的控件级颜色破坏主题一致性。
- 从左侧页签进入数据页面时必须加载并显示当前数据；不能因懒加载异常而静默显示空列表。
- 列表数据应支持符合业务边界的新增、编辑、删除；页面级“新建/添加”按钮放在标题区域右侧并保持一致。
- 异步页面加载、弹窗打开、外部 Git 操作失败时应转为可读反馈，不能让未处理异常导致桌面程序退出。
- 展示账号、路径、远程地址时避免泄漏 HTTPS 密码、Token、私钥内容；只展示必要的别名、主机和文件名。

## 7. 验证、构建与交付

- 代码修改后默认只做静态校验：检查任务相关引用、DI 注册、本地化键、XAML 绑定、迁移逻辑和 diff。**除非用户明确要求，不主动执行 `dotnet build`、`dotnet test`、发布或安装包构建。**
- 用户明确要求测试时，优先执行最小相关测试；需要全量验证时使用 `dotnet test GitBinder.slnx -c Debug`。测试结论必须如实说明范围和结果。
- 涉及真实 Git 的测试必须使用临时仓库；涉及 `git config --global` 必须设置隔离的 `GIT_CONFIG_GLOBAL`，绝不能改写开发机真实 Git 全局配置。
- 用户明确要求打包时，使用 `build/build-installer.ps1 -Version <x.y.z>`；该流程会发布桌面程序和 Credential Helper，再生成 `dist/GitBinder-<x.y.z>-setup.exe`。
- 用户明确要求 macOS 打包时使用 `build/build-macos.ps1 -Version <x.y.z>`；同时生成 `osx-arm64` / `osx-x64` 实验 `.app.zip`，独立暂存于 `artifacts/macos/`，不清理 Windows 发布目录或覆盖历史包。macOS 本机签名、公证与运行验收须单独说明，不得以跨平台发布成功代替。
- 用户明确要求 Linux 打包时使用 `build/build-linux.ps1 -Version <x.y.z>`；生成 `linux-x64` / `linux-arm64` 的 `.deb` 与便携 `.tar.gz`，独立暂存于 `artifacts/linux/`。静态检查包结构和 ELF 架构不等于安装、GUI、密钥环或真实认证验证；不主动安装系统依赖、覆盖安装或启动生产应用。Linux 默认不关闭到托盘，防止无托盘宿主的桌面隐藏窗口后无法找回。
- 打包后核对安装包存在、文件版本和哈希；不自动覆盖安装、启动安装程序或修改用户的现有 Git 配置，除非用户明确授权。
- 若创建本地提交，提交前检查变更范围；可以提交但禁止推送远端。

## 8. 汇报要求

- 先给出结论，再列出改动位置、静态检查或测试结果、以及尚未验证的边界。
- 不把静态检查称为编译、测试或真实 Git/远程验证。
- 对需要用户安装、覆盖、删除数据或选择账号的步骤，明确说明影响与下一步，不擅自执行。
