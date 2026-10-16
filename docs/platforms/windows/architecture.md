# Windows 宿主与输入边界

共享规则与宿主端口见[工程架构](../../engineering/architecture.md)。以下内容仅属于 src/windows/ 下的 Windows 实现，不作为其它平台必须实现的协议或通用约束。

## 生产模块与平台内共享

TypeLay.Windows.Contracts 引用 Core，仅拥有 App 与 Broker 共用的 DTO、协议版本和 WindowsTarget 编解码。它可在普通 .NET 下测试，但其职责仍属于 Windows。

`TypeLay.App` 是 Windows 宿主。Windows/WindowsRuntime 组装共享运行时与 WPF 视图；WindowsInputService 把类型化输入操作转换为 Broker 请求，持有 HWND、焦点恢复和 IPC 期限。KeyboardHook/OnDemandHotkeys、Job Object、注册表启动项属于 Windows。UI 保持 WPF 展示与编辑，不直接写源字段。Infrastructure 的 AppPaths、ModelInstaller、SingleInstance 仍属于当前 Windows 分发/安装，不把 Windows Python wheel 选择冒充共享安装器。

`TypeLay.Broker` 引用 Core/Protocol/Windows.Contracts，在单一 MTA 线程上拥有 UIA COM 对象。Input 的 UiaInputProvider 不按应用白名单定义普通输入支持；Interop 只封装平台调用。生产入口是 Program 和标准输入输出管道，而不是 fixtures。TypeLay.NativeInput 的系统 Edit 协调写入仍是独立、受限的 Windows x64 路径。

`TypeLay.Launcher` 是 Windows 原生入口。发布时从 C 源码编译，调用发行目录 bootstrap/start.ps1；它不包含 SDK 下载和源码编译功能。

## 身份、时序与进程

WindowsTarget 保留 PID/进程启动时间、根/控件 HWND、provider、元素令牌与生命周期。Broker 协议 v3 固定跨进程授权时钟，v1/v2 客户端或服务端不能混用；worker 帧与消息不变。

App、Hook 与 Broker 的观测、触发、许可和执行期限统一使用 `src/windows/shared/WindowsInterruptClock.cs`。App/Broker 链接同一份平台内部源，Contracts 仍只含协议 DTO。时间值是 `QueryInterruptTimePrecise` 的自启动以来中断时间向下取整为毫秒，包含睡眠时间，不随 .NET 的 `Environment.TickCount64` 行为变化。整数取整误差小于 1ms；这不是浏览器端校准、睡眠恢复或物理按键已经验收的证明。所有 Core 期限保持不变。P/Invoke 使用 Windows SDK 的 `api-ms-win-core-realtime-l1-1-1.dll` 导入合同，要求 Windows 10 / Server 2016 或更新系统，不降级到粗粒度时钟。

系统 Edit 的共享内存事务保留仅覆盖 GUI 线程派发的私有 500ms 预算，C# 生产端与 C 执行端均显式调用 `GetTickCount64`。前台写入另将原 Broker 请求的授权截止时间原样传给目标线程，按同一 `QueryInterruptTimePrecise` 毫秒域校验；两项期限必须同时有效，私有预算不能续签原许可，两个时间域不相减。后台 F10 的 op2 路径仍沿其既有独立事务预算。模型下载进度限频不参与授权，保留普通运行时时钟。

Windows 轮询、debounce、许可与执行期限不因分层改变。Hook 回调不解析不透明 ID，在发布租约时解码并保存本地 HWND。进程族继续由 Job Object 管理，不以共享 Process 类型替代；私有管道 token 不构成同用户恶意进程隔离。

## 分发契约

Windows 部署根包含 TypeLay.exe、app/、worker/、bootstrap/、README 与第三方声明。AppPaths.Home 由启动脚本指向整个发行根；Broker、App 与独立原生练习宿主 TypeLay.Practice 放在同一个 app/。练习宿主只提供普通 stock Edit 与共同操作说明；翻译和写入仍由正式 App/Broker 处理，不设置输入法状态或绕过权限。开机启动指向发行根 TypeLay.exe。App 的安装流程在用户同意后准备运行时与模型，Windows wheel 选择不进入共享安装器。

## 暂停时的最小观测

