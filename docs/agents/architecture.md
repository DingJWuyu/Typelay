# Architecture constraints

在改模块边界、进程启动、会话或目录时读取本文件。工作区入口是 TypeLay.slnx；架构取舍见 `docs/adr/0001-workspace-and-delivery.md`。

## 生产边界

`TypeLay.Core` 拥有 Session、AcceptanceGate、WriteSafety、字段安全策略、UTF-16 选区映射和展示状态的纯逻辑。只接收显式快照、时间与结果；不读取操作系统、环境变量、网络或磁盘。回归通过这些公开边界观察，不断言私有字段。

`TypeLay.Protocol` 引用 Core，拥有 Wire 的长度前缀帧及 Messages 数据契约。改变消息结构或大小限制时必须覆盖 C# 契约和真实 Broker 进程回归。

`TypeLay.App` 引用 Core/Protocol。Application 编排会话；Infrastructure 负责设置、安装和子进程；Windows 包含 hook 和原生调用；UI 只管理 WPF 展示与交互，不直接写目标应用。

`TypeLay.Broker` 引用 Core/Protocol，在单一 MTA 线程上拥有 UIA COM 对象。Input 的 UiaInputProvider 不按应用白名单定义普通输入支持；Interop 只封装平台调用。生产入口是 Program 和标准输入输出管道，而不是 fixtures。

`TypeLay.Worker` 是独立 Python 进程，拥有本地模型校验和推理。App 的安装流程在用户同意后准备运行时与模型；worker 不自动下载、不监听端口，不向协议输出原始异常或用户文本诊断。

`TypeLay.Launcher` 是小型原生入口。发布时从 C 源码编译，调用发行目录 bootstrap/start.ps1；它不包含 SDK 下载和源码编译功能。

## 分发契约

源码目录与部署目录不同。部署根包含 TypeLay.exe、app/、worker/、bootstrap/、assets/tutorial/、README 和第三方声明。AppPaths.Home 由启动脚本指向整个发行根；Broker 与 App 放在同一个 app/。开机启动继续指向发行根 TypeLay.exe。

打包只能从明确的运行时文件清单复制。不能把 .git、skills、源代码、测试、归档资料、用户设置或模型缓存带入发布包。改变部署路径后必须运行 relocated-distribution E2E，不能只检查编译。

## 受控测试边界

`tests/support/TypeLay.Platform.Windows` 提供只访问已登记 fixture 控件的 Win32 适配器，服务适配器契约和键盘 Hook 回归，不在生产依赖链。其受控 IME 状态不等于外部应用的真实输入法状态。

`tests/fixtures/TypeLay.NativeFixture` 不带 Hook 参数时是供生产 Broker 探测的真实 Win32 测试窗口；Hook 模式使用生产 KeyboardHook/Core 和固定候选隔离按键问题，不调用模型。

`tests/fixtures/TypeLay.DesktopFixture` 使用生产浮层、SessionController、Hook 和真实 ModelClient，WpfFixtureBroker 仅替换源字段访问。它用于定位 WPF 编辑与焦点问题，不证明生产 Broker 的 UIA 或 IPC 行为。运行方法见 `docs/agents/testing.md`。

共享测试准备仅留在 tests 内，不向生产代码导出额外的“方便测试”接口。

## 开放风险与安全边界

**B-01：非原子写回与按键所有权，发布阻塞。** UIA 读原文、比较与 SetValue 不是原子事务，写后核验不能恢复被并发覆盖的新文本。Hook 同步消费 Tab 后，异步 Broker 仍可能拒写。`ExperimentalWriteback` 默认关闭；测试开启时只使用已备份非敏感内容。关闭此项需要与目标文本变化可靠协调的写入机制及物理按键所有权证据，增加重读、缩短期限或盲目补发 Tab 均不足以解决。

按下时的短效接受许可与已消费按键的远程执行期限是两个边界，具体值以 Core 的 Limits 为准。派发前核验 Hook 输入/焦点版本，Broker 写前重读、期限检查与写后核验不能省略。浮层编辑按键与源字段输入版本必须区分，避免把译文编辑误判成原文变化。

**B-02：真实桌面验收不完整。** 独立网站、物理 Tab、完整中文 IME、多显示器/DPI 与视觉一致性需要对应用户路径的证据。构建、fixture、软件注入和模型自检结果均不能替代这些验收；具体操作见 `docs/agents/testing.md`。

**B-03：通用输入后端不完整。** 缺少可靠 IME 状态的普通字段只能提供只读预览，不能进入人工编辑或接受；值可能含未提交字符，不能当成已提交原文。已知组合态只提供已提交前缀，Unknown 不伪装成 Clear，预览不因状态恢复自动成为接受候选。缺少 ValuePattern 的富文本不能降级为整框 SetValue；未暴露可编辑文本接口的自绘控件仍缺少后端。补齐的是控件能力，不是应用名单或自然语言标签。

字段元数据可能错误或缺失。敏感、密码、终端、代码等排除仍须先于正文读取，不能宣称对伪装字段零误判。SetValue 写的是计算后的整个字段，即使选区外文字保持相同，也仍须验证受控组件状态、撤销栈及事件语义。

文本和模型输入/输出限制以 Core 与 worker 的实现为准，超过限制拒绝而非静默截断。结构保护分段保留 URL、邮箱、mention、编号、emoji 和换行，可能影响上下文质量；人工追加保留人工前缀，不保证全文语义重新融合。

子进程使用 Job Object 管理生命周期，私有管道 token 不构成同用户恶意进程隔离。worker 没有网络调用路径不等于 OS 网络沙箱；无正文日志不代表能控制系统崩溃转储或内存分页。发布仍须审查实际组件/模型许可、供应链及签名，见 `THIRD_PARTY_NOTICES.md`。

## 修改底层接口时的依据

- UIA 组合态：[IUIAutomationTextEditPattern](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationtexteditpattern) 与 [GetActiveComposition](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition)。接口不可用时保留 Unknown，不推断已提交。
- 写回与按键：[ValuePattern.SetValue](https://learn.microsoft.com/dotnet/api/system.windows.automation.valuepattern.setvalue) 与 [LowLevelKeyboardProc](https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc)。Hook 不等待 UIA 或推理，许可的原子消费不代表目标写入原子性。
- IME 后备观测：[ImmGetContext](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcontext) 与 [ImmGetCompositionStringW](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcompositionstringw)。上下文取不到不能变成 Clear。
- 评估编辑锁协调：[TSF 架构](https://learn.microsoft.com/windows/win32/tsf/architecture) 与 [编辑上下文](https://learn.microsoft.com/windows/win32/tsf/edit-contexts)。这是待评估的后端方向，不是当前跨进程 UIA 已具有编辑锁或通用支持的证明。
