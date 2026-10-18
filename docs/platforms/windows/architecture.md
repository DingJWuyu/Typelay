# Windows 宿主与输入边界

共享规则与宿主端口见[工程架构](../../engineering/architecture.md)。以下内容仅属于 src/windows/ 下的 Windows 实现，不作为其它平台必须实现的协议或通用约束。

## 生产模块与平台内共享

TypeLay.Windows.Contracts 引用 Core，拥有 App 与 Broker 共用的 DTO、协议版本、WindowsTarget 编解码，以及 Windows 浏览器原生桥的私有握手合同。它可在普通 .NET 下测试，但其职责仍属于 Windows。

`TypeLay.App` 是 Windows 宿主。Windows/WindowsRuntime 组装共享运行时与 WPF 视图；WindowsInputService 把类型化输入操作转换为 Broker 请求，持有 HWND、焦点恢复和 IPC 期限。KeyboardHook/OnDemandHotkeys、Job Object、注册表启动项属于 Windows。UI 保持 WPF 展示与编辑，不直接写源字段。Infrastructure 的 AppPaths、ModelInstaller、SingleInstance 仍属于当前 Windows 分发/安装，不把 Windows Python wheel 选择冒充共享安装器。

`TypeLay.Broker` 引用 Core/Protocol/Windows.Contracts，在单一 MTA 线程上拥有 UIA COM 对象。Input 的 UiaInputProvider 不按应用白名单定义普通输入支持；Interop 只封装平台调用。生产入口是 Program 和标准输入输出管道，而不是 fixtures。TypeLay.NativeInput 的系统 Edit 协调写入仍是独立、受限的 Windows x64 路径。

`TypeLay.Launcher` 是 Windows 原生入口。发布时从 C 源码编译，调用发行目录 bootstrap/start.ps1；它不包含 SDK 下载和源码编译功能。

## 浏览器合作输入

Windows 浏览器适配通过 `WindowsInputRouter` 接入同一个 SessionController，页面实现来自 `src/shared/browser/`；平台只拥有 native-messaging 注册、传输、Hook 和视图路由。`TypeLay.BrowserHost` 由 Chrome 启动，双方按命名管道的实际进程核同用户、会话、发行路径和寿命，再核登记的精确扩展 origin。浏览器进程身份只筛选连接，字段授权仍由同一文档端口、目标、元数据、输入法与内容版本证明，不能由 PID 或最终前台窗口推导。标准 HTTP(S) 顶层 input/textarea 以外不开放此适配，失败不转 UIA 写入。

Windows 浏览器按需命令由精确连接、文档、前台根窗口与配置代绑定的单一消费者处理。进入页面消费前须确认两个全局热键均注销且旧按键组已释放，页面按有序事件回执启用；退出时须收到精确退役回执，或等所有已发出归属租约到期及旧按键组释放，才恢复 native 热键。800ms 归属租约及 200ms 准备预算不延长字段读取权限。字段策略批准仍只有原 150ms，正文输入只撤旧快照；敏感属性、字段、文档或配置变化撤批准。页面在原 trusted keydown 同步冻结当前正文、选区和原 t0，宿主与 Runtime 分别核精确目标、时钟边界和原 150ms，不能用后来的捕获追认旧按键。已受理的直接替换仍绑定原 DOM 和原范围，允许后台完成，不激活窗口或提交表单；自动接受由页面同步裁决，已执行回执进入共享完成路径，不再派第二次写。送写后的断连、超时或不完整回执按未知结果处理，不重试。原生连接和页面转发各自绑定请求归属，旧连接回包不能进入新请求。该按需交接已在真实发行 App、Host、扩展和模型下，通过标准网页的一次软件 F10 选区精确替换，以及受理后切至另一字段、仅后台原框写入且不抢焦点的检查；断连负控仍未闭环。

自动接受可使用精确源端的 `SourceLocalAcceptanceVersion=1`：仅 Windows 显式请求，标准 input/textarea 的同步执行端以实际字段身份回复；App 核已注册 origin 的真实连接、文档路由、字段、配置代与按键绑定后，绑定连接寿命传给 Runtime。该能力只允许发布仍受原焦点、输入法、版本和期限约束的许可，执行后以原计划回执终结，不允许无回执的接受命令转入异步 `WriteAsync`。默认关闭的实验写回门不变，普通 UIA 与旧版、未知版本、未请求、page dispatch、富文本路径不因此开放；按需 F10 仍沿既有用户原次直接替换授权。正式用户接入同意仍待完成。真实发行 App、Host、扩展及模型现已在实验门关闭时通过自动预览、一次软件 Tab 精确接受、原框焦点保持和零提交；这是受控软件输入证据，物理输入仍待验证。新单消费者组合已通过公开设置状态就绪、正常输入后一次原 F10 的前台选区及后台原框检查，均为精确一次替换、零提交。断连场景在等待页面冒泡按键时超时，后续零变更断言未执行，不能据此宣称断连验收通过。CfT 加载开发扩展不等于品牌 Chrome 的最终安装流程已经验证。

