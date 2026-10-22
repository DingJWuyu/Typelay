# Windows 待解决问题

## 阅读范围

当前任务、负责人和修复顺序统一见[当前开发状态与安排](../../engineering/development-state.md)。本文保留原始用户问题、真实场景证据和关闭约束，遵循[共同应用支持与完整体验](../../product/application-support.md)。共享产品定义只维护一份，各平台提供真实连接事实与本机实现；一端通过不代表双端完成。

本文服务于尚未解决的问题，不作为历史阶段报告保留。未来逐项补齐复现、修复和验收；全部解决后移除本文，将仍有效的支持范围和操作说明并入现役文档。

## 用户观察与证据范围

用户表示使用远端最新 Windows 版本，F9 可以翻译，报告三类现象：

1. 按需模式 Ctrl+Shift+F10 无翻译、无替换、无报错。测试位置为 Windows 搜索框、Chrome 顶部地址/搜索栏、Codex desktop 输入框、打开的 .txt 文档。
2. Chrome 顶部地址/搜索栏可翻译，但新建标签页的 Google 中央搜索框（提示文字为 `Ask Google`）在自动和按需模式都不工作。用户已明确这是点 `+` 后出现的新标签页，不是输入 `google.com` 打开的普通网页。
3. 翻译似乎必须输入标点才出现，只等最后一句标点，没有实时跟随输入的体验。

2026-10-13 只读核对的远端 `windows-preview` 为 `47d456f8ae4892a2564be0dfd15323cfb7fa3808`。这是源码版本确认，不是用户安装包哈希确认；尚未在用户 Windows 环境复现上述故障。以下实现事实不等于用户根因。

## F10 无反馈与写回能力

