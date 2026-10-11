# 自动化测试模式

客户端支持仅在本次进程中生效的 `--automation-test` 启动参数。该模式允许 Windows 标记为软件注入的 Tab 和 Esc 参与产品处理。浮层和托盘明确显示“自动化测试”，浮层作为可单独选择的顶层窗口出现在任务栏，方便桌面自动化准确发送编辑按键；普通启动保持工具浮层样式并拒绝软件注入的接受按键，不在设置文件中保存这个开关。

```powershell
# 先正常退出已有客户端，再在项目根目录运行
$env:TYPELAY_HOME = (Get-Location).Path
.\app\TypeLay.App.exe --automation-test
```

该模式仍需本地模型就绪、已开启实验写回、源输入框有焦点、许可未过期、组合输入状态明确且已结束，并通过目标、原文、选区及写后核验。它不放宽字段支持策略，不关闭 B-01 非原子写回发布阻塞项。

在本地 fixture 或自建编辑器测试页签使用无敏感内容的测试文本。运行结束后退出测试进程，正常启动根目录 TypeLay.exe。

## 证据范围

- Core 中验证默认拒绝注入输入、测试模式的一次按下/释放所有权，以及测试模式仍拒绝错误目标、焦点变化、组合输入、未知输入法、修饰键、过期许可、旧快照和输入变化。
- 桌面自动化操作浏览器及原生浮层，才可以记录 Windows Hook、Broker、真实本地模型及写回的自动化闭环。
- Playwright/CDP 的页面键盘事件不自动等同于 Windows 全局键盘钩子收到的输入；填写文本、检查 DOM、提交计数和选区可以用于页面侧验证。
- 自动化注入按键、人工物理键盘、真实输入法候选交互分别记录，不能互相冒充。

工具拒绝浏览器操作或无法确认当前 URL 时，记录阻塞原因，不能把未执行的项目标为通过。

## 2026-10-10：当前焦点的生产链路回归

目标是自动跟随当前聚焦的普通输入框。以下字段及记事本都是用于覆盖不同控件能力的测试样本，不代表应用支持名单。

本轮在普通 Win32 Edit 自主复现生产 Broker 没有浮层：TextEdit 不可用，IMM 上下文为 0，输入法状态 Unknown。修复后这类普通字段值可生成只读译文；值可能包含尚未提交字符，不能据此进入编辑或 Tab 写回。已知组合态仍只读取已提交前缀。Unknown 预览显示“译文预览”和“Tab 交还应用”，不能拖选编辑；鼠标隐藏已实测。

默认启动原生 fixture，不传 Hook 测试参数，即可使用正式客户端、独立 Broker 和真实模型观察其普通字段：

```powershell
# 需先构建；本窗口不注册专用适配器，也不替换生产 Broker
dotnet .\fixtures\TypeLay.NativeFixture\bin\Release\net10.0\TypeLay.NativeFixture.dll
```

- “Continuous input (100 ms / committed-text replay)” 在 A 字段内每 100 毫秒追加已提交汉字，共 45 字，无标点。它直接修改自有控件，非物理键盘或真实 IME。实测 4953 毫秒，输入结束前 8 次非空预览，源框保持焦点，submitCount 为 0。
- “Focus switching (20 ms / 100 changes)” 请求每 20 毫秒切换两个自有字段，共 100 次，实测 3188 毫秒，最终 A 字段译文绑定正确、原文未变、submitCount 为 0。该回放只验证预览绑定与恢复，不验证写回原子性。
- 生产 Native 字段状态为 Unknown，Tab 正常移焦且原文未变。此项只能记录为预览及交还按键通过，不能记录为 Native Tab 写回通过。
- 记事本无标点输入从 18 字追加至 35 字，预览从 79 增长至 126 字符；正式 Broker 的 Tab 写回返回 Verified。工具按 n、i、space 驱动真实微软拼音，组合态保留 11 字已提交前缀及 58 字符预览，候选为 0，提交后预览更新。人工编辑的第一次 Tab 保留 12 字原文及 42 字符草稿，第二次 Tab 写回 42 字符并核验。该实际 IME 回归使用软件注入按键。
- 原生密码、只读和 PIN 字段在确认实际焦点后无浮层；不把此 UI 观察当作生产 Broker 零正文读取证明。

证据位于 [validation-2026-10-10](../artifacts/validation-2026-10-10)：final-production.jsonl、final-fixture.log 记录生产链路；ui-final.jsonl、ui-fixture.log 记录最终只读预览及排除界面；core.json、integration.json、native.json、python.log、source-checks.json、build.log 记录工程检查。NativeFixture 16 项测试另使用专用适配器，不经过生产 Broker。installed.json 核对四个已安装 DLL 的 SHA-256，runtime.json 记录普通启动恢复状态。