暂停使用独立的 `observe-pause` Broker 操作；返回 `PauseAnchor`，不构造 `InputSnapshot`，不读取值、正文、选区或光标，也不调用原生 Edit 采集和 IME 组合正文接口。元数据须确认真实前台元素、进程/字段寿命、可编辑属性、可见边界及非敏感/非排除状态；ComboBox 与正常预览一致，需要 ValuePattern 和明确可编辑的 TextPattern。未知状态没有弱入口。

WPF 暂停视图清除文本及撤销状态，只显示共享呈现定义中的恢复按钮，不抢焦点。按钮回传显示代次；共享控制器复读专用元数据，验证目标、前台/窗口版本及观察时效后，才将最新设置的 `Enabled` 改为 `true`。模式等其他偏好保持；失败、离焦、断开或旧观察到达均不能让退役入口复活。保存失败仍保持暂停并通过现有反馈说明原因。

## B-01：非原子写回与按键所有权

**B-01：非原子写回与按键所有权，发布阻塞。** UIA 读原文、比较与 SetValue 不是原子事务，写后核验不能恢复被并发覆盖的新文本。Hook 同步消费 Tab 后，异步 Broker 仍可能拒写。`ExperimentalWriteback` 默认关闭；测试开启时只使用已备份非敏感内容。关闭此项需要与目标文本变化可靠协调的写入机制及物理按键所有权证据，增加重读、缩短期限或盲目补发 Tab 均不足以解决。

按下时的短效接受许可与已消费按键的远程执行期限是两个边界，具体值以 Core 的 Limits 为准。派发前核验 Hook 输入/焦点版本，Broker 写前重读、期限检查与写后核验不能省略。浮层编辑按键与源字段输入版本必须区分，避免把译文编辑误判成原文变化。

## 系统 Edit 前台事务

原生私有协议 v2 使用独立版本导出、注册消息和字段寿命属性；Broker 只接纳同版 DLL，缺少或旧版本不能提供原生能力。Broker DTO 仍为 v3。前台 op5 在源控件 GUI 线程核对准确焦点、前台根窗口、完整原文、当前 `Expected.Selection`、控件资格、寿命 cookie 和明确的 IME Clear；设置替换范围后，再核对这些安全条件与原授权期限。当前选区与替换 `Scope` 分开传递，允许人工编辑返回后的折叠光标与原替换范围不同。

选中的原生请求失败不回退 UIA。后台 F10 op2 以及既有通用 UIA 实验路径不因此扩大或取消。派发后不能确认的结果继续为未知，隔离目标且不自动重试。这项底层约束没有授予练习 scope、打开自动接受开关或证明物理按键所有权。

## 自有练习与首 Tab 接受

按需练习沿用当前配置的 F9/F10、正式模型和 Broker。自动首接受候选仅支持现配置为无修饰 Tab 的自有 stock Edit，使用独立受管进程的本次授权；其它接受键不被改写，按需入口保留。全局 `ExperimentalWriteback` 和第三方 B-01 门保持原状。该首接受已通过真实 Windows 软件输入链验证，保留精确回执、焦点、光标和 Undo；不开放编辑回源、不保存完整教程完成状态。练习进度只记录已生成预览；浮层接受提示另核本次精确计划的安装 ACK 与原有效期，撤权、尝试接受或会话变化立即取消提示，呈现查询本身不授予权限。

**精确捕获：**`capture-owned` 绑定 PID/启动时间、根/控件 HWND、线程、cookie、run 代次和 native FocusRevision，在读取正文及安装 hook 前核验。独立 `TypeLay.NativeInput.OwnedCapture.v1` 消息使用 owned 专属 op6，旧 v2 hook 不接收；op6 的专属焦点版本字段不是写入截止时间。精确 Edit 的 EN_SETFOCUS/EN_KILLFOCUS 与顶层 WM_ACTIVATE 在其它操作前推进单调 native FocusRevision 并撤权；Begin/Retire 不重置计数。捕获、安装、键见证和执行固定同一版本，最终回到相同 HWND 不能恢复旧授权。

**键与执行：**该自有自动接受路径的 LL Hook 不吞原 Tab，只发布有界 FIFO 事件见证；真实消息仍到自有 GUI 队列，在 IsDialogMessage 前与一次许可共同裁决。安装前事件不追认，丢序、歧义和 overflow 撤权。native TryExecute 的无副作用前置门全部通过后才标记 accepted 并调用 SETSEL；GUI 只消费 accepted 的原键。前置失败沿原消息路径放行；接受后失败按实际拒绝或 Unknown 回执处理，不补发 Tab。正文、选区、IME、身份和原期限仍在 SETSEL 后再次核验。

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