## 身份、时序与进程

WindowsTarget 保留 PID/进程启动时间、根/控件 HWND、provider、元素令牌与生命周期。Broker 协议 v3 固定跨进程授权时钟，v1/v2 客户端或服务端不能混用；worker 帧与消息不变。

App、Hook 与 Broker 的观测、触发、许可和执行期限统一使用 `src/windows/shared/WindowsInterruptClock.cs`。App/Broker 链接同一份平台内部源，Contracts 仍只含协议 DTO。时间值是 `QueryInterruptTimePrecise` 的自启动以来中断时间向下取整为毫秒，包含睡眠时间，不随 .NET 的 `Environment.TickCount64` 行为变化。整数取整误差小于 1ms；这不是浏览器端校准、睡眠恢复或物理按键已经验收的证明。所有 Core 期限保持不变。P/Invoke 使用 Windows SDK 的 `api-ms-win-core-realtime-l1-1-1.dll` 导入合同，要求 Windows 10 / Server 2016 或更新系统，不降级到粗粒度时钟。

系统 Edit 的共享内存事务保留仅覆盖 GUI 线程派发的私有 500ms 预算，C# 生产端与 C 执行端均显式调用 `GetTickCount64`。前台写入另将原 Broker 请求的授权截止时间原样传给目标线程，按同一 `QueryInterruptTimePrecise` 毫秒域校验；两项期限必须同时有效，私有预算不能续签原许可，两个时间域不相减。后台 F10 的 op2 路径仍沿其既有独立事务预算。模型下载进度限频不参与授权，保留普通运行时时钟。

Windows 轮询、debounce、许可与执行期限不因分层改变。Hook 回调不解析不透明 ID，在发布租约时解码并保存本地 HWND。进程族继续由 Job Object 管理，不以共享 Process 类型替代；私有管道 token 不构成同用户恶意进程隔离。

## 分发契约

Windows 部署根包含 TypeLay.exe、app/、worker/、browser/chromium/、bootstrap/、README 与第三方声明。AppPaths.Home 由启动脚本指向整个发行根；Broker、App 与独立原生练习宿主 TypeLay.Practice 放在同一个 app/。练习宿主只提供普通 stock Edit 与共同操作说明；翻译和写入仍由正式 App/Broker 处理，不设置输入法状态或绕过权限。开机启动指向发行根 TypeLay.exe。App 的安装流程在用户同意后准备运行时与模型，Windows wheel 选择不进入共享安装器。

