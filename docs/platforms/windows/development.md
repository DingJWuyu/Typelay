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

新增或拆分 `KeyboardHook` partial 时，同步核对 `tests/windows/fixtures/TypeLay.NativeFixture/TypeLay.NativeFixture.csproj` 的显式 `Compile` 链接及依赖闭包；该 fixture 不引用 App 程序集。App、App.Tests 或 DesktopFixture 编译通过，以及 folder 格式检查通过，都不能替代这个独立消费者的构建。

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

六行在原合成授权期限内实际探测并冻结源线程 HIMC/HKL，调用现有带 input-context 的 native 执行入口；探测失败明确拒绝，不用零值补成 Clear。覆盖正常首键与 repeat/up 一次写入、PM_NOREMOVE 零写、原文冲突/原期限失效/错误 cookie 的零写旁路，以及一次 native accepted 后回调返回前的 1200ms 有界暂停。记录 inputContextProbed、native 调用次数、实际 EN_CHANGE 次数、回执、GUI dispatch 前后计数、焦点、选区与 Undo；暂停中从另一线程只读属性观察原键尚未进入 dialog/控件路径，不用队列 hook 的先后顺序推导目标收到消息。结果保存在 `artifacts/source-synchronous-acceptance/<唯一目录>/result.json`，固定 `productionReadiness=false`。每行原 30 秒 owner 预算与 60 秒 xUnit 上限不变，清理自有按键和 hook；运行器须有外层总截止并保存失败证据。

