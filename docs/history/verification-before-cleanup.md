# 验证状态 · 2026-10-10

## 最新核对 · 2026-10-10

**验收目标是自动跟随当前聚焦的普通文本输入框，在连续输入时更新翻译，并用 Tab 接受。应用名称和教程字段只是代表性测试场景，不构成支持名单，也不要求用户判断控件类型。当前尚未达到所有普通输入框完整可用的目标。**

2026-10-10 已自主执行新的桌面复现、修复和回归，无需用户操作。正式客户端、独立 Broker 和真实本地模型在普通 Win32 Edit 中复现无浮层：TextEdit 接口缺失、IMM 上下文为 0，快照一直为 Unknown。现允许从普通字段值生成明确的只读预览，未知输入法状态仍不会成为写回候选。已知组合态仍只翻译已提交前缀，不读取候选范围文字。未知状态的普通字段值可能包含尚未提交的字符，不能将其称为已确认文字。

- 持续翻译：正式 Broker 的 45 字无标点回放，目标间隔 100 毫秒，实测 4953 毫秒，输入结束前生成 8 次非空预览；源框保持焦点、提交计数为 0。该回放在控件内追加已提交字符，不能代替物理键盘或输入法验收。
- 焦点切换：两个自有字段请求每 20 毫秒切换，共 100 次，实测 3188 毫秒；结束后显示最终 A 字段的译文，原文未变、提交计数为 0。只验证预览绑定和恢复，不能据此关闭写回竞态。
- 写回与编辑：记事本无标点追加从 18 字增长为 35 字，预览从 79 增长为 126 字符；Tab 将 35 字原文替换为 126 字符，Broker 返回 Verified。人工编辑后第一次 Tab 保留 12 字原文及 42 字符草稿，第二次 Tab 写回 42 字符并返回 Verified。
- 实际输入法：工具按 n、i、space 驱动微软拼音。组合态提取 11 字已提交前缀，接受候选为 0；提交“你”后源文为 12 字，预览更新为 37 字符。这是实际 IME 的软件注入回归，非人工物理键盘。
- 界面及排除：只读预览显示“译文预览”和“Tab 交还应用”，不能编辑时阻止鼠标形成误导选区；未知或组合态不显示 Esc 快捷键承诺，鼠标隐藏已实测成功。密码、只读和 PIN 测试字段在确认实际焦点后均无浮层；此项不等于证明生产 Broker 零正文读取。
- 工程：Release 构建 0 警告、0 错误；Core 114、Integration 48、NativeFixture 16、Python 36、源码检查 8 项通过。第一次构建被正在运行的 fixture 锁住，退出后重建通过；失败日志保留。NativeFixture 16 项使用其专用适配器，不能冒充正式 Broker 的写回通过。
- 安装与清理：App、Broker、Core、Protocol 四个已安装 DLL 与最新 Release SHA-256 一致。测试客户端及 fixture 已退出，根目录启动器恢复普通模式，诊断变量关闭；本轮记事本测试页签保存为证据后关闭，既有页签保留。证据存于 artifacts/validation-2026-10-10。此前被自动审核拒绝删除的旧发布副本仍保留，没有重试删除。

**未完成的通用能力：** B-01 非原子 UIA 写回与异步拒绝消耗 Tab 仍开放；新增 B-03 明确记录缺少可靠输入法状态或文本写回接口的普通字段目前只有预览，不能实现通用 Tab 接受及完整浮层编辑。未暴露可编辑文本接口的自绘输入框也尚无后端。这些是实现缺口，不能将要求改成让用户选择“合格输入框”。

