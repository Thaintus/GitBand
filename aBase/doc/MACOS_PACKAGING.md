# macOS 实验包：适配、打包与验收

## 范围与交付边界

GitBinder 的 Windows 安装包仍为正式发行目标。macOS 首次适配提供 `osx-arm64`（Apple Silicon）与 `osx-x64`（Intel）两份自包含应用包，最低系统版本为 macOS 14。Linux 不在本次范围内。

在 Windows 上跨平台发布成功，只证明该目标的编译、依赖收集与应用包生成成功，不证明 Mac 实机启动、钥匙串授权、认证或窗口行为正确。未经 Apple Developer ID 签名、公证与实机验收的产物必须标为实验包，不应作为已验证的正式 Mac 版本发布。

## 平台边界

- `PlatformPaths.CreateApplicationDataPath()` 为桌面端及 Credential Helper 提供相同的数据根。Windows 保留原目录；Mac 使用 `~/Library/Application Support/GitBinder/`，不迁移或读取 Windows 数据。
- macOS 登录钥匙串存储 HTTPS 等机密，数据库仅保存引用。通过 Security / CoreFoundation 原生接口访问，不执行携带秘密的 `security -w` 命令，不降级为明文文件。
- 创建钥匙串项时，仅信任同一应用包内的桌面程序与凭据助手；其他应用不被无条件授信。钥匙串锁定、用户取消授权、签名变化等仍可能导致访问失败。凭据助手非交互读取遇到需授权的情况直接失败，不偷偷回退系统账号。
- 私钥仍是用户导入文件的副本：平台导入器在创建时设 `0600`、目录设 `0700`，禁止把已有 `keys` 符号链接作为目标；导入失败只清理由本次导入新建的不完整文件，不碰源私钥。
- 路径正规化保留大小写，存在的路径用系统 `realpath` 解析符号链接，避免把大小写敏感卷上的不同项目合并。
- Finder 启动的 PATH 与终端可能不同。Git / SSH 检测兼容无 `.exe` 文件名，以及 `/opt/homebrew/bin`、`/usr/local/bin`、`/usr/bin`；用户自定义 Git 路径继续优先。
- Git 子进程的空配置设备在 macOS 使用 `/dev/null`。写入 Git 的 shell 路径必须安全引用空格、单引号及 `$` 等字符；普通模式和全局模式都要遵守。
- 应用包内两个可执行文件同处 `Contents/MacOS/`，凭据助手名为 `gitbinder-credential`。绑定后不可随意移动应用，否则 Git 配置内的绝对路径会失效。

数据库 Schema 和账号优先级不变；这些平台实现不接管提交、推送或分支管理。

## 从源码生成实验包

安装 .NET 10 SDK 后，在 PowerShell 中执行：

```powershell
./build/build-macos.ps1 -Version 1.2.0
```

脚本分别发布桌面程序和凭据助手，合并时检查同名文件的哈希，生成 `Info.plist` 与复用现有图标的 `.icns`，再打包为带 Unix 可执行权限的 ZIP。暂存目录位于 `artifacts/macos/` 的独立子目录；不清理 Windows 发布目录，不覆盖已有同名 ZIP 或校验文件，不执行单元测试。

默认产物：

```text
dist/GitBinder-1.2.0-osx-arm64-unsigned.zip
dist/GitBinder-1.2.0-osx-x64-unsigned.zip
```

各有同名 `.sha256` 文件。`unsigned` 表示应用包尚未完成发行签名、公证；SDK 对个别 Mach-O 的内部临时签名不等同于可信发行签名。

## 在 Mac 上生成本机 DMG

1. 选择与 CPU 匹配的 ZIP，核对 SHA-256 后解压。系统需可用的 Git / OpenSSH；无需另外安装 .NET Runtime。
2. 在解压目录打开终端，执行 `bash finalize-macos.sh`。脚本仅处理应用的独立副本，默认使用 ad-hoc 本机签名，并生成未公证的 DMG；不会覆盖旧包或删除源应用。
3. 挂载生成的 DMG，将 `GitBinder.app` 拖到 `/Applications`，在固定位置首次启动。不要直接在临时解压目录绑定真实项目。
4. macOS 可能阻止未公证应用运行；只在已核对来源后使用系统针对该应用的“仍要打开”等授权入口。不要全局关闭 Gatekeeper，也不要批量移除其他应用的隔离属性。

持有 Developer ID 证书的发布者可将证书名作为脚本第三个参数，使用 Hardened Runtime 签名。脚本提供 .NET JIT 所需 `com.apple.security.cs.allow-jit`，不启用调试权限或任意库加载权限；正式发行还必须由发布者在 Mac 单独完成 Apple 公证、staple 与 Gatekeeper 检查。脚本不包含开发者凭据，也不自动提交 Apple 服务。

本机 ad-hoc 签名用于实验验证，不等同于 Apple 公证。更换签名或更新程序后钥匙串可能再次要求授权；凭据助手自身禁止弹出授权交互，已有条目的 ACL 不会因重新保存账号自动更新。如果助手被拒绝，需在“钥匙串访问”中明确批准当前助手；也可在确认已掌握原凭据后删除对应旧钥匙串项，再重新录入生成新条目。不要删除其他应用的钥匙串项。已有 Windows DPAPI 密文不能导入 Mac，请重新录入凭据与本机仓库路径。

## Mac 实机验收清单（待执行）

- 分别在 Apple Silicon / Intel 目标系统检查首次启动、菜单栏托盘、退出、文件选择、弹窗与 Esc。
- 使用临时账号和本地测试仓库，检查 SSH 私钥权限、HTTPS 钥匙串保存/读回/删除，以及取消授权和锁定钥匙串的失败提示。
- 确认凭据助手与主程序访问同一数据库，首次 helper 授权及应用更新后的 ACL 行为符合预期。
- 使用隔离的 Git 全局配置，验证普通绑定、全局模式、解绑恢复及空提交邮箱；不要直接修改开发者日常 Git 配置做验证。
- 在授权的测试远程仓库中验证只读认证、克隆、快进拉取与本地改动保护；测试成功不代表有推送权限。
- 检查中文、空格、单引号等本地路径，以及 APFS 大小写敏感卷、符号链接仓库的去重行为。
- 检查签名、DMG 挂载、拖拽安装及真实 Gatekeeper 行为；交付记录应分别列出打包与实机验证结果。

## 一手参考

- [Avalonia：macOS 应用包结构与发布](https://docs.avaloniaui.net/docs/deployment/macos)
- [Microsoft：.NET macOS 签名、JIT 权限与发布](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos)
- [Microsoft：.NET 10 支持系统矩阵](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Apple：Keychain Access 属性](https://developer.apple.com/documentation/security/ksecattraccess)
- [Apple：SecAccessCreate](https://developer.apple.com/documentation/security/secaccesscreate(_:_:_:))
