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
