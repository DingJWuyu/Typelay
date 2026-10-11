# TypeLay Windows MVP 工程设计

日期：2026-09-29。这是工程建议与实现边界，不重新定义产品。已开始 M1；本文中的 WPF 客户端、broker、Hook、模型 worker 和 Settings 均是**待实现架构**，不是本包已交付功能。

## A. MVP 范围

发布目标是 Windows 上经过认证的普通纯文本输入框：原位输入 → 本地译文 → 直接编辑或 Tab 接受 → 仅写回且不提交。全文、选区、两阶段人工译文接受、真实 IME、安全失效、自己的写回抑制、托盘与基础设置均属于 MVP 的完成条件，不因分期而删除。

先验收自有 Win32 EDIT，再验收明确版本的 Edge 普通 `input` / `textarea`。**M1 当前仅能访问自有 fixture；尚无任何第三方应用获得新代码的支持认证。** “Edge 可读”不能代表 Edge 内所有网站/控件可写。必须按浏览器版本、字段类型、语言/IME、读写/选区/焦点能力分别记录。

首发工程以 Windows x64 普通用户会话为目标。具体 Windows 版本与构建在验收时登记；历史 PoC 的 23H2 环境不自动成为新产品支持声明。提升权限的应用、UAC 安全桌面、密码管理器/安全输入区、无法证明语义的编辑器、富文本与终端默认排除。

非范围：Linux/Wayland 实现、完整富文本、云端推理/历史/同步/账户、AI 写作、多候选、模型商城、完整术语表、主题系统、大量应用兼容。首发未完成的风格控制或术语入口必须禁用并说明尚不可用，不能给出虚假的“已应用”。

### 从第一阶段就处理的风险

焦点进入浮层时会话不得结束；真实 IME 的 Unknown 不能当 Clear；对旧原文/选区/目标的拒写与按键放行必须分别证明；慢 UIA provider 与卡死 worker 不能拖住键盘；一般控件没有跨进程原子替换保证。这些是发布门槛，不是后期 UI 打磨。

## B. 技术路线与选择理由

建议 C# + .NET 10 LTS + WPF；Windows 访问与推理分进程，核心状态独立于 WPF/Win32。微软当前支持表将 .NET 10 列为 LTS；WPF 提供 Windows 桌面控件、文本输入与焦点事件，但只运行于 Windows。[R1][R2]

以下比较是本项目的工程取舍，不是框架性能实测：

| 方案 | 本轮判断 |
|---|---|
| C# / WPF | 作为 Windows 主路线；可直接组织 HWND/COM 互操作和持久 TextBox，不必为主浮层引入 WebView 边界。代价是 WPF UI 未来不能直接搬到 Linux。 |
| WinUI 3 | 可实现，但本轮重点不是新视觉组件；仍需细做原生浮层/焦点互操作。暂不额外引入另一套 UI/打包集成工作。 |
| Tauri / Electron | 可以做壳，但关键问题仍落在原生输入、焦点、IME 和按键 Hook；引入网页编辑器并不自动解决它们。当前不选。 |
| Rust / C++ 原生 | 可作为日后极小平台组件选项，但本轮不先承担整个 UI、生命周期和 FFI 的多语言组合成本。 |
| Qt / Python-Tk | Qt 是备选而非被否定；本轮不以跨平台 UI 复用为首要目标。Tk 的已知焦点风险不应成为正式架构负担。 |

采用 .NET 并不让跨应用焦点/IME 自动正确。选择依据是缩短 Windows 主链路的实现路径，而非声称换框架即可解决 PoC 问题。

### 进程与线程

```text
TypeLay.App（每用户桌面应用，不是 Session 0 Windows Service）
  ├─ WPF STA：Overlay / 持久 TextBox / Quick Panel / Settings
  ├─ Session actor：串行状态、请求版本、接受计划、UI 投影
  ├─ Input thread：消息泵 + 极短 Hook 回调 + 原子 permit
  ├─ 受限本地 IPC → TypeLay.WindowsBroker
  │                     └─ 无 UI 的专用 COM MTA：UIA、平台 provider、观察/写回
  └─ 受限本地 IPC → TypeLay.TranslationWorker
                        └─ 本地常驻模型、单次在途任务、latest-only 等待槽
```

