# Windows 待解决问题

Windows 尚未关闭的用户问题集中在直接替换、Chrome 新标签页和持续预览。现有受控检查已经缩小部分原因，但没有在用户原环境完成同路径验收。本页保留这些问题的现场范围、当前证据与关闭条件。

## 阅读范围

任务负责人和下一安排统一见[当前开发状态](../../engineering/development-state.md)。产品要求遵循[共同应用支持与完整体验](../../product/application-support.md)，代表性应用不构成支持名单。

本页只保留仍待关闭的问题。问题解决后，将仍有效的使用方法或平台约束并入对应现役文档，历史调查由 Git 与原始测试证据追溯。

## 用户观察与证据范围

用户表示使用当时远端最新 Windows 版，F9 可以翻译，但遇到以下现象：

1. 在 Windows 搜索框、Chrome 顶栏、Codex desktop 输入框和打开的 `.txt` 文档中，`Ctrl+Shift+F10` 没有翻译、替换或报错。
2. Chrome 顶栏能翻译，但点击 `+` 后新标签页中央的 `Ask Google` 框，在自动与按需模式都不工作。这不是 `google.com` 普通网页。
3. 译文似乎要等句末标点才出现，没有持续跟随输入。

当时只核对了远端源码，未取得用户安装包哈希或四处输入框的实际控件身份。因此源码候选原因、受控复现与用户根因仍须分开。新候选先核[完整包身份](README.md#当前可验证候选)，不能仅凭“最新版本”合并结论。

## F10 无反馈与写回能力

F9 与 F10 共用 ModelClient，F9 已能翻译时，不应先要求重新下载模型。F10 需要原目标的直接替换能力，UIA 可读和只读预览都不足以授写权。

当前系统 Edit 后端要求原生 x64、精确 `Edit` 类、非零字段 HWND、匹配 DLL 与目标线程 Hook，并核字段属性、IME、选区、正文和寿命。`.txt` 只是文件类型，不能证明使用经典 Edit。浏览器合作输入另有明确接入与 DOM 合同，不补齐未接入字段或通用 UIA 写回。

[UiaInputProvider](../../../src/windows/TypeLay.Broker/Input/UiaInputProvider.cs)在 IME Unknown 时可返回预览文字，F9 可在允许条件下唤醒，F10 仍要求 Clear。显式命令的无安全观察、焦点或版本变化、超时与服务问题，经共享反馈由托盘或快速面板说明。首次唤醒读取失败也会给一次提示，不自动重试。提示改善不等于写入覆盖已补齐。

[既有真实模型检查](https://github.com/DingJWuyu/Typelay/actions/runs/38441769291)在独立 x64 stock Edit 中，用一次软件 F10 获得 Verified、真实模型匹配、焦点保持和零提交。[多行练习检查](https://github.com/DingJWuyu/Typelay/actions/runs/38527315890)还验证了 TextPattern 明确可编辑时的直接替换，避免把缺 ValuePattern 一律误判为只读。这些局部结果不能关闭用户原来的四处输入框，也不证明现代记事本或物理 IME。

关闭条件是：在实际失败字段上取得准确能力与拒绝原因，再验证一次真实命令完成安全替换，或在能力不足时给出明确反馈。已发写入后设置变化只能报告结果待确认，不能宣称原文一定未改。开启实验 Tab 写回不能作为 F10 后备。

### 正式 Notepad 只有预览，没有源接受能力

保留的 CI518 产品包在经典系统 Notepad 已取得真实预览，但公开能力为仅预览，自动接受在发键前失败，人工稿接受依赖未运行。该包的独立 F9、Esc、直接替换、Undo 和退出通过软件输入检查，详见[当前候选](README.md#当前可验证候选)。

当前源码已把有序 source-message 接入正式 factory 与 Broker/Main。逐捕获 eligibility、源安装、来源、原许可和输入法仍各自验证；接线完成不代表正式 Notepad 接受已通过。旧包的能力缺口不能直接描述为当前源码恒为版本 0，当前构建也不能倒推旧包已有接受能力。

验收必须使用真实模型当前译文和同一公开浮层。明确仅预览时，记录 `required-capability-unavailable` 失败，不能改成“预期拒绝即通过”。真实提示存在时，仍要取得可信来源的实际接受证据。普通 SendInput 不作为有序来源阳性；隔离物理包也不代替正式 Notepad 产品链。

[B-01](architecture.md#b-01非原子写回与按键所有权)仍要求原键消费与正文事务协调。已观察到的低级回调超时泄漏否定旧 sent 路线；当前有序路线仍缺完整物理、真实 IME 与部署验收。接受后主动换应用的尾键交给新应用，不再要求跨应用持续抑制，但这项产品边界没有关闭 B-01。

TSF 的两条已否定假设仍约束后续方案。先在 OnTestKeyDown 占位、再由 KeyDown 拒绝恢复原 Tab 的实验丢失了默认导航；相同 view HWND 的 top context 也曾返回与原生 Edit 不符的空正文。相关写入实验已停止，后续方案仍须遵守[TSF 约束](development.md#tsf-正文来源约束)。这些结果不等于所有 TSF 路径不可行。

## Chrome 新标签页中央搜索框未工作

顶栏、内部新标签页和普通网页是不同目标。复现时先观察点击中央框及开始输入后，光标仍在中央框还是转到顶栏；不能只凭现象判断失焦或“不支持 Chrome”。

当前 UIA 捕获接受合格的 Edit、Document 与 ComboBox。ComboBox 只读预览同时要求可编辑 ValuePattern 和明确可编辑 TextPattern，选择器、密码、只读或未聚焦字段仍拒绝。该路径不授 Tab 或 F10 写权。实现入口见 [UiaInputProvider](../../../src/windows/TypeLay.Broker/Input/UiaInputProvider.cs)及[字段策略](../../../src/shared/TypeLay.Core/FieldPolicy.cs)。

[既有 NTP 实测](https://github.com/DingJWuyu/Typelay/actions/runs/38834570056)在隔离 Chrome 确认了内部 Document、唯一中央编辑框和真实点击焦点。生产 Broker 捕获及无标点追加通过，真实模型自动与单次 F9 模式均出现首译并刷新。实验写回关闭，零产品写请求、零 Enter 注入。场景没有提交计数器，且先用 UIA 定位，不能声称冷态首次发现、物理 IME 或全部提交路径通过。浏览器目录清理仍失败，因此整例仍失败。

普通网页的 DOM 合作路径不能关闭新标签页问题。扩展不向受保护的 Chrome 内部页注入脚本，这个边界由固定 Chromium 的 [restricted URL 检查](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/extensions/common/permissions/permissions_data.cc)说明。不能增加权限或关闭安全限制求通过。

关闭条件是在用户实际 Chrome 版本和真实输入法下，分别验证 NTP 冷态捕获、自动更新与一次按需唤醒。顶栏和普通网页只作对照。FieldPolicy 的地址栏词项并不匹配所有本地化标签，顶栏可翻译不能用来推断中央框的拒绝原因。

## 标点与实时更新

当前源码没有句末标点门。[Limits](../../../src/shared/TypeLay.Core/Domain.cs)设置 300ms debounce 与 500ms 最大调度等待，[Session.TakeTranslationJob](../../../src/shared/TypeLay.Core/Session.cs)按可用快照和待处理任务调度。500ms 不是译文显示保证，采集、模型忙碌和推理仍影响延迟。

模型返回完整结果，不逐 token 显示。组合期间依赖可获得的已提交前缀，缺少可靠前缀时可能没有新可译源。标点可能伴随提交或停顿，但尚无用户现场证据证明原问题由前缀读取造成。

[无标点真实模型回归](https://github.com/DingJWuyu/Typelay/actions/runs/38441769291)已观察到输入仍追加时首译出现，并最终更新至完整源。该次 UIA IME 为 Unknown，全程只读预览；它证明这条预览链不依赖标点，不证明物理拼音前缀或用户原场景已经通过。

后续复现分别记录无标点停顿、持续追加、真实组合、提交与标点后的表现。用源快照、任务派发、推理结束和浮层更新时间区分读取与显示延迟，不拿单次耗时承诺固定响应，也不以批量推理为停到标点才更新免责。

## 最小取证和验收

先确认实际包身份，再在原输入框复现一次。F10 没有系统通知时，查看快速面板的同次操作状态。已有 F9 能翻译的信息无需重复采集，正常模型也无需重新安装。

记录目标的真实控件类别、字段 HWND、Value/TextPattern 能力、IME 状态与拒绝阶段，避免收集用户正文。对浏览器保持顶栏、NTP、普通网页三组对照。真实输入法、选区、焦点切换、冲突和 Undo 均须按冻结规格验收。

Linux 主机只能完成静态或交叉编译，不能执行 Windows WPF 桌面。Windows Actions 提供受控桌面证据，用户实际应用和输入法仍须单独验收。

浏览器取证须区分同一进程的多个窗口。PID 不能推导精确窗口或网页字段，导航、窗口迁移和断连后须重新确认身份。身份未知时保持拒绝，不能由页面声明 HWND 或关闭浏览器安全限制来补齐。现役输入合同见[浏览器合作输入](architecture.md#浏览器合作输入)。

### 新标签页实机复现

在获准的 Windows 环境，用[已核候选](README.md#当前可验证候选)完整解压启动。记录提交、ZIP 校验和与 Chrome 版本，确认已有模型就绪。只用以下合成文字，不按 Enter，不开启实验性 Tab 写回。

1. 设置 English 到中文和自动模式。新建标签，点击中央 `Ask Google` 框，输入 `hello I would like to confirm tomorrow`，再追加 ` afternoon`。记录首译、更新、焦点与光标位置。
2. 切按需模式，在新标签输入同一句。按一次当前唤醒快捷键，默认 `Ctrl+Shift+F9`，检查首译与追加更新。
3. 再新建标签输入同一句，不先唤醒，按一次当前直接替换快捷键，默认 `Ctrl+Shift+F10`。候选未承诺 NTP 可写；能力不足时应保留原文并提示，且不发送搜索。

最小回报包括候选身份、Chrome 版本、模式与实际快捷键、光标位置、预览是否出现或更新，以及 F10 提示和原文是否变化。无需提供正文或个人 profile。需要区分组合态时，另用微软拼音输入合成短句，分别观察提交前后。

需要无正文诊断时，先退出 TypeLay，在完整包的 `TypeLay.exe` 目录运行：

```powershell
$trace = Join-Path $env:TEMP ("typelay-ntp-" + [guid]::NewGuid().ToString("N") + ".jsonl")
$env:TYPELAY_TRACE = $trace
Start-Process .\TypeLay.exe
Remove-Item Env:TYPELAY_TRACE
$trace
```

只复现上述合成输入一次，退出后从普通入口重开即可停止诊断。保留本次前缀的 `.jsonl`，不打包数据目录、profile、模型或整个临时目录。日志记录固定拒绝类别、IME、长度、版本和耗时，不记录输入/译文、窗口标题或键值；字段身份仍须另按授权取得。

## 独立 CI 待定位项

当前保留的问题包括 Windows 设置进度的首目标帧未在原 3 秒内出现，以及本地稿 Replace 的过期 claim 后 anchor 已无效。它们来自基线 CI587，具体原件与后续动作见[现役问题表](../../engineering/development-state.md#仍需目标平台验证的已有问题)。本轮重构没有取得对应 GUI 新证据。

早期 F9 回归曾分别出现无首观察和旧快照拒绝，不能合并成一个根因。首次只读捕获、反馈和交付末门已有针对性实现，当前仍按同次源身份、唯一原命令和原期限复验。旧失败不会因别的场景通过、加等待或重跑消失。
