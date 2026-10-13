# Test changes

先读根 AGENTS.md 与 docs/engineering/testing.md。测试的价值是阻止错误行为再次出现，不是数量、行覆盖或目录齐全。

## 目录归属

一级目录按实际产品消费者分为 shared、windows、linux。套件名称表达可观察边界；能在 Linux 上运行的 Windows DTO 测试仍归 windows。

平台内 fixtures 是可启动的受控宿主和输入资源，support 是被多个测试消费的辅助库。套件私有 fixture 就近保留；没有跨平台消费者时不建共享 fixture/support。辅助代码不进入生产依赖链。

## 选择可观察边界

- shared/TypeLay.Core.Tests：Session/AcceptanceGate/WriteSafety 的公开操作序列，尤其预览与接受资格分离、输入版本、人工草稿、字段策略、UTF-16 选区映射和拒绝条件。时间显式传入，不用 sleep。
- shared/TypeLay.Runtime.Tests：通过宿主端口驱动生产控制器，覆盖工作模式、直接任务、异步失效、人工草稿和缺失能力；真实临时文件验证设置。确定性时钟不使用 sleep，受控端口不是桌面或真实模型的证据。
- shared/TypeLay.Protocol.Tests：通用帧序列化、worker 消息与非法输入拒绝，只依赖共享 Protocol。
- windows/TypeLay.Windows.Contracts.Tests：Windows 不透明目标编解码、Broker DTO 与通用帧的往返契约；普通 .NET 可运行，shared-check 继续执行。标准 xUnit Facts/Theories 由 dotnet test 发现，不维护自制注册表或测试执行器。
- windows/TypeLay.App.Tests：真实 WPF 设置界面、快捷键与生产 Broker 桌面路径；纯设置持久化由 shared/TypeLay.Runtime.Tests 覆盖。
- shared/worker/：协议、文本结构和真实隔离进程的启动失败。无模型依赖；翻译器替身只能验证结构保留，不能证明语言质量或真实推理成功。
- windows/test_distribution.py：解压实际 ZIP 到含空格和中文的新目录，再启动真实 App/Broker/worker 入口。通过公开管道验证生命周期、鉴权、拒绝和隐私，不调用 Broker 私有成员。
- windows/TypeLay.Native.Tests 与 windows/support/TypeLay.Platform.Windows：验证 Hook 工具依赖的受控 Win32 字段适配器契约，不是生产 UIA、物理按键或真实 IME 验收。
- linux/：实际 tar.gz 的安装/卸载与 CLI、嵌套 niri 的原生 GTK 契约及带真实模型的生产桌面流程。分别调用 distribution_e2e.py、native_e2e.py 和 native_e2e.py --desktop；具体依赖与命令见 docs/platforms/linux/README.md，不把 XTest 当成物理按键或 IME 验收。
- windows/fixtures/：NativeFixture 提供生产 Broker 的可重复输入宿主及 Hook 普通/注入模式对照；DesktopFixture 使用真实浮层、会话、Hook 和模型隔离 WPF 编辑问题。具体用途与命令见 docs/platforms/windows/testing.md。
- linux/fixtures/：GTK/Qt 受控控件、Fcitx 探针与编辑器资源；linux/support/：浏览器进程隔离与可访问性观察辅助库，均只服务 Linux 测试。

测试及辅助代码同样遵循根 AGENTS 的内容保留规则。只验证已退休实现、没有现役消费者或与有效回归完全重复的测试应删除；不能仅靠仍能编译或测试互相引用证明保留价值。

## 修复顺序

1. 在最接近原用户路径的可用环境运行一次可失败的回归，并记录失败现象。
2. 提出可证伪的原因；一次只改变一个相关因素。不能先修生产代码再编造“之前必然失败”的结论。
3. 用同一测试证明修复，再跑受影响的邻近行为与工作区检查。
4. 删除临时探针，保留有效回归。测试环境不足时标注未测，不放宽安全条件或静默跳过要求。

端到端测试必须有超时和自身资源清理。不得终止不属于测试的进程、改真实用户设置或下载模型。不能为了通过而增加任意等待、无上限重试或代码关键字断言。

只在已有边界无法观察问题时增加新的接口。删掉实现形状测试前确认行为回归已经覆盖其真实意图。测试名称说明用户行为或协议结果，不说明实现细节。
