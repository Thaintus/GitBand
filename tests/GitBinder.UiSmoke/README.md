# 隔离 UI 运行验证

此项目用于人工明确要求运行验证时，加载真实已编译的 Avalonia 页面和样式，生成真实运行时渲染图，并运行少量窗口/控件交互断言。它不加入主解决方案，不会因普通 `dotnet test` 自动弹出窗口。

## 运行

在仓库根目录、具有可交互 Windows 桌面的环境执行：

```powershell
dotnet build tests/GitBinder.UiSmoke/GitBinder.UiSmoke.csproj -c Release -p:NuGetAudit=false --ignore-failed-sources
dotnet tests/GitBinder.UiSmoke/bin/Release/net10.0/GitBinder.UiSmoke.dll
```

默认输出 `tests/GitBinder.UiSmoke/artifacts/`。也可向第二条命令传一个输出目录参数；该目录必须是专用验证目录。重跑会覆盖同名 PNG 与 results.txt，不会清空目录。输出已由仓库 `artifacts/` 忽略规则排除。

仅需当前界面截图时，增加 `--screenshots-only` 参数，可同时传入新的输出目录。此模式加载并截取五页面，以及平台新增、克隆仓库空表单的中英双语、两种尺寸界面；保留隔离状态、页面加载、按钮比例及弹窗关键控件可见性检查。空表单仅打开和取消，不保存或传输；跳过搜索、分组编辑、结果弹窗、账号编辑及新增弹窗保存/传输的交互验证。

宿主会短暂显示真实验证窗口，遍历页面及弹窗后自动退出。请勿在验证期间操作这些窗口；如需中断，可在启动终端按 Ctrl+C，仅停止本验证进程。不要启动/关闭用户已经安装的软件，也不要结束其他 `dotnet` 进程。

### 拖动专项

使用前文构建后的 DLL，单独运行 `--drag-only`；修改宿主或产品样式后需先重新构建。各专项模式分别执行，不与其他模式参数组合：

```powershell
dotnet tests/GitBinder.UiSmoke/bin/Release/net10.0/GitBinder.UiSmoke.dll artifacts/verification-drag --drag-only
```

- `DragPreviewChecks.cs` 在隔离账号列表上发送托管 `PointerPressed` / `PointerMoved` / `PointerReleased` 路由事件，经过产品 `ListReorder` 的真实 500ms 长按等待、指针捕获、命中与预览路径；80ms 短按应不改变顺序。
- 验证长按后拖到下一项时先实时让位，已实现卡片的屏幕 Y 坐标随顺序变化；源卡片须同时保留预览标记与捕获。检查最终生效的浅主色背景和主色描边，不能只凭 class 存在认定样式正确。
- Escape 必须还原顺序、释放捕获且保留主窗口，重载内存仓储仍为原序。第二次长按后在已移动的源卡片自身位置松开，必须保存当前预览，重载后仍是该顺序；最后恢复同一 Fixture 的演示顺序。
- 输出 `results.txt`、`accounts-drag-preview.png`、`accounts-drag-committed.png` 和可选 Avalonia 日志。此专项仅覆盖账号列表的托管拖动路径，不向操作系统注入鼠标/键盘，不代表系统级鼠标驱动或四类列表均已执行指针自动化。

### 资源观察专项

```powershell
dotnet tests/GitBinder.UiSmoke/bin/Release/net10.0/GitBinder.UiSmoke.dll artifacts/verification-resource --resource-profile
```

- `ResourceChecks.cs` 分别向内存 Fake 追加 300 个账号、平台及项目，并添加自建分组；最终条数以 `resource-profile.json` 的 Dataset 为准。检查三类列表顶部/底部实际实现容器数与末项可见性，确认没有一次创建全部卡片；反复导航后检查集合和行 VM 引用复用。
- 四类排序通过公开 VM 预览契约验证位置变化与取消，并对账号执行一次内存设置保存/重载。它不替代 `--drag-only` 的长按、捕获及控件路由验证。截图和导航计时包含主动布局等待，不作为应用响应延迟基准。
- 观察可见窗口、临时关闭缓存项目视图进度动画的诊断对照、隐藏窗口三个状态；对照完成后恢复原动画值，隐藏/显示也仅操作本隔离宿主。各状态先等待稳定，再通过区间前后 `TotalProcessorTime` 差及墙钟时间计算 CPU 比例，单个逻辑核满载记为 100%。
- 工作集/私有字节使用 CPU 计时区间之外的前后两个端点读取，避免将 `Process.Refresh` 与内存探测开销混入空闲 CPU 区间。报告中的 min/max 只是这两个端点的较小/较大值，不是连续采样峰值。仅读取 GC 次数及 `GC.GetTotalMemory(false)`，不强制 GC、不裁剪工作集。
- 输出 `resource-profile.json`、`results.txt` 和相关渲染图。内存包含测试宿主、Fake 数据和渲染器；本模式没有优化前版本 A/B 基线，CPU 比例不是电功率或真实瓦数，不能据此宣称生产应用内存/功耗下降幅度。所有账号、仓库及传输继续隔离，不调用真实 Git、Secret Store 或用户数据库。

## 仅用于归因的资源对照

`--resource-profile --suppress-test-uia` 使用派生的隔离测试窗口，令其 `OnCreateAutomationPeer` 返回 null，以区分外部 UIAutomation 查询带来的开销。此参数只允许与资源模式组合；正常完整回归及正式 MainWindow 均保留辅助功能。该条件下的低 CPU 数值不能作为生产软件性能验收，也不代表可将禁用辅助功能作为优化方案。

