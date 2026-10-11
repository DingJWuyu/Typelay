# ADR-0003：共享产品运行时与独立桌面宿主

状态：接受。范围是已有 Windows 产品的架构，不是 Linux/macOS 客户端交付。本决策调整 ADR-0001 中的 App 内部编排位置，不改变 ADR-0002 的按需/直接替换语义，也不关闭 B-01/B-02/B-03。

## 决定与依赖方向

将真正可复用的会话编排、设置与 worker 客户端提取到普通 .NET 的 TypeLay.Runtime。Core 继续拥有纯规则。Windows App 作为组合根装配运行时与原 WPF、输入、进程和安装实现，不切换 UI 框架，不复制一份控制器，也不新建空 Linux/macOS 项目。

```text
TypeLay.Runtime ------------> Core + Protocol
TypeLay.Windows.Contracts --> Core
Windows App ----------------> Runtime + Windows.Contracts + Protocol
Windows Broker -------------> Core + Windows.Contracts + Protocol
Windows NativeInput --------> Win32（C DLL，由 Broker 独占协调）
Python worker <-------------> Protocol 的 worker 帧契约
```

Protocol 只包含通用帧和 worker 消息，不反向引用 Core。Windows.Contracts 虽能在普通 .NET 中编译，其 PID/HWND/provider 编码及 Broker DTO 仍是 Windows 专属语义，不是跨平台接口。Runtime 不引用它，未来后端可以选择直接访问本机接口或使用自己的隔离进程。

## 实际接入面

SessionController 通过 IInputService 获取目标观察、返回源字段及有类型的写入回执；通过 IInputMonitor 获取焦点家族、输入版本、命令通知与短期许可发布。前者是异步的外部输入服务，后者是平台对输入事件的即时权威，不要求每个平台安装 Windows 式全局 Hook。两者仍分开，避免让同步按键路径等待远程读值或模型。

ISessionLoop 提供宿主串行回调和单调时钟。控制器及其异步继续体都在该串行上下文运行；平台回调通过监视器队列进入，不在后台线程直接更新 Session。Windows 保持 DispatcherTimer 的原优先级和间隔，不增加转发线程或额外 IPC。ISessionView 负责现有显示/编辑交互，WPF OverlayWindow 直接实现它，不增加只转发调用的视图包装层。

ModelClient 接收运行时文件路径与自有子进程工厂，保留原帧序列、请求编号、超时、隔离启动及错误状态。Windows 工厂保留 Job Object。SettingsStore 保留现有 JSON、原子文件替换、默认值与升级行为，路径、登录启动副作用、应用标识规范化由宿主注入。Windows 仍使用原数据目录、进程名处理与注册表项；共享存储不把其它平台的应用 ID 当作 .exe 文件名。

这些接口都有实际生产调用方与受控测试调用方。没有插件框架、服务容器、全局平台注册表、通用 RPC 总线或预填的平台能力矩阵。

## 身份、能力与失败

TargetId 是 backend、opaque value、lifetime 的值身份，Core 只比较相等性。WindowsTarget 的规范编码完整保留原身份的全部字段，解码拒绝非规范别名或其它 backend。Windows Broker 协议提升为 v2，旧对端在握手时失败；不是尝试兼容可能被误解的写入目标。worker 协议没有因此改变。

源字段观察包含独立的访问状态、IME、文本/选区、几何、版本及写能力。WriteSupported 和 DirectWriteSupported 默认 false；现有 Windows 生产者经原检查显式给出原能力。能展示预览不意味着能编辑、拦截 Tab、普通写回或后台直接替换。共享控制器另外检查 CanInterceptAcceptance；WriteSafety、AcceptanceGate、未知结果隔离与原生后端的再验证保留各自职责。