- F10 与普通/F9 翻译共享同一个 `ModelClient`，不需要专用模型。统一准备中英双向 Argos 模型及运行组件。F9 已能翻译，不应先让用户重复下载模型。
- Windows x64 原生系统 `Edit` 直接替换后端必须获得非零控件 HWND、严格匹配 `Edit` 类名、确认原生 x64、加载 `TypeLay.NativeInput.dll`、成功安装目标线程 hook，并通过输入法、选区、属性和生命周期校验。能读到 UIA 文本不等于能安全写回。另有经明确接入、身份与字段策略核验的生产浏览器合作路径，当前实现与检查边界见[Windows 使用说明](README.md#当前可验证候选)；它不补齐未接入应用或通用 UIA 写回。
- `.txt` 是文件类型，不能证明编辑器使用标准 `Edit`；现代记事本也必须检查实际控件，不能承诺此项必过。四个测试位置的实际控件类型均尚未取得。
- `UiaInputProvider` 在 IME 为 Unknown 时可提供只读 `PreviewText`，但不给写回资格。`KeyboardHook.TryInvokeShortcut` 仅允许 F9 在 Unknown IME 且有预览文本时唤醒；F10 仍要求 Clear。
- 显式快捷键被拒绝时，当前实现会产生不携带可执行源快照的拒绝通知。共享控制器说明输入法、焦点、版本、观察期限或服务问题，并由 Windows 托盘显示不抢焦点的提示。进行中和成功状态仍留在快速面板，普通输入过程不反复弹通知。旧配置、迟到和重复通知不能覆盖新命令。
- 真实 Windows 快捷键回归已在修改前复现“无安全观察时没有拒绝通知”；用户四处输入框究竟触发哪条拒绝路径仍需实际采集证明。提示修复不等于直接替换覆盖已经补齐。
- 模型未就绪、空文本、拒写和取消有可见反馈；已发出写回请求后更改设置只能说明结果待确认，不能宣称原文必然未改变。F10 本身不显示预览胶囊。实验性 Tab 写回不是 F10 的后备，开启它不能补齐直接替换能力。
- [真实 Windows 模型检查](https://github.com/DingJWuyu/Typelay/actions/runs/38441769291)已在独立 x64 stock Edit 上通过完整 F10 路径：生产原生采集确认能力后，仅注入一次 Ctrl+Shift+F10，取得 Verified 回执，原字段内容与同一本地模型的独立推理结果相符，焦点保持、未提交、未显示浮层，清理和提交计数正控均通过。295ms 是该次观察的按键到确认耗时，不是延迟保证。此结果只证明既有原生 Edit 后端和显式自动化模式，不能关闭用户四处输入框的问题，也不代表物理输入法验收。

按需练习的多行 stock Edit 已在真实运行中出现“预览成功、F10 写前拒绝”：回执为 `RejectedBeforeMutation / Sensitive`。采集阶段允许明确可编辑的 TextPattern，而原直接写回阶段把缺少 ValuePattern 一律当成只读；Windows 多行 Edit 正常可以只提供 TextPattern。当前修正对原绑定元素重查 [TextPattern.IsReadOnlyAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.textpattern.isreadonlyattribute?view=windowsdesktop-10.0)，只有明确 `false` 才继续；未知或混合属性仍拒绝，最终原生类、只读/密码、IME、寿命和文本原子检查不变。[同一真实多行练习回归](https://github.com/DingJWuyu/Typelay/actions/runs/38527315890)已确认修正：一次 F9 预览保持原文，再一次 F10 取得 Verified 回执并与同一真实模型的独立结果匹配，焦点及关闭后的用户设置保持。该结果不能扩展为通用 UIA 或现代记事本支持。

源码入口：`KeyboardHook.TryInvokeShortcut`、共享 `SessionController.Shortcut` / `QueueDirect`、`NativeEditTransactions` 与 `UiaInputProvider`。

## Chrome 新标签页中央搜索框未工作

不能仅凭现象判断是“没有聚焦”或“Chrome 不支持”。浏览器自身顶栏、内置新标签页与普通网页输入区域的 UIA 类型、值/文本能力、只读属性、输入法状态和焦点身份可能不同，需查实际采集拒绝点。原问题应在新标签页中复现，先确认点击中央框和开始输入后，真实焦点仍在中央框还是移到了顶栏。

`UiaInputProvider.Capture` 要求原始/托管 UIA runtime ID 一致，并检查字段元数据、边界和焦点。原入口只接受 Edit/Document；Chromium 155 新标签页真实输入框的 `role=combobox` 会映射为 Windows UIA ComboBox。这是与用户现象吻合的源码候选，不是用户机器的采集结论。

当前实现增加 ComboBox 的只读预览路径，但必须同时具有可编辑 ValuePattern 和明确可编辑的 TextPattern；普通选择器、密码、只读或未聚焦字段仍被拒绝。此路径不授予 Tab 或 F10 写回能力。[Windows 实际回归](https://github.com/DingJWuyu/Typelay/actions/runs/38427227828)已通过受控 WPF UIA ComboBox 到生产 Broker 的无标点采集、追加后版本更新，以及选择器/密码/只读拒绝；它没有启动模型，也不是 Chrome 新标签页实测。

[当前新标签页实测](https://github.com/DingJWuyu/Typelay/actions/runs/38834570056)已确认隔离 Chrome 的内部页身份、唯一中央编辑框及点击后的精确焦点。生产 Broker 的合成原文与无标点追加采集通过；真实模型的自动模式和单次 `Ctrl+Shift+F9` 按需模式均完成首译与追加刷新，原文、焦点与内部 Document 保持。实验写回关闭，写请求和 Enter 动作为零；新标签页没有提交计数器，不能据此声称已验证所有提交路径。该场景先用 UIA 定位，已预热可访问性树，不能外推冷启动首次捕获或用户输入法，也不证明快捷键在所有环境均可靠。测试身份观察已移离产品 WPF 调度线程。浏览器目录清理仍报告文件占用，自有 Job 归零与已持有进程退出已确认，锁持有者未知，整例仍红。

固定版本来源：[新标签页输入模板](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/ui/webui/resources/cr_components/searchbox/searchbox_input.html.ts#L24-L54)、[Blink 的可编辑组合框角色](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/third_party/blink/renderer/modules/accessibility/ax_object.cc#L6252-L6275)、[Windows UIA 类型映射](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/ui/accessibility/platform/ax_platform_node_win.cc#L1502-L1512)。检查到的中央框焦点/输入事件更新本地搜索框并请求补全，未要求迁到顶栏；其他版本/配置仍需观察，不能只从源码保证行为相同。

需同时核对一个反差：`FieldPolicy` 的机器区域正则实际排除 `address.?bar`、`omnibox`、`地址栏`，但并不匹配所有本地化标签，例如“地址和搜索栏”。顶栏能翻译不代表中心搜索框应被排除，也不能在未读现场属性时认定是哪条正则命中。

新标签页、顶栏和普通网页中的自然语言输入都应提供一致的用户体验，但必须分别建立真实复现和验收。普通网页 DOM 身份/事务接入是另一条覆盖路径，即使通过也不能关闭本项。Chrome 内置页面受扩展注入保护，不能靠增加扩展权限或关闭浏览器安全限制绕过；本项需验证 Windows UIA/浏览器原生可提供的能力。

该限制可核对 [Chromium 155 的 `PermissionsData::IsRestrictedUrl`](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/extensions/common/permissions/permissions_data.cc)：普通扩展不能向受保护的 `chrome://` 页面注入脚本。此来源说明接入边界，不证明当前 UIA 捕获失败的原因。

源码定位：`src/windows/TypeLay.Broker/Input/UiaInputProvider.cs:29-107,204-205`、`src/shared/TypeLay.Core/FieldPolicy.cs:14-26`。

## 标点与实时更新

- 源码没有“输入句末标点才允许翻译”的硬门槛。
- `Limits` 定义 300ms debounce 和 500ms 最大调度等待；`Session.TakeTranslationJob` 在有可用源快照和待处理工作时使用这些阈值。500ms 是调度条件，不是译文必须显示的期限，仍受模型忙碌、捕获和推理耗时影响。
- 当前模型使用完整 `translate_batch` 返回完整结果，不是 token 流式显示。“实时跟随输入”的产品要求不等于必须采用 token 流，但不能用批量推理为仅到标点才更新的体验免责。
- 组合输入期间走已提交前缀读取，无法获得可靠前缀时可能没有新可译源。标点伴随提交或停顿，可能让该路径恢复，但没有现场证据证明用户的问题由 IME 前缀失败造成。

[真实 Windows 模型回归](https://github.com/DingJWuyu/Typelay/actions/runs/38441769291)已在独立 stock Edit、生产 Broker/控制器/浮层和实际安装的本地模型之间跑通持续无标点输入：模型就绪后开始追加，945ms 时显示首译且输入仍继续，源从 28 字继续至 46 字，最终译文与完整源的真实推理结果相符，原文完整、零提交。本轮 UIA 的 IME 为 Unknown，始终只有只读 Preview、没有写候选；这证明该预览链不依赖句末标点，不证明物理拼音已提交前缀、新标签页或用户原场景通过。耗时是该次运行的观测值，不是延迟保证；使用方法见[真实模型持续输入检查](testing.md#无标点持续翻译的真实模型检查)。

浮层已避免重复文字测量、资源重设和多次同步窗口 resize，并修正无空格长词的测量换行方式；实际 Windows 布局回归覆盖首次定位、窗口移动、缩宽、字体、主题、展开和人工草稿选区。它减少了已观察到的显示阻塞，但模型推理、输入采集和桌面调度仍影响首译，不能据单次耗时承诺固定响应时间。

源码入口：`src/shared/TypeLay.Core/Domain.cs` 的 `Limits`、`src/shared/TypeLay.Core/Session.cs` 的 `TakeTranslationJob`、`src/windows/TypeLay.Broker/Interop/NativeUia.cs` 的 `ReadCommittedPrefix`、`src/shared/TypeLay.Worker/typelay_worker/engine.py` 的 `translate_batch`。

## 最小取证和验收

1. 在原输入框按一次 F10，记录托盘提示；系统通知不可见时查看托盘快速面板中的同一操作状态。不要重复要求已提供的 F9/应用信息，也不先要求重装模型。
2. 对 Chrome 顶栏、新标签页 `Ask Google` 中央框和普通网页输入分别使用相同已提交的普通测试文字作对照。新标签页先记录点击、开始输入前后的焦点是否转移，再记录实际焦点元素、ControlType、HWND 类名、Value/TextPattern 能力、IME 状态和具体采集拒绝位置。避免收集用户真实正文。
3. 对实时问题分别测试无标点输入后停顿、持续输入、中文组合及提交、加标点；记录源快照更新时间、已提交前缀是否可得、任务派发/推理结束/浮层更新时间，分清输入读取和推理显示延迟。
4. 先确认安装包对应版本，再用真实 Windows 输入链路重现；当前版本已有状态、IME 和前缀诊断，但没有后来 Linux 分支增加的 `shortcut-*` 入口 trace，不要要求旧包查不存在的日志字段。
5. 依据 Frozen 产品规格第4节、第9.2节和 Accepted `ADR-0002` 验收：普通网页/桌面自然语言输入可捕获和持续预览；无标点也可更新；F9 唤醒复用正常会话；F10 对具备能力的原控件完成一次替换、不抢焦点、不发送；拒写不绕过安全检查，并检验显式命令的失败反馈。测试应覆盖真实输入法、选区、焦点切换、冲突和撤销，不用源码检查或假模型证明产品可用。

先运行最接近用户路径的最小回归，保留失败证据，再按实际根因修复。Linux 主机不能执行 Windows 桌面交互；Windows Actions 可提供受控桌面证据，真实输入法和用户报告的独立应用仍须各自验收。

### 新标签页实机复现

以后有获准使用的 Windows 验证环境时再执行，无需现在准备设备。使用[已核对候选包](README.md#当前可验证候选)，先从托盘退出旧客户端，完整解压到新目录再启动；记录候选提交/ZIP 校验和与 Chrome 版本。确认模型就绪，已有正常工作的模型不必重新下载。只用以下合成文字，不按 Enter，不打开真实聊天，不开启实验性 Tab 写回。

1. 设置 English → 中文、自动模式，点 Chrome 的 `+` 新建标签页，点击 `Ask Google` 中央框，输入 `hello I would like to confirm tomorrow`。应出现译文；再追加 ` afternoon`，应更新且输入焦点仍留在浏览器。记录输入光标是在中央框还是顶栏，以及预览是否出现/更新，不把未完成的输入误当已提交搜索。
2. 切按需模式，重新新建标签并输入同一句合成文字。正常不应自动出现浮层；按一次默认 `Ctrl+Shift+F9`，应出现预览，继续追加后应更新。若快捷键已自定义，使用设置页显示的实际组合。
3. 再新建标签输入同一句，不先唤醒浮层，按一次默认 `Ctrl+Shift+F10`。当前候选没有承诺新标签页可直接替换：无法安全写回时应保留原文并给可理解的提示，不发送搜索。记录提示文字和原文是否变化；只读预览成功不能当作 F10 通过。

最小回报只需：候选提交、Chrome 版本、模式/实际快捷键、焦点在中央框还是顶栏、预览是否出现/更新、F10 提示与原文是否变化。无需提供输入正文或浏览器个人资料。若需要进一步区分 IME，可另用微软拼音输入合成短句，分别观察提交前后；顶栏和普通网页只作对照，不能代替新标签页验收。

<details>
<summary>失败时的无正文诊断（可选）</summary>

先退出 TypeLay，在完整候选的 `TypeLay.exe` 所在文件夹打开 PowerShell，执行：

```powershell
$trace = Join-Path $env:TEMP ("typelay-ntp-" + [guid]::NewGuid().ToString("N") + ".jsonl")
$env:TYPELAY_TRACE = $trace
Start-Process .\TypeLay.exe
Remove-Item Env:TYPELAY_TRACE
$trace
```

只复现上方合成输入一次，之后从托盘退出 TypeLay，再从普通入口启动即可停止该次诊断。仅保留本次路径开头的 `.jsonl` 文件；它们记录固定采集/快捷键拒绝类别、IME 状态、长度、版本和耗时，不记录输入/译文正文、窗口标题或键值。不要打包 `%LOCALAPPDATA%\TypeLay`、Chrome profile、模型或整个临时目录。诊断文件不导出字段名等 UIA 属性；若仍需现场控件身份，另由获准诊断取得。

</details>

[当前普通网页实测](https://github.com/DingJWuyu/Typelay/actions/runs/38836830477)在新建隔离 Chrome profile 中通过 DOM 就绪、单次真实点击与 DOM 焦点定位，首次译文出现前没有测试 UIA 访问。生产 Runtime 自然采集并显示真实模型首译，随后才独立核对 UIA 字段、Document 与原文，再确认下一次自然采集及浮层；原文和源焦点保持，实验写回关闭且没有产品写请求或 Enter 动作。这证明本轮普通受控网页无需测试预热即可自动翻译，不代表整机首次启动性能、NTP 冷态发现或物理输入法验收。原 90 秒总预算、20 秒准备上限和生产超时/恢复调度保持不变。浏览器目录清理仍因文件占用失败，后续 NTP 模型场景按清理门未运行；NTP 两模式结论来自上面的独立已验证运行。

独立 CfT 身份探针仍未完成真实握手。其最后一次自有窗口观察显示可见、启用且响应窗口消息；服务接受了两条扩展 POST，没有识别到页面 GET。导航启动快照只有一次，不能断言整个等待期的状态。未查到端口错配或 HTTP 握手等待自堵的确定原因。普通页自动翻译通过不证明 CfT 身份实验或用户本机的新标签页已可用；按浏览器实验的[停止边界](browser-identity.md#无事件或清理失败时如何定位)，没有新线索时不重复扩探针、增加等待或改变安全开关。用户本机的新标签页、无预热首次发现及实际输入法仍需验证。

## 独立 CI 待定位项

[CI run 38344023454](https://github.com/DingJWuyu/Typelay/actions/runs/38344023454) 的 Windows `SettingsRendering` / `ControllerDesktopScenario:117` 失败，断言为 `Returning to A requires a new explicit wake`。该轮 Core 191、Runtime 34、Protocol 29 项通过；不能因此宣称 Windows 桌面链路通过。

本轮控制器 trace 显示第一次 F9 的快照年龄为 140ms，已 `notice-enqueued`；第二次 F9 在 `atMs=313046` 被以 `future-or-stale` 拒绝，年龄为 156ms。当时 IME 为 Clear，内容版本 1==1、前台版本 2==2；现有观察期限是 150ms，因此**该次 CI 快捷键入口因快照超过期限 6ms 而拒绝**。这只定位该次 CI 的入口分支，尚未解释快照为什么超时，也不等同于用户 F10 故障根因；不得通过延长期限或重试掩盖失败。

证据包：artifact `11788680275`，SHA-256 `9eed74f6c66aaa5f56552cb1a3d58dce02d9ea54ba75adba420fffe0f940e3c4`。此项仍需后续复现与定位。

[CI run 38351805969](https://github.com/DingJWuyu/Typelay/actions/runs/38351805969) 又在同一设置回归的 `ControllerDesktopScenario:89` 超时，断言为 `The source must be observed`。该轮已通过输入服务就绪与 fixture 实际焦点前置，但在首次观察到源应用之前失败，尚未发送 F9。桌面 trace 只留下两条 `shortcut-configure`（最后两快捷键注册均为 true），没有对应的首次 `capture`；不能从这份诊断断言是 IME、许可期限或注册失败，也不能与前述第二次 F9 过期视为同一根因。

此轮只提交 Linux 测试前置修正，Windows 桌面失败仍保留，未通过重跑消除记录；先前相同 Windows 路径曾通过也不能替代这次失败的定位。证据包：artifact `11791695218`，SHA-256 `4b4715d3fe79effe607101ab62ec21433d092a7e26d47de65a20f432ff128754`。需继续区分采集失败、调度延迟和入口拒绝。

[CI run 38363808032](https://github.com/DingJWuyu/Typelay/actions/runs/38363808032) 在结构迁移后通过构建与 254 项共享/Windows DTO 测试，但 `ControllerDesktopScenario:93` 的首次 F9 唤醒失败。实际快捷键于 `atMs=392828` 进入，`reason=future-or-stale`、快照年龄 688ms，IME 为 Clear，内容与前台版本均相等。这次已确定是入口旧快照拒绝，尚未解释产生旧快照的原因，也不能据此宣称结构迁移完全无回归。证据包：artifact `11795703671`，SHA-256 `b4adaab30847d5809800c1cc34908daeed6551cfacacbe722fb2482e8e9b703d`。保留此失败，在相同入口检查采集各阶段耗时及拒绝原因。
