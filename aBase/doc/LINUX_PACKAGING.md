# Linux 实验包：打包、安装与验收

## 当前定位

Linux 提供 x64 / ARM64 的 `.deb` 和便携 `.tar.gz` 实验包，版本与 Windows/macOS 一致。当前由 Windows 跨平台发布，包结构与原生架构校验不等于 Linux 安装、桌面启动、密钥环或 Git 操作验收。具体批次及实际检查结果见 [迭代记录](../iteration/V1-首版开发.md)。

目标环境为 Ubuntu 22.04 / 24.04、Debian 12 及满足依赖的 glibc 桌面发行版；不是 Alpine/musl、32 位系统或无图形会话的服务器包。ARM64 包需要 ARM64 系统，不能在普通 x64 电脑直接运行。其他发行版须单独验收。

## 生成包

开发环境需要 PowerShell 7.4 或更新版本、.NET 10 SDK 和 NuGet 网络访问。脚本通过自身位置定位仓库，不依赖工作目录或固定盘符。

```powershell
# 默认生成两种架构；不会运行测试
pwsh -File ./build/build-linux.ps1 -Version 1.2.0

# 只生成一个架构
pwsh -File ./build/build-linux.ps1 -Version 1.2.0 -Runtime linux-x64
```

产物输出到 `dist/`，每个包旁附 `.sha256`：

| 系统架构 | Debian/Ubuntu 安装包 | 便携包 |
| --- | --- | --- |
| x64 / amd64 | `gitbinder_1.2.0_amd64.deb` | `GitBinder-1.2.0-linux-x64.tar.gz` |
| ARM64 / arm64 | `gitbinder_1.2.0_arm64.deb` | `GitBinder-1.2.0-linux-arm64.tar.gz` |

脚本为每次打包创建独立的 `artifacts/linux/<批次>/`，分别发布 Desktop 和 Credential Helper，再合并到同一目录；同名文件必须哈希一致。使用自包含、非单文件、不裁剪发布，包含 .NET 运行时。包内显式记录 root 所有权、目录/ELF 可执行文件 `0755` 与普通文件 `0644`，并检查关键 ELF 的 CPU 架构。`.deb` 是包含 `debian-binary`、`control.tar.gz`、`data.tar.gz` 的标准 ar 归档，无安装/卸载脚本。

遇到同名历史包或校验文件时拒绝覆盖；不清理 Windows/macOS 的发布目录与产物，也不修改用户数据库或 Git 配置。安装包继续作为本地产物，不纳入 Git 提交。

## 安装与运行依赖

以下安装命令由用户在目标 Linux 桌面执行；生成包不会执行这些命令：

```bash
# 按电脑架构选择对应文件
sudo apt install ./gitbinder_1.2.0_amd64.deb
# ARM64 系统改为 ./gitbinder_1.2.0_arm64.deb
```

程序安装到 `/opt/gitbinder/`，桌面启动项位于 `/usr/share/applications/gitbinder.desktop`，图标位于 hicolor 的 256×256 应用目录。请以普通桌面登录用户启动，不要 `sudo` 启动应用。安装包自身未签名，应核对发布来源和 SHA256；校验文件只能证明文件一致性，不能独立证明来源可信。

便携包需完整解压到新的固定目录并保留权限，然后运行 `./GitBinder.Desktop`。`gitbinder-credential` 必须与主程序同目录。配置绑定后 Git 会引用 Helper 的绝对路径，因此不要随意移动或删除程序目录；它不同于可相对定位的源码目录。

自包含不等于零系统依赖：