组件准备在数据根内建立独立代，完成请求方向的模型自检后，由长期持有的 ModelClient 对候选进程进行一次真实握手；原子发布活动指针与接管同一连接后才报告成功。准备失败不覆盖仍可用的旧运行时，候选失败不恢复旧连接已失效的健康状态；旧请求的迟到结果不能修改新连接。启动只尝试指针指定的活动代和明确上一代，回退也须实际加载并确认指针恢复；没有指针时兼容原固定目录，不扫描目录猜版本。提交结果未确认时保留可能被引用的文件。默认仍准备完整中英双向；设置中的可选单向使用 schema2 非空方向集合，旧 schema1 仍代表完整双向。补装以已提交安装记录与本次请求的并集为准，自检、候选握手和发布集合必须相同；坏记录不能猜测方向，只能完整修复。[真实 Windows 检查](https://github.com/DingJWuyu/Typelay/actions/runs/39040582082)已通过单向准备、缺向准确拒绝、补齐并集与双向模型验证，以及相关界面回归、封装和搬迁检查；模型接口检查不等于实际 F10 缺向验收，整套桌面验收仍未全绿。

## 暂停时的最小观测

暂停使用独立的 `observe-pause` Broker 操作；返回 `PauseAnchor`，不构造 `InputSnapshot`，不读取值、正文、选区或光标，也不调用原生 Edit 采集和 IME 组合正文接口。元数据须确认真实前台元素、进程/字段寿命、可编辑属性、可见边界及非敏感/非排除状态；ComboBox 与正常预览一致，需要 ValuePattern 和明确可编辑的 TextPattern。未知状态没有弱入口。

WPF 暂停视图清除文本及撤销状态，只显示共享呈现定义中的恢复按钮，不抢焦点。按钮回传显示代次；共享控制器复读专用元数据，验证目标、前台/窗口版本及观察时效后，才将最新设置的 `Enabled` 改为 `true`。模式等其他偏好保持；失败、离焦、断开或旧观察到达均不能让退役入口复活。保存失败仍保持暂停并通过现有反馈说明原因。

## B-01：非原子写回与按键所有权

**B-01：非原子写回与按键所有权，发布阻塞。** UIA 读原文、比较与 SetValue 不是原子事务，写后核验不能恢复被并发覆盖的新文本。Hook 同步消费 Tab 后，异步 Broker 仍可能拒写。`ExperimentalWriteback` 默认关闭；测试开启时只使用已备份非敏感内容。关闭此项需要与目标文本变化可靠协调的写入机制及物理按键所有权证据，增加重读、缩短期限或盲目补发 Tab 均不足以解决。

按下时的短效接受许可与已消费按键的远程执行期限是两个边界，具体值以 Core 的 Limits 为准。派发前核验 Hook 输入/焦点版本，Broker 写前重读、期限检查与写后核验不能省略。浮层编辑按键与源字段输入版本必须区分，避免把译文编辑误判成原文变化。

共享配置可保留 Ctrl+功能键的阶段性表达。Windows 实现仅在自有 stock Edit 练习支持源接受/退出与编辑回源/退出：原 Ctrl 消息沿自有 GUI 正常分发，以原票、精确修饰键及同次输入上下文裁决功能键，不重建原观察或延长期限。仅当前 GUI 线程的被动消息观察安装成功才报告 `ControlPrefixObserved`；观察只推进失效版本，不授写权。私有见证协议 v4 关联原前缀序号与真实修饰键，旧版本拒绝；owned native v3 现场核对 HIMC/HKL，通用事务与 owned capture 版本不变。四个操作的精确键冲突统一拒绝。该组合链已通过真实 Windows 软件输入的首接受、编辑退出、键与鼠标回源、人工稿接受及完成保存；物理按键和实际输入法仍待验证。设置现提供 Ctrl+F1–F24；普通输入框另以原退出观察和编辑器原 token 支持零写组合退出、编辑回源，源 Ctrl 立即推进输入版本并撤销写许可，后继功能键不能换绑新字段。WPF 仅消费本次实际消息与 Hook 原按次匹配的命令，整组释放后才开始新按次。普通回源仍经原目标、配置与新鲜焦点核验。已注册的普通 stock Edit 在全局 UIA 焦点尚未确认时，可由原 native 生命周期的精确线程焦点前后夹一次新的 UIA 身份与 FieldPolicy 元数据检查来确认零写回源；不创建或更新绑定，后续仍须完整 fresh capture 与 Core 原稿基线确认。该替代分支已在真实模型链执行，后续新捕获保稿、编辑退出和源退出均通过；普通源内组合接受仍关闭，不扩大 B-01 或通用 UIA 写回。公开设置选择、保存与重开已通过实际检查，普通系统 Edit 无写路径已通过本轮软件输入检查，默认键和既有按需组合不变。

## 系统 Edit 前台事务

原生私有协议 v2 使用独立版本导出、注册消息和字段寿命属性；Broker 只接纳同版 DLL，缺少或旧版本不能提供原生能力。Broker DTO 仍为 v3。前台 op5 在源控件 GUI 线程核对准确焦点、前台根窗口、完整原文、当前 `Expected.Selection`、控件资格、寿命 cookie 和明确的 IME Clear；设置替换范围后，再核对这些安全条件与原授权期限。当前选区与替换 `Scope` 分开传递，允许人工编辑返回后的折叠光标与原替换范围不同。

选中的原生请求失败不回退 UIA。后台 F10 op2 以及既有通用 UIA 实验路径不因此扩大或取消。派发后不能确认的结果继续为未知，隔离目标且不自动重试。这项底层约束没有授予练习 scope、打开自动接受开关或证明物理按键所有权。

## 自有练习与首 Tab 接受

按需练习沿用当前配置的 F9/F10、正式模型和 Broker。完整自动练习使用独立受管进程内自有 stock Edit 的本次授权；默认无修饰 Tab 已验证，已保存的 Ctrl+功能键已通过[自有练习完整软件输入链](https://github.com/DingJWuyu/Typelay/actions/runs/39046030080)，物理输入仍待验证。当前合法会话键均可进入自有自动练习，不再以无修饰功能键触发按需模式确认；当前按需模式仍直接进入按需练习。全局 `ExperimentalWriteback` 和第三方 B-01 门保持原状。首接受保留精确回执、焦点、光标和 Undo；编辑回源与完成保存沿下述自有练习合同。练习进度只记录已生成预览；浮层接受提示另核本次精确计划的安装 ACK 与原有效期，撤权、尝试接受或会话变化立即取消提示，呈现查询本身不授予权限。

**精确捕获：**`capture-owned` 绑定 PID/启动时间、根/控件 HWND、线程、cookie、run 代次和 native FocusRevision，在读取正文及安装 hook 前核验。宿主每轮从精确控件 HWND 重新取得 UIA 元数据和 runtime-id，保留字段策略、应用排除和原目标隔离；不再以全局 UIA 焦点查询发现此自有字段。独立 `TypeLay.NativeInput.OwnedCapture.v2` 消息使用 owned 专属 op6，旧 owned 组件与通用 Transaction.v2 hook 不接收；op6 的专属焦点版本字段不是写入截止时间。原生在正文读取前后核对资格、IME、焦点版本及直接子控件的当前可见几何；UIA 边界仅用于显示，不替代身份。这一专路不改变第三方捕获或许可期限，该专路已通过受控 Windows 的真实模型首 Tab，物理设备与长期稳定性仍待验证。精确 Edit 的 EN_SETFOCUS/EN_KILLFOCUS 与顶层 WM_ACTIVATE 在其它操作前推进单调 native FocusRevision 并撤权；Begin/Retire 不重置计数。捕获、安装、键见证和执行固定同一版本，最终回到相同 HWND 不能恢复旧授权。

**键与执行：**该自有自动接受路径的 LL Hook 不吞原 Tab，只发布有界 FIFO 事件见证；真实消息仍到自有 GUI 队列，在 IsDialogMessage 前与一次许可共同裁决。安装前事件不追认，丢序、歧义和 overflow 撤权。native TryExecute 的无副作用前置门全部通过后才标记 accepted 并调用 SETSEL；GUI 只消费 accepted 的原键。前置失败沿原消息路径放行；接受后失败按实际拒绝或 Unknown 回执处理，不补发 Tab。正文、选区、IME、身份和原期限仍在 SETSEL 后再次核验。

**源框退出：**默认 Esc 使用独立、无写权的退出租约，绑定当前练习、配置、焦点版本与原期限；独立退出序号关联原次 FIFO 见证，旧协议版本明确拒绝。即使当前没有写许可，源 GUI 仍可在精确身份、焦点和 IME 校验后结束会话；此路径不读正文、不调用 SETSEL，也不创建写许可。配置变化、退休或过期撤销退出资格。真实 Windows 软件输入已验证默认源 Esc 结束会话且保持练习窗口、原文、选区与焦点，无写许可及按住跨退休的配对也已通过；生产注入、旧绑定、过期与配置变更负控均拒绝。物理键仍待验证。

**自有练习编辑交接：**完整编辑能力仅面向当前可信自有自动练习；生产仍拒绝注入输入，软件输入测试使用显式测试会话。真实单击固定原视图与同步进入编辑后的两枚 token，先封住旧代次并取得源 GUI 的精确 ACK；原点击仅豁免其配对 release。真实 Windows 已观察到源端 root 失活先于 Edit 失焦，但两侧 `WM_ACTIVATE` 对端均为 NULL，旧精确 HWND 对端合同因此拒绝。当前实现以 `WM_ACTIVATEAPP` 的精确对端 GUI 线程、源端局部 root→Edit 失焦及本次 TextBox 实际焦点共同确认进入编辑；500ms 内缺证或意图取消即结束。现前台事件流观察到任何家族外窗口（包括迟到事件）均永久取消，不能用最终 HWND 追认，也不宣称跨队列原子连续性。编辑期间没有源写许可；回源仍须基于固定原目标与原稿重新取证并创建新许可。真实 Windows 软件输入已验证一次单击进入编辑、修改译文及保持原框正文与选区，且没有新增源写入；物理输入仍待验证。

**回源与完成：**沿共享回源合同固定原目标与原稿：键回源及自然鼠标回源均先封旧通道并取得 GUI ACK，再以新焦点证据、完整捕获与 Core 确认建立新代许可；首次回源不写入，第二次 Tab 才能接受。无写权返回期间，仅精确的当前 pending 请求可在原截止内保持短暂焦点空隙；这不构成新鲜焦点证据，明确离开或请求失效仍结束。鼠标路径已实测源端 APP 激活不提供对端线程；仅将其记为本地激活边，仍须同次点击意图、浮层精确出边、源框焦点边与独立 ACK 合取，不凭缺失对端授予身份或写权。完成与 Finish 复用共享只读完成水位及退休 ACK，保存失败只重试保存。真实 Windows 软件输入已验证 Tab 与鼠标回源、人工稿与原框保留、回源零写入及完整新绑定，并验证第二次 Tab 精确写入人工稿、保留光标和源焦点；完成状态已退休确认，Finish 保存、退出和重新读盘也已在同次软件输入流程验证。物理输入与实际输入法仍待验证。

编辑器默认 Esc 另以当前视图同步预备的退出意图复用原回源流程：原按次、500ms 截止、执行端 ACK 与完整新源确认成立后，在发布新许可前结束会话。其它后端默认不启用该延续能力；关闭、配置或输入变化取消旧意图。首次预览尚未获得教程接受学分时，也记录真实入编辑的 Core revision 供新鲜回源绑定，教程计步条件不变。真实 Windows 软件链已验证首次预览改稿、Esc 回源退出、按住跨回源的释放配对及真实新输入恢复，物理按键仍待验证。

**私有传输与退休：**继承 stdio 控制仍限 4 KiB 且不载正文。每 run 的匿名见证映射只向该子进程复制只读句柄；独立有界 plan/result 映射通过安装、撤权与资源释放 ACK 分清所有权，不复用旧 run 的许可。关闭、重启、真实设置变化先阻止新授权，再等待精确执行端退休；活动自动练习的设置保存不得在 ACK 前宣称成功。断管或超时不重新授权，已执行事务依真实回执收束。自动 owned 标记随新 cookie 原子建立并保留到该寿命结束，撤销 plan 不恢复旧 v2 op2/op5 写入口；普通第三方及新按需寿命的 v2 合同不变。

真实 `EM_SETSEL` 同步回调内 owned 退休已证明可拒绝正文替换并保留实际选区副作用，见[事务回归](testing.md#系统-edit-前台事务回归)；其它重入情形不能据此推定通过。测试沿既有显式 automationTest 构造执行软件输入，见证保留 Injected 标记，生产默认拒绝；没有用户设置或发行 CLI 豁免。物理 Tab、用户输入法、完整编辑返回和完整教程仍须分别验收，同用户恶意进程隔离不在此私有通道的承诺内。

## 通用 UIA 后端缺口

缺少可靠 IME 状态的普通字段只能提供只读预览，不能进入人工编辑或接受；值可能含未提交字符，不能当成已提交原文。缺少 ValuePattern 的富文本不能降级为整框 SetValue；未暴露可编辑文本接口的自绘控件仍缺后端。

SetValue 写的是计算后的整个字段，即使选区外文字保持相同，也须验证受控组件状态、撤销栈及事件语义。当前实现问题与修复顺序见 [Windows 待办](open-issues.md)，真实操作与受控证据见[测试说明](testing.md)。

## 修改底层接口时的依据

- UIA 组合态：[IUIAutomationTextEditPattern](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationtexteditpattern) 与 [GetActiveComposition](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition)。接口不可用时保留 Unknown，不推断已提交。
- 写回与按键：[ValuePattern.SetValue](https://learn.microsoft.com/dotnet/api/system.windows.automation.valuepattern.setvalue) 与 [LowLevelKeyboardProc](https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc)。Hook 不等待 UIA 或推理，许可的原子消费不代表目标写入原子性。
- IME 后备观测：[ImmGetContext](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcontext) 与 [ImmGetCompositionStringW](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcompositionstringw)。上下文取不到不能变成 Clear。
- 授权时间域：[QueryInterruptTimePrecise](https://learn.microsoft.com/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryinterrupttimeprecise) 、[Windows SDK 导入映射](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/libMappings.rsp) 与 [.NET TickCount 行为变更](https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/11/environment-tickcount-windows-behavior)。跨进程时钟合同不能依赖运行时版本，也不能用墙钟恢复过期许可。
- 评估编辑锁协调：[TSF 架构](https://learn.microsoft.com/windows/win32/tsf/architecture) 与 [编辑上下文](https://learn.microsoft.com/windows/win32/tsf/edit-contexts)。这是待评估的后端方向，不是当前跨进程 UIA 已具有编辑锁或通用支持的证明。
