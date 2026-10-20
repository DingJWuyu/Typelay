# ADR-0003：共享产品运行时与独立桌面宿主

状态：接受。范围是共享运行时和 Windows/Linux 宿主边界。本决策调整 ADR-0001 的 App 内部编排位置；[产品验收入口](../../README.md#产品验收入口)中的冻结规格与已接受 ADR-0002 不变，B-01/B-02/B-03 不因此关闭。

## 决定

Core、Runtime、Protocol 和 Python worker 在同一主线只维护一份。纯规则归 Core；会话编排、设置存储和 worker 客户端归普通 .NET 的 Runtime。Windows App 与 TypeLay.Linux 各自装配共享运行时及本机输入、视图、事件循环、进程和安装实现。平台不复制控制器，也不把本机 toolkit 或 Windows Broker DTO 推入共享层。

```text
TypeLay.Runtime ------------> Core + Protocol
TypeLay.Windows.Contracts --> Core
Windows App ----------------> Runtime + Windows.Contracts + Protocol
Windows Broker -------------> Core + Windows.Contracts + Protocol
Windows NativeInput --------> Win32（由 Broker 协调）
Linux host -----------------> Runtime
Python worker <-------------> Protocol 的 worker 帧契约
```

Protocol 不反向引用 Core。Windows.Contracts 的 PID/HWND/provider 编码只服务 Windows Broker；Linux 使用自己的桌面进程与同用户 socket。当前文件归属以 [架构约束](../engineering/architecture.md#生产边界)为准，构建、安装和测试分别归 eng、packaging、tests，生产构建不反向依赖测试 runner。

## 小接口与宿主责任

- IInputService 负责异步观察、返回源字段和有类型的写入回执；IInputMonitor 负责即时输入/焦点版本、命令通知与短期许可。二者分开，使同步按键路径无需等待远程读值或模型，也不要求所有平台采用 Windows 式全局 Hook。
- ISessionLoop 提供宿主串行回调与单调时钟，SessionController 及异步继续体在该上下文执行。ISessionView 负责显示/编辑，真实视图直接实现端口，不增加只转发调用的包装层。
- ModelClient 接收运行时路径和自有子进程工厂，保留帧序列、请求编号、超时、隔离启动和错误状态。进程族清理由平台保证；Windows 使用 Job Object，Linux 使用自己的监督实现。
- SettingsStore 拥有 JSON、原子替换、默认值和升级；路径、登录启动副作用、应用标识规范化由宿主注入。共享存储不把 Linux 应用 ID 当作 Windows .exe 文件名。

这些接口均有实际生产调用方。复杂的 toolkit 身份、IME 协作、按键投递和写入实现封装在平台后端内部，不按工具链建立通用插件框架、服务容器、RPC 总线或空平台层。

## 身份、能力与失败

TargetId 只暴露 backend、opaque value、lifetime 的相等性；平台独立解释内部身份。Windows 规范编码与 Broker 协议 v3 拒绝旧对端和非规范别名，worker 协议不因此变化。

访问、IME、文本/选区、几何、版本和写能力分别提供证据。WriteSupported、DirectWriteSupported 默认 false，拦截接受另需 CanInterceptAcceptance。能力属于每个字段的当次观察，预览、编辑、Tab、前台写回和后台直接替换不能相互推导。当前产品要求有效锚点；没有锚点不自行改成另一种交互。

写入回执区分 RejectedBeforeMutation、Verified、OutcomeUnknown。共享计划不授予权限，后端操作前必须重新验证；未知结果隔离原目标，不能通过抢焦点、模拟粘贴、改写新焦点或重试伪造成功。直接任务仍按 ADR-0002 绑定触发源，并与前台会话生命周期分开。

## 取舍与扩展

保留 Windows 的 WPF、UIA Broker、Hook、原生 Edit 事务、安装与分发实现，可以在提取共享规则时避免同时重写已存在的系统边界。Linux 使用 GTK/Qt 合作输入和浏览器事件线程，复用产品规则而不假设它们具有 Windows 的权限或写入语义。ModelInstaller 的平台 wheel、XDG/注册表、托盘、主题/DPI 和启动生命周期分别维护；共享 worker 不等于跨平台原生依赖已经验证。

新平台先证明真实字段、权限、寿命、IME、几何与按键所有权，再实现对应端口。不按发行版名称授予能力，不提前建立没有生产使用者的项目；macOS 当前未交付。当前 Linux 能力与缺口统一见 [Linux 能力表](../platforms/linux/README.md#与-windows-的功能对齐)。

## 验证要求

共享改动需要跨平台共享回归；宿主或输入改动需要对应平台的真实进程/桌面链路；启动、布局或分发改动需要搬迁后的实际发行包验证。命令与证据分层统一见 [开发流程](../engineering/development.md)和 [测试协议](../engineering/testing.md)。受控端口、构建与自动化成功不代替真实模型、物理输入、第三方应用、视觉或 B-01 写入竞态验收。
