# Architecture constraints

在改模块边界、进程启动、会话或目录时读取本文件。工作区入口是 TypeLay.slnx；不要照搬 Cargo 文件或另造依赖图校验器。

## 生产边界

`TypeLay.Core` 拥有 Session、AcceptanceGate、WriteSafety、字段安全策略、UTF-16 选区映射和展示状态的纯逻辑。只接收显式快照、时间与结果；不读取操作系统、环境变量、网络或磁盘。回归通过这些公开边界观察，不断言私有字段。

`TypeLay.Protocol` 引用 Core，拥有 Wire 的长度前缀帧及 Messages 数据契约。改变消息结构或大小限制时必须覆盖 C# 契约和真实 Broker 进程回归。

`TypeLay.App` 引用 Core/Protocol。Application 编排会话；Infrastructure 负责设置、安装和子进程；Windows 包含 hook 和原生调用；UI 只管理 WPF 展示与交互。当前目录迁移保留命名空间和外部类型身份，不引入为目录而建的项目。

`TypeLay.Broker` 引用 Core/Protocol，在单一 MTA 线程上拥有 UIA COM 对象。Input 的 UiaInputProvider 不按应用白名单定义普通输入支持；Interop 只封装平台调用。生产入口是 Program 和标准输入输出管道，而不是 fixtures。

`TypeLay.Worker` 是独立 Python 进程，拥有本地模型校验和推理。App 的安装流程在用户同意后准备运行时与模型；worker 不自动下载、不监听端口，不向协议输出原始异常或用户文本诊断。

`TypeLay.Launcher` 是小型原生入口。发布时从 C 源码编译，调用发行目录 bootstrap/start.ps1；它不包含 SDK 下载和源码编译功能。

## 分发契约

源码目录与部署目录不同。部署根包含 TypeLay.exe、app/、worker/、bootstrap/、assets/tutorial/、README 和第三方声明。AppPaths.Home 由启动脚本指向整个发行根；Broker 与 App 放在同一个 app/。开机启动继续指向发行根 TypeLay.exe。

打包只能从明确的运行时文件清单复制。不能把 .git、skills、源代码、测试、历史证据、用户设置或模型缓存带入发布包。改变部署路径后必须运行 relocated-distribution E2E，不能只检查编译。

## 测试支持不是产品实现

`tests/support/TypeLay.Platform.Windows` 只服务旧原生 fixture 回归，不在生产依赖链。
`tests/fixtures/TypeLay.DesktopFixture` 的 WpfFixtureBroker 是受控替身，不证明真实生产 Broker 的 UIA 或 IPC 行为。
共享测试准备仅留在 tests 内，不向生产代码导出额外的“方便测试”接口。

## 保留的风险

B-01：UIA 观察、验证、SetValue 非原子，异步拒绝也不能恢复已被 hook 消费的物理 Tab。默认关闭实验写回，不能因本轮重组解除。
B-02：真实桌面、物理 Tab、中文 IME、多显示器/DPI 与 UI 视觉验收尚不完整。
B-03：通用普通输入后端尚不完整；Unknown IME 只读预览与只读 TextPattern 不能升级为可写。

关闭这些风险需要对应用户路径的证据，不接受结构检查、构建成功或 fixture 成功替代。