UIA 的跨进程调用与事件订阅不放在 WPF UI 线程；COM 对象保持在专用 apartment，事件注册与注销在同一线程，异常/阻塞由宿主 watchdog 处理。微软明确提示 UIA 的 UI 线程和 apartment 生命周期问题。[R3]

每个 worker 采用明确帧大小、协议版本、request/session/config revision、超时与取消标识；生产 IPC 需要当前用户 ACL、核对实际子进程身份与随机会话凭据。不能仅因连接来自本机就可信。进程绑定 Job Object 随父进程退出，限制并发与重启频率。IPC 未在 M1 实现。

模型崩溃仅取消候选；broker 卡住立即撤销许可与 UI，不能堵塞键盘。写回 worker 超时可能已经完成修改，必须进入“结果未知”，不能另起 worker 重试同一写回。

### 输入区域检测与隐私

先查前台/焦点元数据，再走经过认证的 provider。身份包含进程 ID、进程创建时间、顶层 HWND、控件 HWND/UIA RuntimeId、provider 类型与会话生命周期；HWND 重用或控件重建必须失效。

元数据拒绝发生在 `GetText` / `GetValue` 之前：密码、只读、禁用、应用排除、PIN/安全码、机器语言区、不支持类型都不能先读再过滤。仅 `IsPassword=false` 不足以保证“非敏感”；仅应用名、窗口标题、DOM automation id 也不足以保证可靠支持。无法建立分类依据的区域不读取、不展示。

M1 的 fixture 注册表是测试宿主的可信协作元数据，不是面向任意应用的分类算法；正式网站/应用不要求安装扩展或主动适配。若某 provider 只能在协作 fixture 上工作，它不能因此获得真实应用认证。

### 文本、选区与写回

优先做普通 Win32 EDIT；UIA 采用 capability-based 认证：能读全文、可靠获得单选区/折叠 caret、绑定同一目标、可靠写回、正确恢复 caret，缺一项则该能力不开放。

所有核心偏移为 UTF-16 code units。UIA `TextUnit.Character` 不直接等于 UTF-16 索引；provider 必须建立可验证的映射，覆盖 emoji、组合字符、CRLF，不把 TextRange 端点整数化后猜测位置。无法确认的多选区或富文本结构不替换。

没有选区 → 替换全文；有选区 → 只替换绑定范围。人工编辑期间允许鼠标回到原控件时选区折叠，但必须确认整个原文与原目标未变，且没有另一段非空选区；保留原范围，不按新 caret 擅自转成整框翻译。

写回顺序：

```text
验证 Session/语言/模型版本 → 新观察目标、全文、选区、IME、epochs
→ 构造精确 WritePlan → 撤销可重复接受许可
→ 平台再次校验 → 定向替换 → 重读文本与 caret → 验证结果
→ 仅在 Verified 时静默结束并抑制自己的写回事件
```

不用剪贴板、不用 Ctrl+A/C/V、不注入 Enter、不执行表单提交。Win32 `EM_REPLACESEL` 可做选区替换，但不提供跨进程 compare-and-swap 事务；UIA TextPattern 也不是通用写入 API。[R7][R8] 前后校验只能降低风险，不能证明跨多次调用没有竞态。

如果平台确认**未发生任何修改**就失败，返回 `RejectedBeforeMutation`；从第一次改变选区开始出现不确定结果，返回 `OutcomeUnknown` 并隔离目标。隔离不因 Esc、暂停/恢复、重新轮询而解除；M1 内核保存的是身份元数据，不保存历史文本。正式服务的身份生命周期与隔离存储上限还需实现。

### 焦点模型：本轮最关键的 UI 工程

采用一个持久 Overlay HWND 和一个持久 WPF TextBox（不假设 TextBox 自身具有独立 HWND），不用整个浮层关闭/重建来切换“查看/编辑”。预览阶段不激活、不抢焦点、不进入 Alt+Tab；显式点击译文才允许激活编辑。

在处理鼠标激活**之前**，将当前 overlay HWND 纳入当前会话的 focus family，并进入编辑过渡；随后接收实际焦点事件。不能用“原控件失焦立即关闭浮层”作为统一规则，也不能只对整个 TypeLay 进程放宽，否则打开设置也会错误延续旧会话。

