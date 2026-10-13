# Windows 待解决问题

## 当前安排

先完成 Linux 主线及 trunk 整合、结构和文档整理，再通知用户亲自检查。**必须等用户检查后回复，才开始 Windows 修复。** 当前仅记录问题和只读核查结果，不修改 Windows 代码、CI 或分支。

本文服务于尚未解决的问题，不作为历史阶段报告保留。未来逐项补齐复现、修复和验收；全部解决后移除本文，将仍有效的支持范围和操作说明并入现役文档。

## 用户观察与证据范围

用户表示使用远端最新 Windows 版本，F9 可以翻译，报告三类现象：

1. 按需模式 Ctrl+Shift+F10 无翻译、无替换、无报错。测试位置为 Windows 搜索框、Chrome 顶部地址/搜索栏、Codex desktop 输入框、打开的 .txt 文档。
2. Chrome 顶部地址/搜索栏可翻译，但页面中心搜索框在自动和按需模式都不工作。
3. 翻译似乎必须输入标点才出现，只等最后一句标点，没有实时跟随输入的体验。

2026-10-13 只读核对的远端 `windows-preview` 为 `47d456f8ae4892a2564be0dfd15323cfb7fa3808`。这是源码版本确认，不是用户安装包哈希确认；尚未在用户 Windows 环境复现上述故障。以下实现事实不等于用户根因。

## F10 无反馈与写回能力

- F10 与普通/F9 翻译共享同一个 `ModelClient`，不需要专用模型。统一准备中英双向 Argos 模型及运行组件。F9 已能翻译，不应先让用户重复下载模型。
- 当前直接替换仅有受限 Windows x64 原生系统 `Edit` 后端。必须获得非零控件 HWND、严格匹配 `Edit` 类名、确认原生 x64、加载 `TypeLay.NativeInput.dll`、成功安装目标线程 hook，并通过输入法、选区、属性和生命周期校验。能读到 UIA 文本不等于能安全写回。
- `.txt` 是文件类型，不能证明编辑器使用标准 `Edit`；现代记事本也必须检查实际控件，不能承诺此项必过。四个测试位置的实际控件类型均尚未取得。
- `UiaInputProvider` 在 IME 为 Unknown 时可提供只读 `PreviewText`，但不给写回资格。`KeyboardHook.TryInvokeShortcut` 仅允许 F9 在 Unknown IME 且有预览文本时唤醒；F10 要求 Clear，否则直接返回。
- 无新鲜快照、焦点/内容版本不符、观察超时、输入观察未就绪等也会在快捷键入口静默返回。它们不会产生新的 `OperationStatus`，能解释一种“F9 可用、F10 无反馈”的程序路径，但目前未证实用户触发的是哪条路径。
- 命令进入 `QueueDirect` 后，模型未就绪、空文本、翻译中、拒写及结果会记录到托盘快速面板。F10 本身不显示预览胶囊。实验性 Tab 写回不是 F10 的后备，开启它不能补齐直接替换能力。

源码定位（以上述 Windows 提交为准）：
- `src/TypeLay.App/Windows/KeyboardHook.cs:209-237`
- `src/TypeLay.Runtime/SessionController.cs:286-366`
- `src/TypeLay.Broker/Input/NativeEditTransactions.cs:25-47,72-119`
- `src/TypeLay.Broker/Input/UiaInputProvider.cs:151-198,273-278`
- `src/TypeLay.App/UI/QuickPanel.cs:170-174`

## Chrome 页面搜索框未工作

不能仅凭现象判断是“没有聚焦”或“Chrome 不支持”。浏览器自身顶栏与网页输入区域的 UIA 类型、值/文本能力、只读属性、输入法状态和焦点身份可能不同，需查实际采集拒绝点。

`UiaInputProvider.Capture` 要求原始/托管 UIA runtime ID 一致，只接受 Edit/Document 类型，要求 ValuePattern 或明确可编辑的 TextPattern，并检查字段元数据、边界和焦点。代码没有针对普通网页搜索框的禁令。

需同时核对一个反差：`FieldPolicy` 的机器区域正则实际排除 `address.?bar`、`omnibox`、`地址栏`，但并不匹配所有本地化标签，例如“地址和搜索栏”。顶栏能翻译不代表中心搜索框应被排除，也不能在未读现场属性时认定是哪条正则命中。