本项目已上传至 DingJWuyu/Typelay 的长期分支 `windows-preview`，本地跟踪同名远端分支；`main` 保持原提交 `09fe613a606073876483b51446211030a66a9597`。2026-10-10 的 [GitHub Actions 运行](https://github.com/DingJWuyu/Typelay/actions/runs/38106663687) 在提交 `6bd13aa0d5f66890dd21979a8ed051aab8366715` 上通过结构检查、Python 测试、Windows 编译、逻辑回归及自包含打包。工作流已升级到 Node.js 24 并固定官方发布 SHA，旧运行时弃用提示消失；此结果不代表云端执行过真实桌面、物理 Tab、IME 或模型推理。机器记录见 [github-actions-windows-preview.json](evidence/github-actions-windows-preview.json)。

浏览器回归未执行完成：Chrome 连接器不可用，桌面 Chrome 启动又被自动审批拒绝，返回仅为 blocked by policy；没有重试被拒动作。当前测试桌面只暴露一块 1260×840 显示器，真实多屏 DPI 未执行。此前禁止操作某个应用的限制仅影响该测试场景，不限制产品的通用输入目标。

### 本轮证据索引

| 结果 | 本轮文件 | 证据范围 |
|---|---|---|
| 持续输入、100 次焦点切换及记事本写回 | [final-production.jsonl](artifacts/validation-2026-10-10/final-production.jsonl)、[final-fixture.log](artifacts/validation-2026-10-10/final-fixture.log) | 正式客户端、独立 Broker、真实模型；回放是程序修改自有控件，记事本按键是软件注入 |
| 最终只读预览及排除字段 | [ui-final.jsonl](artifacts/validation-2026-10-10/ui-final.jsonl)、[ui-fixture.log](artifacts/validation-2026-10-10/ui-fixture.log) | 桌面观察确认实际焦点、无浮层及鼠标隐藏；不证明生产 Broker 零正文读取 |
| 构建及逻辑回归 | [build.log](artifacts/validation-2026-10-10/build.log)、[core.json](artifacts/validation-2026-10-10/core.json)、[integration.json](artifacts/validation-2026-10-10/integration.json)、[python.log](artifacts/validation-2026-10-10/python.log)、[source-checks.json](artifacts/validation-2026-10-10/source-checks.json) | 构建和确定性检查，不代替桌面验收 |
| 原生专用适配器回归 | [native.json](artifacts/validation-2026-10-10/native.json) | 16 项自有 Win32 fixture 专用适配器，不经过生产 Broker |
| 安装与普通启动 | [installed.json](artifacts/validation-2026-10-10/installed.json)、[runtime.json](artifacts/validation-2026-10-10/runtime.json) | 四个 DLL SHA-256 对齐；普通进程及其 Broker/worker 运行，非完整 UI 验收 |
| 机器汇总 | [final-summary.json](artifacts/validation-2026-10-10/final-summary.json) | 从实际日志与进程观察汇总，保留未完成项 |

以下章节保留 2026-10-07 及更早的历史证据，不应把旧测试计数或旧启动状态当作本轮结果。

## 历史结论 · 2026-10-07

本机已完成 Windows 构建、安装和真实模型推理；用户已实测 Edge 教程 A · Message 与 B · Comment 均出现浮层和译文。此前的无浮层故障由 Broker 在托管 `TextPatternRange.GetText` 中发生访问冲突导致，已改用原生 UIA 文本范围调用并复测成功。

用户已确认 Edge 教程中物理 Tab 整框替换成功，Submit count 仍为 0。随后在浮层拖选译文并按 Delete 时，实际删除了网页原文的一个字符，且拖选期间出现频闪、选区中断。已修改点击聚焦、编辑期间暂停网页选区轮询、焦点交接时保留源输入框绑定、超时不反复隐藏以及位置未变时不重设窗口；修复版客户端及 Broker 已发布到项目 app 并重启。用户随后反馈本轮测试都成功，浮层拖选、选区删除、编辑后的两阶段 Tab 及网页局部选区替换记录为用户实测通过。

本轮已新增显式自动化模式，并自主完成真实 Windows Hook 和真实模型桌面回归。正式浮层的选区 Delete、两次 Tab、鼠标拖选后 Delete、Esc、暂停/恢复和局部写回均通过；写回后的提交计数为 0，提交按钮正向对照可递增计数。该桌面回归使用自有 WPF 字段后端，不经过浏览器 UIA 或 Broker 管道。原生 Hook 对照使用明确标注的固定候选，普通模式拒绝注入、测试模式允许的实际按键行为均已验证。

完整日常使用验收仍未完成：独立网站、快速焦点竞态和多屏 DPI 等边界待测。Edge 自动启动被自动审批拒绝，未认领浏览器自动化闭环。本轮真实微软拼音在记事本已完成部分验收。UIA 非原子写回竞态和异步拒绝可能消耗物理 Tab 的发布阻塞项仍开放。源码默认关闭实验写回，本机此次为受控教程测试开启。

上一轮只修复了 WPF 回放的持续输入调度饥饿：每 100 毫秒追加一个已确认汉字、共 47 字无标点时，输入期间出现 6 次新预览，最终 Tab 与编辑回归通过。该回放不经过浏览器 UIA，也不模拟真实中文输入法。用户随后在真实 Edge 仍复现失败，因此不能据此认领真实 Edge 已修复。

本轮补修正式捕获链路与输入法读取：同窗口捕获期间收到按键或控件焦点事件仍保留预览，接受许可立即撤销；组合态用独立 TextPattern 文档范围裁剪出候选起点之前的已提交文字，避免 TextEdit 继承接口读取失败。无需等待标点或捕获组合结束间隙。本机已用正式客户端、独立 Broker 管道和真实 CPU 模型验证记事本：无标点追加后更新；真实微软拼音候选启动、提交与取消；候选保持打开时重启客户端，首次组合态快照提取 83 字已提交前缀并生成 178 字符译文，候选长度为 0。最大化记事本不显示浮层也已复现并修复，改用活动行的原生 UIA 几何定位。

输入发现不再限制 Edge/Chrome 进程或自然语言标签。普通 Edit 和明确可编辑 Document 自动检测；值和范围不一致时先显示预览；仅有 TextPattern 的控件不会被整框 SetValue 写入。Codex 与 Windows 搜索不再被进程名单排除，但 Codex UI 受 computer-use 技能禁止，Windows 搜索启动被工具产品策略拒绝。Edge 文件教程仍因 URL 无法确认未完成本轮复验。三者均没有新实测通过声明。

记事本 Tab 回归发现许可期限被错误用于远程执行：按下时许可有效，排队后却到期。现保留 100 毫秒同步按键许可，为已消费按键设置独立 250 毫秒执行期限，派发前再次检查 Hook 输入和焦点版本，Broker 保持全部新快照校验，并在最终远程读后检查期限。修复后记事本整框 Tab 写回 Verified，83 字中文替换为 178 字符英文且光标核验成功。修复前拒绝记录仍保留，不将其标为通过；B-01 非原子竞态未关闭。

## Windows 本机实际执行

| 检查 | 结果 | 证据及范围 |
|---|---|---|
| .NET SDK 10.0.401 Release 构建 | 通过，0 警告、0 错误 | 本机命令输出；客户端、Broker、测试均编译 |
| Core / Integration / NativeFixture | 108 / 47 / 16 通过 | 新增预览能力、组合态已提交前缀、同文版本更新不闪烁、源输入与编辑按键版本分离及通用字段策略回归；NativeFixture 不能代替 Edge 或物理键盘验收 |
| Python / 源码结构检查 | 36 / 8 通过 | 逻辑与结构检查 |
| 模型安装 | 通过 | `%LOCALAPPDATA%/TypeLay/setup-self-check.json`；中英双向 CPU 实际调用 |
| 本机模型复用 | 通过 | 本机 Argos 1.9 双向包与安装权重 SHA-256 相同，安装锁记录本地来源 |
| Edge 单行、多行翻译预览 | 用户实测通过 | 本地教程 Message / Comment；不等同于独立网站支持 |
| Broker 恢复 | 通过 | 终止 Broker 后自动重新启动；修复后用户输入期间未继续崩溃 |
| 源码启动脚本 | 通过 | 复用本机 SDK，自动构建、逻辑测试、发布并启动；缓存再次启动通过 |
| 自包含客户端与 Broker | 生成并启动通过 | 项目 `app/`；Broker 在无 DOTNET_ROOT 环境握手并正常退出；根目录 TypeLay.exe 已启动 app 中的客户端与 Broker |
| 物理 Tab 整框替换 | 用户实测通过 | 用户确认原文被替换，Submit count 仍为 0 |
| 浮层拖选、Delete、编辑返回 | 修复后用户实测通过 | 用户确认本轮测试都成功；原文误删与拖选中断问题已复测 |
| 编辑后两阶段 Tab、网页局部选区替换 | 用户实测通过 | 本地 Edge 教程范围；用户反馈，无独立网站验收证据 |
| 自动化模式的实际 Windows Hook | 自动实测通过 | NativeFixture 模拟 Tab 整框及选区写回 Verified、接受 2 次且提交 0 次；Esc 1 次；默认模式模拟 Tab 移焦、不写回；候选为固定测试文本 |
| 正式浮层 / 会话 / Hook / 真实模型 | 自动实测通过 | DesktopFixture 整框及局部写回、选区 Delete、鼠标拖选后 Delete、两次 Tab、Esc、暂停/恢复；选区 9..14，emoji 和第二行保留；不经过浏览器后端 |
| WPF 无标点回放及编辑回归 | 历史自动实测通过 | 100 毫秒已确认文字回放，输入期间 6 次预览刷新；最终及人工译文写回 Verified，提交 0；用户随后仍复现真实 Edge 失败 |
| 正式 Broker / 记事本 / 真实微软拼音 | 自动实测通过 | 无标点追加更新、候选启动/提交/取消、组合态首次捕获已提交前缀翻译；工具注入实际驱动 IME，非人工物理键盘 |
| 最大化编辑器浮层 / 记事本 Tab | 修复后自动实测通过 | UIA 活动行定位，83 字中文整框替换为 178 字符英文，Broker 回执 Verified；无页面提交计数器，不冒称 Submit count 0 |
| 正式 Broker 的人工编辑两阶段 Tab | 修复后自动实测通过 | 首次回归发现编辑按键误增源版本导致草稿丢失；分离 SourceUserEpoch 后，38 字中文返回时保留 144 字符人工译文，第二次 Tab 写回 Verified |
| 内置浏览器 Playwright 页面侧 | 自动实测通过 | 正常 Tab 移焦、中文选区、Backspace、提交正向计数；不代表 TypeLay 全局 Hook 或浏览器写回 |
| 正常退出测试窗口 | 自动实测通过 | Alt+F4 后 fixture 进程与其模型 worker 退出，测试设置与用户设置隔离 |
| Edge 自动化闭环 | 未执行完成 | 独立 Edge 启动被自动审批拒绝，返回 blocked by policy；此前已有文件页面无法确认当前 URL；未重试被拒动作 |
| Windows 搜索 / Codex / 本轮 Edge | 未完成实机复验 | Windows 搜索工具产品策略拒绝；Codex 技能禁止；Edge 无法确认 URL；无绕过或通过声明 |
| 独立网站、多屏 DPI、完整物理输入法边界 | 未验收 | 记事本部分 IME 通过不能代替完整跨应用验收 |

## 初始生成环境实际执行的历史记录

| 检查 | 结果 | 证据及局限 |
|---|---|---|
| Python unittest | 36 passed | `evidence/python-tests.txt`。涵盖协议、UTF-16 边界、结构保护、模型库存完整性、缺模型子进程失败处理。结构测试中的替身仅测纯逻辑，完全不代表真实模型翻译。 |
| 源码结构检查 | 8 passed | `evidence/source-checks.json`。XML/文件引用、Python 语法、原始资料哈希、网络导入静态检查、安全策略文本检查、PE 头、fixture JS 语法、包卫生。**不编译 C#，不验证 UIA。** |
| 原生启动器 | 交叉编译成功；两次产物逐字节一致 | `evidence/launcher-build.txt`、`launcher-pe.txt`。Clang/LLD Linux → Windows x64 PE；没有在 Windows 执行。 |
| 本轮上传基线 | 与保留基线 SHA-256 一致 | `reference/CURRENT_INPUTS.json`。三份资料内容与 M1 保留副本相同。 |

## 初始生成环境未执行的历史记录

.NET solution 构建；Core.Tests、Integration.Tests 和 Windows.Tests 的执行；PowerShell 启动和发布脚本的真实执行；WPF/XAML 运行；安装 SDK/私有 Python/wheel/模型；真实模型 CPU 翻译；Edge 或 Chrome UIA 读写；物理 Tab down/up、重复按键、焦点交接；微软拼音的提交、取消、候选操作；窗口移动/滚动/多屏 DPI；首次安装/更新/中断恢复；像素级原型对比；签名/SmartScreen/安装体验；翻译质量与时延。

环境为 Linux x64。未找到 dotnet、C# 编译器或 Wine/Windows 桌面。向依赖服务器的网络访问失败，未取得 SDK 或模型字节。公开文档查阅只用于 API/版本设计，不构成已成功下载安装的证据。

## 证据文件说明

启动器仅在构建与逻辑回归都成功后写入 `%LOCALAPPDATA%\TypeLay\build\<source-hash>\build-complete.json`，其中 GUI、Tab、IME 仍为 `not-run`。模型安装器仅在执行实际 CPU 调用成功后写 `setup-self-check.json`；该自检不输出正文，不评估翻译语义质量。

`tools/validate.ps1` 产生 `artifacts/validation-*/result.json`，默认不运行交互式原生 fixture。`tools/publish-windows.ps1` 成功后才产生 `evidence/windows-publish.json`。这些未来本机结果不会自动覆盖本次原始验证报告，且不能消除安全阻塞项。

本轮自动化证据位于 artifacts/automation：core.json、integration.json、native-adapter.json、model-self-check.json、hook-default.log、hook-automation.log、desktop-final.log、desktop-proof.png、desktop-proof.txt 和 browser-proof.png。鼠标拖选首次从文本起点边缘操作未形成选区，复查从文本内部拖选成功并保持选区，随后 Delete 只影响译文；未把第一次操作记录为通过。后续完整 solution 构建在退出 fixture 释放 DLL 锁后完成，零警告错误。

持续翻译证据位于 artifacts/continuous：before.log、after.log、core.json、integration.json、build.log、desktop-proof.png。首次最终 Tab 未写回并正常移焦；重新确认源框焦点与就绪候选后接受成功，未将首次尝试标为通过。临时测试进程已退出。旧临时发布副本的批量删除被自动审批拒绝，仅返回 blocked by policy，因此旧构建目录仍保留，未重试被拒绝的删除动作。

连续翻译修复已发布到项目 app 并经根目录 TypeLay.exe 正常重启，没有 --automation-test 参数。App/Core/Protocol 的已安装文件与发布产物 SHA-256 相同，客户端、Broker 和模型 worker 已启动；测试窗口及其 worker 均退出。安装校验见 artifacts/continuous/installed.json，发布输出见 publish.log。持续回放统计见 replay-summary.json；本轮重新执行的 16 项原生控件与 8 项源码检查分别见 native-adapter.json 和 source-checks.json。

以上 artifacts/continuous 是上一轮 WPF 修复的历史安装记录。当前补修证据在 artifacts/input-adapter：core.json、integration.json、native-final.json、source-checks.json、build.log；client-trace.jsonl、ime-prefix.jsonl、final-trace.jsonl、final-ime.jsonl 和 editor-final.jsonl 分别记录生产捕获、首次组合态翻译、期限误用、最终组合态与编辑回归。最终组合态在客户端重启后，从 37 字已提交前缀生成 121 字符预览，随后无标点提交“你”后保留旧预览并更新为 134 字符。notepad-ime-proof.png、notepad-write-proof.png、notepad-editor-write-proof.png 保存对应截图。记录只有状态、长度、版本及耗时，不输出正文；截图用于本地实测证据。

原生 fixture 首次未取得前台焦点时产生 14 项失败；已修正前台前置条件，等待实际激活，无法取得则记为 not-run。实际激活后 16 项均通过，原失败文件 native.json 仍保留。临时文件批量删除此前被自动审核拒绝，旧发布副本仍保留，未绕过删除限制。

当前 App、Broker、Core、Protocol 四个已安装 DLL 与本轮 Release 构建的 SHA-256 一致，见 artifacts/input-adapter/installed.json。测试客户端与其子进程已退出，根目录 TypeLay.exe 已正常启动客户端、Broker 与真实模型 worker，客户端命令行无 --automation-test，诊断变量关闭。最终结果与限制汇总见 artifacts/input-adapter/result.json。