能力可以逐字段、逐次观察变化，不将一个桌面环境简单标成“全部支持”。Unknown IME 继续只读；权限撤销或缺少可靠目标/选区/几何时返回不可用，不合成成功。当前产品仍要求有效锚点才进入输入浮层体验；没有锚点的平台不会自动改成另一个产品交互。新的无锚点体验属于后续明确的产品决定。

写入返回 RejectedBeforeMutation、Verified 或 OutcomeUnknown。只有后端能够为真实目标给出相应证据；共享计划不是写许可。直接操作始终绑定触发快照，前台会话结束不取消它，设置/暂停/释放使尚未派发操作失效。后端不得通过抢焦点、模拟粘贴、其它输入框或重试来伪造成功。已派发的写入不能假装撤回。

## 保留 Windows 而不是重新实现 Windows

WindowsInputService 隔离 Broker DTO、HWND 与前台恢复；WindowsRuntime 隔离注册表、路径、WPF 时钟和 Job Object 组装。UI 布局与文案、捕获/翻译时序、快捷键配置、人工草稿、短效 Tab 许可、受限原生 Edit 事务及默认关闭的实验写回不重做。Hook 只在租约发布时解码目标，按键回调使用已绑定的本地 HWND。

原 ModelInstaller 仍是 Windows 安装器。它同时处理下载、校验和 Windows Python/wheel 发布，未在没有第二种真实分发实现时拆成大量安装策略接口。worker 模型清单校验、结构保护与推理代码可共享，但其它平台的 Python 原生依赖、CPU 架构、包许可及安装生命周期必须各自验证。Windows 启动器、单实例、托盘、剪贴板、主题/DPI 与签名分发同样仍是宿主职责。

## 后续平台扩展

先在目标桌面验证可观察字段、权限与寿命标识，再实现有证据的输入适配器及本机视图/事件循环。复用 Core、Runtime、worker 帧与 Python 逻辑；不要求沿用 Windows Broker、UIA、HWND 或 WPF。路径、登录启动、进程清理、安装打包与更新由各平台选择合适机制，不能以普通 Process 启停代替已验证的整个进程族生命周期。

Linux 的首个候选环境包括 CachyOS + niri（Wayland），但能力判断必须属于实际后端与会话，不放在发行版名称分支中。需验证 AT-SPI 等输入观察、portal/合成器权限、快捷键触发与裸 Tab 所有权、焦点恢复、几何与多屏缩放、IME、目标销毁、后台协调写入。macOS 需对应验证 Accessibility/TCC、文本/选区与目标寿命、事件所有权、IME、窗口与坐标、协调写入及分发权限。本轮不声称这些组合已经可用。

选择边界时查证的事实只支撑“不假定能力等价”，不是平台实现方案：XDG [GlobalShortcuts](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html) 提供会话化快捷键绑定和激活通知，不提供文本事务；niri 的 [Important Software](https://niri-wm.github.io/niri/Important-Software.html) 明确它不是完整桌面环境，配套服务需要配置；Apple 的 [AXUIElementIsAttributeSettable](https://developer.apple.com/documentation/applicationservices/1459972-axuielementisattributesettable) 将属性可写性作为单独查询，并可能返回 API 不完整等错误。快捷键注册或属性可写均不能推导出安全的原子替换。

## 验证边界

`./dev.ps1 shared-check` 在非 Windows 环境运行真正的 Core/Runtime/协议代码与 worker 回归，证明共享路径不需要加载 Windows 桌面实现。受控端口只验证编排，不证明模型质量、系统权限或物理输入。Windows `check` 保留 WPF/生产 Broker/真实系统 Edit 的自动化契约，`pack` 校验新增程序集并在迁移后的真实分发包运行进程 E2E。构建、格式、锁文件和运行时清单使用原工程工具。

真实第三方应用、物理 Tab、中文 IME、多屏/DPI、视觉一致性、模型性能与 B-01 的写入竞态仍按测试协议单独验收。自动化成功不能关闭这些风险，也不能用 Linux 上的共享测试声称交付了 Linux 客户端。
