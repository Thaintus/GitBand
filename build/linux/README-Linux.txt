GitBinder __VERSION__ — Linux 实验包（__RUNTIME__）

一、适用环境
  目标：Ubuntu 22.04 / 24.04、Debian 12 及兼容的 glibc 桌面发行版。
  linux-x64 对应 amd64；linux-arm64 对应 arm64。
  包含 .NET 10 运行时，无需另行安装 .NET SDK 或 .NET Runtime。
  不适用于 Alpine/musl、32 位系统或没有图形会话的服务器。
  这是跨平台发布的实验包，打包成功不等于已在各桌面/发行版实机验收。

二、推荐使用 .deb 安装包
  在下载目录中执行（将文件名替换为与你的架构对应的实际文件）：
    sudo apt install ./gitbinder___VERSION_____ARCHITECTURE__.deb
  apt 会解析系统依赖；如已有 GitBinder 安装，会按包管理器规则更新程序。
  安装位置：/opt/gitbinder/；桌面菜单中显示 GitBinder。
  安装过程不启动应用、不修改 Git 配置，也不迁移或删除个人数据。
  启动和使用应用时请使用普通登录用户，不要以 sudo/root 身份启动。

三、便携 tar.gz 包
  使用归档管理器解压到新的、固定的目录，保留所有文件与可执行权限。
  在图形桌面的终端进入解压后的 GitBinder-__VERSION__-__RUNTIME__ 目录：
    ./GitBinder.Desktop
  必须将 gitbinder-credential 与主程序及其依赖始终放在同一目录。
  应用会把 Credential Helper 的绝对路径用于仓库配置，绑定后不要随意
  搬动或删除此目录；如需迁移，先在应用中解除相关绑定/全局模式。
  tar.gz 不会自动安装依赖或注册桌面菜单。如需菜单与依赖自动处理，
  请使用 .deb 包，而不是手工覆盖 /opt/gitbinder 下的文件。

四、系统依赖和凭据存储
  需要系统 Git、OpenSSH、X11（Wayland 会话需 XWayland）、Fontconfig，
  以及 .NET 原生依赖：libc6、libgcc-s1、libstdc++6、libgssapi-krb5-2、
  ICU、OpenSSL 3、libbrotli1、tzdata、zlib1g、ca-certificates。
  窗口依赖：libx11-6、libice6、libsm6、libfontconfig1、libxext6、
  libxrandr2、libxi6、libxcursor1。中文字体建议安装 fonts-noto-cjk。
  凭据依赖：libsecret-1-0、会话 D-Bus、已经解锁的 Secret Service。
  GNOME Keyring 可提供该服务；KeePassXC 或桌面自带的其他实现需要
  显式启用 Secret Service 集成。只安装库、不运行或不解锁密钥环不够。
  密钥环不可用时保存/读取凭据会失败，不会退回明文存储。
  前台保存可能要求创建或解锁密钥环并授权，删除可能出现系统确认；
  后台凭据助手不会主动解锁，使用前请先在当前桌面会话解锁密钥环。
  便携包用户可参考 .deb 的 control 依赖列表或下方官方文档补齐环境；
  ICU/OpenSSL 包名随发行版不同，不要从其他发行版强装二进制依赖。

五、数据、托盘及卸载注意事项
  数据默认位于 ~/.local/share/GitBinder/；若设置绝对路径
  XDG_DATA_HOME，则使用其下的 GitBinder/。凭据存储在系统密钥环。
  Linux 默认关闭窗口即退出；确认桌面支持托盘后，可在设置中开启驻留。
  托盘显示取决于桌面环境是否支持状态图标，开启前请确认托盘入口可见。
  卸载前请先关闭全局模式并解除项目绑定，恢复 Git 配置后再退出应用；
  卸载不会替你恢复仓库的 Credential Helper 配置，也不会删除个人数据。
  .deb 可通过系统软件管理器卸载；便携目录可在上述解绑步骤后自行处理。
  不要把 Windows DPAPI 或 macOS 钥匙串凭据直接复制到 Linux 使用，
  请在 Linux 重新录入凭据并导入需要的 SSH 私钥。

六、校验
  发布文件旁有 SHA256 文件。下载到同一目录后可运行：
    sha256sum -c <实际安装包文件名>.sha256

官方运行依赖：
  https://docs.avaloniaui.net/docs/deployment/linux
  https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install
  https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian
包格式依据：
  https://manpages.debian.org/bookworm/dpkg-dev/deb.5.en.html