- 图形界面依赖 X11；Wayland 会话需要 XWayland，以及 X11、ICE、SM、Fontconfig 等原生库。建议安装中文字体 `fonts-noto-cjk`。
- .NET 仍依赖 glibc、libgcc、libstdc++、Kerberos、ICU、OpenSSL 3、Brotli、zlib、时区和 CA 证书。具体包名见 `build/linux/control.template`；ICU/OpenSSL 包名随发行版变化，不要跨发行版强装依赖。
- 需要系统 Git / OpenSSH；打开目录需要 `xdg-utils`。打开终端当前仅支持 `/usr/bin/gnome-terminal`，缺少时明确报错，不会安装软件或猜测其他终端参数。
- HTTPS 凭据存储需要 `libsecret-1-0`、GLib/GIO/GObject、当前用户会话 D-Bus 及运行中的 Secret Service。GNOME Keyring 可提供服务；KeePassXC 等替代方案需启用 Secret Service 集成，仅安装库不够。

完整依赖以包的 control 为准。`.deb` 的 apt 安装会解析系统包依赖，但不保证密钥环会话已正确启动/解锁；便携包也不会自动补依赖或注册菜单。

## 数据、认证与托盘

Desktop 与 Helper 共用 `PlatformPaths`。设置有效绝对路径 `XDG_DATA_HOME` 时使用 `$XDG_DATA_HOME/GitBinder/`，否则使用 `~/.local/share/GitBinder/`，其中包含数据库、日志和私钥副本。二者必须在同一用户、相同 XDG 与会话环境下运行；不要直接复制 Windows DPAPI 或 macOS 钥匙串数据当作凭据迁移。

HTTPS 凭据经 libsecret 原生 API 存入 Secret Service，数据库只保存引用；不通过 shell/进程参数传入秘密、不落明文备用文件。保存可能要求创建/解锁密钥环并授权，删除可能出现系统确认。后台助手读取不使用 `UNLOCK`，锁定或服务不可用时失败，不尝试其他账号。调用在线程池执行并带取消与 30 秒超时。Secret Service 依赖当前用户会话，不应描述为对同一登录用户下其他应用的强隔离。

SSH 私钥是导入的文件副本，不是密钥环密文；导入目录为 `0700`、创建时文件为 `0600`，拒绝 keys 目录符号链接。Linux 路径区分大小写，已存在路径通过 `realpath` 解析链接。SSH 仍需用户在可信终端核实首次主机指纹，需要口令的私钥须由 Agent 解锁。

Linux 默认关闭窗口即退出；Windows/macOS 默认行为不变。确认桌面有可见托盘入口后，可手动开启关闭到托盘。Avalonia 的图标可见设置并不能证明桌面存在托盘宿主，因此没有按该属性自动开启驻留。

卸载或移动程序前，先关闭全局模式并解除相关项目绑定，恢复 Git 配置后再退出。卸载只处理软件包文件，不自动恢复仓库配置、不删除个人数据或密钥环条目。

## 目标系统验收清单（尚待执行）

- 分别在 x64、ARM64 Linux 桌面安装、启动、升级和卸载，确认菜单图标、中文字体、文件/目录选择器及窗口交互。
- 使用独立测试用户和临时仓库验证 XDG 路径一致、数据库持久化、目录/文件权限、大小写与符号链接处理。
- 在目标 Secret Service 实现验证保存/读取/删除、授权取消、锁定、缺服务、超时和 Helper 后台无解锁提示；不使用真实生产账号做自动验证。
- 验证 HTTPS/SSH 测试、克隆、快进拉取、绑定/解绑与全局模式恢复；确保失败时不退回其他账号。
- 检查不同桌面托盘、退出行为与终端打开；测试保留数据卸载和重新安装。

不要将包结构检查、发布成功或其他平台的测试记录替代以上验收。

## 官方参考

- [Avalonia Linux 部署](https://docs.avaloniaui.net/docs/deployment/linux)
- [.NET Ubuntu 原生依赖](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install)、[Debian 原生依赖](https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian)
- [Debian 包格式](https://manpages.debian.org/bookworm/dpkg-dev/deb.5.en.html)
- [libsecret 查找接口](https://gnome.pages.gitlab.gnome.org/libsecret/method.Service.search_sync.html)、[保存接口](https://gnome.pages.gitlab.gnome.org/libsecret/method.Service.store_sync.html)、[删除接口](https://gnome.pages.gitlab.gnome.org/libsecret/method.Item.delete_sync.html)