HIMC/HKL 只提供离散身份样本，既有 owned 执行的任意末期重入安全、TSF profile 连续性及组合权限仍未闭合。该实验不安装第三方源组件，不涉及 Notepad，不提供物理键、真实中文组合态及完整生产许可关联的验收证据。与旧 LL 写后超时反例比较的是同步源内机制，不代表 hook/TSF 自动解决普通输入框支持；原生产写回门继续关闭。既有只读边界保持原覆盖；消费完成与原期限的机制约束见 [B-01](architecture.md#b-01非原子写回与按键所有权)，不扩展跨应用尾键要求。

## 普通 source 事务与同一 MSG 的受控接线

`ObserveOrdinarySourceMessageAcceptance` 只验证一个缺失节点：已安装的普通 `SourceInstallation` 能否在源线程实际 `PM_REMOVE` 的同一个 MSG 上承诺消费，然后执行原生正文事务。它不借 `OwnedPracticeField`，也不再从源线程调用明确要求跨线程的 `TypeLaySourceActivate`。正式 App/Broker 不安装新的 `TypeLaySourceGetMessageProc` 导出，`SourceLocalAcceptanceVersion` 与默认关闭门不变。普通事务 v2 不变；私有 source wire 已升为 v4 并明确拒绝旧版本，以区别 Prepared 与 Armed，不给旧消费者隐式授予新语义。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveOrdinarySourceMessageAcceptance'
if ($LASTEXITCODE -ne 0) { throw '普通 source MSG 研究失败，保留原结果，不重试。' }
```

仅两个自有 stock Edit 场景，保留既有六项 owned 同步实验原覆盖：

1. 同一个首 Tab 两次 `PM_NOREMOVE` 均不消耗 invoked/claim，再由 `PM_REMOVE` 执行一次普通 source 事务。native 返回后、原 GetMessage 返回前，fixture 的 managed hook 有界暂停1200ms；外部只读属性确认原泵尚未返回。原泵在 GetMessage 下一条语句记录实际返回 MSG，随后仍走原 IsDialogMessage/TranslateMessage/DispatchMessage。验证一次真实正文变化、原字段焦点、零导航/提交。原泵返回后、尾键之前，同一 grant 的精确 Retire 必须仍非 Quiet，无 Quiet 授权的 fixture Close 必须拒绝且保留全部 hooks；随后由 native 消费同字段 repeat/up，原 Retire 才可 Quiet，最后核 Undo。fixture 仅观察原 MSG 与暂停，不代做尾键消费。
2. 安装后真实修改源文，首 Tab 不被改写、零 claim 和正文替换，实际选区保持，原 dialog 自然转焦 B。portable API 边界另核拒绝不进入 SETSEL；真实 Windows 此行不声称观察到了每一次底层 API 调用。

测试内的真实 `NativeEditTransactions` 后端以默认关闭的 `sourceMessageRoute` 构造显式选入，在原精确 x64 binding 生命周期中成对安装 CALLWNDPROC 与 GETMESSAGE，两者使用同一个已加载 HMODULE。首次安装失败关闭本实例路线，并回滚已安装 hook；失败回滚仍未知时保留原句柄/精确进程/模块，不回退旧 LL 激活。正式 App/Broker 构造不选入，不发布新能力。

fixture 不再 Load/Free 事务 DLL 或直接调用 native delegate，只在后端只读预备捕获后、原100ms grant之前安装上游观察 hook，以 CallNextHookEx 取得 native 返回的实际 MSG。它只借用已经加载的精确模块并采一次完整路径；后置结果仍核 `AppContext.BaseDirectory/TypeLay.NativeInput.dll` 与实际 module base，不借独立 Broker 观察进程的同名副本。路径不能证明目标只有一个模块，也不补齐来源资格。首键、同字段尾键、Undo/Quiet、成对后端 hook 所有权、早 Dispose 不退役及退出后模块回收已由 [CI497 source 两例](https://github.com/DingJWuyu/Typelay/actions/runs/40066099478)取得实际证据；安装失败回滚仍为静态审查范围。v4 Prepared/Arm 与下述 App 回执接线也已由 [CI503](https://github.com/DingJWuyu/Typelay/actions/runs/40079876227) 的原 source 两例及隔离 App 消费者取得实际证据；这不补齐物理来源、真实 IME 或第三方部署资格。

隔离 `NativeSourceControllerTests` 的无修饰 Tab 消费者现在选择 `--source-message-broker`，沿生产 App adapter、认证 Broker 和真实 native 后端取得完成回执，不再由 LL `_stockClaim` 触发执行。安装先发布 Prepared；App 取得原映射后先发布本地撤权 control 和 capture fence，再以一次 CAS Arm。原 generation/grant/目标/计划和100ms期限不变；Prepared/Armed 可撤，Claimed/Withdrawn 不得重置。源回调在进入下游链前实际观察到 Prepared 的输入先撤权并保留拒绝，LL 对尚未 Arm 的输入也只撤候选、原样旁路；这不是严格来源或安装前旧队列键的证明。

仅精确 owner 的 `source-poll`/`source-retire` 实际完成能排入一次 `CompletedReceipt`，复用 Runtime 直接结算而不再 `WriteAsync`；poll 时间不填写为按键时间。已交付正文回执与尚未 Quiet 的尾键/资源所有权分开记录。`Acknowledged` 只来自真实匹配 command/operation 的 native 响应，默认、超时、错 ACK 及仅进程退出均为 false；它不是新授权。有效非 Quiet ACK 可继续原退休等待，未知 ACK 则隔离 owner，不作为重试理由。post-write capture 在回执交给控制器或精确无写终结前保留原2秒失败 fence。现有两个消费者分别验证一次 Tab、零 LL 激活/二次写与真实 capture response 的结算顺序，以及 ACK 未到且原许可未过期时撤销后的唯一 Tab 旁路。F7/F24 等旧 sent-route 回归保留，不对新的固定 Tab 源路径声明同样能力。正式 Main/default/capability0、来源和 IME 门均未改变。

终态结果的 `NativeResponse` 仅在既有命令收到有效 Quiet ACK 后保留已经采样的 native status/raw reason，并在释放映射前读取现有 shared invoked 字；超时、错 ACK、仅进程退出或非 Quiet 结果保持 null。不增加接受过程内查询、消息或轮询，也不改变公开拒绝映射。`0x10000 | queue flags` 区分 sent-activation 的 pending-work 拒绝与普通 raw 15，queue flags 只是队列类型提示；invoked=1 只说明此 grant 的共享尝试位已消费，不证明 claim、正文执行或特定 module 的完成。多个 module 仍可回链写同一响应，raw 15 也不标识具体撤销事件；这份已确认终态 wire 快照不能当作唯一 GETMESSAGE 拒因的完整轨迹。

两个源入口使用不同准入条件：sent-activation 保留原 `GetQueueStatus(0x1dff)` 全 mask 拒绝门；retrieved-MSG 路径不读队列提示，而在实际同一 `PM_REMOVE` 回调内重核当前原文、选区、资格、生命周期、输入 context 和原期限。私有 admission 的 MSG 与 expected-MSG 指针必须同时存在或同时为空，缺一即写前拒绝，不提供远程 skip 开关。队列提示不是已有消息完成的凭据，也不要求所有历史 raw 输入已 Dispatch。当前源的非接受键/按钮被该 hook 实际 `PM_REMOVE` 时，复用既有输入种类单调撤销本 grant，原样旁路；NOREMOVE 和负 hook code 不撤销、不消费 invoked。该观察不补造安装前历史或严格来源证明。

native 在调用一次 CallNextHookEx **之前**占住 source callback 寿命门，之后重核原 MSG 与同 grant。claim CAS 赢后立即原地置 `MSG.message=WM_NULL`，两者之间没有 OS 调用、回调、分配或日志，再进入首个 SETSEL；拒绝前不改 MSG，受理后出现拒绝或未知也不恢复/重注入原键。普通 source 的完整回执仍来自实际重验，MSG 路径不伪造 sent command ACK，退休由原 poll/retire 协议确认。NOREMOVE 不耗一次尝试，其他失败不以 repeat 重试。

输入是既有受控 SendInput，不证明物理来源、严格软件注入拒绝、旧排队键历史或真实 composition。先 Next 再执行不能约束上游 hook 在本实现返回后改写或提前分发 MSG，结论仅限自有受控 hook 链。native 在 actual MSG claim 后、首 SETSEL 之前记录同按次尾键义务，sent-activation 不产生该义务。它绑定原字段、线程、root/cookie 与 scan/extended/context bits；原100ms期限只管首写，不解除已接受按次。尾键回调在 Next/IMM 后重核同一 MSG 和资格，匹配 repeat/up 才置 WM_NULL，up 结清义务。新首 Tab、错按次、busy 重入或未知 HIMC/HKL 单调封禁旧消费，未知不伪称离域或松键，义务仍保留；明确字段离焦/销毁、原寿命或已知输入 context/lifecycle 失效结束该原 scope 的义务，等值恢复不复活。此阶段只覆盖原 fixture 的同字段范围，不证明同源应用内换字段仍沿原按次的完整产品合同，也无实际跨应用证据。HIMC/HKL 与 exact-context 无组合观察均不授全局 Clear；不安装或切换 IME，不恢复已退役 TSF 全文路径。

精确 Quiet 之前后端 Dispose 不拆这两个源 hook；原成功行在尾键尚欠时尝试 Dispose，再用同一 repeat/up 的原断言验证未早拆。Quiet 后先卸 GETMESSAGE，再卸 CALLWNDPROC；任一步失败保留未关闭所有权。[Unhook 返回不代表没有在途回调](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwindowshookex)，本隔离构造保守持有本地 DLL 引用至精确目标进程句柄 kernel-signaled，零超时查询不等待、不阻挡目标退出，不承诺第三方热卸载。fixture Close 的显式参数仍只声明原 grant Quiet，不能授写权；未知时保留观察 hook，沿既有 owner 结束自有进程。无论此前是否 Quiet，退出后再次 Dispose 并要求 `sourceRouteClosed=true`；进程退出退休与 native Quiet 分开记录，不将 Kill 清理称作自然关闭。

隔离 App/Broker/native 完成回执闭环已接通并实跑：原自有源正例核一次完成、零 LL Activate/二次 Write、真实 capture fence、原键实际零派发及 Undo；撤回反例核原 down/up 实际派发。旧 sent-route 的 `_stockClaim`→`source-receipt` 仍供 F7/F24 等现役回归，不能与新路线混称。现有 `RealNotepadAcceptsOnceOrPassesTabAfterWithdrawal` 两例改用同一 `--source-message-broker` 路线，保留原按键序列、28秒截止、精确正文/选区/焦点和原生 Undo；它以真实 Notepad 结果观察派发，不添加 fixture raw hook 计数。[CI504](https://github.com/DingJWuyu/Typelay/actions/runs/40083637614) 的精确提交已实际通过这两个新路线用例：接受恰一次 Verified 正文回执，撤回零正文回执且原键旁路，两例保留原 Undo、焦点、零 LL Activate/二次 Write、Quiet 与清理断言；不是沿用旧路线结果。UIAccess 兼容范围已按[产品选择](../../product/README.md#windows-接受译文的辅助输入兼容范围)确认；同消息来源、真实 IME 与第三方部署仍须分别完成，正式 Main 与 capability0 不变。

保持原100ms grant；sent-activation 的 pending-work 拒绝门不变，原 MSG 使用上述实际状态与一次消费条件。首 down 仍单独发送，确认后才发尾键和后置屏障，不增加输入、重试或续期。两行沿原30秒 owner/60秒单项上限，独立 CI 步骤复用退役 owned-TSF 的2分钟预算，Windows 总 job 仍75分钟，模型与其它步骤不变。`artifacts/source-message-acceptance/<唯一目录>/result.json`、runner.json/log 进入既有早期 source artifact 与末尾 artifact；runner 按实际退出码、两行完整性和清理区分成功观察、失败、缺失/重复及清理未知，`running`/空退出码仍是未完成。continue-on-error 仅使其它独立验收继续，不把研究失败改为通过。安装所有权迁移与 v4 接线后的 Windows 原两行已通过；sent pending 拒绝、MSG hint 不误拒及实际输入撤销/expiry/cookie/revoke 的对照当前仅覆盖 portable API 边界，不冒充 Windows 实测。私有两指针缺一的防御另由局部不变量断言核验，不伪称公开入口可构造该状态。

## 源接受来源门的下一实现切片

**产品选择已确认，以下是待实现方案，不是已完成能力。** 场景是用户或获准的辅助工具在有效译文出现后按一次 Tab；受控 Notepad 已证明事务闭环，但当前 native 未检查正式来源，旧队列/重入与来源对应仍有缺口。实际发生率未知，错误接受可修改用户正文；复用现有源入口和回归比再建独立分类探针更有价值。范围只到无修饰 Tab 的隔离严格来源构造，不改正式 Main/capability0、系统权限、TIP 或其它快捷键。

1. 在 `TypeLaySourceGetMessageProc` 的 `HC_ACTION / PM_REMOVE` 入口先做纯本地 MSG 筛选与复制，在 `GetCurrentThreadId`、`GetKeyState`、IMM、映射、日志及 `CallNextHookEx` 等其它应用 OS 调用之前采一次 GCIMS。按[来源资格](architecture.md#输入来源资格)分类；结果只活在这次回调栈中，不建立跨队列 time/vk 匹配器或通用策略层。只允许来源类别合格且原门全过的首 Tab 进入原 claim/置空/写入路径；普通注入和未知来源保持原键、正文与选区不变。
2. 原 callback 寿命门须覆盖下游链及全部写前重验。实际嵌套检索、MSG 被改、grant/字段切换或撤销须单调使外层失效；内层不能借外层来源结果或替换 grant。同一原 MSG 的标量比较防止本栈对象变化，不冒充 OS 唯一事件身份。尾键也按其自身实际回调取源，拒绝的注入/未知尾键不能伪造原按次已松开或恢复消费资格。未知后的 Quiet/退休沿原保守所有权处理。
3. **旧排队首键是独立的实现与验收项。** 保留 Prepared 期已观察输入的撤权；另构造安装前或 Arm 前排队、Arm 后才取出的原 Tab，验证不消费、不写入且后续 repeat 不重试。实现者须给出该切片能实际建立的新鲜性依据及失效路径，未建立时严格入口保持拒绝，不能因类别为 HARDWARE 就放行。`MSG.time` 可辅助拒旧但不是事件 ID；[KEYBDINPUT.time](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput) 可由发送者指定，不能仅用它与 Arm 时间比较宣称已解决。队列提示也不能代替原事件历史；不要求杜绝所有同用户恶意进程。
4. 接线复用现有 App/Broker/native 认证所有权与回执，不把测试专用 `allowInjectedInput` 作为正式策略。原受控注入的事务正例继续证明其原范围；新严格入口的同类输入须成为零写拒绝证据，二者明确区分，不能把全部拒绝称为正向接受通过。必要的私有 ABI 变更须同步所有消费者并拒绝旧版本；sent-route、按需 F9/F10 与正式能力保持原行为。
5. 验证跨实际 native 入口，不只测枚举函数：受控 API 边界覆盖来源类别、查询失败、首键/尾键、Prepared、旧键、实际嵌套调用和 MSG 改写；Windows 在同一严格入口检查普通 SendInput、PostMessage 的旁路与零写、原回执/Undo/Quiet 的邻近行为。Windows 实际类别可观测性已有 CI495 证据，不恢复其独立自动探针；控制 API 给出的 HARDWARE 只算策略阳性，不算硬件/UIAccess 实测。真实物理键盘、既有合法 UIAccess 工具及真实 IME 阳性须各自现场证据，不能为补测而新授 UIAccess 或改系统配置。

切片以最小来源门、相应回归和明确剩余边界交付；类别、旧键或本回调对应任一条件未知即零写拒绝，不能用延时、重发、宽松来源或较晚采样补成成功。如果实际 callback 不能提供可靠当前来源，或旧键/重入仍能误认，停止该候选的升级并保留具体反例，不开放生产，也不把机制未完成重新写成等待用户选择。独立的 Wake/F10 与其它已授权开发继续。

## 同一检索处的独立来源分类观察

`ObserveInputOriginAtRemovedMessageAndOuterReturn` 保留为显式、只读 F8 手动维护入口，用于即将评估的生产原始 MSG 来源检查。只有该来源边界实现、GCIMS 调用时点或平台契约变化时才手动验证，不作日常 CI 或默认 suite；既有完整观察已达到一次研究的停止条件。源 fixture 只装专用 WH_GETMESSAGE，不加载事务模块、不建 grant、不调用 claim/write、不改 MSG；父测试沿原 Desktop helper 保留版本校验与闲置 Broker，二者清理仍须证实。该入口不向 source 接受路径授予来源能力，既有 native 同字段尾键两行、owned 六行与生产默认关闭门不改。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveInputOriginAtRemovedMessageAndOuterReturn'
```

实际 SendInput 调用进程先用 TOKEN_QUERY 查询自己的 TokenUIAccess，成功、返回 DWORD 长度且值0后才允许包括准备点击在内的输入；失败或非0直接前置失败，不提权。一次准备和 arm 后冻结源身份，唯一三段为 F8 SendInput down/up、同字段 PostMessage down/up、F8 SendInput down/up。阶段间只读完成/失败属性，完成属性仅由原 F8 up 的实际 dialog/dispatch 返回发布；不使用带 F23 的 Press、重新 Focus 的 Arm、sent snapshot 或后置 marker。结束或失败时最多读取一次结果，失败不恢复焦点或重发首键。

真实 HC_ACTION/PM_REMOVE hook 入口，以及原 GetMessage 返回后首条调用的第一分支，先按本地 MSG 标量筛出 F8，再以 GetCurrentInputMessageSource 为第一项应用 OS 查询，之后才作焦点查询/Record/CallNext。完整有效运行期望六个 F8、十二次采样，缺消息不补足；其它 WM_APP/paint 不采来源。仅保留六个 F8 的原始 BOOL、device/origin enum、失败错误与完整入口/返回 MSG；失败输出不分类。BeforeRead 的局部检索序号、removed 数量和 hook 深度只关联此 fixture 的结构，不是 OS 事件 ID；多重/嵌套对应、额外输入、曾离焦、缺失或不成对均为 inconclusive，不用 time/vk 搜索或异步补采。

入口与外层返回独立解释：两段 SendInput 均为 Keyboard/INJECTED，夹层 posted 为来源不可识别时，记 `origin-class-transition-observed`；六条全为 Keyboard/INJECTED 只记 `sources-not-distinguished`，不能推为 stale；其它原始分类记 `unknown-or-unexpected-source`。观察完成不等于来源绑定成功，`productionReadiness`、`hardwarePositiveObserved`、`bindingProven` 永远 false。官方 [GCIMS](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcurrentinputmessagesource) 没有保证 hook/return 位置的逐事件身份，且 SendMessage 期间 device 可为不可用；[HARDWARE 类别含 UIAccess 软件](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ne-winuser-input_message_origin_id)，该兼容范围现已由用户明确允许，但分类观察仍不能代替正式来源门及其验收。Token 查询按 [DWORD 合同](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_information_class) 与 [TOKEN_QUERY 权限](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation) 进行。

CI495 的完整 Windows 原件包含六条消息、三个阶段和十二次成功采样；hook 入口与外层返回分别观察到 `INJECTED (device=1, origin=2) → UNAVAILABLE (0,0) → INJECTED (1,2)`，两处均为 `origin-class-transition-observed`。`firstExtraInput=null`，`unchanged`、`inputsPaired`、`hooksClosed`、`processesExited` 均为 true，三项生产/物理/绑定字段仍为 false。这只证明单个非 UIAccess 发送者与受控源泵中的有限 source class 可观测性，不证明不可伪造的 MSG 绑定、物理正控、UIAccess 信任或 IME 资格。CI494 的额外输入原因仍为 unknown，本次无额外输入不能解释旧失败，也不为追查它重跑。

手动入口沿原 Desktop30秒/单事实60秒期限，唯一结果写入 `artifacts/source-message-origin/<唯一目录>/result.json`，记录源、发送者、两个采样点、原输入配对、未变正文/选区/焦点及实际 hook/进程清理。缺件、额外结果、API 查询失败或不完整均不记完成；成功但来源未知/不可区分仍是有效诊断，不是产品能力通过，未知结果不自动触发重复研究。CI 已移除此入口的自动调用、专属 runner 和 artifact 白名单；`windows-source-keyboard` 只保留原 peek/forged/dialog 三例及独立结果完整性/清理校验，原条件、四分钟硬上限及普通 source 两例独立入口不变。

## 自有标准 Edit 的既有 TSF context 准入

`ObserveExistingContextOfSelfOwnedStockEdit` 是普通 xUnit 回归默认不运行的第一阶段必要条件检查，不是键盘或写支持。它复用 NativeFixture 的未 subclass stock Edit，但在独立 STA GUI 线程创建和运行；普通 fixture 的 MTA、消息泵和生产路径不改。仅查询该线程的 `TF_GetThreadMgr → GetFocus → GetTop → GetActiveView → GetWnd`，要求 HWND 恰为当前自有字段，并重核 canonical IUnknown 身份、原生焦点和本次焦点通知版本。不给 root、NULL、base 或 parent 作替代映射。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.TsfContextAdmissionProbe.ObserveExistingContextOfSelfOwnedStockEdit'
if ($LASTEXITCODE -ne 0) { throw '既有 TSF context 未准入或清理失败，保留结果，不自动重跑。' }
```

先用错误字段负控验证焦点拒绝，再做 A/B/A 三次真实控件焦点观察；任一必要条件缺失即保留原结果并失败，不继续扩展。没有 Broker、输入 Hook、软件注键、服务注册、profile 修改、TSF Activate、sink、textstore、edit session 或正文写入。只释放本次查询取得的 COM 引用，不 FinalRelease 共享 RCW；STA 上平衡 COM 初始化，正常关闭窗口后确认精确子进程退出，失败仍由原进程 owner 有界回收。宿主25秒定时关闭、线程29秒外限、测试操作30秒及60秒 xUnit 外限；执行器还须设置外层总截止。结果在 `artifacts/tsf-context-admission/<唯一目录>/result.json`，不覆盖失败；未实际执行的键路由、同步只读锁及真实 composition 明列 `not-tested`。

CI 在 Windows build 成功后、既有同步源实验之后，以独立 `continue-on-error: true` 研究步骤显式调用上述唯一方法，外限2分钟，不加入默认产品测试或扩大场景。既有 always artifact 保存该目录的 `runner.log`、`runner.json` 和唯一原始 `result.json`。runner 记录源码 SHA、原退出码、文件数量/缺失状态、原实验状态、退出/清理证据及观察数量；必要条件未满足单列 `prerequisite-not-met`，程序失败、缺失/重复结果和清理未确认分别保留。步骤的非零退出不是产品或模型否决，零退出也只代表既有 context 首门观察，不是键盘或写能力通过；尚在 `running` 或退出码为空的记录属于未完成。

[微软原文](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-tf_getthreadmgr)允许 `S_OK` 但 manager 为 NULL；[GetWnd](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontextview-getwnd)也不保证非 NULL 或精确字段 HWND。早期[微软 TSF 文章](https://learn.microsoft.com/en-us/archive/msdn-magazine/2007/july/speak-up-support-dictation-with-text-services-framework)说明普通 Edit 可以由系统提供 TSF 支持，故值得核验，但不是当前系统配置的成功保证。[CI473](https://github.com/DingJWuyu/Typelay/actions/runs/39934831201) 已确认本入口四次观察、错误字段负控及 A/B/A 精确 context/view、引用释放与正常进程退出；该结果仍不是键路由、读锁或写支持。Linux 托管编译不能补作 Windows COM 实测。

仅三次精确 context 观察及清理全通过，才进入下一独立阶段：在真实 context 上订阅观察 sink，始终 `pfEaten=false`，分别报告实际 test/key 回调；需要 client id 时明确记录 Activate 前后及同线程配对 Deactivate。同步只读锁、真实 composition/focus 失效、旧排队键、注入拒绝和第三方部署仍须各自证明。首门缺失、错误 view 或不能清理即停止这条具体条件下的后续实现，不用合成 context、改泵或注册服务补成成功；新路线须经 root 重新裁决，不推论 TSF 全部不可行。

### 独立被动键路由与同步选区读取

普通源接受仍缺少实际接收边界。首门已证明 CI 的自有 stock Edit 存在精确 context，因此下一有界实验只问：原泵下 Tab/End 的处理询问是否到达该 context，以及当场的一次同步只读请求是否真的能读取选区。日常发生频率仍未知；此实验能淘汰不成立的必要路径，不把机制结果转成产品能力。它是另一显式入口，不改变上述首门或默认产品测试：

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.TsfKeyRoutingProbe.ObservePassiveTabAndEndRoutingAndSynchronousSelectionRead'
```

每键使用独立自有进程、同一个未改造 stock Edit 和原 `GetMessage/IsDialogMessage/TranslateMessage/DispatchMessage` 路径。取得已有 manager/document/context/view 并核精确身份、HWND、线程、焦点与版本后，明确做一次 [Activate](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-activate) 取得 client ID，立即重核同一基线；这一步是状态变更，结果标记 `ActivatedForReadProbe`，不称纯查询。只通过原 context 的 [ITfSource](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfsource-advisesink) 安装自己的一个 sink，不注册服务、创建 context/textstore、手动调用键管理器或替换输入法。

所有 OnTest/OnKey 的 `pfEaten` 始终为 false。仅第一个指定键、非重复且身份稳定的 OnTestKeyDown 尝试一次 `TF_ES_SYNC | TF_ES_READ`；无异步回退或重试。[RequestEditSession](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession) 的外层 HRESULT、session HRESULT、实际同步进入/退出顺序分别保存；DoEditSession 用真实 cookie 执行 [GetSelection(default, 1)](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-getselection)，记录 HRESULT/fetched/range 存在及释放，不读取正文或改变选区。`SynchronousSessionObserved` 不单独证明可读，只有 `SelectionReadSucceeded` 才包含完整同步读取条件。每次 COM 回调/读取前后重新核身份，重入、读取中焦点变化或晚到 session 均拒绝。

自动化只对精确自有前台进程发送一次 SendInput down/up，**不证明物理来源或生产注入拒绝**。End 从位置1的 caret 起验证默认移动到单行末尾；Tab 验证默认转到 B。原泵 after-dispatch 仅记录实际 key-up 完成，不消费或补发消息。文本不变、零提交及子进程退出必须证明。Tab 正常导航后的 focusVersion 变化会关闭后继读取资格，但不撤销此前完整同步读取的记录；不要求 up 仍到 A 或 OnTest 恰好一次。[Context sink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfcontextkeyeventsink) 不接收输入法已过滤/preserved 的键；[TestKeyDown](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-testkeydown) 返回 false 后没有 OnKeyDown 不是失败。

`artifacts/tsf-key-routing/<唯一目录>/result.json` 分键保存开始/关闭原件、自动化默认效果和清理。`prerequisite-not-met`、`test-route-not-observed`、`route-observed-synchronous-lock-refused`、`synchronous-session-selection-unavailable`、`selection-read-observed` 是不同研究结果；仅无回调的 `TF_E_LOCKED` / `TF_E_SYNCHRONOUS` 及同步回调内的 `TF_E_NOSELECTION` 作为文档明确的读取阴性；未知 HRESULT、参数错误、无效 cookie、异步响应或成功却无同步回调不算正常拒锁。预期阴性不使实验程序失败，也绝不表示产品通过。异常、身份失守或清理未证单列 `probe-failed` 并非零退出。外层 `research-observation-complete-not-product-acceptance` 只说明两键研究已完整收尾，须继续检查各键状态，不能据退出0开启能力。

关闭发生在回调及 setup/cleanup COM 调用栈外，可重入关闭请求只延到原外层泵，先封准入，再 [Unadvise](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfsource-unadvisesink) 自己的 cookie、释放自己的引用并同 STA 平衡单次 Deactivate。解除未成功时保留 sink/COM 强引用，直接非零退出这个自有隔离进程，不先卸载仍可回调的对象，也不标记正常清理。每进程仍限25秒宿主、29秒线程、30秒操作，清理单独4秒正常退出及5秒精确进程回收；整体 xUnit 90秒，调用者需另设外限。

CI 在 Windows build 成功后、首门之后以独立 `continue-on-error: true`、2分钟外限调用这个唯一 selector；runner 记录 source SHA、退出码、唯一结果及两键各自 status/route/session/read，summary 逐键展示，不据 exit0 宣称准入。早期研究 artifact 白名单保存 runner.json/log 与每次唯一 result.json，末尾原全包亦保留该目录。缺文件、重复结果、清理未证和程序失败分列；超时留下 running/null exit 时属于未完成，不拼成成功。

[CI475](https://github.com/DingJWuyu/Typelay/actions/runs/39961358090) 已实测 Tab/End 的 `selection-read-observed`：各一次同步读取，`RequestStart < EditEntry < EditExit < RequestEnd`，原键默认效果、文本不变、零提交和正常清理/退出均成立。Tab 读完后的默认切焦标记 `FocusInvalidated=true`，不是读取中身份失守。这个入口继续保持所有 `pfEaten=false`；无路由、拒锁或准入失败仍是当前条件下的终态，不修改它制造成功。真实 composition、旧排队键隔离、物理输入、唯一消费、原子写与第三方部署仍未证明，生产 WriteSupported/Practice 许可链保持不变。

### 自有源键盘回调与 TSF 同步只读兼容性

当前普通源接受仍缺原键与文本事务的共同裁决点。Context sink 的占位/回退路径已被真实拒绝丢 Tab 否定，限制见[普通源接受问题](open-issues.md#正式-notepad-只有预览没有源接受能力)；这里改用已有源线程 WH_KEYBOARD 的独立原键旁路合同，而非继续诊断已否决的内部消息转换。最小实验只问该实际源回调栈能否同步读取精确 TSF context，同时保留原 Tab。日常频率未知，失败直接淘汰组合，成本限一个自有进程/一个按次，无新平台或控件矩阵。

在独占、已解锁的 Windows x64 测试桌面完成现有 Release 构建后，按需执行：

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.TsfKeyRoutingProbe.ObserveSourceKeyboardSynchronousSelectionRead'
```

`TsfKeyRoutingProbe.ObserveSourceKeyboardSynchronousSelectionRead` 是独立显式入口，只发送一次 Tab down/up。复用首门的真实 manager/context/view、单次 Activate 与同 STA 配平，但不订阅任何 context key sink、不调用键管理器。仅当前自有 GUI 线程的 WH_KEYBOARD、HC_ACTION、非 repeat down、无修饰键且精确身份稳定时，申请一次 `TF_ES_SYNC | TF_ES_READ`，以真实 cookie 做 GetSelection。NOREMOVE、up、未知/重入均零请求；所有分支把原 code/wParam/lParam 交给 CallNextHookEx，并原样返回其值，零消费、零正文写、零提交。

[KeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/keyboardproc)定义该旁路与消费合同；[RequestEditSession](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession)未保证任意 WH_KEYBOARD 栈可同步授锁，因此这是实际栈兼容性试验，不是官方通用写支持。阳性须满足 `hook-entry < request < edit-entry < edit-exit < request-end < hook-exit`、同线程/精确 context/view/焦点版本保持至下游 hook 返回、成功读取与 range 释放，且原 Tab 自然 A→B、原文与 A 选区保持、原 key-up 和正常退出可观察。拒锁、身份未知或原键不保留均停止该组合，不异步补偿、不重试。

回调全栈（含 CallNextHookEx）沿原外层关闭门防重入，先关闭资格、解除自己的 hook，再释放 COM/平衡 Activate；卸载失败保留 callback 与引用，并沿 owned 进程 fail-stop，不宣称清理成功。保留25秒宿主、29秒线程、30秒操作及4秒正常清理/5秒精确回收外限；显式 xUnit 60秒。该手动入口保留，结果在 `artifacts/tsf-source-key-read/<唯一目录>/result.json`，逐项保存 route/session/read、原键效果和退出；exit0只说明研究结果完整。该入口不在自动 CI 中重复运行；只有新的真实 composition 验证条件或明确回归目的才手动执行，原75分钟 job与模型预算不增。

源 hook 的同栈读取、原 Tab 与清理已在 [CI477](https://github.com/DingJWuyu/Typelay/actions/runs/39990193024) 获得阳性；Linux Compile/格式不代替这项 Windows 实测。旧排队键、注入身份、真实 IME、RW/原子写和第三方部署仍未证明。跨进程 hook 需要位数匹配且真实位于目标 GUI 线程的原生 DLL及另外授权，不能由当前自有线程实验推导普通 Notepad 支持。

### 同次源读取中的 composition 三态与输入配置库存

[CI478](https://github.com/DingJWuyu/Typelay/actions/runs/39997840353) 已在原 selector、同一 Tab 和同一次 `SYNC | READ` 中观察 `none-in-exact-context-at-read`，Query/Find 为 S_OK，Next 为 S_FALSE、fetched=0；同栈读取、原 Tab 与正常退出均成立。这不是实际 IME composition 正例。该显式只读入口继续保留以下观察，不新增锁请求。在真实 `DoEditSession` cookie 下查询精确 context 的 `ITfContextComposition.FindComposition(cookie, null)`，再只取 `Next(1)`。NULL range 覆盖该 context 的全部组合，不仅是当前选区；不读取组合文字，不调用 StartComposition、TerminateComposition 或其它写入口。官方合同见 [FindComposition](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontextcomposition-findcomposition) 与 [Next](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-ienumitfcompositionview-next)。

结果严格分为 `present`、`none-in-exact-context-at-read`、`unknown`。有组合需要成功取得一个真实 composition view；无组合仅在新枚举 S_FALSE、fetched=0、空 view，且原读锁、精确身份、回调与释放全部成立时给出。接口不可用、拒锁、异常输出、重入或身份失效不能算无组合；不确定所有权不能算清理成功。该快照不能替代生产 ImeState.Clear，也未证明跨时段的组合开始/结束或配置切换失效。

同一自有 STA 在 hook 外仅以 `CoCreateInstance(CLSID_TF_InputProcessorProfiles)` 读取 [GetActiveProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-getactiveprofile) 和有界 [EnumProfiles(0)](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-enumprofiles) 库存，最多64项，只保存 ID、状态与 HRESULT；截断/失败为未知，不推断已安装或可用。没有安装、启用、切换 profile、持久设置修改或第三方加载。库存不等于实际 IME 可组合，空枚举也不构成真实 IME 正例。本阶段固定 `realImePositive=positive-not-run`；真实用户 IME 的组合开始/取消须另有明确环境和有界自有场景后验证，不能自造 composition 求覆盖。

所有 enum/view 在原 STA 释放，保留当前截止、原键转发和退出要求。只读门不可用或未知即保持不准入，真实正例环境未证时止于观察/契约准备，不扩 fakeIME 矩阵；Linux 静态编译不代替 Windows 实测。手动结果与库存保存在既有 source-key-read 目录，生产能力、模型及 job 预算不变。


### TSF 正文来源约束

[CI481](https://github.com/DingJWuyu/Typelay/actions/runs/40010604024) 已取得同栈 RW 与读取成功，但既有 top context 返回空正文、选区 `[0,0]`，原生 stock Edit 快照为6个 UTF-16码元、选区 `[3,5]`。完整正文 equality 因此正确地在修改前拒绝：零 SetText，原生正文/选区保持，原 Tab 后焦点为 B、提交数为0，正常清理与退出已证。成功写入、Undo、提交正控及其余三例未验证，不能把安全拒绝改称写入通过。

该 context 尚未证明是原生控件的完整正文来源，故已删除专属全文写入 fixture、四例测试与自动 CI 步骤，不再为此追加元数据探针，也不恢复已证明的 READ 自动运行来填补预算。相同 view HWND 不能代替正文绑定；这不是所有 stock Edit 或 TSF 都不支持的结论，见[输入后端的安全准入](architecture.md#输入后端的安全准入)。既有六项 native 同步回归及上面的手动 READ/composition 入口各自保留，普通生产接受继续关闭。

## Hy 生产载荷候选构建

`./dev.ps1 hy-runtime-prepare` 显式获取并完整校验固定 b11717 官方 Windows CPU ZIP，不下载 GGUF。已有缓存仍校验全部 SHA；CPU 闭包只包含固定依赖、官方 CPU variants 与许可证。

`./dev.ps1 pack -HyRuntime artifacts/hy-production-runtime`（或 `run`）从已核 CPU 目录构建并打包 ABI5 生产 DLL、五项导出、固定锁和 production-adapter 清单；不启用 fixture 的 audit 宏，不携带源码、原 ZIP 或模型权重。普通 `pack/run` 未传该参数仍生成 Argos-only 包。只准备参数或 CPU 缓存不能证明发行包已含 Hy，正式候选验收须对这个实际打包路径运行搬迁及加载检查。

Windows 开发候选在包内生产组件齐备时提供 Hy 准备入口；组件存在不代表模型已验证或就绪。模型包事务已接通精确库存、确认时冻结的代次、离线选择/非当前包移除和失败恢复；旧 runtime 缺少可校验清单时，相关新包操作要求先修复，不暗中联网。正常启动旧 schema1/2 不增加这一前置。代使用锁阻止删除仍在另一会话中使用的模型，清理失败会保留副本并提示空间未回收。设置与快捷面板的语言列表取当前已加载方向，切换后保留原语言偏好并标明不可用值。[CI507](https://github.com/DingJWuyu/Typelay/actions/runs/40092766109) 已通过真实 Windows ABI5 生产加载、五阶段生产检查与 15 项 WPF 设置场景，覆盖安装、选择、明确确认的中英方向补充、切换、移除及正常退出；14 方向冷启和中英普通源双向预览亦通过。这些生命周期与普通预览结果不代表质量放行；高级 scope 尚未实机验收，中英风格/术语能力门仍关闭，跨旧版本恢复仍待验证。新装默认十二方向与原产品权限不变。

正式候选 CI 使用 `hy-product-prepare` 在有界准备阶段校验固定 CPU 与 GGUF 缓存，再由唯一一次 `pack -HyRuntime artifacts/hy-runtime` 生成实际发行包。生产前置通过真实模型进程所有者检查加载与退出，随后在真实设置窗口验证下载、切换、中法预览和移除。权重仅进入缓存和临时安装目录，不上传测试 artifact。手动 `hy-adapter-e2e` 的隔离审计仍可检查字面 token，不作为正式发行验收的替代。

完整 Windows 检查会在普通回归之外独立运行上述源线程前三项显式探针，外部总上限为 4 分钟，并将 `artifacts/source-keyboard-boundary/` 纳入既有始终上传的结果。各数据行自身 60 秒上限不变；诊断完成不构成生产写回验收。