本轮 Chrome 连接器不可用，桌面启动又被自动审批拒绝，仅返回 blocked by policy，没有重试被拒动作。测试桌面只暴露一块 1260×840 显示器，真实多屏 DPI 未执行。B-01 非原子写回和 B-03 通用编辑后端缺失仍开放，不能将部分测试通过写成所有普通输入框已可用。

## 本机可重复的桌面回归

先构建 solution，在未锁定的交互式 Windows 桌面运行以下测试程序。正常客户端可继续运行；测试程序的字段身份与正常支持策略独立。

```powershell
$env:TYPELAY_HOME = (Get-Location).Path
# 实际 Windows Hook 与原生 Edit 写回，候选固定为 HOOK TEST，不调用模型
dotnet .\fixtures\TypeLay.NativeFixture\bin\Release\net10.0\TypeLay.NativeFixture.dll --hook-test-automation
# 同一 Hook 默认模式，软件注入 Tab 应正常移焦且不写回
dotnet .\fixtures\TypeLay.NativeFixture\bin\Release\net10.0\TypeLay.NativeFixture.dll --hook-test
# 正式 WPF 浮层、SessionController、Hook 和真实本地模型
dotnet .\fixtures\TypeLay.DesktopFixture\bin\Release\net10.0-windows\TypeLay.DesktopFixture.dll .\artifacts\automation\desktop
```

DesktopFixture 只接入两个自有 WPF 输入框的进程内测试后端，不经过浏览器 COM UIA 或 Broker 管道。它使用独立的 fixture-settings.json，不改用户设置。模型仍由正式 ModelClient 启动实际 worker，从已安装目录加载。窗口显示写回和提交计数，标准输出记录状态、选区范围和写后核验，不打印输入正文。提交正向对照可证明计数器有效。

“Continuous input without punctuation” 按钮每 100 毫秒向 A 输入框追加一个已确认汉字，共 47 字且无标点。它用于复现持续输入下的调度饥饿，日志包含 replayRunning、sourceLength、translatedPreviews 和 overlayVisible。该回放直接修改测试控件，不模拟物理键盘或真实中文输入法。持续输入期间的旧结果只能显示为预览；最后一次推理与当前原文完全对应后才可接受。修复前后的实际模型日志位于 artifacts/continuous。

2026-10-07 通过桌面自动化完成了整框写回、译文选区 Delete、人工编辑的两次 Tab、鼠标拖选后 Delete、Esc 取消、暂停与恢复，以及 UTF-16 范围 9..14 的中文局部替换。选区外 emoji 和第二行保持不变，所有接受动作的提交计数为 0。该历史测试日志和截图位于 artifacts/automation；范围见根目录 VERIFICATION_STATUS.md。

内置浏览器的 Playwright 测试只验证页面正常 Tab 移焦、中文选区、Backspace 和提交计数正向对照，不作为 TypeLay 浏览器写回通过证据。独立 Edge 启动被自动审批以 blocked by policy 拒绝；已有文件教程页面此前因无法确认当前 URL 停止自动化。没有重试被拒动作，也没有将这部分记录为通过。

## 历史记录：2026-10-07 正式 Broker 与真实输入法复验

用户在真实 Edge 再次复现连续翻译失败后，本轮修复生产捕获链路：同一窗口捕获期间收到键盘或控件焦点事件，仅撤销接受许可，保留预览；已知组合态从原生 TextPattern 的文档范围裁剪到 TextEdit 活动组合起点，获取已提交前缀。DocumentRange 使用独立的 TextPattern 接口，避免本机 TextEdit 继承接口读取失败。没有读取候选范围文字。

本机已用正式客户端、独立 Broker 管道与真实 CPU 模型验证记事本：无标点文字追加后译文更新；按 n、i、space 驱动真实微软拼音候选并提交；候选窗口保持打开时重启客户端，首次捕获即从 83 字已提交前缀产生 178 字符英文预览。组合态候选长度为 0，不允许写回。最大化记事本的浮层消失也已复现并修复，使用活动行的原生 UIA 几何定位。

这些按键由桌面工具注入，确实驱动了 Windows 输入法，但不等同于人工物理键盘。证据位于 artifacts/input-adapter；历史 WPF 回放、生产 Broker 和 Edge 用户反馈分别记录。Windows 搜索启动被工具按产品策略拒绝，Codex UI 被 computer-use 技能禁止，本轮均没有实测通过声明。可用 TYPELAY_TRACE 环境变量暂时记录状态、长度、阶段及耗时，不记录原文、译文、标签或窗口标题；正常启动不启用这些诊断。

该轮最终复验还包含：重启后在组合态取得 37 字已提交前缀并产生 121 字符预览，提交“你”后更新为 134 字符；38 字原文在人工编辑后第一次 Tab 保留草稿、第二次 Tab 写回 144 字符并核验成功。全局键盘版本仍撤销许可，独立 SourceUserEpoch 排除浮层编辑按键，避免将译文编辑误判为源文变化。100 毫秒按键许可与消费后 250 毫秒远程执行期限分开；全部 Broker 写前检查与写后核验保持启用。