编辑器自己拥有文本、caret、鼠标选区与撤销栈。进入编辑时只初始化一次；编辑期间不重设 `Text`、不换 DataContext 导致控件重建、不用渲染回写覆盖原生编辑事件。Delete / Backspace / 方向键 / 拖选均由 TextBox 正常处理。

第一 Tab 只请求回源，进入 `ReturningToSource`。必须观察到**原控件真正获得焦点**且原文/绑定仍安全，才进入 `EditedTranslationPending`；下一次物理 Tab 才能接受。SetForegroundWindow/SetFocus 调用不是焦点确认，Windows 可以拒绝前台切换。[R4] 回源失败时不发送第二次动作、不强抢其它应用焦点，保留当前可编辑界面；用户仍可鼠标返回。

打开 Quick Panel / Settings、切换其他字段或 Alt+Tab 到其他应用均离开当前焦点组合并结束候选。Esc 只终止 TypeLay；源控件保持原文本，编辑器内 Esc 可请求回原控件，但不得模拟切到另一个应用。输入法正在处理 Esc/Tab 时优先交还输入法。

### 全局键盘与许可

后续使用专用消息泵线程上的低级键盘 Hook。回调只检查按键、事件标志、修饰键镜像、当前焦点/输入/IME epochs 和原子 permit，不调用 UIA、磁盘、模型、UI dispatcher 或阻塞等待。微软提示低级 Hook 必须及时返回，超时可能被静默移除。[R5]

必须维护 down/repeat/up 的完整归属：已放行的 Tab 按住后不能因为此刻候选就绪而吞掉重复键；接受只触发一次；注入事件不能冒充用户物理接受。修饰键跟踪不能只依赖当前回调中尚未更新的异步键状态。[R5]

`AcceptanceGate` 当前只实现上述纯判定。permit 100 ms、观察 150 ms、debounce 300 ms 是可测量的工程初值；UIA provider 的秒级响应预算不能拿来延长可接受旧候选的时间。

**尚未解决的冲突**：在 Hook 放行/吞键决定之后再异步校验写回，可能吞掉一个最终不能接受的 Tab；也不能把它重放到已变化的焦点。短 permit 不等于解决原子性。生产 provider 在该问题解决/范围认证前不发放许可；这会限制可支持场景，不允许把它宣传成“完整 Tab 条件已满足”。需要行为变更时先向用户明确提案，不能默认改成偶尔吞 Tab。

### IME

内部保留 `Unknown / Clear / Composing`。只有明确 Clear 才能生成翻译任务和接受许可。Unknown 不翻译、Tab 放行，不展示 IME 术语。

自有 fixture 的 WM_IME_START/END 只说明该进程内观察逻辑；正式外部 provider 优先评估 UIA TextEditPattern 的 active composition 与事件，并根据真实输入法/控件组合认证。GetActiveComposition 可返回当前组合范围，但接口不存在/调用失败不等于无组合。[R6] 不把本进程 TSF sink、当前键盘布局、没有收到事件当成整个桌面 composition 的可靠证明。

真实微软拼音需覆盖开始输入、候选翻页/选字、提交、取消、拼音中 Tab/Esc、原文已有译文时新组合、点击浮层前后。后续日/韩输入法按明确范围扩展。没有真机证据时保持 not-run，不从原生 fixture 人工 SetProp 推断。

### 本地模型

M2 的首个集成基线建议为**独立可替换的 Argos worker**，因为已有旧项目的真实本地链路知识；不继承 Tk，不把旧 Argos 版本/模型品质当成新产品冻结标准。Argos 官方提供本地 Python 翻译库和语言模型包。[R9]

worker 可私有封装 Python runtime；客户端与安全状态机仍为 C#。若本地 CTranslate2/ONNX 等方案在后续小样本基准中更合适，替换 worker 而不是重写焦点/安全内核。本轮不平行开发多个引擎。

接口结果包含请求身份、模型/语言配置版本与候选；自动语言检测必须本地运行，already-target 返回 NoTranslation 而非改写。初期选定中文/英文方向验证，其他语言仅在模型可用时宣称支持；Auto 不能用“有汉字/无汉字”冒充通用检测。

冻结具体依赖、模型 SHA-256、许可证与分发清单之前不打包发布；CPU-only 必须实测，分别记录冷启动、warm p50/p95、峰值内存、快速输入/取消时的队列和 UI 响应。不得先写一个无证据的延迟承诺。

