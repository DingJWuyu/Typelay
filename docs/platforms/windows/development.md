# Windows 开发与分发

本页用于构建 Windows 客户端、生成可搬迁的发行包，以及运行已有的受限机制实验。日常开发先走根目录 `dev.ps1`；只有具体输入边界需要取证时，才使用后半页的显式研究入口。

共享依赖与检查选择见[共享开发流程](../../engineering/development.md)。当前实现见[Windows 架构](architecture.md)，产品验收见[Windows 测试](testing.md)。本页的命令说明不表示当前源码已在 Windows 桌面执行。

## 任务入口

| 开发任务 | 入口与交付物 |
| --- | --- |
| 修改 Windows 代码并运行检查 | [准备与执行](#准备与执行)列出环境和根 `dev.ps1` 命令。 |
| 构建可搬迁候选 | [分发和注册](#分发和注册的验证条件)定义实际 ZIP、组件配套与注册回归条件。 |
| 准备原生推理载荷 | [Index](#index-默认载荷与独立检查)与 [Hy](#hy-生产载荷候选构建)分别管理固定载荷和生产消费者。 |
| 修改源接受机制 | 先读[架构中的有序源接受](architecture.md#有序源接受怎样保留原按次)，再选择[真实 Runtime 受控组装](#真实-runtime-的受控组装)或[正向接受入口](#正向接受的剩余条件)。 |
| 核对具体输入机制假设 | [机制实验](#运行机制实验前确认范围)保留各入口的目的、停止条件和证据限制。 |
| 研究浏览器窗口身份 | [独立身份探针](browser-identity.md)仅在新线索满足其准入条件时运行，不进入日常检查。 |
| 准备 Chrome 商店材料 | [商店发布准备](chrome-store-release.md)使用已核实际候选，创建条目与发布另需明确授权。 |

## 准备与执行

使用 Windows x64、PowerShell 7.2+、[global.json](../../../global.json)指定的 .NET SDK、Python 3.13，以及 LLVM clang/lld-link。先创建并激活工程 Python 虚拟环境，再安装固定检查工具和构建：

```powershell
git clone --branch trunk https://github.com/DingJWuyu/Typelay.git
cd Typelay
python -m venv artifacts/build-tools
.\artifacts\build-tools\Scripts\Activate.ps1
python -m pip install -r eng/requirements.txt
./dev.ps1 check
./dev.ps1 run
```

之后每次打开新的开发终端，先在仓库根目录激活同一环境。构建脚本会使用当前 PATH 中的 `python`；这个工程环境不替代产品自行准备的模型环境。

| 命令 | 执行范围 |
| --- | --- |
| `./dev.ps1 build` | 准备字体，锁定依赖还原，构建 solution，并把警告作为错误。 |
| `./dev.ps1 test` | 构建后运行托管合同与 worker 测试。 |
| `./dev.ps1 check` | 执行上述测试，再检查 .NET 格式、Ruff lint 与 Python 格式。 |
| `./dev.ps1 shared-check` | 运行可移植共享检查，不启动 Windows 桌面或安装模型。 |
| `./dev.ps1 format` | 修正格式和可自动修复的 lint。 |
| `./dev.ps1 run` | 生成开发目录并启动真实客户端。 |
| `./dev.ps1 pack` | 生成自包含 ZIP 和摘要，再运行搬迁分发与进程回归。 |
| `./dev.ps1 e2e` | 对已有产品 ZIP 运行分发与进程回归。 |
| `./dev.ps1 native-e2e` | 在已解锁桌面验证受控 Win32 适配器。 |

Windows 代码变更先取得相关基线。缺陷修复沿实际用户路径复现，再按[测试约定](../../../tests/AGENTS.md)保留回归。完成变更后运行格式与相关检查；发布路径、启动、资源或进程边界改变时，增加 `pack` 验证。

`check` 的 App 测试需要交互式 Windows，会管理自己的测试控件，不下载模型。`run` 启动真实客户端，模型调试须经正式准备流程。`pack/run` 可以获取固定 CPU 运行库，模型权重仍由用户确认后安装。

根 [dev.ps1](../../../dev.ps1)转交到[平台脚本](../../../eng/windows/dev.ps1)。原生入口和输入 DLL 分别由 [build-launcher.py](../../../eng/windows/build-launcher.py)与 [build-native-input.py](../../../eng/windows/build-native-input.py)构建。新增或拆分 KeyboardHook 文件时，还要检查 [NativeFixture 项目](../../../tests/windows/fixtures/TypeLay.NativeFixture/TypeLay.NativeFixture.csproj)的显式 Compile 链接；App 或 App.Tests 编译通过不能替代这个独立消费者。

### 分发和注册的验证条件

`pack` 发布 App、Broker、Practice、BrowserHost 与明确列出的 Chrome 资源。自包含包位于 `artifacts/TypeLay-Windows-preview.zip`，开发目录位于 `artifacts/dev/TypeLay`。完整目录布局属于分发合同，不能只搬可执行文件。

默认搬迁检查验证 BrowserHost 依赖和非法入口拒绝，不注册扩展。只有独占测试账户明确设置 `TYPELAY_BROWSER_REGISTRATION_E2E=1`，才运行真实注册维护回归。起点要求 TypeLay 数据目录与 HKCU/HKLM 两视图同名 host 均不存在；冲突直接失败，清理只删除本次精确拥有的注册和文件。这个测试开关不构成用户本机的接入同意。

## Index 默认载荷与独立检查

当前 `pack/run` 默认包含 Index ABI1/b11913 CPU 载荷。未传 `-IndexRuntime` 时，打包脚本在显式构建阶段准备固定官方 CPU archive。已有目录仍由构建器校验。也可先准备，再传入目录：

```powershell
python eng/windows/index/prepare_runtime.py --cache artifacts/index-runtime-download --output artifacts/index-runtime
./dev.ps1 pack -IndexRuntime artifacts/index-runtime
```

独立 `index-windows` CI 使用实际发行包，通过 DesktopFixture 的 `--verify-index-production` 调用正式 ModelInstaller、ModelClient 与 ModelChildProcess。它验证载荷身份、默认准备与激活、连续推理、冷启、失败及取消保留旧代、Repair、Argos 安装、离线切换、移除和自有进程退出。

生命周期上限为 600 秒，清理上限为 7 秒。下载、构建分别有界，不重试模型。模型来自固定官方来源，不进入 GitHub cache 或上传 artifact；只上传结构化结果。未执行阶段保留 `not-run`。这个非 UI 检查不能替代设置、普通源、IME 或语言质量验收。

## Hy 生产载荷候选构建

Hy 使用独立 b11717/ABI5 目录和锁，不与 Index 混用。只准备 CPU 载荷时运行：

```powershell
./dev.ps1 hy-runtime-prepare
./dev.ps1 pack -HyRuntime artifacts/hy-production-runtime
```

`hy-runtime-prepare` 显式下载并完整校验固定官方 Windows CPU ZIP，不下载 GGUF。第二条命令从已核目录构建生产 DLL、导出、固定锁与 production-adapter 清单，不启用 fixture audit 宏，也不携带源码、原 ZIP 或模型权重。普通 `pack/run` 未传 Hy 参数时不包含 Hy。

正式候选 CI 用 `hy-product-prepare` 校验固定 CPU 与模型缓存，再由唯一一次 `pack -HyRuntime artifacts/hy-runtime` 生成实际包。生产检查通过真实模型进程所有者验证加载与退出，再由真实设置窗口验证安装、切换、预览和移除。单独准备缓存不能证明最终 ZIP 已包含或能加载 Hy。

Hy 新装仍为十二方向。中英补充必须由用户明确确认；修复、选择和冷启不自动补方向。包事务保留旧安装与选择，使用锁阻止清理仍在用的代。安装、模型就绪与质量放行分别判断，验收入口见[Hy 验证](testing.md#可选-hy-隔离-adapter-验证)。

## 运行机制实验前确认范围

以下入口解决具体 Windows 输入机制问题。它们要求独占、已解锁的 Windows x64 桌面和已完成的 Release 构建，结果进入被忽略的 `artifacts/`。每次保留首错、原退出码和清理证据；失败后不补发输入、不延长产品期限，也不自动重跑。

实验退出 0 只表示取得该入口定义的完整结果。`not_observed`、拒绝或来源未知可以是有效研究结果，均不表示可接受译文。外部执行器仍须约束进程总寿命，处理无法合作取消的系统调用。[check.yml](../../../.github/workflows/check.yml)保留的独立研究步骤不属于普通产品通过数，`continue-on-error` 只让其它独立工作继续。

### 源接受低级回调超时诊断

此入口验证 B-01 的具体交错：源已 Claim 并写入，而低级 Hook 尚未返回。最低系统为 Windows 10 1709。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.NativeSourceTimeoutProbe.RecordClaimThenLowLevelCallbackTimeout'
```

[诊断实现](../../../tests/windows/TypeLay.App.Tests/NativeSourceTimeoutProbe.cs)只有正常与暂停两组。暂停组在真实 Claim 后阻塞自己的低级回调至少 1600ms；独立 observer、原源队列和尾标记记录原键是否到达。结果位于 `artifacts/source-ll-timeout/<唯一目录>/result.json`。

`observed_leak` 表示原 down/up 泄漏，`not_observed` 只表示本次没有看到，`precondition_failed` 表示前置或清理不完整。整例上限 150 秒，CI 外限 3 分钟。既有 Windows 原件已观察到写入后原 Tab down 泄漏，[B-01](architecture.md#b-01非原子写回与按键所有权)继续保留该否定结论。

### 自有源线程键盘边界诊断

此入口只观察自有源线程的 `WH_KEYBOARD` 和原消息泵，不写正文或安装低级 Hook。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveSelfOwnedSourceKeyboardBoundary'
```

三个场景分别验证 PM_NOREMOVE 不消费、posted/sent 伪消息不能取得 Hook 权威，以及 Tab 在 IsDialogMessage 前消费时的导航差异。每例使用原 30 秒操作预算与 60 秒 xUnit 上限，结果位于 `artifacts/source-keyboard-boundary/<唯一目录>/result.json`，CI 外限 4 分钟。

`mechanism_observed_not_product_acceptance` 只说明局部机制被观察。它不能证明物理来源、LL 与 MSG 的唯一关联、跨线程尾键或完整接受。posted handler、回调末尾和队列提示也不证明最外层检索已经返回。

### 自有源线程同步正文事务实验

此入口在自有 stock Edit 的真实源线程回调中执行生产 `OwnedPracticeField.TryExecute`，研究原键消费与同步事务能否共存。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveSelfOwnedSynchronousAcceptance'
```

六个场景覆盖正常一次写入、NOREMOVE 零写、正文冲突、期限失效、错误 cookie，以及 accepted 后的 1200ms 有界暂停。原期限内实际探测 HIMC/HKL，探测失败拒绝，不能用零值补 Clear。结果记录调用、实际正文变化、回执、原键派发、焦点、选区、Undo 与清理，保存在 `artifacts/source-synchronous-acceptance/<唯一目录>/result.json`。

计划是固定的合成研究计划，不是正式控制器许可。每行 30 秒操作预算和 60 秒 xUnit 上限保持，`productionReadiness=false`。离散 HIMC/HKL 样本不证明 TSF profile 连续性、真实组合态、第三方部署或任意末期重入安全。

### 普通 source 事务与同一 MSG 的受控接线

此入口直接验证普通 SourceInstallation 在同一实际 `PM_REMOVE` MSG 上认领、置空并执行事务，使用受控软件输入。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveOrdinarySourceMessageAcceptance'
```

[两个真实场景](../../../tests/windows/TypeLay.App.Tests/SourceKeyboardBoundaryProbe.Message.cs)分别要求一次正文替换及 Undo，或在源文变化后零写并让原 Tab 正常导航。正例先用两次 NOREMOVE 确认不消费，再在 REMOVE 后观察同一个 MSG。源端负责真实 repeat/up；尾键未结束时，Retire 不能报告 Quiet，提前 Dispose 不能拆 hook。

后端在原精确 binding 中成对安装 CALLWNDPROC 与 GETMESSAGE，并共享同一个已加载模块。安装或回滚未知时保留句柄与模块，不回退旧激活。精确 Quiet 后按顺序拆 hook；在途 callback 仍可能存在，本构造保守持有 DLL 到原进程确认退出，不承诺第三方热卸载。

source wire v7 明确拒绝旧版本，普通 Transaction v2 保持。Prepared 安装后，App 先发布本地撤权和 capture fence，再 Arm，原 100ms 不续期。完成回执只由精确 poll/retire 结果产生，正文终态与资源 Quiet 分开；源端已写时 Runtime 只结算，不再 WriteAsync。错误 ACK 或超时不能充当未写证明。

[既有 CI504](https://github.com/DingJWuyu/Typelay/actions/runs/40083637614)在真实 Notepad 的隔离构造验证了接受、撤回、Undo、零二次写与清理。那条非有序机械路线不具备当前严格来源证明。结果在 `artifacts/source-message-acceptance/<唯一目录>/result.json`；两行沿原 30 秒操作、60 秒单项及 CI 2 分钟外限执行。

### 隔离严格来源拒绝入口

`TypeLaySourceStrictGetMessageProc` 是保留的拒绝构造。它在实际 MSG 上先查来源，普通注入、System、Unknown 或查询失败给出 `OriginUntrusted`；类别合格但缺少有序历史时给出 `QueueFreshnessUnproven`。两者都在正文变化前拒绝，原 MSG 和选区保持，grant 单调撤销。

[StrictSourceOriginLeavesSyntheticTabUnconsumed](../../../tests/windows/TypeLay.App.Tests/SourceKeyboardBoundaryProbe.Message.cs)用 SendInput/PostMessage 检查零写和原键派发，结果位于 `artifacts/source-origin-rejection/`。这个入口不提供正向接受。`MSG.time` 或空队列也不能单独排除已被上游移出的旧键。当前有序路线的来源合同另见[输入来源资格](architecture.md#输入来源资格)。

### 隔离有序LL见证与严格源接受候选

本节保留原有锚点，当前源码已经把有序路线接入正式 factory。旧推荐包仍按自身结果解释，不能因源码接入而更新下载包能力。

有序路线复用现有 LL 线程和每源寿命 FIFO，不新增全局 Hook、线程、输入重发或源泵改造。休眠映射、源 attach ACK、recording-active 与 Arm 的顺序，以及原 MSG 的来源和队首检查，统一见[有序源接受](architecture.md#有序源接受怎样保留原按次)。

当前受控 native API 回归直接编译生产 C，验证初始化、首键、尾键、旧历史、乱序、重入、IME、期限和注入拒绝。它证明算法在受控系统响应下的合同，不能替代 Windows、物理或 UIAccess 输入。Windows 的普通注入负例另外要求真实 LL 记录、原键派发、正文不变、零写与精确退休。

#### 隔离 Ctrl+F1–F24 候选

Ctrl 观察使用显式 `ObserveControlPrefix`，观察本身不能执行计划或显示接受就绪。原 Ctrl 正常派发并撤旧许可，功能键接受要求 Ctrl 后新捕获、新计划与新安装。旧在途读只可暂保人工文字，不能给 Ctrl 后接受授权。

[Modifiers 回归](../../../tests/windows/TypeLay.App.Tests/NativeSourceControllerTests.Modifiers.cs)保留 fresh、旧读、正文、选区、配置与过早 Fn 的普通注入负例，以及显式物理 Ctrl+F7 人工稿入口。两种释放顺序、repeat、F10 SYS 形态和旧前缀拒绝由生产 C 边界覆盖。既有 Windows 软件检查不代表物理 Ctrl、UIAccess 或真实组合态已通过。

#### 真实 runtime 的受控组装

[WindowsRuntime.CreateSession](../../../src/windows/TypeLay.App/Windows/WindowsRuntime.cs)和 [Broker/Main](../../../src/windows/TypeLay.Broker/Program.cs)固定选择有序源路线。逐捕获 eligibility 只为计划建立资格；缺失、错目标、旧观察、错误版本、Direct、Wake 或 owned 不能借此获得普通源接受能力。

[Ordered 回归](../../../tests/windows/TypeLay.App.Tests/NativeSourceControllerTests.Ordered.cs)用真实 factory、Router、Dispatcher、WPF、认证 Broker 与源 DLL。固定译文仅替代模型。Tab/F7 普通注入负例须先观察同一有效许可的真实接受提示，再发送唯一键对，核 `RejectedBeforeMutation/Unsupported`、`OriginUntrusted`、invoked=0、原键派发、零正文写和同 owner Quiet。

本轮重构只完成相关构建与受控验证，当前 Windows GUI 未运行。旧 CI524 原套件的注入负例通过，但搬迁 Tab 曾在发键前到期；后来的包装职责调整没有把该旧失败改判为通过。当前推荐物理包仅采用下节明确的搬迁检查，完整证据状态以[开发状态](../../engineering/development-state.md)为准。

### 正向接受的剩余条件

现有人工入口 `ManualPhysicalAcceptanceKeyUsesOrderedSourceOnceAndSettlesItsOriginalTails` 每行只接收一次指定物理键。阳性要求恰一次 Verified、零原键派发、零 LL Activate 或二次写、原生 Undo、尾键退休与自有退出。每 grant 100ms 和 fixture 30 秒不变，失败不能补按或续期。

物理 Tab、F7/F24、Ctrl+F7、UIAccess、真实输入法与正式外部应用部署各有独立范围。普通注入拒绝、受控 C 阳性或隔离物理用例不能相互替代。当前正式接线未形成这些完整证据，B-01 不能据此关闭。

#### 无 SDK 的隔离物理 Tab 验证包

[package-physical-tab.py](../../../eng/windows/package-physical-tab.py)保留 App.Tests、NativeFixture、Broker 三个 Release 输出树和锁定依赖许可，生成独立验证 ZIP。使用者需要 .NET 10 Desktop Runtime x64、同一 x64 `dotnet`、独占已解锁桌面和非管理员 PowerShell，不需要 SDK、Python 或模型。

打包前要求本轮原 App TRX 中 Tab/F7 注入负例均 Passed。候选 ZIP 再解压到 checkout 外的中文空格路径，发现唯一未跳过的 `ManualPhysicalKey=Tab` 行，并运行缺 eligibility 的 false/true 两个 factory 负例。必须恰好两项执行并通过，无跳过或 runner 错误，且自有资源清理已确认。

搬迁检查只证明加载与缺资格时零安装、零提示、零写，不代替原 source-install 或注入拒绝。没有原套件测试时的逐字快照，不能声称最终包与原套件运行字节完全相同。失败只保留原始发现、TRX 与输出，不产出可执行验证包。

包身份集中在[使用说明](README.md#隔离物理-tab-验证包不是产品体验包)。用户按[包内说明](../../../packaging/windows/physical-tab/README.md)亲手执行一次 Tab，必须取得一项执行、一项通过、零跳过。当前包交付尚无物理阳性，不能作为产品体验包。

### 同一检索处的独立来源分类观察

此只读 F8 入口仅在来源边界实现、调用时点或平台合同改变时手动使用，既有研究已达到停止条件，不加入日常 CI。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.SourceKeyboardBoundaryProbe.ObserveInputOriginAtRemovedMessageAndOuterReturn'
```

[实现](../../../tests/windows/TypeLay.App.Tests/SourceKeyboardBoundaryProbe.Origin.cs)先确认实际 SendInput 调用者的 TokenUIAccess=0，再执行 SendInput、PostMessage、SendInput 三段 F8 键对。源 WH_GETMESSAGE 与原 GetMessage 返回点分别采来源，六条消息、十二次采样必须完整配对；额外输入、离焦、缺件或嵌套歧义均不能补采。

结果位于 `artifacts/source-message-origin/<唯一目录>/result.json`，沿原 30 秒操作与 60 秒 xUnit 上限。两处来源独立解释，`origin-class-transition-observed` 仅说明类别变化可观察；`sources-not-distinguished` 与未知类别也是有限研究结果。`productionReadiness`、`hardwarePositiveObserved`、`bindingProven` 均保持 false。

### 自有标准 Edit 的既有 TSF context 准入

此入口先确认未改造 stock Edit 是否有精确的现成 Text Services Framework（TSF）上下文，不测试写入。

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.TsfContextAdmissionProbe.ObserveExistingContextOfSelfOwnedStockEdit'
```

[实验](../../../tests/windows/TypeLay.App.Tests/TsfContextAdmissionProbe.cs)只查询原 STA 的 manager、document、top context、active view 与 HWND，先验证错误字段拒绝，再观察 A/B/A。必须重核 COM 身份、精确字段、焦点和版本，不接受 NULL、root、base 或 parent 替代。它不 Activate、注册服务、安装 sink、创建 textstore 或读写正文。

三次精确观察和清理都成立后，才可进入独立读锁研究。结果位于 `artifacts/tsf-context-admission/<唯一目录>/result.json`。宿主 25 秒、线程 29 秒、操作 30 秒、xUnit 60 秒，CI 外限 2 分钟。[既有 CI473](https://github.com/DingJWuyu/Typelay/actions/runs/39934831201)取得该首门结果，不证明键盘、组合态或写能力。

#### 独立被动键路由与同步选区读取

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.TsfKeyRoutingProbe.ObservePassiveTabAndEndRoutingAndSynchronousSelectionRead'
```

每键使用独立自有进程和原消息泵。取得精确 context 后显式 Activate 一次，结果标记 `ActivatedForReadProbe`。只安装自己的 context sink，所有 `pfEaten=false`；首个合格 OnTestKeyDown 只申请一次 `TF_ES_SYNC | TF_ES_READ`，以真实 cookie 读取选区，不读正文或改变选区。

必须分别记录外层 HRESULT、session HRESULT、实际同步进入/退出与选区结果。`SynchronousSessionObserved` 不等于 `SelectionReadSucceeded`。原 Tab 应自然切焦，End 应正常移动光标，正文不变、零提交且退出已证。默认效果改变、身份失效、异常 HRESULT、异步响应或清理未知都不能算正常拒锁。

结果在 `artifacts/tsf-key-routing/<唯一目录>/result.json`。无路由、拒锁、选区不可得与读取成功分别报告，预期阴性不自动成为程序失败，也不成为产品通过。xUnit 上限 90 秒，CI 外限 2 分钟。关闭在 COM 回调栈外执行，先封准入，再解除自己的 sink、释放引用并同 STA 配平 Deactivate；解除失败时保留引用并让自有进程失败退出。

#### 自有源键盘回调与 TSF 同步只读兼容性

```powershell
& ./tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe `
  -explicit only -method 'TypeLay.App.Tests.TsfKeyRoutingProbe.ObserveSourceKeyboardSynchronousSelectionRead'
```

此入口只在原源线程 WH_KEYBOARD 的合格首次 Tab 回调中申请一次同步只读锁，不安装 context key sink。所有分支把原参数交给 CallNextHookEx，并原样返回，零消费、零正文写、零提交。阳性要求读锁完整位于 Hook 栈内，且下游返回前身份稳定、原 Tab 正常 A 到 B、原文与选区保持、range 释放和退出均可观察。

拒锁、未知或原键不保留即停止，不异步补偿或重试。结果在 `artifacts/tsf-source-key-read/<唯一目录>/result.json`，xUnit 上限 60 秒；宿主、线程与操作仍为 25/29/30 秒，另有 4 秒正常退出与 5 秒精确回收。该入口只按明确回归目的手动运行。[既有 CI477](https://github.com/DingJWuyu/Typelay/actions/runs/39990193024)证明了该自有栈的读取兼容性，未证明跨进程部署。

#### 同次源读取中的 composition 三态与输入配置库存

同一个 source-key-read 入口在原读锁中查询精确 context 的 `FindComposition(cookie, null)`，只取一个 composition view，不新增锁请求或读取组合文字。结果只分 `present`、`none-in-exact-context-at-read`、`unknown`。缺接口、异常、拒锁、重入或身份变化不能变成无组合。

Hook 外仅查询输入 profile 库存，最多 64 项，保存 ID、状态和 HRESULT。它不安装、启用或切换 profile；库存不证明实际 IME 可组合。当前 `realImePositive=positive-not-run`，既有空枚举观察不能替代生产 Clear 或真实用户的组合开始、取消和提交。

#### TSF 正文来源约束

[CI481](https://github.com/DingJWuyu/Typelay/actions/runs/40010604024)中，同栈 RW 获锁后，top context 正文为空、选区为 `[0,0]`，原生 Edit 却有 6 个 UTF-16 码元、选区 `[3,5]`。完整正文 equality 在修改前正确拒绝，零 SetText，原文和选区保持，原 Tab 正常导航。

这个 context 未证明是原生控件的完整正文来源，专属全文写入 fixture 和自动步骤已删除。保留上述只读入口与 native 回归，不能改读其它 context 或追加探针来把原失败改成通过。成功写入、Undo、真实 composition 与第三方部署仍未由该实验验证；这也不等于全部 TSF 方案不可行。
