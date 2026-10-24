# Windows 测试与桌面验收

按[共享验证协议](../../engineering/testing.md)表述证据；本页只适用于 Windows，Linux 入口见其[开发与验证](../linux/development.md)。

## 普通外部桌面应用验收

`--verify-ordinary-notepad` 从完整发行目录启动真实 App，以当前 Windows 已安装的 `System32/notepad.exe` 和临时合成 `.txt` 文档为源。先核对原进程镜像、原生 x64 与唯一严格原生 `Edit` 控件（经典记事本的 UIA 类型可以是 Document，不与原生类名混淆）；不存在、转交其它进程、现代自绘控件或已有 Notepad 进程均失败，不回退 NativeFixture，不声称支持现代记事本或用户此前报告的四处输入框。

本入口复用同一轮 `continuous-translation` 已正式准备并验证的 Argos 文件，保持库存/代次，不下载模型；真实 App 再校验并加载。仅在 `%LOCALAPPDATA%/TypeLay` 原本不存在且没有运行中的 TypeLay 时独占创建测试数据。通过实际设置确认实验性 Tab 写回及切换工作模式，依次验证无标点原文的自动预览、一次 Tab、原生撤销、编辑后第一 Tab 回源且不写、第二 Tab 精确接受、按需唤醒/取消、一次直接替换和正常退出。应用有自己的消息循环、实际 UIA/Hook 与模型；软件注入模式仍不等于物理输入法或最终安全验收。

自动接受仅在发送任何接受键之前，读到同一 App 唯一浮层内唯一当前译文的明确公开 `required-capability-unavailable` 时保留该场景的 failed、原阶段及观察 facts，人工稿接受标为 `not-run / dependency-unavailable` 并记录依赖。随后重新只读确认同一预算未取消、原 App 存活、既有 worker 观察无故障、源身份仍有效、原文不变且精确原字段仍聚焦，才继续原按需唤醒、Esc、一次直接替换和 Undo；不恢复焦点或正文来补成准入。按需链成功后才执行原 `ordinary-exit`。未知/歧义、取消、其它异常和任何接受键调用后的失败均停止后续输入；即使独立场景成功，原接受失败仍使整体 exit1，不把拒绝或未运行算通过。`OrdinaryNotepadScenarioTests` 通过实际固定尾段与 `Report.Save` 验证这项编排和落盘聚合；它不证明 Notepad、模型或物理输入，公开状态分类仍由 WPF/UIA 回归另验。

```powershell
$fixture = 'tests/windows/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows'
foreach ($assembly in @('TypeLay.App', 'TypeLay.Runtime', 'TypeLay.Core', 'TypeLay.Protocol', 'TypeLay.Windows.Contracts')) {
  Copy-Item "artifacts/package/TypeLay/app/$assembly.dll" "$fixture/$assembly.dll" -Force
}
dotnet "$fixture/TypeLay.DesktopFixture.dll" --verify-ordinary-notepad artifacts/ordinary-notepad artifacts/package/TypeLay artifacts/continuous-translation
```

必须在前一个模型消费者已确认退出后串行执行。内层总预算 540 秒含 20 秒清理预留；全部业务共用原 520 秒 token，浮层局部 90 秒和正常退出局部 5 秒均受其截断，不续时，外层既有进程 owner 最多 585 秒，CI 步骤预留 10 分钟。`result.json` 记录逐段结果、实际系统应用版本/哈希与控件类别；`consumer-exit.json` 单独证明所有自有子进程退出；失败保留 trace 和结果，未测不得计为通过。只有 source、App 及失败启动时返回的全部 owner 均证明整个进程族退出，且没有创建/观察器未知状态，才删除自有数据；正常 root 退出不授权删除。便携契约回归另验证真实 root 已退出但 family proof 失败时保留临时数据。此入口已接入 `check.yml`；CI507 已实际进入独立按需链，但唯一 F9 在首租约前被 no-lease 拒绝，未进入预览、Esc 或 F10；首次只读捕获修复仍待后续精确提交的 Windows 实跑。步骤位于 continuous 与 browser 的退出均经独立校验后；browser 必须实际执行完毕，业务失败仍保留失败，但不单独阻断 Notepad。步骤先用当前工作流的 run/attempt/head 与各自输出 nonce 校验两份严格退出证明，任一缺失、未知或取消均不启动 Notepad；Hy production 的准入仍取决于同 run/attempt/head 与 nonce 的三份独立消费者退出证明，不要求 Notepad 业务成功，但任何原业务失败仍保留整轮失败；缺少经典原生 Edit 不跳过、不换成 fixture。原 Windows job 仍为 75 分钟，准备前的 admission 为完整后续链保留 60 分钟（准备 10、browser 10、Notepad 10、Hy production 6、Settings 16、缓存/上传尾部 7、打包 1）；前段超过 15 分钟即写失败 admission 并失败退出，不静默略过真实应用验收。CI 上传 `artifacts/ordinary-notepad/*.json` 与 `trace*.jsonl`，包括消费者退出证明。

## 首次无租约 Wake 回归

`FirstWakeCaptureTests` 的冷整链阳性以真实 stock Edit、生产 Hook/Router/Service 和认证 Broker pipe 验证无任何租约发布时的一次 F9，要求新真实正文、原字段/选区不变、零写能力/许可与零提交。旧普通 read 在按键后才发布时仍须独立发 `capture-wake`，不能借旧 body 或旧 epoch；此例同时核发请求时读取的当前排除列表。这两例已在 CI508 通过，不证明每次冷 UIA 都能在期限内返回。冷整链失败只补录原请求身份/t0/期限、公共 wire 写完与回包时间、notice 时间、status/focus/snapshot 是否存在，不推断 Broker 内部阶段，也不预热或重试。

`ACompletedWakeReplyIsCheckedAtActualDelivery` 独立验证末门：真实 Hook/字段/UI 变化保留，但 `IInputBroker` pending 口按本次原点请求构造明确合成回包，不借旧真实正文冒称新读。完成回包后才执行同根换字段、同根或另一顶层往返、源输入、Esc、配置或更新命令，再调用实际 `router.TryRead`。仅 Router 最后交付的 now 可控为原 t0+1；不变上下文须成功，独立 t0+151 须过期拒绝。发请求前、排队 current、Broker 与原 `ExpiresAt=t0+150` 均不换时钟、不延期限。Esc 核仅 content 版本推进，源输入/跨顶层往返核真实变化，并以拒绝后的相邻命令序号排除其它命令先退休。此层证明异步末门，不证明真实 Broker 读取时效；软件输入需显式 opt-in，不作为物理输入法证据。

`WakeCaptureBrokerTests` 通过真实 Broker/系统 Edit 核 fresh-after-clear、精确原点与混搭拒绝、已有 direct 绑定保留，以及真实 WPF 虚拟 UIA 字段的零正文读取拒绝；虚拟元素不因 root HWND 一致获得新入口。`BrokerQueueDeadlineTests` 使用真实子进程 pipe 核等待槽过期或发送前上下文撤销时不发送、不杀 active owner，以及已发只读响应超过命令期限仍排空、拒绝交付且后续连接可用。保留原 `SessionShortcutHookTests` 裸 Hook no-lease 拒绝与 `ShortcutFeedbackTests` direct-no-lease、stale/future epoch 阴性；不改正式 Notepad 的立即回源、唯一 F9 或原期限，不加内部 lease 等待、sleep 或重试。

