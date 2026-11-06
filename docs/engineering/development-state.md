# 当前开发状态与安排

本页只维护现役任务。产品要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与最近 CI

用户已授权实施[目标架构](architecture-target.md)，当前分支是 `work/architecture-deepening`，基线为 `48baf7fe59a65a67a7a981470e259f01fdc7b4a4`。**本轮架构实施已收敛。代码提交 `dd48597bbd0d8764985d2207c76ea70d6a3207f5` 获得两位全新上下文 advisor 的独立确认，评分分别为 86.3/100、87.0/100，必须修正项均为零。** 后续提交仅整理评估与状态文档。本轮仅本地开发，未推送、合并、运行 CI 或发布，也没有操作用户桌面。

产品规格、已确认原型和有效 ADR 保持。评分只由 advisor 按[固定 rubric](architecture-rubric.md)给出，结论维护在[独立评估](architecture-assessment.md)。架构实施与完整桌面产品验收分别判断，以下未测能力不会因目录迁移或构建通过而关闭。

## 当前 DAG 与责任

完整依赖图及退出条件保留在[目标架构的迁移顺序](architecture-target.md#迁移顺序与退出条件)。

| 切片 | 负责人 | 当前状态 | 完成条件 |
| --- | --- | --- | --- |
| M0 基线与环境 | 主代理 | 已完成，基线 Runtime 1565 项通过，固定 SDK 与隔离验证环境可用 | 已区分保留路径、既有失败与未测平台能力 |
| M1 设置提交 | 主代理、`/root/implement_settings` | 普通设置、教程、浮层风格及 QuickPanel 已迁移；Windows 术语浮层、浏览器与删除撤销入口也已优先消费共享消息。新增 WPF 断言仅编译，未运行 | 两端不再推断提交点，已保存警告不随源窗口退休而丢失 |
| M2 Linux 输入所有权 | `/root/implement_linux_input` | 实现完成，Input 独占原生记录，受控 Input 221 项通过 | Overlay 退出 Input 私有记录读写，异步结果仍关联原连接 |
| M3 交互身份与呈现 | 主代理、`/root/implement_presentation_consumers` | 实现完成，共享投影与实际消费者已迁移，真实 DOM 34 项及三端原生编译通过 | 文案与显示刷新不再决定交互寿命，真实编辑与原生授权保持独立 |
| M4 会话与结算 | `/root/implement_session_operations`、`/root/implement_write_settlement`、`/root/implement_windows_write_retirement` | 实现完成，具体操作持有继续与退休责任；原生 GUI 验证范围见下文 | 主捕获退出专用阶段分支，原结果结算后才释放接收者 |
| M5 教程职责 | `/root/implement_session_operations` | 实现完成，进度与真实源权限分开，按需命令登记和退休归回所有者 | 进度不参与授权，公共会话不直接修改练习私有状态 |
| M6 清理与复评 | 主代理、`/root/implementation_advisor_r6`、`/root/implementation_advisor_r7` | 已完成，同一代码提交两次独立认可，评估与状态文档已同步，临时 worktree 和 trace 已清理 | 无未处理的必须修正项，两次独立认可，现役文档与临时资源收尾 |

实现代理与当前两位 advisor 均以 `fork_turns=none` 创建，请求配置为 `gpt-6-astra`、`xhigh`。工具只确认创建，实际配置未核实。advisor 不读取旧分数或另一位的结论，只读取固定 rubric、目标第 1 至 331 行、源码和原始证据；两位分别补充工程标准与需求符合性核查，同时独立评价全部八维。

主代理已核对 `artifacts/architecture/implementation-advisor-r6.json` 与 `implementation-advisor-r7.json` 的证据、指纹和算术，评估页已同步。本轮没有待修代码、待返回的 advisor 结论或运行中的自有测试。基线临时 worktree 与手工诊断 trace 已移除，构建服务器已退出；源码、有效回归、工具链和验证原件保留。

后续验证由主代理接续，在对应目标环境可用时沿现有入口执行少量受影响场景：Windows 术语告警与撤销、编辑回源和关闭结算；GTK4/Qt 成功交互与未用许可退休。以下平台缺口没有被本轮关闭，完整合作包、物理 IME、Undo 与视觉验收仍需要对应真实证据，不自动恢复此前模型采集、CI 或发布任务。

## 当前验证环境与交付范围

本机 .NET SDK 为 `artifacts/tools/dotnet/dotnet` 10.0.401。工程 Python、Ruff 与 Ninja 只在 `artifacts/tools/engineering/` 私有 venv。GTK3、GTK4、Qt6 开发库可用，三端原生源码已编译；Windows 可交叉编译，不能运行 WPF。Windows App.Tests 的 `-t:Compile` 通过，完整构建仍缺生产 `TypeLay.NativeInput.dll`，不报告完整测试项目通过。

以下原件保存在被忽略的 `artifacts/`。测试数量只描述运行结果，不代表完整平台覆盖。

| 验证 | 原件 | 已证明的范围 |
| --- | --- | --- |
| 最终 Runtime 1598 项通过 | `architecture/validation/runtime-final-implementation.trx` 与 `.log` | `46b6629` 的共享生产编排配合受控端口，不是原生桌面验收 |
| Core 320、Linux Input 221 项通过 | `architecture/validation/core-final.trx`、`input-final.log` | 纯规则和输入合同；最后菜单修正未改变 Core/Input 源码 |
| 菜单通知失败复现与修复 | `architecture/validation/style-menu-notification-red.{log,trx}`、`style-menu-notification-final.{log,trx}` | 公共 `StyleRequested` 加真实设置文件；先观察保存成功但反馈缺失，再验证菜单/风格 68 项通过 |
| Python 设置反馈 5 项通过 | `architecture/validation/style-menu-linux-feedback-green.log` | 真实 Python 消费方法保留共享结果；受控 Host 不证明系统通知可见 |
| 最新双端生产构建 | `architecture/validation/style-menu-linux-final-build.log`、`style-menu-windows-final-build.log` | Linux Host 与 Windows App 均零警告、零错误；Windows 路由新增回归仅完成编译 |
| Windows 术语消费者修正 | `architecture/validation/glossary-consumer-windows-build.log`、`glossary-consumer-windows-compile.log` | App 构建及 App.Tests 的 `-t:Compile` 零警告、零错误；本机无 WPF，未执行原生红绿复现或新增 GUI 断言 |
| 真实浏览器 DOM 34 项通过 | `m3-consumers/browser.log`、`browser-source-regressions.log`、`browser-write-settlement.log` | 隔离 headless Chromium 的呈现、交互与结算消费者，未连接用户浏览器 |
| GTK3 拒绝与未用许可退休 | `m3gtk/native-result.json`、`run.log`、`inside.log` | 既有 `native_e2e.py --gtk-refusal` 退出 0，覆盖 Unknown IME 拒绝、按键透传及精确单次 `writeRetired` |
| 正式 setup-only Linux 包与搬迁 4 项通过 | `architecture/package/build-info.json`、`architecture/validation/linux-package-build-with-ninja.log`、`linux-relocated-package.log` | 包来源 `9dd5ada`，干净源码；实际 worker/Host、中文空格目录安装重装、CLI 启退与保数据卸载 |

GTK3 使用独立 Xvfb、niri 和私有 D-Bus；Xvfb/xdotool 仅解包到 `artifacts/tools/x11/` 并核系统包签名，没有安装到系统。原件还含既有受控通知和直接写入边界，但不证明成功捕获、编辑、接受或原生 Undo。自有桌面测试进程已经退出。

搬迁包通过正式脚本准备并校验固定 CPU 运行库，没有下载翻译模型。它不含合作桌面 payload，且来源早于最后的风格反馈修正，不能当成最终提交的完整发行验收。首次缺 Ninja 的构建失败原件为 `architecture/validation/linux-package-build.log`；补齐私有工具后成功，不改构建规则或放宽检查。

**仍未验收：** Windows 本轮真实设置、写入退休与关闭；GTK3 成功捕获、编辑和接受；GTK4/Qt GUI；物理 IME、真实 Undo、多屏和逐页像素表现。完整合作 Fcitx 环境未准备。编译、受控端口、CLI 退出和 GTK3 Unknown 拒绝不能替代这些结果。

本轮最后修正的实际价值是：用户从浮层移除已有应用风格覆盖时，文件已保存而通知观察者失败，原消费者却遗漏反馈。频率未知，未证实数据丢失。修正复用既有共享消息及两端反馈入口，只允许本次提交的精确后继接收菜单消息；全局反馈保留提交事实，不猜原生可见性、不抢焦点、不重放保存。没有新增通用错误框架、传输确认状态或完整 GUI 矩阵。变更 C# 的格式检查、Python Ruff 与差异空白检查通过。

Windows 术语修正同属这一结果消费问题：浮层将 inactive 通知失败映射成普通成功，浏览器丢弃共享警告，删除撤销只读 `Saved`。三个现役入口已按源码调用链确认可达，修正只优先消费已有 `Message`，沿既有本地化与反馈路径呈现。保留原保存次数、表单身份、焦点与质量门；扩展现有 WPF 表单和撤销用例，不新增测试框架。本机缺少运行条件，不能将静态确认与编译称为真实 GUI 复现通过。

本轮完成条件是职责迁移、受影响验证、现役文档及独立实现评估。以下已有产品缺口仍需目标平台证据，不自动恢复此前诊断、模型采集、CI 或发布任务。

## 仍需目标平台验证的已有问题

这些问题来自基线 [CI587](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940)，不是本轮新头结果。它们仍限制产品验收；只有取得对应新证据才能关闭。具体产品缺口以[产品基线](../product/README.md)和[原型对照](../design/prototype-parity.md)为准。

| 已有问题 | 当前已知事实与后续验证边界 |
| --- | --- |
| Qt 目标语言搜索 | 实际 Composing 日语行的新 press 已到，clicked 等待失败；release 接收者与几何根因未明。验证原单次动作，不改原期限、次数或 IME 门 |
| GTK4 术语及配置 | 源端术语首例在 capture-more 到 retain-more 之间 fixture 退出，40 个案例未完成；另有配置窗口 readiness 时服务器焦点为 0、本地为 1 的分歧。不能以耗时或本地标志推断真实源失焦或 IME Clear |
| Windows 设置进度 | 首个目标帧未在原 3s 观察期限内出现，尚未到后续重定向断言。需沿实际 WPF 渲染定位，不放宽时限 |
| Windows 本地稿 Replace | 过期 claim 后原 anchor 已无效，尚未验证下一次 fresh 操作；需核实际取消与 claim 顺序，不续原 down 起算的 500ms |
| 普通输入框与浏览器 | 物理 Tab、完整中文 IME、不同显示器/DPI、完整混版及恢复路径尚未形成同头验收；软件注入、DOM 合同与受控端口不能替代 |
| 分发与视觉 | 搬迁后的完整发行包、真实原生 Undo/redo 与各端逐页像素一致性仍需对应平台证据；构建和静态检查不关闭这些缺口 |

## 模型能力边界

Index 仍是用户明确准备时的默认模型，保留已有 Argos/Hy 安装与选择。术语仅对已登记固定 Index profile 的中英双向开放；风格、另述及组合质量门不因架构整理而开放。安装、握手、采集和语言质量是不同证据。本轮没有采集新模型质量证据或改变发布行。
