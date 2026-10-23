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

每组有界截止、回调有独立最长等待，finally 配对释放按键、解除等待、卸载测试 hook、退休原许可，并复用现有 Desktop owner 确认自有源与 Broker 进程退出。整例上限 150 秒；外部执行器还应限制进程总寿命并始终上传该目录，单独记录诊断失败，不掩盖普通回归。完整 Windows 检查会在普通回归之外单独调用此显式入口，设 3 分钟外部上限，并由既有始终上传的 Windows artifact 保存结果；普通回归的过滤与失败判定不变。CI456 已取得暂停组原 Tab down 泄漏的真实 OS 证据，具体观察及其对正式开门的约束见 [B-01](architecture.md#b-01非原子写回与按键所有权)；后续运行仍须逐组读取结果，不能以诊断退出成功代替安全验收。

## 自有源线程键盘边界诊断

`ObserveSelfOwnedSourceKeyboardBoundary` 只观察自有 NativeFixture 源线程的 `WH_KEYBOARD` 和原消息泵，不写正文、不安装低级 Hook，不启用正式接受。先按上节构建，在独占已解锁 Windows 测试桌面显式运行：

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveSelfOwnedSourceKeyboardBoundary'
if ($LASTEXITCODE -ne 0) { throw '源线程诊断前置、观察或清理失败，请保留结果，不自动重跑。' }
```

三个独立用例分别核验：普通 GetMessage 对照及两次 PM_NOREMOVE 不消费、随后 HC_ACTION 一次消费；posted/sent 伪消息不取得键盘 Hook 权威，同时软件输入有真实正控；Tab 原始导航与在 IsDialogMessage 前消费 down 的焦点对照。固定容量记录保留源线程/进程、原键位、阶段、消费次数与原计数；溢出、未知注入结果及清理失败均失败。每个新按次先确认自有源前台，只为已确认成功的 down 配对 up；每例有原 owner 的 30 秒操作截止及独立清理，xUnit 上限 60 秒。外部执行器应另设总寿命并始终收集结果，不影响普通回归的过滤或失败判定。

每例写入独立的 `artifacts/source-keyboard-boundary/<UTC时间-唯一标识>/result.json`，不覆盖失败。`mechanism_observed_not_product_acceptance` 仅表示这些机制本次被观察；`precondition_failed` 表示未取得预期观察或清理失败。本入口已取得真实 Windows 软件输入的局部机制观察，当前结果与限制见 [B-01](architecture.md#b-01非原子写回与按键所有权)。它不证明物理输入、LL 与实际 MSG 的逐事件关联、跨线程 repeat/up 归属或整组接受，也不准许打开正式写回。后两类交错实验不在本入口范围。

## 自有源线程同步正文事务实验

这是尚待 Windows 执行的显式机制实验，不加入默认检查。复用上述 NativeFixture 的真实 stock Edit、源 GUI 线程 `WH_KEYBOARD` 及生产 `OwnedPracticeField.TryExecute`；仅 `HC_ACTION` 首次 down 同步执行，accepted 后非零返回，前置拒绝旁路，`HC_NOREMOVE` 不写。计划是固定原期限的合成实验计划，不是正式控制器许可。源控件的真实 IME 门不变，Unknown 不能补成 Clear。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveSelfOwnedSynchronousAcceptance'
if ($LASTEXITCODE -ne 0) { throw '同步源实验失败，保留结果，不重试或开启生产接受。' }
```

六行覆盖正常首键与 repeat/up 一次写入、PM_NOREMOVE 零写、原文冲突/原期限失效/错误 cookie 的零写旁路，以及一次 native accepted 后回调返回前的 1200ms 有界暂停。记录 native 调用次数、实际 EN_CHANGE 次数、回执、GUI dispatch 前后计数、焦点、选区与 Undo；暂停中从另一线程只读属性观察原键尚未进入 dialog/控件路径，不用队列 hook 的先后顺序推导目标收到消息。结果保存在 `artifacts/source-synchronous-acceptance/<唯一目录>/result.json`，固定 `productionReadiness=false`。每行原 30 秒 owner 预算与 60 秒 xUnit 上限不变，清理自有按键和 hook；运行器须有外层总截止并保存失败证据。

该实验不安装第三方源组件，不涉及 Notepad，不提供物理键、真实中文组合态及完整生产许可关联的验收证据。与旧 LL 写后超时反例比较的是同步源内机制，不代表 hook/TSF 自动解决普通输入框支持；原生产写回门继续关闭。既有只读边界与双队列实验保持原覆盖，不扩展跨应用尾键要求。

## 自有双队列消费顺序诊断

这是独立于上述三个源键盘探针的入口，仅保留实际检索返回与原期限的局部机制检查，当前边界见 [B-01](architecture.md#b-01非原子写回与按键所有权)；不能把诊断成功解释为接受安全。只有新机制或明确回归目的才重跑。执行须在独占、已解锁的 Windows x64 测试桌面、现有 Release 构建完成后显式进行：

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveSelfOwnedConsumptionOrder'
if ($LASTEXITCODE -ne 0) { throw '消费顺序诊断失败或交错未建立，请保留结果，不自动重跑。' }
```

两行各启动一个自有 NativeFixture 进程，只有两个无 parent/owner 关系的 GUI root、两个 GUI 线程及最多两个 thread-specific `WH_KEYBOARD`；协调线程没有窗口。不启动 Broker，不安装全局/低级 Hook、TSF 或驱动，不使用 `AttachThreadInput`，不注入第三方、不读写正文。固定合成 F7/F23 输入不是物理输入法或生产授权：

1. `completion` 用真实 `HC_NOREMOVE` 对照、同步跨线程重入及嵌套 `PeekMessage`，让 posted handler 在原 Hook/外层检索尚未退栈时执行；无正文动作只能在包含原回调的最外层 native 检索实际返回后执行。记录检索代次、深度、线程及先后序号，不能用回调末尾 flag 或 posted handler 代替返回证明。
2. `deadline` 分别观察认领前过期/撤销的整组旁路，与合法认领后原期限耗尽/撤销的零动作、整组仍消费。固定合成 admission 从第一次真实键盘观察起计 100ms；返回、repeat、up 均不续期，生产所需前置检查没有由此实现或后移。

每行沿现有进程 owner 的 30 秒操作截止（子端最多 25 秒观察、独立 4 秒清理）及 60 秒 xUnit 外限；外部执行器为整入口另设 5 分钟进程总寿命。512 条事件、48 条输入和 8 条观察固定容量；溢出、身份错误、注入结果未知、Hook/线程/进程退出未证实均失败。仅为确认成功的 down 配对 up，清理不借用已取消的操作 token。每行结果保存到 `artifacts/source-consumption-order/<UTC时间-唯一标识>/result.json`，保留原事件与独立进程退出结果，不覆盖旧失败。

`mechanism_observed_not_product_acceptance` 只表示取得限定机制观察；前提未建立或入口/身份/观察断言、清理失败均不得转绿。所有结果固定 `productionReadiness=false`。当前 CI 保留原三行的精确 selector 与 4 分钟预算，并以独立显式步骤选择这两行、设置 5 分钟外限。新步骤失败仍使 Windows job 失败；只向既有始终上传的 artifact 增加 `artifacts/source-consumption-order/**/result.json` 白名单，不上传整个 artifacts。75 分钟 job 与后续模型链的原 admission 预算保持不变；本步骤占用前置预算，不绕过 admission 或挤占模型验收保留量。

## Hy 生产载荷候选构建

`./dev.ps1 hy-runtime-prepare` 显式获取并完整校验固定 b11717 官方 Windows CPU ZIP，不下载 GGUF。已有缓存仍校验全部 SHA；CPU 闭包只包含固定依赖、官方 CPU variants 与许可证。

`./dev.ps1 pack -HyRuntime artifacts/hy-production-runtime`（或 `run`）从已核 CPU 目录构建并打包 ABI5 生产 DLL、五项导出、固定锁和 production-adapter 清单；不启用 fixture 的 audit 宏，不携带源码、原 ZIP 或模型权重。普通 `pack/run` 未传该参数仍生成 Argos-only 包。只准备参数或 CPU 缓存不能证明发行包已含 Hy，正式候选验收须对这个实际打包路径运行搬迁及加载检查。

Windows 开发候选在包内生产组件齐备时提供 Hy 准备入口；组件存在不代表模型已验证或就绪。模型包事务已接通精确库存、确认时冻结的代次、离线选择/非当前包移除和失败恢复；旧 runtime 缺少可校验清单时，相关新包操作要求先修复，不暗中联网。正常启动旧 schema1/2 不增加这一前置。代使用锁阻止删除仍在另一会话中使用的模型，清理失败会保留副本并提示空间未回收。设置与快捷面板的语言列表取当前已加载方向，切换后保留原语言偏好并标明不可用值。软件合同与交叉编译已可检查，真实 Windows ABI5 加载、完整安装/切换/移除及 WPF 操作尚待验收；已有 ABI3 隔离结果不能替代这些检查。

正式候选 CI 使用 `hy-product-prepare` 在有界准备阶段校验固定 CPU 与 GGUF 缓存，再由唯一一次 `pack -HyRuntime artifacts/hy-runtime` 生成实际发行包。生产前置通过真实模型进程所有者检查加载与退出，随后在真实设置窗口验证下载、切换、中法预览和移除。权重仅进入缓存和临时安装目录，不上传测试 artifact。手动 `hy-adapter-e2e` 的隔离审计仍可检查字面 token，不作为正式发行验收的替代。

完整 Windows 检查会在普通回归之外独立运行上述源线程前三项显式探针，外部总上限为 4 分钟，并将 `artifacts/source-keyboard-boundary/` 纳入既有始终上传的结果。各数据行自身 60 秒上限不变；诊断完成不构成生产写回验收。