CI508 原26项新增回归为23通过、3失败：旧末门理论的同根/跨顶层往返在全部 TryRead 排空后采样为155/184ms，包含最后空调用，不能据此确定生产末门的采样时刻或拒绝原因；sibling 在失效操作前已收到空 Snapshot，具体原因仍未知。上述确定性末门替换及两个对照尚未实跑，不能据替换测试宣称冷读问题已根治。本地受限环境未运行 MSBuild、桌面或 socket 检查，静态/格式检查不代表通过。

## 自动化层级

`dev.ps1 check` 在 Windows 验证构建、标准 xUnit 行为/契约测试、Python 行为/启动测试和格式/lint。
CI 在本轮构建成功后独立收集行为、格式、原生适配器和发行包结果；任一失败仍使任务失败。构建失败或取消时不运行依赖产物的后续验证，避免使用旧产物。
`dev.ps1 pack` 生成真实自包含 ZIP，再运行分发与进程 E2E。该流程不装模型、不打开用户文档、不通过假翻译器伪造 ready 状态。
`dev.ps1 native-e2e` 需要已解锁桌面，验证受控 Win32 适配器的读写、选区、拒绝与零提交契约。它同时约束 Hook 测试依赖的字段后端；前置条件失败是未执行，不是通过。

`TypeLay.App.Tests` 的浏览器采集回归使用运行机已经安装的 Google Chrome、独立临时配置目录和本地普通搜索页。它走生产 Broker，实际注入不含标点的文字并检查源快照更新；不预先打开特殊可访问性开关，不下载模型，也不把采集成功当作真实译文、写回或物理输入法通过。运行机缺少 Chrome 是前置失败，不静默跳过。

浏览器前置必须先确认本次自有进程家族中的唯一测试页面窗口、前台身份与页面输入焦点，不能将启动进程的 `MainWindowHandle` 或任意当前焦点当成浏览器目标。前置失败不执行正文采集，也不计为输入框支持结论。

普通搜索页回归将外部 Chrome 的单次冷启动、临时配置和页面焦点准备独立限制为 120 秒，沿用现役浏览器合作 fixture 的准备上限；页面就绪后才开启原 20 秒产品采集预算，保留原 6 秒观察循环；正在进行的采集仍受总取消 token 约束，不把循环阈值称为单次调用的硬上限。xUnit 160 秒是测试超时及合作取消预算，用于两阶段和既有诊断、清理余量，不是硬进程寿命保证，也不能中断同步 UIA 阻塞；外层执行器另负责进程截止。失败记录具体阶段；前置未就绪时正文标记 `not-run`，不重启浏览器、不重试、不跳过。此拆分纠正外部应用准备挤占产品验证预算的问题，不声称修复 Chrome 初始化慢或挂起；若准备到顶仍按前置失败处理。保留独立 profile、唯一窗口/前台身份、单次 SendInput 点击、最终 DOM 焦点确认及产品 150ms 观察权限，绝不扩大产品时效或将冷启动耗时当作采集性能通过。

新标签页仍使用原 20 秒共用回归预算，另用独立配置启动 `chrome://newtab`，在自有 renderer 内通过 Document 的 URL 元数据确认内部页身份，只定位该 Document 中唯一可见、启用、可聚焦的编辑控件，再以真实矩形单击。此例先用 UIA 预热浏览器可访问性树，不证明冷启动无前置的首次自动捕获；内部页身份、唯一控件或同一字段/Document/前台连续性不足均失败，转入顶栏明确报告 `focus-transfer`。两段非敏感无标点合成输入只验证生产 Capture 的正文更新与 epoch，不按 Enter、不读取建议；不代表真实模型、两种翻译模式或物理输入法通过，也不保证浏览器不会请求网络建议。

