# Windows 输入问题诊断

本页提供原字段取证与 Chrome 新标签页复现方法。未解决的 [F10 原现场 #25](https://github.com/DingJWuyu/Typelay/issues/25)、[新标签页 #26](https://github.com/DingJWuyu/Typelay/issues/26)及[持续预览 #27](https://github.com/DingJWuyu/Typelay/issues/27)由 Issues 维护。Windows 设置与编辑回源的 CI 问题分别见 [#16](https://github.com/DingJWuyu/Typelay/issues/16)、[#18](https://github.com/DingJWuyu/Typelay/issues/18)。

先核对[完整包身份](README.md#当前可验证候选)，再沿用户原字段复现。产品要求遵循[共同应用支持](../../product/application-support.md)，代表性应用不构成支持名单。Linux 主机仅读取 Windows Actions 证据。

## F10 无反馈与写回能力

F9 与 F10 共用 ModelClient，F9 已能翻译时，不应先要求重新下载模型。F10 需要原目标的直接替换能力，UIA 可读和只读预览都不足以授写权。

当前系统 Edit 后端要求原生 x64、精确 `Edit` 类、非零字段 HWND、匹配 DLL 与目标线程 Hook，并核字段属性、IME、选区、正文和寿命。`.txt` 只是文件类型，不能证明使用经典 Edit。浏览器合作输入另有明确接入与 DOM 合同，不补齐未接入字段或通用 UIA 写回。

[UiaInputProvider](../../../src/windows/TypeLay.Broker/Input/UiaInputProvider.cs)在 IME Unknown 时可返回预览文字，F9 可在允许条件下唤醒，F10 仍要求 Clear。显式命令的无安全观察、焦点或版本变化、超时与服务问题，经共享反馈由托盘或快速面板说明。首次唤醒读取失败也会给一次提示，不自动重试。提示改善不等于写入覆盖已补齐。

在实际失败字段上取得准确能力与拒绝原因。验证同一次真实命令是否安全替换，或在能力不足时给出明确反馈。已发写入后设置变化只能报告结果待确认，不能宣称原文一定未改。开启实验 Tab 写回不能作为 F10 后备。

### 分开验证 Notepad 的预览与接受

验收必须使用真实模型当前译文和同一公开浮层。明确仅预览时，记录 `required-capability-unavailable` 失败，不能改成“预期拒绝即通过”。真实提示存在时，仍要取得可信来源的实际接受证据。普通 SendInput 不作为有序来源阳性；隔离物理包也不代替正式 Notepad 产品链。

[B-01](architecture.md#b-01非原子写回与按键所有权)仍要求原键消费与正文事务协调。已观察到的低级回调超时泄漏否定旧 sent 路线；有序路线的物理、真实 IME 与部署验收由 [Issue #21](https://github.com/DingJWuyu/Typelay/issues/21)跟踪。接受后主动换应用的尾键交给新应用，不再要求跨应用持续抑制，但这项产品边界没有关闭 B-01。

TSF 的两条已否定假设仍约束后续方案。先在 OnTestKeyDown 占位、再由 KeyDown 拒绝恢复原 Tab 的实验丢失了默认导航；相同 view HWND 的 top context 也曾返回与原生 Edit 不符的空正文。相关写入实验已停止，后续方案仍须遵守[TSF 约束](development.md#tsf-正文来源约束)。这些结果不等于所有 TSF 路径不可行。

## 区分 Chrome 顶栏、新标签页与普通网页

顶栏、内部新标签页和普通网页是不同目标。复现时先观察点击中央框及开始输入后，光标仍在中央框还是转到顶栏；不能只凭现象判断失焦或“不支持 Chrome”。

当前 UIA 捕获接受合格的 Edit、Document 与 ComboBox。ComboBox 只读预览同时要求可编辑 ValuePattern 和明确可编辑 TextPattern，选择器、密码、只读或未聚焦字段仍拒绝。该路径不授 Tab 或 F10 写权。实现入口见 [UiaInputProvider](../../../src/windows/TypeLay.Broker/Input/UiaInputProvider.cs)及[字段策略](../../../src/shared/TypeLay.Core/FieldPolicy.cs)。

普通网页的 DOM 合作路径不能关闭新标签页问题。扩展不向受保护的 Chrome 内部页注入脚本，这个边界由固定 Chromium 的 [restricted URL 检查](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/extensions/common/permissions/permissions_data.cc)说明。不能增加权限或关闭安全限制求通过。

在用户实际 Chrome 版本和真实输入法下，分别验证 NTP 冷态捕获、自动更新与一次按需唤醒。顶栏和普通网页只作对照。FieldPolicy 的地址栏词项并不匹配所有本地化标签，顶栏可翻译不能用来推断中央框的拒绝原因。

## 标点与实时更新

当前源码没有句末标点门。[Limits](../../../src/shared/TypeLay.Core/Domain.cs)设置 300ms debounce 与 500ms 最大调度等待，[Session.TakeTranslationJob](../../../src/shared/TypeLay.Core/Session.cs)按可用快照和待处理任务调度。500ms 不是译文显示保证，采集、模型忙碌和推理仍影响延迟。

模型返回完整结果，不逐 token 显示。组合期间依赖可获得的已提交前缀，缺少可靠前缀时可能没有新可译源。标点可能伴随提交或停顿，但前缀读取是否导致原现场故障仍由 [Issue #27](https://github.com/DingJWuyu/Typelay/issues/27)调查。

复现时分别记录无标点停顿、持续追加、真实组合、提交与标点后的表现。用源快照、任务派发、推理结束和浮层更新时间区分读取与显示延迟，不拿单次耗时承诺固定响应，也不以批量推理为停到标点才更新免责。

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