`--direct-composition` 仅给测试宿主设置 DirectComposition / RedirectionSurface 后备顺序，不改变产品启动配置。用于与默认 WinUI 合成作诊断对照，一次观测不能证明某个后端是异常根因。

资源模式还统计可见 CPU 区间内的布局、根视图/ListBox 属性、容器及集合变化，并临时隐藏账号视图（特定条件下再只隐藏列表）对照；所有可见性、进度条和事件订阅均在 finally 中恢复。测试输出必须保留诊断条件，不把对照值混为普通可见窗口结果。

## 覆盖与边界

- 使用实际 App.Initialize 加载真实编译后的资源、样式、ViewLocator；独立重写启动方法且不调用产品启动逻辑，前后断言 App.Services 仍为 null。
- 五页面、中文/英文、1180×780 与 960×640 视口，共 20 张基础渲染图；另附 8 张平台新增及克隆仓库正常/窄尺寸弹窗图。完整模式还包含筛选、分组、结果、账号编辑、新增表单校验和克隆运行/失败状态图。
- 断言界面使用同一个语言服务实例，并验证既有主窗口文案在中英切换及切回中文时立即刷新。
- 真实 TextBox 绑定输入过滤、分组新增/空名校验/重命名/取消、结果正文选择、确认取消、账号新建窗打开/取消。
- 六类应用弹窗（结果、二次确认、账号编辑、平台编辑、分组编辑、克隆仓库）均断言 `CanMinimize=false`、`CanMaximize=false`；空闲表单从输入框发送 Escape，仅关闭当前窗且不保存/执行传输。嵌套确认 Escape 返回取消并保留底层账号窗及主窗。此处验证窗口属性和路由事件，不替代 Windows 原生标题栏按钮视觉检查。
- 平台新增/编辑真实模态窗口：空名校验、Fake 写入失败留窗及保留字段、写入中禁用与阻止关闭、成功刷新、编辑回填/取消、Escape 取消新增；原平台筛选与启用排序单测继续保留。
- 克隆仓库真实模态窗口：账号/地址/目录的真实绑定、自动目录名和手工名称保留、输入校验、Fake 传输失败留窗、进行中禁用/重复保护、关闭请求取消且留窗、成功关闭刷新。首次空地址校验在 520×480 窄窗口触发，通过错误区域相对 ScrollContentPresenter 的实际坐标与 ScrollViewer.Viewport 检查完整落入可见视口，而不只检查 IsVisible；截图后恢复默认窗口尺寸。提示和错误均验证为可选择正文，不使用只读输入框。
- 通过控件事件及 ViewModel 命令驱动，不向系统注入鼠标/键盘操作。结果正文只断言选择范围，**不覆盖系统剪贴板**。
- Escape 路由源码位于 `EscapeChecks.cs`：项目下拉优先收起、地址/路径编辑按焦点取消且不写入、路径保存及传输/克隆收尾/分组/绑定忙时保留窗口；五页面空闲时走正常 `Closing` 入口。仅用内存 Fake 的 MainWindow 和可取消关闭事件模拟托盘接管，不构造真实托盘、不关闭用户窗口。克隆取消用受控 Fake 收尾闸门验证“请求取消后、传输尚未返回前，仍留窗”，而不只检查最终取消结果。
- 项目单行并发源码位于 `ProjectConcurrencyChecks.cs`：从真实 Button 的 Command/CommandParameter 启动两行受控 Fake 拉取，检查 `IsEffectivelyEnabled`、账号/分组下拉、页头入口、搜索及目录入口（不启动文件管理器）；单行期间批量操作受保护，同项目重复执行被拒绝。真实 TextBox 输入和 Escape 路由验证其他空闲行仍可编辑/取消；运行中全量刷新、取消首行的目标刷新及第二行自身刷新均保留 Item 实例和草稿。两个 TaskCompletionSource 独立控制取消收尾，确认首行取消不影响第二行且最后任务完成前保留退出保护。额外输出运行中、取消一项和完成后的三张截图。
- 账号、仓库、分组、设置、Secret、Git 配置和传输全部为内存 Fake；路径和邮箱均为演示数据。不读取 `%LOCALAPPDATA%/GitBinder`，不创建真实数据库、不调用 DPAPI、不访问网络、Git 或用户项目。完整模式仅在专属输出目录内创建 `clone-sandbox/fake-success-<UUID>` 空目录，让应用服务通过目录存在性检查；不会创建 `.git`、源码或凭据。截图模式不创建该目录且 Fake 传输调用为零；完整模式有失败/取消/成功三次受控 Fake 克隆、取消/成功两次受控 Fake 拉取及零次认证测试。
- `results.txt` 记录本次 PASS/FAIL；退出码 0 表示断言通过，非 0 表示失败。运行时 Avalonia 警告可写入 `avalonia.log`（没有日志时该文件可能不存在）。截图仍需人工查看确认视觉效果。
- 这不是安装测试、真实账号认证/拉取测试、系统托盘测试、系统目录选择器或操作系统级键鼠/剪贴板验证；不替代独立 xUnit 用例。普通异步操作等待上限为 5 秒；克隆取消 Fake 闸门为 10 秒，跨多次布局和截图的拉取 Fake 闸门为 30 秒，拉取闸门另在 finally 中释放，避免失败时无限等待。

新增断言只有在用户授权后重新构建并运行本宿主、且 `results.txt` 记录通过时，才可报告为已验证。安装包构建不运行本项目，不代表上述 Escape 回归已执行。