下载更新与翻译进程分开。推理时没有联网回退/自动下载逻辑；使用实际断网和受限网络配置验证推理。Python monkeypatch socket 不是 OS 沙箱证明。模型缺失显示不可用，不能偷偷用字典 Mock 返回“真实译文”。

### Tray / Settings / 日志 / 分发

WPF 应用在用户登录会话常驻；后续用 Shell_NotifyIcon 或受控 NotifyIcon 包装，支持 Explorer 重启后重注册、单实例和正常退出清理。左键 Quick Panel 按原型层级，开启/暂停、源/目标、交换、模型状态、设置、退出。Auto 源不可直接交换；方向交换不写回任何文本。

Settings 首批真正可用：全局开关、源/目标/Auto、应用排除、本地模型状态、开机启动明确用户选择、隐私说明。七个信息架构入口保留；快捷键自定义、完整风格与术语管理先显示不可用入口，不提供虚假工作状态。暂停后只保留托盘，不显示输入框弱胶囊（按已确认 Tray 原型）。

设置本地版本化文件、原子保存，只含配置。诊断默认无全文、译文、窗口标题、剪贴板、异常消息、请求对象序列化；只用受控事件码与时序/结果。默认不抓崩溃 dump，不建历史数据库。源文存活在当前会话内存；托管字符串不能承诺物理零残留清除。

发布阶段做普通用户安装、签名/更新、模型包校验、卸载残留和开机启动同意。初期开发使用 SDK 构建；首个面向用户分发包再选择稳定的每用户打包方案，不先做复杂更新器。

## C. 模块边界

| 模块 | 负责 | 不负责 | 当前状态 |
|---|---|---|---|
| Core / Session | 会话、版本、状态、人工译文绑定、候选有效性 | HWND / UIA / UI 绘制 | 已编写 |
| Core / WriteSafety、Utf16 | 全文/选区替换计划、纯校验 | 原子系统事务的虚假保证 | 已编写 |
| Core / AcceptanceGate | 原子许可和按键归属判定 | 安装 Hook、实时观察 OS | 已编写 |
| Core / Presentation、Placement | 用户文案投影、物理坐标候选矩形 | WPF HWND、监视器/DPI 枚举 | 已编写 |
| Platform.Windows | provider、身份、分类、IME、定位、定向读写 | 翻译与产品状态 | 当前仅自有 Native fixture adapter |
| App.Windows / Overlay、Input、Tray、Settings | WPF 持久编辑器、Hook、托盘与用户设置 | 直接访问任意 UIA 文本 | 待实现 |
| WindowsBroker | UIA MTA、超时/事件/IPC/进程隔离 | 将 HWND/COM 对象跨线程泄漏 | 待实现 |
| TranslationWorker / Model | 本地检测与推理、模型生命周期 | 云回退、默认历史 | 待实现 |
| Privacy / Diagnostics | 分类规则和受限事件、日志存储控制 | 默认全文日志 | 已有枚举/受限接口；实际分类与存储待实现 |
| tests / fixtures | 可复现测试与证据 | 以 Mock 冒充实机通过 | 已建立入口，见验证状态 |

先用少量项目保持边界，不为空目录各建一个程序集。待真实 UIA、worker、WPF 进入相应 milestone 时再拆独立进程项目。

## D. 阶段与验收

详见 `MILESTONES.md` 与 `TEST_MATRIX.md`。下一步先在 Windows 上构建当前 M1，再跑自有 fixture。通过后接真实模型与极小浮层，立即验证编辑焦点/IME 能力，不先把全部设置页做完。旧 PoC 证据只能给方向，不作为新代码的测试结果。

## 官方技术依据（2026-09-29 查阅）

[R1] .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

[R2] WPF overview: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/

[R3] UI Automation threading: https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading

[R4] SetForegroundWindow: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow

[R5] LowLevelKeyboardProc: https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc

[R6] GetActiveComposition: https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition

[R7] EM_REPLACESEL: https://learn.microsoft.com/en-us/windows/win32/controls/em-replacesel

[R8] UIA text pattern: https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview

[R9] Argos official documentation: https://argos-translate.readthedocs.io/en/latest/
