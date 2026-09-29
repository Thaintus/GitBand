GitBinder __VERSION__ / __RUNTIME__ — macOS 实验包（未签名、未公证）

本压缩包包含 GitBinder.app、自包含 .NET 运行时、凭据助手和本机签名打包脚本。
osx-arm64 对应 Apple Silicon（M 系列）；osx-x64 对应 Intel Mac。
要求 macOS 14 或更高版本，并已安装 Git 和 OpenSSH。无需另装 .NET。

此包是在 Windows 上交叉发布的，尚未完成真实 Mac 启动、钥匙串、Git 和托盘验证。
它不是已通过 Gatekeeper 的正式发行包；请勿将“可解压”理解为“已验证可运行”。
压缩包保存了 Unix 可执行权限；如果解压软件没有保留这些权限，请改用 macOS 自带归档实用工具。

Mac 本机试用步骤：
1. 解压到自己有写入权限的目录；在终端进入解压目录。
2. 阅读 finalize-macos.sh，确认来源可信后执行：
   /bin/bash ./finalize-macos.sh
   脚本仅复制并签名当前目录的 GitBinder.app，然后生成 adhoc-unnotarized.dmg。
   不覆盖原 app 或已有 DMG，不安装应用，不启动应用，不关闭 Gatekeeper，不提交 Apple 公证。
   需要系统 codesign/hdiutil 工具；若提示缺少开发工具，可按 Apple 提示自行安装 Command Line Tools。
3. 打开生成的 DMG，将 GitBinder 拖入“应用程序”。首次运行可能受到系统安全策略拦截；
   只有核实下载来源与 SHA256 后，才按 macOS 的“隐私与安全性”提示允许该应用。
4. 新建独立账号/测试仓库进行验证。首次保存或使用凭据时，钥匙串可能请求授权。
   数据目录：~/Library/Application Support/GitBinder；凭据保存在当前用户的系统钥匙串。

Developer ID 签名（可选，需要自己钥匙串中已有有效证书）：
   /bin/bash ./finalize-macos.sh ./GitBinder.app ./signed "Developer ID Application: Your Name (TEAMID)"
这仍然只是 developerid-unnotarized.dmg，不会自动完成公证，也不代表可无警告公开分发。
正式 GitHub Release 前，应在 Mac 验证实际功能，再完成 Developer ID 签名与 Apple 公证。

Mac 本机签名/打包会保留一个 .gitbinder-macos-* 暂存目录便于排查，不自动删除任何历史包。
重新打包同版本时，指定新的输出目录，避免覆盖历史产物。

参考：
https://docs.avaloniaui.net/docs/deployment/macos
https://learn.microsoft.com/en-us/dotnet/core/deploying/macos
https://support.apple.com/102445