普通网页输入框和自然语言搜索框属于产品目标，应建立此路径的复现和验收；按控件技术能力划分适配边界，不能以每个应用都要适配为由跳过问题。

源码定位：`src/TypeLay.Broker/Input/UiaInputProvider.cs:29-107,204-205`、`src/TypeLay.Core/FieldPolicy.cs:14-26`。

## 标点与实时更新

- 源码没有“输入句末标点才允许翻译”的硬门槛。
- `Limits` 定义 300ms debounce 和 500ms 最大调度等待；`Session.TakeTranslationJob` 在有可用源快照和待处理工作时使用这些阈值。500ms 是调度条件，不是译文必须显示的期限，仍受模型忙碌、捕获和推理耗时影响。
- 当前模型使用完整 `translate_batch` 返回完整结果，不是 token 流式显示。“实时跟随输入”的产品要求不等于必须采用 token 流，但不能用批量推理为仅到标点才更新的体验免责。
- 组合输入期间走已提交前缀读取，无法获得可靠前缀时可能没有新可译源。标点伴随提交或停顿，可能让该路径恢复，但没有现场证据证明用户的问题由 IME 前缀失败造成。

源码入口：`src/TypeLay.Core/Domain.cs` 的 `Limits`、`src/TypeLay.Core/Session.cs` 的 `TakeTranslationJob`、`src/TypeLay.Broker/Interop/NativeUia.cs` 的 `ReadCommittedPrefix`、`src/TypeLay.Worker/typelay_worker/engine.py` 的 `translate_batch`。

## 恢复工作后的最小取证和验收

1. 在原输入框按一次 F10 后打开托盘快速面板，记录是否出现新操作反馈及准确文字。不要重复要求已提供的 F9/应用信息，也不先要求重装模型。
2. 对 Chrome 顶栏和中心框使用相同已提交的普通测试文字作对照，记录实际焦点元素、ControlType、HWND 类名、Value/TextPattern 能力、IME 状态和具体采集拒绝位置。避免收集用户真实正文。
3. 对实时问题分别测试无标点输入后停顿、持续输入、中文组合及提交、加标点；记录源快照更新时间、已提交前缀是否可得、任务派发/推理结束/浮层更新时间，分清输入读取和推理显示延迟。
4. 先确认安装包对应版本，再用真实 Windows 输入链路重现；当前版本已有状态、IME 和前缀诊断，但没有后来 Linux 分支增加的 `shortcut-*` 入口 trace，不要要求旧包查不存在的日志字段。
5. 依据 Frozen 产品规格第4节、第9.2节和 Accepted `ADR-0002` 验收：普通网页/桌面自然语言输入可捕获和持续预览；无标点也可更新；F9 唤醒复用正常会话；F10 对具备能力的原控件完成一次替换、不抢焦点、不发送；拒写不绕过安全检查，并检验显式命令的失败反馈。测试应覆盖真实输入法、选区、焦点切换、冲突和撤销，不用源码检查或假模型证明产品可用。

在获得用户检查 trunk 后的回复前，以上步骤仅是待办，不执行 Windows 修复或发布。

## 独立 CI 待定位项

[CI run 38344023454](https://github.com/DingJWuyu/Typelay/actions/runs/38344023454) 的 Windows `SettingsRendering` / `ControllerDesktopScenario:117` 失败，断言为 `Returning to A requires a new explicit wake`。该轮 Core 191、Runtime 34、Protocol 29 项通过；不能因此宣称 Windows 桌面链路通过。

本轮控制器 trace 显示第一次 F9 的快照年龄为 140ms，已 `notice-enqueued`；第二次 F9 在 `atMs=313046` 被以 `future-or-stale` 拒绝，年龄为 156ms。当时 IME 为 Clear，内容版本 1==1、前台版本 2==2；现有观察期限是 150ms，因此**该次 CI 快捷键入口因快照超过期限 6ms 而拒绝**。这只定位该次 CI 的入口分支，尚未解释快照为什么超时，也不等同于用户 F10 故障根因；不得通过延长期限或重试掩盖失败。

证据包：artifact `11788680275`，SHA-256 `9eed74f6c66aaa5f56552cb1a3d58dce02d9ea54ba75adba420fffe0f940e3c4`。此项保留待后续复现与定位，不改变等待用户检查 trunk 后回复再修 Windows 的安排。
