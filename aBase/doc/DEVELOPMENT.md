# GitBand 开发与构建

[返回项目首页](../../README.md) · [使用指南](USER_GUIDE.md)

普通用户可直接从 [Releases](https://github.com/thaintus/GitBand/releases) 选择适用附件。本页面向需要修改源码、运行开发版或自行打包的开发者。

## 开发环境

- Windows、.NET 10 SDK、系统 Git；SSH 功能还需 OpenSSH。
- 生成 Windows 安装包需要 Inno Setup 6，其 `Languages` 目录需包含 `ChineseSimplified.isl`。
- 首次还原依赖需要能访问 NuGet。
- macOS / Linux 目前属于实验性平台，系统依赖和打包方式见下方专门文档。

项目使用 C#、Avalonia、CommunityToolkit.Mvvm、SQLite，以及系统 Git / OpenSSH。

## 获取源码与检查

```powershell
git clone https://github.com/thaintus/GitBand.git
cd GitBand

dotnet build GitBinder.slnx -c Debug
dotnet test tests/GitBinder.Tests/GitBinder.Tests.csproj -c Debug --no-build
```

也可使用 `.\build\build.ps1`：默认执行依赖还原、构建和单元测试，`-SkipTests` 可跳过测试，`-Package` 会追加安装包构建。隔离 UI 验证另见 [UiSmoke 说明](../../tests/GitBinder.UiSmoke/README.md)，不由上述单元测试命令代替。

以上是供开发者按需执行的命令；文档中的命令不表示当前变更已经通过这些检查。

## 本地运行

完整的 HTTPS 认证需要桌面程序与凭据助手一起发布。仅对 Desktop 执行 `dotnet run` 不会准备完整的凭据组件。

在 Windows PowerShell 中：

```powershell
# 使用脚本当前默认版本，同时发布桌面端与凭据助手
.\build\publish.ps1 -Runtime win-x64

# 运行完整发布目录中的桌面程序
.\build\publish\app\GitBinder.Desktop.exe
```

脚本会重新生成 `build/publish/` 临时产物，最终程序位于 `build/publish/app/`。Desktop 与 Credential Helper 各有入口和运行配置，共享自包含 .NET 及其他依赖；分发时需保留整个目录，不能只复制 EXE。

> [!CAUTION]
> 开发版与安装版默认共用当前用户的数据目录。启动只恢复登记信息，但保存绑定、账号同步和全局模式切换会写 Git 配置。真实 Git 验证应使用专用测试用户和临时仓库，并隔离 `GIT_CONFIG_GLOBAL`，不要改写开发机真实全局配置。

## 生成安装包

```powershell
# 发布 Desktop 与 Credential Helper，再生成 Windows x64 安装包
.\build\build-installer.ps1 -Runtime win-x64
```

输出为 `dist/GitBinder-<版本号>-setup.exe`。该入口执行发布编译和打包，**不执行单元测试**。脚本使用当前默认版本；发行新版本时应同步项目版本，通过 `-Version <x.y.z>` 指定新版本号，避免覆盖同名历史产物。

发布目录共享运行时并排除调试符号；这是包体积优化，不代表已经验证内存或功耗下降。详细发布规则、文件核对和现有验证边界见：

| 文档 | 内容 |
| --- | --- |
| [Windows 打包](WINDOWS_PACKAGING.md) | 发布目录、依赖合并、安装器与产物核验 |
| [macOS 打包](MACOS_PACKAGING.md) | 双架构实验包、Mac 本机签名、公证与验收要求 |
| [Linux 打包](LINUX_PACKAGING.md) | 双架构 deb / 便携包、桌面依赖与密钥环 |
| [性能与资源使用](PERFORMANCE.md) | 当前优化实现、测量记录与尚未完成的验收 |

macOS / Linux 分别使用 `build/build-macos.ps1` 与 `build/build-linux.ps1`，具体参数参见对应说明。跨平台发布成功不等于已在目标系统完成安装、图形界面或真实认证验证；各平台公开版本以 Release 附件为准。

## 项目结构

```text
GitBinder.slnx
├── src/
│   ├── GitBinder.Desktop/           # 界面、ViewModel、本地化与组合根
│   ├── GitBinder.Application/       # 用例编排与端口接口
│   ├── GitBinder.Domain/            # 实体、领域规则与有效账号决策
│   ├── GitBinder.Infrastructure/    # Git CLI、SQLite 等外部实现
│   ├── GitBinder.Platform/          # 系统机密存储、路径和私钥导入
│   └── GitBinder.CredentialHelper/  # Git 凭据助手
├── tests/
│   ├── GitBinder.Tests/            # 单元测试及隔离 Git 测试
│   └── GitBinder.UiSmoke/          # 隔离 UI 验证宿主
├── build/                         # 构建与各平台打包脚本
├── aBase/                         # 使用、设计、迭代与 Schema 资料
└── LICENSE                        # Apache License 2.0
```

依赖方向为 `Desktop → Application → Domain`。Infrastructure 与 Platform 实现抽象端口，通过依赖注入组装；Domain 不依赖 UI、数据库或平台 API。

### 移动源码目录

项目引用与构建脚本使用相对路径，移动源码后从新位置重新打开解决方案即可。IDE 历史记录和 `bin/obj` 缓存可能保留旧路径，不要手动改写生成文件；需要时在新位置重新还原与构建。

用户数据不会随源码移动。已登记仓库、私钥和写入 Git 配置的凭据助手需要稳定路径；如果也移动了这些目录，请通过应用或对应配置重新定位，不要批量替换数据库或 Git 配置。

## 参与贡献

欢迎通过 [Issues](https://github.com/thaintus/GitBand/issues) 讨论问题或提交 Pull Request。较大功能先说明使用场景，保持工具聚焦于身份与凭据管理。

- 保持职责分层，参照现有用例服务和接口，不在 ViewModel 中直接执行 Git 或访问数据库。
- 用户可见文案同时维护中文和英文；为行为变更补充有意义的相关验证。
- 按 [aBase 维护规则](../README.md)更新迭代记录；涉及设计或 Schema 时同步对应资料。
- 在 PR 中注明实际执行过的检查与测试，不把静态检查、单元测试或打包成功等同于真实认证和实机验收。
- 不提交真实账号、私钥、凭据、个人 Git 配置、`bin/`、`obj/`、`build/publish/` 等生成产物；安装包通过 Release 附件分发。

## 进一步阅读

[设计索引](设计文档索引.md) · [产品设计](GitBind设计文档.md) · [技术架构](TECH_ARCHITECTURE.md) · [界面规范](UI_VISUAL_GUIDELINES.md) · [数据库资料](../sql/README.md) · [迭代记录](../iteration/README.md)

设计文档可能包含未落地的方案，当前行为应结合代码与最新迭代记录判断。
