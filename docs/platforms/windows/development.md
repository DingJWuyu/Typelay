# Windows 开发与分发

共用依赖、变更检查选择与 CI 约定见[共享开发流程](../../engineering/development.md)。本页命令要求 Windows x64，平台机制见[Windows 架构](architecture.md)。

## 准备与执行

在 Windows x64 使用 PowerShell 7.2+、global.json 中的 .NET SDK、Python 3.13 和 LLVM clang/lld-link。
运行 `python -m pip install -r eng/requirements.txt` 安装固定版本的检查工具。不要在仓库或发布包安装模型来跑快速测试。

| 命令 | 用途 |
| --- | --- |
| `./dev.ps1 run` | 构建并启动真实 Windows 开发客户端 |
| `./dev.ps1 check` | Windows 构建、行为/契约、Python、lint 与格式检查 |
| `./dev.ps1 format` | 修正格式及可自动修复的 lint |
| `./dev.ps1 pack` | 自包含 Windows 包、校验和、搬迁分发与进程 E2E |
| `./dev.ps1 e2e` | 对已有发布包重跑分发与进程回归 |
| `./dev.ps1 native-e2e` | 已解锁 Windows 桌面的受控 Win32 适配器契约 |

`check` 的 App 桌面测试需要可交互 Windows，会启动和清理自己的测试控件进程，不下载模型，也不操作用户正在编辑的窗口。`run` 使用真实客户端；需要模型的调试先通过正式安装流程准备组件。

```powershell
git clone --branch trunk https://github.com/DingJWuyu/Typelay.git
cd Typelay
python -m pip install -r eng/requirements.txt
./dev.ps1 check
./dev.ps1 run
```

Windows 代码变更前运行 `./dev.ps1 check` 获取基线；缺陷修复还须按 tests/AGENTS.md 建立红色回归。代码变更后运行 `./dev.ps1 format`，再运行 `./dev.ps1 check`。发布路径、启动、资源或跨进程变更还须运行 `./dev.ps1 pack`。

命令实现位于 eng/windows/dev.ps1；原生入口与受限输入 DLL 分别通过 eng/windows/build-launcher.py、eng/windows/build-native-input.py 构建。根 dev.ps1 保持上述用户命令。

`pack` 包含独立 `TypeLay.BrowserHost` 和明确的 Chrome 扩展运行文件。搬迁分发回归默认只验证其依赖及非法入口拒绝，不替开发者注册扩展。仅在明确独占的 Windows 测试账户设置 `TYPELAY_BROWSER_REGISTRATION_E2E=1`，才执行真实 `register/status/unregister` 回归：起点要求 TypeLay 用户数据目录与 HKCU/HKLM 两视图同名 host 均不存在；冲突直接失败，清理只删除本次精确拥有的注册与文件。CI 的专用 Windows runner 显式设置该开关，不用于用户本机授权。实际 Chrome 与模型验收另有自己的生命周期，不能用注册成功代替。

受控工具、生产桌面及物理输入法验收见[Windows 测试](testing.md)。当前未结问题与修复顺序见 [Windows 待办](open-issues.md)；按共同产品定义并行开发，Windows 桌面与真实输入法证据仍须单独取得。

## 源接受低级回调超时诊断

这是 B-01 的显式风险诊断，不计入产品安全验收或普通回归通过数。仅在独占、已解锁的 Windows x64 测试桌面执行，至少 Windows 10 1709；先由既有 `./dev.ps1 check` 构建所有生产组件和自有 fixture。普通测试默认跳过这个 Explicit 用例，不为诊断修改普通回归过滤条件。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.NativeSourceTimeoutProbe.RecordClaimThenLowLevelCallbackTimeout'
if ($LASTEXITCODE -ne 0) { throw '低级回调诊断前置或清理失败，请保留原始结果，不自动重跑。' }
```

一次调用只有正常与暂停两个组，不重试 Claim。暂停组在真实原生 Claim 返回后阻塞测试自己的低级回调至少 1600ms，正常组必须先证明写入回执与整组 Tab 消费；独立 LL observer 和同源输入队列中的 F23 尾标记给出观察边界，不用读取文本充当排空键盘队列。恢复后的消息泵 ping 与 marker 回调计数仅描述行为，不声称 Windows 提供 hook 移除查询。

结果写入 `artifacts/source-ll-timeout/<UTC时间-唯一标识>/result.json`，不覆盖旧失败。`observed_leak` 表示已 Claim 按次的原 down 或 up 到达源队列；`not_observed` 只表示本次未观察到泄漏；`precondition_failed` 表示未建立可解释实验或清理失败。退出码 0 仅表示取得可解释诊断，**不表示没有泄漏，也不准许打开正式写回**。必须读取两组 JSON、原回执、文本与计数，不能把 xUnit 的通过标识当安全结论。

每组有界截止、回调有独立最长等待，finally 配对释放按键、解除等待、卸载测试 hook、退休原许可，并复用现有 Desktop owner 确认自有源与 Broker 进程退出。整例上限 150 秒；外部执行器还应限制进程总寿命并始终上传该目录，单独记录诊断失败，不掩盖普通回归。完整 Windows 检查会在普通回归之外单独调用此显式入口，设 3 分钟外部上限，并由既有始终上传的 Windows artifact 保存结果；普通回归的过滤与失败判定不变。此入口仍须实际 Windows 运行后才能取得 OS 交错证据。

## Hy 生产载荷候选构建

`./dev.ps1 hy-runtime-prepare` 显式获取并完整校验固定 b11717 官方 Windows CPU ZIP，不下载 GGUF。已有缓存仍校验全部 SHA；CPU 闭包只包含固定依赖、官方 CPU variants 与许可证。

`./dev.ps1 pack -HyRuntime artifacts/hy-production-runtime`（或 `run`）从已核 CPU 目录构建并打包 ABI5 生产 DLL、五项导出、固定锁和 production-adapter 清单；不启用 fixture 的 audit 宏，不携带源码、原 ZIP 或模型权重。普通 `pack/run` 未传该参数仍生成 Argos-only 包。只准备参数或 CPU 缓存不能证明发行包已含 Hy，正式候选验收须对这个实际打包路径运行搬迁及加载检查。

Windows 开发候选在包内生产组件齐备时提供 Hy 准备入口；组件存在不代表模型已验证或就绪。模型包事务已接通精确库存、确认时冻结的代次、离线选择/非当前包移除和失败恢复；旧 runtime 缺少可校验清单时，相关新包操作要求先修复，不暗中联网。正常启动旧 schema1/2 不增加这一前置。代使用锁阻止删除仍在另一会话中使用的模型，清理失败会保留副本并提示空间未回收。设置与快捷面板的语言列表取当前已加载方向，切换后保留原语言偏好并标明不可用值。软件合同与交叉编译已可检查，真实 Windows ABI5 加载、完整安装/切换/移除及 WPF 操作尚待验收；已有 ABI3 隔离结果不能替代这些检查。

正式候选 CI 使用 `hy-product-prepare` 在有界准备阶段校验固定 CPU 与 GGUF 缓存，再由唯一一次 `pack -HyRuntime artifacts/hy-runtime` 生成实际发行包。生产前置通过真实模型进程所有者检查加载与退出，随后在真实设置窗口验证下载、切换、中法预览和移除。权重仅进入缓存和临时安装目录，不上传测试 artifact。手动 `hy-adapter-e2e` 的隔离审计仍可检查字面 token，不作为正式发行验收的替代。
