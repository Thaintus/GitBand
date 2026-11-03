# Windows 发布与安装包体积

## 当前发布布局

- 入口为 `build/build-installer.ps1 -Version <新版本号> -Runtime win-x64`，调用 `build/publish.ps1` 后用 Inno Setup 生成版本化安装包。用户明确要求打包后才能执行；此入口不运行测试，也不自动安装或启动应用。
- Desktop 和 Credential Helper 分别自包含、非单文件发布到 `build/publish/desktop/` 与 `helper/`，合并至 `build/publish/app/`。安装包只收集 `app/`。两份 EXE 是各自的 apphost，与自身 DLL、deps.json 和 runtimeconfig.json 同目录；.NET 及共享 DLL/原生库只存一份，用户无需另外安装 .NET。
- 保留 Helper 自己的 runtimeconfig，包括 InvariantGlobalization；主程序与 Helper 的运行时配置不混用。凭据助手仍位于主程序 AppContext.BaseDirectory 下，原 Git Helper 路径约定不变。移动或分发发布目录时必须保留完整目录。
- 合并时只接受哈希相同的同名文件；不一致则停止，不以最后一次发布覆盖前一份依赖。仍打包 SkiaSharp、HarfBuzzSharp、SQLite 及 .NET 原生运行时，不按体积手工删减运行库。

## 体积策略

- 关闭 DebugType、DebugSymbols 和 CopyOutputSymbolsToPublishDirectory，合并时再跳过全部 `.pdb`；Inno 的 Excludes 同样排除 `.pdb`，防止第三方原生调试符号进入安装包。
- 合并前同步清理各自 deps.json 的 native/runtimeTargets 中 `.pdb` 诊断条目，使依赖清单与实际分发文件一致；其余依赖、资源及运行配置保持不变。
- 保持 SelfContained=true，不启用裁剪、AOT 或 ReadyToRun。采用共享目录降低两个独立单文件各自携带运行时造成的重复，不增加用户安装运行时的步骤。
- Inno 继续使用 LZMA2 实心压缩。原先显式文件项与通配项中的重名源在 1.2.11 日志中只压缩一次；清理重复声明是维护性调整，不以此计算体积收益。
- 不主动清理用户安装目录中的历史 PDB；上述策略控制新包内容。研发需要的调试符号可单独留档，不随用户安装包分发。
- 发布脚本在 `build/publish/payload-manifest.json` 记录最终 `app/` 的文件数、逐项字节数及总有效载荷，并再次拒绝 PDB。清单不进入安装包，可供下一次明确打包时与基线比较；压缩安装包还需单独统计实际大小。

## 优化前基线及验收边界

2026-10-31 只读统计现有 1.2.11 发布文件：

| 项目 | 大小 |
| --- | ---: |
| 已有安装包 | 76,936,617 字节 / 73.37 MiB |
| 发布目录全部文件 | 289,988,913 字节 / 276.55 MiB |
| 8 个 PDB 文件 | 105,240,768 字节 / 100.37 MiB |
| Desktop 单文件 | 107,938,782 字节 / 102.94 MiB |
| Helper 单文件 | 76,633,488 字节 / 73.08 MiB |

同一批编译目录中还找到 200 个同名同 SHA256 的共享 DLL，合计 79.32 MiB，可作为共享依赖的依据。原始文件大小不能直接折算为压缩安装包的减量；优化前基线与共享文件清单位于 `artifacts/package-size-analysis/`。

2026-10-31 已生成 1.2.12 Windows x64 安装包并完成产物核对：

| 项目 | 1.2.11 | 1.2.12 |
| --- | ---: | ---: |
| 安装包 | 76,936,617 字节 / 73.37 MiB | 37,267,491 字节 / 35.54 MiB |
| 发布有效载荷 | 289,988,913 字节 / 276.55 MiB | 115,101,505 字节 / 109.77 MiB |
| 分发 PDB | 8 个 | 0 个 |

压缩安装包减少 39,669,126 字节，约 51.56%。合并后的 241 个文件包含两份 x64 apphost、各自配置及一套 .NET 10.0.11；检查 435 项依赖资产均存在，Helper 的 InvariantGlobalization 保留。完整报告在 `artifacts/release-1.2.12/`。此结果仅证明编译/打包与静态依赖完整性，未启动或安装，也未测运行内存/功耗。

后续打包需要核对两个 apphost 的版本/架构、各自 DLL/deps/runtimeconfig、共享 .NET 与图形/SQLite 原生库是否完整，且 `app/` 不含 PDB。再核对安装包版本、大小、哈希并保留历史安装包。发布成功不代表安装、GUI、凭据助手或真实 Git 认证已作运行验收。

## 1.2.13 运行验证后的修复包

2026-10-31 按用户要求测试，90 项相关单测和最终 232 项隔离 UI 检查通过，发现并修复拖动预览状态样式被后续普通卡片样式覆盖。1.2.13 只将该界面修复及版本更新加入原有优化，不禁用正式软件的辅助功能、不切换正式渲染后端。

安装包为 37,267,294 字节（35.54 MiB），SHA256 `3F098DCC29F0B2B0370215BEAE75C5BB7BB54921866EED851D7936B1211287A8`，相对 1.2.11 仍减少约 51.56%。两个 x64 apphost 的文件版本均为 1.2.13.0，435 项依赖资产、241 个文件、115,101,505 字节有效载荷及 Helper 独立配置核对通过，无 PDB，原有 20 个安装包/校验文件保持不变。安装包未签名，未自动安装。

实际发布 Helper 的共享 host/托管/原生依赖采用隔离 StartupHook 验证，在生产 Main 前退出，范围与明细见 `artifacts/release-1.2.13/package-runtime/`；不代表凭据业务或真实认证验证。安装包核对记录位于 `artifacts/release-1.2.13/verification.json`。测试及 CPU 未通过项见 `artifacts/verification-1.2.12-20261031/README.md`（目录沿用测试起点版本）。包体积结论成立，运行时低 CPU/功耗目标尚未完全验收。

## 1.2.14 拖动体验优化包

2026-11-02 已按用户要求完成 Windows x64 1.2.14 发布：包含单卡细框浅底、正常不透明度、取消目标粗插入线、列表实时让位及拖动时抑制提示框。安装包 37,271,296 字节（35.54 MiB），SHA256 `3D4FEC999F3906FA73F6BEB790B908BB8FB48639EFEA29168D8CC13336F74DEF`。

两份 x64 apphost 文件版本均为 1.2.14.0；有效载荷 241 文件、115,100,993 字节、435 项依赖资产核对通过，无 PDB且保留 Helper 独立配置。22 个历史安装包及校验文件均保持不变，记录位于 `artifacts/release-1.2.14/`。此次仅执行生产发布编译/Inno 打包与静态产物核验，没有运行单元/UI/资源测试、StartupHook 或安装程序；不能将前版测试结果或包体积作为本版界面/功耗验收。
