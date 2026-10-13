# Architecture constraints

在改模块边界、进程启动、会话或目录时读取本文件。工作区入口是 TypeLay.slnx；跨平台依赖、宿主契约与扩展取舍见 `docs/adr/0003-cross-platform-runtime.md`；工作区与交付约定见 ADR-0001。产品要求统一从 [README 产品验收入口](../../README.md#产品验收入口)读取冻结规格与已接受 ADR-0002，平台现状不降低要求。

## 生产边界

Windows/Linux 在 `trunk` 共用一份 Core、Runtime、Protocol 与 Python worker。平台源码按宿主职责隔离，不按分支复制共享实现；生产构建在 `eng/`，运行时包装与安装在 `packaging/`，受控工具和断言在 `tests/`。

`TypeLay.Core` 拥有 Session、AcceptanceGate、WriteSafety、字段安全策略、UTF-16 选区映射和展示状态的纯逻辑。只接收显式快照、时间与结果；不读取操作系统、环境变量、网络或磁盘。回归通过这些公开边界观察，不断言私有字段。

`TypeLay.Runtime` 引用 Core/Protocol，拥有 SessionController 的时序编排、设置持久化及 ModelClient 的本地模型协议。宿主注入输入服务/输入观察、串行事件循环、视图、数据路径和进程所有权。这里没有 WPF、Win32、UIA、Registry、Windows Broker DTO 或 provider 名称判定。模型、设置、平台事件通过公开端口进入同一会话所有者，不另建线程、服务容器或消息总线。

`TypeLay.Protocol` 不引用 Core，拥有长度前缀帧和 worker 消息。`TypeLay.Windows.Contracts` 引用 Core，仅拥有 Windows Broker DTO、协议版本和 WindowsTarget 编解码。它可在普通 .NET 下测试，但不是其它桌面平台必须实现的协议。

`TypeLay.App` 是 Windows 宿主。Windows/WindowsRuntime 组装共享运行时与 WPF 视图；WindowsInputService 把类型化输入操作转换为 Broker 请求，持有 HWND、焦点恢复和 IPC 期限。KeyboardHook/OnDemandHotkeys、Job Object、注册表启动项属于 Windows。UI 保持 WPF 展示与编辑，不直接写源字段。Infrastructure 的 AppPaths、ModelInstaller、SingleInstance 仍属于当前 Windows 分发/安装，不把 Windows Python wheel 选择冒充共享安装器。

`TypeLay.Broker` 引用 Core/Protocol/Windows.Contracts，在单一 MTA 线程上拥有 UIA COM 对象。Input 的 UiaInputProvider 不按应用白名单定义普通输入支持；Interop 只封装平台调用。生产入口是 Program 和标准输入输出管道，而不是 fixtures。TypeLay.NativeInput 的系统 Edit 协调写入仍是独立、受限的 Windows x64 路径。

`TypeLay.Worker` 是独立 Python 进程，拥有本地模型校验、文本结构保护与推理。源码及 worker 协议可共享，原生 Python 依赖的目标平台发行与真实推理必须分别验证。App 的 Windows 安装流程仍在用户同意后准备运行时与模型；worker 不自动下载、不监听端口，不向协议输出原始异常或用户文本诊断。

`TypeLay.Launcher` 是 Windows 原生入口。发布时从 C 源码编译，调用发行目录 bootstrap/start.ps1；它不包含 SDK 下载和源码编译功能。

`TypeLay.Linux` 是 Linux 组合根，直接装配共享 Runtime，不复制会话控制器。其 .NET 宿主拥有串行事件循环、类型化输入/视图端口及 worker 生命周期；`desktop/` 的 GTK 进程负责 niri IPC、同用户 socket、设置界面和 Ayatana 托盘。Linux 子进程监督和 XDG 安装路径不进入共享层。

`TypeLay.Linux/native/` 的 `gtk3/`、`gtk4/`、`qt6/` 分别拥有各 toolkit 的源控件与父窗口内浮层，`common/` 保留实际共用的原生实现，`fcitx5/` 保留输入法合作接口；根目录只负责总构建和子进程监督。普通 GTK API 无法证明异步 IME 空闲，通用路径保持 Unknown。可选 Fcitx 合作组件提供真实 context、在途状态、输出序号与字段绑定，只向经过验证的标准控件授予能力。GTK 两代共用私有 TextBuffer 写入实现，平台对象/元数据检查仍归各自模块；Qt 使用实际 QObject/provider 与源 GUI 线程，不复用 GTK 控件身份。标准控件、前台与后台写回的不同范围见 [Linux 能力表](../linux.md#与-windows-的功能对齐)及 [Qt 路径](../qt-linux.md)。

`TypeLay.Linux/browser/` 的扩展在真实文档事件线程处理标准 DOM 输入；单段 Tiptap 富文本通过有界 MAIN-world 桥调用源编辑器事务，隔离脚本持有 IME 与最终写入许可，经浏览器拥有的 native-messaging 子进程进入相同宿主端口。宿主独立验证同用户进程祖先与启动寿命，不接受页面声明的 PID。GTK/Qt 父窗口和浏览器视口坐标明确分开，只有对应的 renderer 使用，不能混为屏幕全局坐标。GTK 锚点契约见 `docs/adr/0004-linux-native-anchors.md`，浏览器部署/限制见 `docs/browser-linux.md`。`vscode/` 仅提供手动预览确认替换，不代表自动浮层后端。

`eng/linux/cooperative/` 拥有合作组件的固定输入、补丁、生产构建和 Qt 运行时探针；`packaging/linux/fcitx-cooperative/` 负责产物、对应源码/许可及逐应用启动器。测试 runner 调用这些构建入口，不再拥有生产 recipe。当前部署与验收边界见 `docs/linux.md`、`docs/fcitx-linux.md`。

## 平台边界不变量

Core 的 TargetId 只比较 backend、opaque value 和 lifetime。WindowsTarget 独立保留 PID/进程启动时间、根/控件 HWND、provider、元素令牌与生命周期，禁止共享逻辑解包句柄。表示变化由 Windows Broker 协议 v2 明确隔离，v1 客户端/服务端不能混用；worker 帧与消息不变。

观察就绪不等于拥有 Tab，也不等于能写回。InputSnapshot 的两种写能力默认 false；Windows 生产者在原有校验通过后显式声明原有能力。共享控制器还要求 CanInterceptAcceptance 才发布 Tab 许可。未知 IME、敏感元数据、焦点/输入版本、时效、字段寿命及未知写入结果继续独立否决，不能用单一“支持平台”开关覆盖它们。

IInputService 的实现负责操作前重新确认目标与权限，不能把 WritePlan 当授权。无法安全写入返回明确拒绝；可能已经变更返回 OutcomeUnknown，不换目标、不模拟粘贴、不自动重试。直接写回保持绑定触发源，不通过前台 return/write 组合实现。缺失输入权限、可靠目标或几何信息时安全不可用，不伪造快照。

所有会话回调在宿主串行事件循环执行；平台回调先排队，不直接并发调用 Session。时间映射到 ISessionLoop 的单调毫秒域，输入/源输入/焦点版本分开保留。Windows 的轮询、debounce、许可与执行期限不因分层而改变。Hook 回调不解析不透明 ID：在发布租约时解码并保存本地 HWND。

控制器释放时停止循环并释放其输入服务/观察器；模型客户端、设置和视图由宿主拥有。平台子进程工厂必须保证创建失败无遗留、stderr 不阻塞且不保存正文、退出清理整个自有进程族。Windows 继续使用原 Job Object 实现，不以共享 Process 类型替代生命周期保障。

## 分发契约

源码目录与部署目录不同。Windows 部署根包含 TypeLay.exe、app/、worker/、bootstrap/、assets/tutorial/、README 和第三方声明。AppPaths.Home 由启动脚本指向整个发行根；Broker 与 App 放在同一个 app/。开机启动继续指向发行根 TypeLay.exe。Linux 发行根由 `eng/build-linux.py` 装配，用户本地安装与 XDG 数据布局见 `docs/linux.md`。

客户端打包只能从明确的运行时文件清单复制，不带 .git、skills、源代码、测试、归档资料、用户设置或模型缓存。可选 Fcitx 合作包另外保留其许可证要求的对应源码、修改和生产重建输入，不把测试 fixture 当运行时依赖。改变部署路径后必须运行 relocated-distribution E2E，不能只检查编译。

## 受控测试边界

`tests/support/TypeLay.Platform.Windows` 提供只访问已登记 fixture 控件的 Win32 适配器，服务适配器契约和键盘 Hook 回归，不在生产依赖链。其受控 IME 状态不等于外部应用的真实输入法状态。

`tests/fixtures/TypeLay.NativeFixture` 不带 Hook 参数时是供生产 Broker 探测的真实 Win32 测试窗口；Hook 模式使用生产 KeyboardHook/Core 和固定候选隔离按键问题，不调用模型。

`tests/fixtures/TypeLay.DesktopFixture` 使用生产浮层、SessionController、Hook 和真实 ModelClient，WpfFixtureBroker 仅替换源字段访问。它用于定位 WPF 编辑与焦点问题，不证明生产 Broker 的 UIA 或 IPC 行为。运行方法见 `docs/agents/testing.md`。

`tests/behavior/TypeLay.Runtime.Tests` 用确定性串行宿主和受控输入/模型端口执行生产控制器，覆盖激活、异步结果隔离、草稿、直接替换和能力拒绝；设置测试使用真实临时文件。它们不证明桌面、真实模型或原子写入。`shared-check` 在非 Windows 环境构建并运行共享层，完整 Windows check/pack 保留。

`tests/linux/` 拥有 GTK/Qt 原生 fixture、合作输入回归、浏览器/VS Code 场景和发行包验证。`fcitx_cooperative/run.py`、`qt_cooperative/run.py` 调用 `eng/linux/cooperative/` 构建，再执行隔离输入事务断言；生产构建不导入或调用测试 runner；打包独立核验构建来源，Qt 可选发行仍要求同一构建的真实异步输入回归记录。

共享测试准备仅留在 tests 内，不向生产代码导出额外的“方便测试”接口。平台端口有 Windows/Linux 生产使用者；不为未开发平台保留空项目或假实现。

## 开放风险与安全边界

**B-01：非原子写回与按键所有权，发布阻塞。** UIA 读原文、比较与 SetValue 不是原子事务，写后核验不能恢复被并发覆盖的新文本。Hook 同步消费 Tab 后，异步 Broker 仍可能拒写。`ExperimentalWriteback` 默认关闭；测试开启时只使用已备份非敏感内容。关闭此项需要与目标文本变化可靠协调的写入机制及物理按键所有权证据，增加重读、缩短期限或盲目补发 Tab 均不足以解决。

按下时的短效接受许可与已消费按键的远程执行期限是两个边界，具体值以 Core 的 Limits 为准。派发前核验 Hook 输入/焦点版本，Broker 写前重读、期限检查与写后核验不能省略。浮层编辑按键与源字段输入版本必须区分，避免把译文编辑误判成原文变化。

**B-02：真实桌面验收不完整。** 独立网站、物理 Tab、完整中文 IME、多显示器/DPI 与视觉一致性需要对应用户路径的证据。构建、fixture、软件注入和模型自检结果均不能替代这些验收；具体操作见 `docs/agents/testing.md`。

Linux 浏览器链路仍有短租约到期与续期之间的可用性空窗风险；某次模型/Tab 回归通过不能证明所有调度条件稳定。保持到期拒绝，不延长授权或放宽断言掩盖。平台实现缺口集中在 [Linux 能力表](../linux.md#与-windows-的功能对齐)，Windows 未结问题见 [Windows 待办](../windows-open-issues.md)。

**B-03：通用输入后端不完整。** 缺少可靠 IME 状态的普通字段只能提供只读预览，不能进入人工编辑或接受；值可能含未提交字符，不能当成已提交原文。已知组合态只提供已提交前缀，Unknown 不伪装成 Clear，预览不因状态恢复自动成为接受候选。缺少 ValuePattern 的富文本不能降级为整框 SetValue；未暴露可编辑文本接口的自绘控件仍缺少后端。补齐的是控件能力，不是应用名单或自然语言标签。

字段元数据可能错误或缺失。敏感、密码、终端、代码等排除仍须先于正文读取，不能宣称对伪装字段零误判。SetValue 写的是计算后的整个字段，即使选区外文字保持相同，也仍须验证受控组件状态、撤销栈及事件语义。

文本和模型输入/输出限制以 Core 与 worker 的实现为准，超过限制拒绝而非静默截断。结构保护分段保留 URL、邮箱、mention、编号、emoji 和换行，可能影响上下文质量；人工追加保留人工前缀，不保证全文语义重新融合。

Windows 子进程使用 Job Object 管理生命周期，私有管道 token 不构成同用户恶意进程隔离。worker 没有网络调用路径不等于 OS 网络沙箱；无正文日志不代表能控制系统崩溃转储或内存分页。发布仍须审查实际组件/模型许可、供应链及签名，见 `THIRD_PARTY_NOTICES.md`。

## 修改底层接口时的依据

- UIA 组合态：[IUIAutomationTextEditPattern](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationtexteditpattern) 与 [GetActiveComposition](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition)。接口不可用时保留 Unknown，不推断已提交。
- 写回与按键：[ValuePattern.SetValue](https://learn.microsoft.com/dotnet/api/system.windows.automation.valuepattern.setvalue) 与 [LowLevelKeyboardProc](https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc)。Hook 不等待 UIA 或推理，许可的原子消费不代表目标写入原子性。
- IME 后备观测：[ImmGetContext](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcontext) 与 [ImmGetCompositionStringW](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcompositionstringw)。上下文取不到不能变成 Clear。
- 评估编辑锁协调：[TSF 架构](https://learn.microsoft.com/windows/win32/tsf/architecture) 与 [编辑上下文](https://learn.microsoft.com/windows/win32/tsf/edit-contexts)。这是待评估的后端方向，不是当前跨进程 UIA 已具有编辑锁或通用支持的证明。