自有自动练习在 `--verify-continuous` 的既有 90 秒总预算内先执行：普通折叠光标、真实自动捕获与模型、当前译文与公开“Tab 接受”提示就绪后的一次软件 Tab、精确许可对应的 CompletedReceipt、正文/焦点/caret 与 Undo。没有 F9 或参考推理预热，不等待内部安装/发布时刻择时。`OwnedPracticeQueueTests` 另用实际自有进程、Hook 和私有管道验证默认注入拒绝、安装前键、FIFO、重复与退休；受控计划不是模型质量证据。队列用例先记录同一子进程未启用自动接受时的实际 Tab 行为，按正文、选区和焦点比较拒绝后的原消息效果；仅该用例启用有界、无正文的分发完成观察，普通生产和真实模型正链默认关闭。软件输入仍沿显式 Hook/子端测试许可，记录 `physicalIme=false`；生产默认拒绝注入输入，完整编辑能力与注入测试开关分离。[完整软件链检查](https://github.com/DingJWuyu/Typelay/actions/runs/38927941305)已验证首接受、点击改稿、Tab/鼠标回源、第二次精确接受人工稿，以及退休完成后真实点击 Finish、保存偏好、关闭与重新读盘。回源不写原框，人工稿、自然光标与新绑定分别核验。相关队列回归仍有失败，不能视为稳定性或整套验收通过。练习无提交计数器，不宣称已测零提交。

显式真实模型入口 `--verify-continuous` 复用同一模型和原 90 秒桌面总预算，在 stock Edit 与练习场景后验证同一 NTP 字段的自动预览及按需预览。两模式各检查首个真实译文与追加后的更新，按需模式只注入一次经当前设置核对的 `Ctrl+Shift+F9`，不等待发布日志择时或重试。`BrowserWindows` 是两个测试消费者编译链接的共享源，保留相同内部页定位、单次点击和焦点检查；局部 Broker 包装仅观察原返回与写请求，不修改生产请求、权限或调度。报告从已有 snapshot 记录 IME 与写能力，保留 UIA 已预热前置；实验写回关闭且没有练习授权。NTP 没有受控提交计数器，报告 `submitCounterAvailable=false`、零 Enter/提交动作注入、零产品写请求和观察到的源/Document 保持，不能将这些事实写成已测 `zeroSubmits`。浏览器停止或目录清理失败仍单列为失败，不抹去已完成的两个预览阶段事实。

同一真实模型入口在 NTP 场景前追加一个隔离普通页冷态自动首译检查：复用 DOM ready、唯一真实点击和 DOM 焦点前置，真实输入后只观察 Runtime 自然捕获与既有 Broker 恢复调度，首个可见参考译文出现后才在 MTA 复核字段和 Document；不增加测试 UIA 预热、手工捕获重试或等待。准备子上限仍为 20 秒，所有模型场景仍共用原 90 秒桌面预算；单列首译事实与清理失败，保留零写请求和无提交计数器边界，结果不外推到 NTP 冷态发现。

模型场景的重复 UIA 身份观察在 MTA 后台执行并逐次完整等待，避免测试祖先遍历占用生产 WPF 捕获调度线程；取消后不发起下一观察或输入，清理不会越过已开始的观察。原身份门、单次按键与产品时效保持不变，结果仅汇总观察次数和耗时，不据此宣称首次 F9 问题已修复。

定位时可单独选择真实场景，避免一个桌面失败挡住其它结果。在 Windows 已完成 Release 构建后运行：

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~BrowserCaptureTests
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~UnsavedShortcutDraftSurvivesSettingsRefreshAndPageNavigation
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ControllerDesktopScenario
```

设置渲染、快捷键草稿和控制器桌面场景共用测试套件的单个 STA/WPF Application，但分别报告结果。单场景失败会清理自有窗口；真正阻塞 STA 的系统调用仍可能令整套件超时，不能将后续未执行项算作通过。

设置默认窗口为 790×560。`SettingsRenderingTests` 在浅色与深色检查首屏的启用、工作模式和三个主题预览（含完整标签），按真实背景计算普通文字、选中导航与主题标签的 4.5:1 对比；保留全部七个页面入口。主题使用原生单选控件，通过 UI Automation 和实际窗口内的方向键检查保存、焦点及写入失败回滚。常规的长模式与接受说明在“使用说明”展开，模型未就绪时保留准备通知与原操作，练习为次级入口。截图、WPF 输入和准备失败/暂停回归须在 Windows 上实际运行；Linux 的跨编译不能替代这些证据。

浮层正文的 UIA `ItemStatus` 区分当前译文、编辑稿、待返回稿与翻译中的状态文字。普通应用观察器只把前三类当作正文结果；忙碌/失败提示、旧 revision 的保留预览和空闲状态不能作为预览通过证据。这个展示语义不授予写回权限，接受仍需独立的公开接受提示与原安全条件。

`OverlayRenderingTests` 检查实际 HWND 位置/尺寸、源焦点、长词换行、字体变化、短文重显及主题/展开/手动草稿。320/520 宽度的浅深色回归保留简短状态、编辑入口的工具提示和 UI Automation 名称，检查正文完整可见、单条编辑焦点边框与中性底色；原生指针回归检查 hover 提示随编辑资格撤回而消失、源焦点不变，并将预览/编辑截图写入 CI 已收集的 `artifacts/test-results/rendering/`。这些检查须在 Windows 实跑；Linux 跨编译不是视觉验收。测量必须与编辑框的 `TextWrapping.Wrap` 一致：[WPF 10.0.12 的 `FormattedText.Trimming` 实现](https://github.com/dotnet/wpf/blob/v10.0.12/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/FormattedText.cs#L1342-L1360)表明 `None` 才强制拆分超宽单词，默认省略会低估无空格文本高度。保留连续长词反例，不能用插入空格让回归通过；这些合成会话只验证展示，真实模型连续更新由下方独立模式验证。

浮层只在首次出现时使用 140ms 透明度淡入，遵循 Windows 的 [ClientAreaAnimation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.systemparameters.clientareaanimation) 偏好；更新、撤销接受资格和安全隐藏不等待动画。隐藏及重新显示前移除旧时钟，最终基础透明度为 1，以 [FillBehavior.Stop 与移除属性动画](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-set-a-property-after-animating-it-with-a-storyboard) 避免旧 HoldEnd 值影响后续显示。淡入回归仅替换系统偏好读取，不修改全局设置或模拟动画时钟；通过真实渲染帧、HWND 可见性与源焦点验证禁用动画、显示/隐藏/再显示、最终透明度及更新不重播。

同一目标、源框锚点、工作区、DPI、宽度及上下避让侧保持不变时，展开/收起和正文自然高度变化使用约 280ms ease-out。通过 [CompositionTarget.Rendering](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget) 调整同一编辑框的视口高度，并以每帧至多一次 `SetWindowPos(SWP_NOACTIVATE)` 更新 HWND；文字不缩放，保留左锚和上方底锚/下方顶锚。新正文、提示和编辑资格立即生效，不创建旧正文副本、不延长接受许可。重复刷新不重启，反向变化从当前几何接续；换目标、源框移动、宽度/DPI或避让变化直接落位。减少动态效果立即到终点，安全隐藏清空正文并撤除帧回调。

高度回归在真实 WPF 渲染帧读取 HWND 几何，检查多个连续中间尺寸、锚点/源焦点、固定字号与字形高度、底栏完整、反向/更新不回弹、Esc/安全隐藏后不复活、人工草稿选区/撤销及减少动态效果。最多五张真实展开帧 `overlay-height-expand-*.png` 和上下锚几何序列 `overlay-height-{above,below}.json` 写入同一 `artifacts/test-results/rendering/`；需检查实际图像以区分视口变高与缩放文字。相关回归须在 Windows 实际运行，跨编译与纯几何计算不能计为动态视觉验收。

## 会话接受与退出按键回归

`SessionShortcutHookTests` 在自有 WPF 输入框上调用生产低级 Hook，验证 F8/F9 与 F6/F7 重新配置后的按住、重复及成对释放。已消费旧键的松开仍归旧操作，新的绑定不能抢走它；实际源字段收到的 F12 正向屏障用于确认消息已经分发。测试租约是明确的受控输入，不调用写入或模型，不能证明生产字段、物理输入法或真实写回安全。

`NativeSourceControllerTests` 使用真实控制器、Broker 与源端 DLL，受控模型只提供固定译文。配置键回归覆盖 Tab/F7/F24、重绑后的原按键释放，以及原键旁路的源线程队列计数。Ctrl 场景另经真实 WPF 编辑窗口回源：源宿主正常消息泵完成 Ctrl 处理后才推进新捕获，检查人工稿、新许可、释放/额外修饰/配置变化/内容键与退休拒绝。消息泵观察不是 `WH_CALLWNDPROC` 对 posted 键盘消息的覆盖证明；F23 后置输入标记仅界定被测按键的队列断言。各分支分别报告实际结果，回源前置失败不能算作 Ctrl 接受或拒绝已经验证，共享受控端口测试不能替代这些证据。

`ConfiguredControlUsesFreshSourceAndPreservesReturnedManualWording` 的回源失败保留最多 64 条变化观察，只从既有焦点读取和 Broker 响应比较源字段、进程寿命、UIA 元素、目标寿命和首次已观察线程，不补查身份；目标寿命不是原生 cookie 的证明。该测试另启用现有 `TYPELAY_TRACE`：它会多一次修正前的前台读取及拒绝后的窗口分类，不能宣称与未启用诊断的运行无扰动等价。Broker 退出后仅扫描日志前 1 MiB、最多 4096 行，将最多四条固定无正文 `return`、前八条 `capture` 及全局/回源后首次非原生捕获结果写入 TRX，明确截断或遗漏；不输出完整捕获日志。原回源操作、期限、断言和后续安全门不变，诊断不是根因修复。

普通 `KeyboardHook.GetFocus` 的 `Other` 结果在 `TYPELAY_TRACE` 下另记录最多八条 `source-focus-refused`：只分类当次原始前台样本为 `none/source-field/other`，不再读取前台、窗口身份或正文。`source-field-unconfirmed` 仍可能是原资格检查或末次前台复核失败，不细推为某一种原因。`queuedAtMs` 与 epoch 是排队时刻的诊断，不是原生原子采样；复用既有 64 条队列与 flush。每 Hook 最多再输出一条 `source-focus-trace-overflow`，`droppedAtLeast` 是该次 flush 已知的遗漏下界，此后未记录的样本不可推为没有发生。原 `focus-left` 日志的 `focus` 会再次观察，不能替代触发停止的那次结果；此诊断不保留未知焦点会话、不恢复旧目标寿命或写入许可。真实 WPF 分类回归对照 TRACE 开关、Source/Overlay/Other、重复拒绝的输出上限与隐私；不强制造出系统 NULL 前台，也不证明间歇回源失败已修复。

普通原生源侧术语保存的零写回源只在本次平台调用仍在途、精确 form/token/basis 与原 receipt 都有效时延后会话退休；期间撤销输入许可并停止普通捕获，不将系统切换中的 NULL 前台认作 Source。证明默认关闭，只由 Windows native 实现；原 Broker 请求的 800 ms 期限、外窗事件、epoch、配置、目标身份与取消条件不变，调用结束立即清除 pending，成功后仍须独立新捕获才能保存。`InlineGlossaryTests` 通过公开受控端口覆盖机器候选/人工稿、Other 期间无捕获或写权，以及无证明、撤销、窗口/配置变化、输入不可用和关闭拒绝；它不制造实际 OS NULL。Windows receipt 回归覆盖预期边前后原期限和单调失效，实际 `NativeSourceGlossaryTests` 的原 WM_NULL barrier 验证 pending 只覆盖在途调用，真实控制器保存用例仍负责完整桌面链；新增修复须以 Windows 实跑确认，普通编辑回源不借用此证明。

`NativeReturnCompletesRootActivationBeforeCorrectingAnotherField` 将两种实际前提分开：同 root 的 B→A，以及另一进程持焦后激活原 root、但宿主不自动恢复字段。后一种复用 fixture 既有 `--return-focus root`，对原一次回源在首个 WM_NULL barrier 内先断言实际 root 焦点，不加补焦点或重试。两种前提均保留成功、原 800 ms 到期及原 cookie 丢失三分支；成功允许 raw foreground 是原 root 或精确原字段，但须同时核原 GA_ROOT、PID/TID、精确 keyboard focus 与原 cookie，再重新捕获同一目标寿命与明确 Clear。原文、选区及其它字段不变，这两种 correction 前拒绝不再修正字段焦点。回源已结束且 barrier 释放后，复用既有 posted drained ACK 观察末态，不延长回源期限。有限无正文失败事实区分 raw foreground、GA_ROOT/ROOTOWNER、精确 keyboard focus 与原 cookie；祖先链不成为替代放行条件。此回归区分 root 激活与字段 correction，不证明 UIA 内部激活调用顺序。

raw foreground 与字段 keyboard focus 是不同观察。[Windows 的激活模型](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#active-window)以顶层窗口为 active；`SetForegroundWindow(field)` 没有保证任意 child 成为 raw foreground，不能将其当作真实 UIA 回源的可重复准备。回源回归保留实际出现原 root 或精确原字段时的全部身份、焦点、cookie、IME 与期限条件，不强制造出某一种 raw HWND 形状。`DirectReplacementStaysBoundToTheOriginalFieldAfterFocusChanges` 的单/多行两例另经真实 direct→ordinary capture 检查同一 native 目标、Clear、原文/选区和两项普通写能力为 false；清除普通观察后原 direct 任务仍能完成，不能从此前 direct 绑定继承普通写权。

现役 native API 边界回归直接执行生产事务源码，覆盖精确 rawfield 正向及 sibling/owned modal/根身份/PID/TID/焦点/cookie/实际 IME 缺失、错误、composing/期限负控，选区回调改变前台仍零正文写入，owned capture/接受仍拒绝 rawfield。这些 API 替身检查不构成真实 Windows GUI、UIA 重入或物理 IME 证据，也不替代 managed classifier/capture 的 rawchild 逐分支确定性覆盖；该覆盖仍未保证。跨进程回源和原 ConfiguredControl 端到端正向须在 Windows 单独执行。

同一 native API 边界现经导出 source hook 验证每个安装 grant 的源线程 HIMC/HKL 标量绑定：身份换值、最终控件读取/资格回调后换值、SETSEL 回调换值、SETCONTEXT 及组合 END 撤销、poll 已观察失配后恢复均不能复活旧 grant；只有精确退休后的新 grant 可重新取证。绑定只留在 source 本地，不扩共享 wire，也不复用 owned 专属门。真实 `NativeSourceActivationTests` 另覆盖自有 Edit 解除既有 HIMC、poll 撤销、恢复后旧 grant 拒绝及新 grant 接受，须由 Windows 实跑；无新 IME/context 创建、profile 切换或伪造组合态。HIMC/HKL 相等和 GCS_COMPSTR 长度零仍不证明 TSF profile 连续性或真实组合正例，默认生产接受保持关闭。

同一 portable 程序另经新增 `TypeLaySourceGetMessageProc` 公开入口检查：在首次 SETSEL 的系统 API 替身处，原调用者 MSG 已为 WM_NULL 且同 grant 已 claim；两次 NOREMOVE 不耗 invoked；下游嵌套检索不能抢同 grant，嵌套 revoke/install 不得提前 Quiet；下游或末次控件调用改变 MSG、withdrawal 赢、过期/寿命/正文/选区失配均不被本实现改写或重试；claim 后输入身份或选区变化拒绝正文仍保留消费承诺。sent-activation 对原 mask 各类 pending 提示仍零 claim/写入；retrieved-MSG 不读 queue hints，提示本身不替代实际状态重验。同一源的非接受键被实际 REMOVE 后撤销旧 grant，NOREMOVE/负 hook code 不撤权、不消费，恢复等值不复活。同一入口的尾键回归另核首 claim 后立即欠债、post-claim Rejected/Unknown 仍欠债、过期不解除同按次、tails 前 Retire 非 Quiet、repeat/up 后 Quiet；NOREMOVE/负 code/别字段不消费，busy/新首键/错 scan/未知输入 context 封旧消费而不猜清债，已知离域后恢复不复活。sent 激活正例不欠尾债。实际两行保留完整首键/暂停/Undo/冲突断言，只把尾键消费由 fixture 移入 native，新增提前 Retire 与无授权 Close 的拒绝对照；这不是跨字段/跨应用实际验收。两 admission 指针缺一的写前拒绝仅是局部不变量断言，不声称公开 hook 可构造该状态。系统 API 替身不证明 Windows 检索或完整 hook 链行为。真实两行入口与证据边界见[普通 source MSG 接线](development.md#普通-source-事务与同一-msg-的受控接线)，旧 owned 六行保持不变。

独立来源观察只通过 [显式 F8 手动维护入口](development.md#同一检索处的独立来源分类观察) 运行，不参与日常 CI、原两行接受或默认 suite 的执行选择。已有六消息/三阶段/十二次成功采样，两个来源点各自观察到 INJECTED → UNAVAILABLE → INJECTED 的有限分类变化，自动采集已停止；保留入口用于生产原始 MSG 来源检查的评估，仅在该边界实现、调用时点或平台契约变化时手动验证。必须以实际源 WH_GETMESSAGE 与原 GetMessage 的同次完整 MSG 配对、原 up after-dispatch 和原样正文/选区/焦点为数据完整性判据；两个来源点独立分类，不要求它们返回相同 origin，也不把一处阳性外推另一处。既有结果不证明不可伪造的 MSG 绑定，无物理、UIAccess 或 system 输入正例，不授予 IME、写入/消费/信任资格，三项生产/物理/绑定字段仍为 false；本地无 Windows 环境时明确未执行，不能用合成来源枚举替身证明 API 可绑定。

`OverlayRenderingTests` 同时覆盖默认 Tab/Esc 与无修饰 F1-F24 的编辑返回、退出、IME 事件、修饰键和旧键释放。第一次接受键仅请求返回原输入框；共享控制器仍须重新观察、取得许可并等待第二次接受。浮层动作携带显示时的 target、配置代次和会话 revision，迟到按钮与菜单不能借更新后的视图取得授权。缺少目标确认时可保留只读预览，但不得编辑或授予接受许可。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~SessionShortcutHookTests|FullyQualifiedName~OverlayRenderingTests'
```

当前配置合同支持默认 Tab/Esc、无修饰 F1-F24 和已保存的 Ctrl+F1-F24；公开组合选项复用四键设置的保存与关闭草稿流程。`SettingsRenderingTests` 验证重复键、写盘失败、后台刷新、切页和关闭保留，并确认保存只合并最新设置中的四个键，不恢复旧工作模式或启用状态。`SetupRenderingTests` 保留当前按需模式的默认 Tab 入口，并核自动模式使用 F8 时不再要求切换模式。普通输入框的组合链另使用独立 NativeFixture、生产 Broker、Hook 与真实模型，检查明确 IME 后的编辑回源保稿、编辑退出和源退出，原框与选区保持且写请求为零；不借自有练习授权。此新增场景待本轮 Windows 实际运行。任意修饰键组合及完整冲突覆盖尚未完成，不能据此关闭冻结规格 §18 的全部缺口。以上测试使用合成内容、受控会话或显式软件注入，Windows 实际结果须以对应运行记录为准。

`QuickPanelRenderingTests` 从快速面板的启用、工作模式、源语言、目标语言及交换控件，以及生产托盘菜单的点击接线进入真实设置保存。临时文件锁阻止写入时，检查已确认设置和磁盘不变、面板保留错误；模型刷新与隐藏重开不能抹去错误。解除文件锁后重新操作原控件应成功并清除错误，不变值的成功保存也须清除错误而不发布配置变化。模式使用原生单选控件，公开 UI Automation 与窗口内实际方向键、空格路径检查选中、单次配置变化及持久化；保存成功、失败回滚后均须保留键盘焦点，解除锁后直接用空格重试。后台刷新与初次渲染不触发保存。明暗主题各一张正常面板及原有窄面板错误截图保存在 `artifacts/test-results/rendering`。[090062a 的 Windows 实跑](https://github.com/DingJWuyu/Typelay/actions/runs/38700885927/job/116308546345)已通过该套件 9 项，包含新增模式渲染、写锁失败恢复和连续键盘操作 3 项；四张明暗正常/错误截图已核对。该证据限于受控 WPF 窗口与软件键注入，不替代物理键盘、输入法及完整桌面验收。

## 系统 Edit 前台事务回归

`ProductionForegroundWriteTests` 通过真实 Broker 管道与独立 stock Edit 验证前台写入；另直接使用生产 DLL 的私有事务 wire，区分 Broker 提前拒绝和目标 GUI 线程最后拒绝。当前选区已折叠但替换范围仍为原选区的单行/多行正例，应保留范围外文字、源焦点和零提交，并验证系统撤销。

负控包括同根另一字段、另一根窗口、仅选区变化、过期原许可、控件资格或寿命变化，以及原许可有效时排队、到目标线程才过期的请求。后者通过 fixture 自有事件屏障同步，仍须在原生私有 500ms 预算有效时拒绝；不得延长期限、增加盲重试或用 Broker 拒绝代替原生覆盖。缺 DLL 和旧 wire 不能获得当前原生写能力；旧 wire 不是整版旧 DLL 的运行证明。`ProductionDirectWriteTests` 保留后台 F10 在焦点位于另一字段时的既有行为。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ProductionForegroundWriteTests|FullyQualifiedName~ProductionDirectWriteTests'
```

`test_native_foreground_guards.py` 另直接编译同一生产 `transaction.c`，仅在独立测试程序中提供系统 API 替身，在 `EM_SETSEL` 返回边界改变焦点、选区或原授权时钟。它要求三个负控均不进入正文替换，并保留不变正控；没有生产测试开关。此算法回归由现有 `pack` Python 测试发现运行，也可单独执行 `python -m unittest discover -s tests/windows -p test_native_foreground_guards.py -v`。缺编译器、编译失败或负控失败都使测试失败；Windows 使用既有 LLVM，Linux 使用本机 C 编译器。

该算法程序也调用生产 owned 登记/开始/退休导出，验证退休后旧 op1/op3/op4/op2/op5 被拒及新 run 中旧 op3/op1 不能复活；仍不替代 Windows wire。`ProductionForegroundWriteTests` 的 owned 模式使用同一生产 DLL 和未修改的 Edit，覆盖退休 ACK 后旧请求拒绝、新 run 的 Undo 正例，以及已实际排队的旧事务先收束再 ACK；GUI barrier 不证明退休消息已在释放前入队。搬迁包练习测试实际执行生产 begin/retire/begin/close 握手，验证 DLL 随包可用与寿命换代。

同一 Python 入口在 Windows x64 另运行一次独立 WinEvent 可观察性 probe，需要已构建的 Release NativeFixture 和可交互桌面。它临时编译 `tests/windows/native/winevent_stack_probe.c` 为测试专用 DLL，仅由自有 fixture 显式加载，不进入发行包。显示并聚焦未修改的 stock Edit，先设置普通折叠 caret，再针对当前 PID/TID 注册两个精确类别的 [WINEVENT_INCONTEXT hook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)，只计数自有字段的 caret location 与 text selection 回调。唯一真实 `CallWindowProcW(EM_SETSEL, 4, 7)` 调用前后设/清栈内标记，不读正文、不注入事件、不嵌套消息泵。[EM_SETSEL](https://learn.microsoft.com/en-us/windows/win32/controls/em-setsel) 没有保证一定产生这些事件；单份 `artifacts/test-results/winevent-stack-probe.json` 的 `observed` 只证明该 API 栈内回调出现，`not-observed` 是完成观测后的负结果，前置、挂 hook、选区结果或清理失败为 `error`。测试成功只表示完成观测，不能替代生产 execute 或 owned 退休重入证明；无事件时不自动换场景或扩大矩阵。 [12d9101 的 Windows 实跑](https://github.com/DingJWuyu/Typelay/actions/runs/38848928499/job/116805630740)已观察到唯一 `EM_SETSEL` 栈内一次 caret location 回调，选区由 `0..0` 变为 `4..7`，两个 hook 均正常卸载。

`OwnedRetirementInsideSelectionCallbackRejectsThePendingWriteBeforeTextMutation` 另将该测试 DLL 用于单个真实生产 op5 反例：在已安装的生产事务 hook 前增加仅本 fixture GUI 线程的精确消息括号，只核本次 nonce 对应的只读映射头、cookie 和范围。真实 caret WinEvent 发生于括号内、请求仍 pending 且实际选区已为 `4..7` 时，才调用同实例生产 DLL 的 owned 退休导出一次。验收要求准确拒绝回执、正文未改、实际选区和焦点、寿命属性撤除及测试 hook 清理；`artifacts/test-results/owned-retirement-reentry.json` 保留固定结果。测试 DLL 在该用例开始时通过现 Python 构建入口生成，不依赖后续打包步骤；[fe53b3 的 Windows 实跑](https://github.com/DingJWuyu/Typelay/actions/runs/38851776444/job/116814398968)已通过一次真实同步退休、status 2 / SourceChanged 拒写、正文保持、实际选区 `4..7`、焦点保持、cookie 撤除和 hook / mapping 释放断言。整例仍因退出后删除测试 DLL 被拒而失败；这不扩大为其它同步重入情形已通过。

[654906ed 的 Windows 实跑](https://github.com/DingJWuyu/Typelay/actions/runs/38760488940/job/116510216177)已通过 22 项前台事务回归（含两项 owned 退休 wire）、5 项生产练习生命周期、11 项练习管道，以及 16 项受控原生适配器和 15 项打包检查。真实 DLL 构建、完整格式检查、搬迁包 begin/retire/begin/close 握手均通过；真实模型按需练习取得 verified，重启进度和关闭保留设置均通过。App 测试为 129 过、1 失败；唯一失败是 Chrome 页面/窗口/DOM 焦点启动前置，尚未进入输入捕获，完整作业仍未通过。真实 `EM_SETSEL` 内 owned 退休拒写现由上述单一反例覆盖；期限变化、其它失焦和重入路径仍不能由算法替身或该反例推定通过。此片不验证完整自动教程、练习授权、物理按键所有权或第三方应用支持。

## 暂停入口回归

`PausedInputTests` 通过生产 Broker 的 `observe-pause` 观察自有 WPF 字段。真实 UIA Value getter 计数在暂停期间必须为零，普通 capture 的正向对照必须实际读取它；密码、只读、敏感、排除、无可用 pattern 以及缺 ValuePattern 的 ComboBox 都须受控拒绝。观察只返回元数据，不能将无 Snapshot 的异常响应误当通过。

同套件的真实会话场景在安全字段间切换，覆盖密码隐藏、应用排除及恢复，并对自有浮层按钮仅注入一次鼠标点击，核对恢复后的持久化设置、源焦点及按需模式不变。`OverlayRenderingTests` 覆盖暂停 HWND 几何、文本/撤销清除、移动、旧代次点击和回到普通预览。共享 Runtime 另覆盖迟到观察、过期/旧目标和设置保存失败；本地可编译及静态审查不代表 Windows 桌面通过。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~PausedInputTests|FullyQualifiedName~OverlayRenderingTests'
```

## 受控工具的边界

`ModelInstallerTests` 通过生产 `InstallAsync` 验证安装失败与显式重试：HTTP 超时和用户暂停分别处理，CPU 自检失败不标为暂停，损坏模型元数据或布局的对应缓存会被清除；清除受阻时保留完整性首因并说明恢复阻塞。权限/空间/取消/CPU 失败不误删完好的下载。测试只替换 HTTP 与子进程系统边界，使用隔离目录、真实 ZIP/哈希/文件锁及自有受控子进程，不连接公网、不读写用户模型。受控小包只验证恢复，不能证明真实模型可用；实际安装/推理仍由下方独立模式验证。

`SetupRenderingTests` 从实际 WPF 准备按钮进入生产安装器，验证网络超时后的共同原因、下一步、折叠诊断、关窗返回重试，以及用户主动暂停后的继续入口。这两个场景不覆盖实际模型成功启动或迟到进度的 UI 验收。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ModelInstallerTests|FullyQualifiedName~SetupRenderingTests'
```

`tests/windows/support/TypeLay.Platform.Windows` 提供只访问已登记 fixture 控件的 Win32 适配器，服务适配器契约和键盘 Hook 回归，不在生产依赖链。其受控 IME 状态不等于外部应用的真实输入法状态。

原生辅助调用失败时只报告固定操作名、消息 ID 和 Win32 错误数值，不记录句柄、参数或正文，默认仍使用原 100ms 上限。`SendMessageTimeoutW` 返回零可能是[失败或超时](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw#return-value)，不能仅凭异常猜超时；当前 .NET 的 [`SetLastError=true`](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.dllimportattribute.setlasterror?view=net-10.0#remarks)会在调用前清零并缓存错误，数值零只表示没有更具体的原因。

仅下方真实持续输入场景生成合成源文字的单次 `EM_REPLACESEL` 允许 1 秒有限等待，避免把输入生成器的短等待误作产品响应时限；不重试，超时仍失败且不能确认消息是否已执行，必须确认自有源进程退出后才进入下一场景。产品观察、接受许可与写回期限不变，首译和更新耗时继续完整计时，正文、焦点和零提交断言不变；通过不代表保证 100ms 输入节奏。

`tests/windows/fixtures/TypeLay.NativeFixture` 不带 Hook 参数时是供生产 Broker 探测的真实 Win32 测试窗口；Hook 模式使用生产 KeyboardHook/Core 和固定候选隔离按键问题，不调用模型。

`tests/windows/fixtures/TypeLay.DesktopFixture` 使用生产浮层、SessionController、Hook 和真实 ModelClient，WpfFixtureBroker 仅替换源字段访问。它用于定位 WPF 编辑与焦点问题，不证明生产 Broker 的 UIA 或 IPC 行为。运行方法见下方“受控桌面工具”。

## 受控桌面工具

以下命令从仓库根目录执行，使用已构建的 Release 项目和已解锁 Windows 桌面。它们是交互工具，不会自动完成全部断言。

```powershell
# 普通 Win32 测试窗口：由正式客户端和生产 Broker 观察，无进程内替身
# replay / focus-churn 操作直接改变测试控件，不代表物理输入或真实 IME
dotnet tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll

# 生产 Hook 的普通模式对照：软件注入 Tab 应交还控件，不接受候选
dotnet tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll --hook-test

# 生产 Hook 的注入模式对照：固定 HOOK TEST 候选，仅隔离按键行为
dotnet tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll --hook-test-automation
```

需要真实 WPF 浮层、SessionController、Hook 和模型时使用 DesktopFixture。先通过正常客户端明确同意安装组件/模型；发行目录由 `./dev.ps1 pack` 生成，也可使用 `./dev.ps1 run` 生成的 `artifacts/dev/TypeLay`。退出其它测试客户端后运行：

```powershell
$previousHome = $env:TYPELAY_HOME
try {
    $env:TYPELAY_HOME = (Resolve-Path 'artifacts/package/TypeLay').Path
    dotnet tests/windows/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows/TypeLay.DesktopFixture.dll artifacts/desktop
} finally {
    $env:TYPELAY_HOME = $previousHome
}
```

TYPELAY_HOME 必须指向包含 worker/ 的完整发行根，不能指向源码根或 app/。DesktopFixture 使用独立 fixture-settings.json 和进程内 WPF 字段后端，不改变普通用户设置。它可以定位拖选、Delete、两阶段 Tab、连续追加、暂停/恢复和提交计数问题；这些结果不覆盖 UIA 或 Broker 管道。连续追加直接修改已提交文字，不模拟 IME。

### 无标点持续翻译的真实模型检查

已有 DesktopFixture 的显式 `--verify-continuous` 模式改用独立 NativeFixture 的 stock Edit、生产 Broker 和完整生产模型路径。它会联网调用生产 ModelInstaller，从相同官方来源下载运行组件和双向模型、检查完整性并执行 CPU 自检；仅用于已授权安装组件的临时 Windows 验证环境。普通 `check` 与 xUnit 不会隐式运行它。

```powershell
# 输出目录必须尚不存在；模型、缓存、设置和无正文诊断均保存在此目录。
dotnet tests/windows/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows/TypeLay.DesktopFixture.dll `
  --verify-continuous artifacts/continuous-translation `
  src/shared/TypeLay.Worker/run_worker.py `
  tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll
```

验收要求真实译文在独立原生输入仍继续追加时可见，随后继续追加并更新至完整最新源的真实模型结果，原输入保持完整、焦点留在源字段且提交计数为零。输入只含合成已提交中文，无句末标点；不模拟物理按键或真实 IME。最多 300 次、每次约 100ms 的有界追加和桌面阶段 90 秒截止用于识别停止响应，不承诺首译 90 秒符合交互体验；`result.json` 单独记录首译和更新耗时。额外完整源推理只用于核对最终可见结果，首次预览验收在这次核对前完成。

连续预览和直接替换是两个独立场景：复用同一次安装和真实模型，各自启动自有 stock Edit、控制器及浮层，共用 90 秒桌面截止。前场景失败会保留首错，完成清理且模型仍可用时才进入另一场景；清理失败、模型不可用或预算耗尽均明确标记，不能在污染宿主上继续。

按需场景使用固定合成中文，等待生产原生后端实证直接替换能力，在原文/选区/焦点完整时仅注入一次 `Ctrl+Shift+F10`。生产 Hook 和 Broker 仍独立检查 IME、身份、版本与期限；只有真实 Verified 回执对应的成功状态、原字段匹配同一真实模型的独立核对结果、焦点保持、浮层始终隐藏且没有额外提交，场景才通过。每个场景最后点击自己的提交按钮验证计数器确实工作。该阶段只覆盖 x64 stock Edit 与显式自动化按键，不代表物理 IME 或其他应用可写；各场景分别记录结果、首错及清理，任一步或清理失败都会令整个检查返回失败，不重试 F10。

整个模式有 20 分钟取消期限，执行者仍须设置外部进程/步骤上限处理不可取消的系统调用。成功返回 0，前置参数错误返回 2，其余失败返回 1；失败不重试或延长安全观察期限。结果、`trace*.jsonl` 和安装 CPU 自检可进入对应 CI artifact，模型、下载缓存、原始输入及译文不上传。`TYPELAY_TRACE` 开启时，浮层最多记录 32 条耗时至少 50ms 的同步渲染阶段样本到 `.overlay.jsonl`，默认关闭。

### 按需快捷键诊断

`TYPELAY_TRACE` 的既有 `shortcut-key`、`shortcut-publish`、`shortcut-revoke`、`shortcut-configure` 只记录固定操作名、状态、时钟与代次。显式命令由 `commandGeneration` + 正数 `commandSequence` 关联 Hook 的拒绝/入队及 Runtime 的 `shortcut-rejected` / `wake-*`；没有另造 CommandId，Broker 的请求 `Id` 也不是这个命令序号。重复键、修饰键等尚未形成命令的诊断，其序号为空。

`leaseCommandGeneration` 是 Hook 发布时采样的配置代次，`leaseObservedAtMs` 与 lease epochs 来自原观察。它们只用于辨认发布、实际撤销或 configure 清除的租约，不增加授权检查或延长时效。Runtime 的 `input-revoked` 记录原撤销调用的固定原因；`lastCommand*` 只是最近处理命令的上下文，不代表该命令导致撤销，`focus=null` 表示没有为日志额外查询焦点。连续相同 Runtime 状态仍去重，正常 capture 不增加新事件；Hook 仍只排入原有 64 条有界队列，溢出由 `shortcut-trace-overflow` 明示。

`capture-window-changed` 对照 capture 起点与返回时的 window/content/foreground epochs、配置及会话代次，并报告 `hasSnapshot` / `resultFocus`。Broker 的 `observed-native` 或练习目标绑定先发生，并不能证明 Runtime 接纳了返回值或 Hook 已发布租约。Hook 队列稍后才写盘，文件顺序不是跨组件全局顺序；这些字段也没有建立一一对应的 Broker 请求因果。缺字段的旧 trace 保持可读，未知诊断字段不参与产品控制。

`practice-capture-exit` 仅覆盖已登记按需练习至首次调用 `PublishShortcut` 的采集，每个配置代次最多 16 条，记录原 gate 的固定退出原因、await 恢复及后续处理耗时与既有焦点检查结果；首次调用仅表示宿主调用发生，不证明原生端已接纳，也不表示首次 F9 竞态已修复。

`SessionDiagnosticsTests` 在确定性公开宿主端口覆盖命令关联、旧代次/过期拒绝、窗口改变不发布租约、正文与目标隐私、TRACE 开关、不可写日志及无新增焦点探测。`SessionShortcutHookTests.ShortcutDiagnosticsPreserveNoLeaseRejectionAndIdentifyLeaseRemoval` 在真实 Hook 上对照 TRACE 开/关、无租约、显式撤销与配置清除；[090062a 的 Windows 实跑](https://github.com/DingJWuyu/Typelay/actions/runs/38700885927/job/116308546345)已通过 TRACE 开/关两例与 Runtime 诊断回归。同次真实模型按需练习在 F9 入口失败：Hook 记录 `wake` / `no-lease`，Runtime 拒绝 `StaleObservation`，结果为 `practice-preview-feedback`；尚未证实原 F9 故障修复。诊断回归不重新按键重试生产失败、不运行模型，也不证明物理输入法通过。

生产 App 的 `--automation-test` 参数仅对该进程允许注入 Tab/Esc，界面明确标示，不写入设置。仅在退出已有客户端、设置好发行根 TYPELAY_HOME 后直接启动发行目录的 `app/TypeLay.App.exe --automation-test`；测试完成退出该进程，再用 TypeLay.exe 恢复普通模式。注入模式仍须遵守全部写回、焦点、IME 和期限检查，不能作为物理键盘验收。

## 真实产品验收

产品要求统一从 [README 产品验收入口](../../../README.md#产品验收入口)读取冻结规格及已接受 ADR-0002。以下项目需在实际 Windows 桌面单独执行，Linux 的对应入口见 [Linux 验证](../linux/development.md#验证)。默认只读优先，实验写回仅用备份的非敏感内容。共用风险见工程架构，Windows 写回风险及关闭条件见[本平台架构](architecture.md#b-01非原子写回与按键所有权)。

1. 完整解压，双击 TypeLay.exe，首次安装需明确同意；重复启动只留一个托盘；退出后 App、Broker、worker 和 hook 全部退出。断网后已装模型仍能推理。
2. 在教程之外的浏览器普通输入框与桌面应用中输入合成文本，验证真实模型候选、无抢焦点/页面挤压。无标签输入不能预先排除；失败控件如实记录。
3. 人工修改候选，观察原生光标、选择、粘贴与换行。第一次 Tab 只回源字段，下一次 Tab 才尝试接受；间隔不限速度，按住键不能提前接受。原文末尾追加与原文重写按产品规格分别处理。局部选区替换保留范围外文字、emoji 和换行；提交计数必须有可递增的正向对照。
4. 使用真实微软拼音在源字段和浮层编辑器覆盖 composition、翻页、选词、Backspace、取消、提交、Shift 切换、组合期间 Tab 和候选窗口期间点击浮层。DOM composition 和 SendInput 均不等于物理 IME/Tab 通过。
5. 对焦点切换、字段关闭/复用、源文本并发修改、Broker 超时/崩溃做拒绝测试。不得覆盖新文本、串写其它字段或提交表单。B-01 未关闭前不能勾选安全写回通过。验证浏览器受控组件、撤销栈和事件同步，不能只比对最终文字。
6. 对照 docs/design 中已确认原型检查浅/深主题、100%-200% DPI、多屏负坐标、窗口移动、滚动、长文本和展开状态。记录截图、实际时延及设备条件，发现明显异常也要处理。HTML/CSS 与 WPF 控件参数相似不等于视觉一致。

### 按需练习的生产入口

`--verify-continuous` 在同一已安装真实模型、共同桌面时限内追加独立练习场景：真实 WPF 默认取消不改模式，明确确认经正式设置持久化，启动发布用 `TypeLay.Practice` 的 stock Edit，由生产会话/Hook/Broker 执行一次 F9 预览与一次 F10 替换。必须比对真实模型参考、预览原文不变、焦点保留、Verified 结果与浮层隐藏；关闭后保留用户后来选的模式，不标记完整教程完成。前场景清理未确认时不启动后场景，任何场景失败仍整体失败。软件键注入不代表物理键盘或真实 IME 验收；未增加实验 Tab 授权。

搬迁发行包回归单独启动实际 `TypeLay.Practice.exe`，确认自有普通 Edit 存在、无正文输出与设置不变，然后正常关闭并确认退出。该测试不替代前述真实翻译场景，也不把新入口尚未运行的 Windows 回归描述为通过。

## Windows 浏览器合作链

`TypeLay.DesktopFixture --verify-browser-cooperation <结果目录> <发行根> <CfT路径>` 使用实际发行 App 的既有 `--automation-test` 入口与 BrowserHost，独立 CfT profile 加载包内标准页扩展。CI 从固定官方版本及摘要取得浏览器，缓存命中仍重新校验；不运行旧浏览器身份研究探针。场景要求当前用户 TypeLay 数据目录和注册为空，仅清理自己创建的资源；使用本轮已校验模型组件并由正式 worker 重新加载。注册写入仅在明确的 `TYPELAY_BROWSER_REGISTRATION_E2E=1` 测试环境启用，普通发行检查默认不写注册。

设置发现检查使用标准 Chrome `User Data` 根，不能把任意测试 profile 路径传给生产发现器，也不改发行 `connection.json`。一般运行仍要求根不存在并由 fixture 原子创建。仅当前 workflow 对官方 GitHub-hosted Windows 一次性 VM 显式开启 `TYPELAY_HOSTED_EMPTY_BROWSER_CONTAINER_E2E=1` 时，可使用预存在但完整枚举确认为空的普通目录；四个已知子项不存在不等于空。目录必须没有 reparse 标记、没有既存 Chrome 进程，持真实目录 handle 禁止写目录及删除/重命名，核对卷号和文件 ID；创建本次 nonce 后再次确认目录只有该文件，才启动浏览器。任何未知项目、观察失败或身份变化均拒绝。预存在容器分支永不删除根、nonce 或浏览器产生的内容，即使退出已证也保留给一次性 VM 回收；原自建目录仍遵守既有 nonce 和进程族退出证明后清理。该测试准入不是面向用户的安装能力，也不是允许使用未知非空 profile。[GitHub 官方说明每个标准 hosted job 使用新 VM](https://docs.github.com/en/actions/how-tos/manage-runners/github-hosted-runners/use-github-hosted-runners)，环境判别使用官方 `RUNNER_ENVIRONMENT` 与 workflow 显式开关，不改变生产设置的目录、权限或身份规则。新空容器路径及其实际 Win32 handle 反例尚待 Windows CI 执行，本地交叉编译不计为通过。

接入确认后从设置返回网页，使用一次真实桌面点击，而不是把程序式抢前台成功当作用户点击的前提。仅在本次受管页面中找到唯一 `A message` 字段，且 UIA 运行时身份、当前点命中、完整字段边界、设置窗口不相交、受管进程和 HWND 身份均成立时才注入一次 down/up；遮挡、身份变化或取消直接失败，不选另一个点或重试。发送完成后仍须观察原 DOM trusted 点击、精确目标、原生前台和字段焦点，才进入翻译流程。`BrowserSourceClickTests` 使用真实 WPF 窗口验证后台可见源的一次点击，以及遮挡/取消零输入；这不替代 CfT 完整合作链实跑。

验收覆盖标准页的自动接受、真实按需直接替换、已受理后原字段后台写入，以及经正式 UI 主动移除后的无写负控。软件输入和开发扩展不代表物理输入法或品牌 Chrome 扩展安装已经验收；当前结果以本轮 run 的 result.json 为准，未执行的后续阶段不得计作通过。

## 可选 Hy 隔离 adapter 验证

此入口仅验证固定 C ABI、原文控制 token 边界和自有 worker 生命周期，不修改产品 catalog、默认模型、设置、发行包或已安装模型。普通 native fixture 模式不会运行 Hy。

显式运行 `./dev.ps1 build` 后，`./dev.ps1 hy-adapter-prepare` 在600秒总限内下载/完整校验固定 Hy-MT2 GGUF、官方 llama.cpp b11717 Windows CPU ZIP及许可，构造CPU-only私有目录并用官方SDK编译小adapter；`./dev.ps1 hy-adapter-e2e` 在180秒内执行取消/恢复和原中文到日文句子一次真实EOG，再做已有canonical literal/孤立CR拒绝审计。ABI3 隔离版本已取得 Windows 真实执行证据；当前 ABI5 生产发行链仍待独立验证，旧结果不替代新版本检查。

冷GGUF+ZIP+模型许可总1,152,754,477字节；缓存命中仍全量SHA核验，坏缓存明确失败。模型/runtime下载仅进独立 `artifacts/hy-download/`，不进入发行包或artifact上传。CPU按固定上游实际最高正score动态选取，不按机器型号猜ISA。源码仅保留必要头文件及许可，不编译上游llama.cpp，不增加模型框架依赖。

worker先以CREATE_SUSPENDED创建；设置Job的KILL_ON_JOB_CLOSE、ActiveProcessLimit=1、process/job committed-memory 2.5GiB并确认归属后再Resume。真实decode中取消后确认Job活动进程为0且根进程已发信号，再开始下一实例；只发送合成正文到有界stdin，不把正文放argv或临时prompt文件。ctx1024、2计算线程、greedy、192输出token、每请求60秒，只有真实EOG成功，失败不输出部分成功。Windows committed-memory限额不是Linux RLIMIT_AS或RSS硬限。

上述隔离审计保留为手动入口。正式 check job 改用实际 ABI5 发行链：固定资产准备后，唯一 `pack -HyRuntime` 构建生产闭包；真实 ModelClient/ModelChildProcess 检查候选 hello 和退出，再由设置窗口执行安装、切回、再选择、中法预览及移除。两个独立 fixture 进程启动前，测试输出中的五份生产程序集（App、Runtime、Core、Protocol、Windows.Contracts）与发行包对齐，并逐一核实际加载位置的 SHA；不改发行包或运行中程序集。生产前置不重复推理，中法句只在实际 App 激活后生成一次预览。原无使用标记的 Argos 旧代不伪造可删除证明；移除必须核实本轮 Hy 退休代及精确下载缓存已回收。

`--verify-hy-packages` 仅按已与发行 App 匹配的实际 Runtime 嵌入目录、并与发行 worker 目录逐字节一致的固定 Hy 身份选择验收分支；只接受完整十二方向或十二方向加 `zh_en/en_zh`，部分或未知目录失败，不根据 worker 自报跳过。正式十二方向仍为原十场景，结果 `englishAcceptance=not-offered-not-run` 不算中英通过。仅候选十四方向追加五场景：真实设置补充动作先取消且当前代/库存/选择/设置不变，再明确确认并核只增加两方向；经真实托盘正常退出、既有 owner 完整退出与锁释放后，原数据冷启同代十四方向；最后沿原普通预览路径各翻译一个固定中英短句，并通过实际语言选择/交换恢复原中法设置，再继续原 Argos 切回、移除及退出。默认安装仍十二方向，冷启不调用准备、修复或选择；原960秒总限、20秒清理预留及请求/退出期限不变。手动中英预览已获产品批准，此分支仍须在包含正式十四方向目录的实际新包运行；托管 Compile、目录门契约通过不代表这些 GUI、实际模型或物理输入已经执行，不增加质量采集或接受写回场景。

Windows job 上限75分钟，准入要求仍余50分钟：准备10分钟、既有浏览器10分钟、生产前置6分钟、设置链16分钟、缓存与尾部7分钟、打包预留1分钟。各步骤原有硬上限和模型请求/退出期限不延长；不足时明确记录未运行。现浏览器场景须成功后才进入 Hy 前置；其失败结果尚不足以独立证明全部自有进程退出，不能仅凭空 cleanupErrors 继续。模型源须来自同轮 continuous 已确认的真实 Ready 库存，缺失则后续未运行。实际完成即结束。仅上传列明的小结果 JSON、有界诊断及发行 ZIP，不上传 GGUF、下载缓存或整个 artifacts 目录。
