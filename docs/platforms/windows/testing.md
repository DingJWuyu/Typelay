# Windows 测试与桌面验收

按[共享验证协议](../../engineering/testing.md)表述证据；本页只适用于 Windows，Linux 入口见其[开发与验证](../linux/development.md)。

## 自动化层级

`dev.ps1 check` 在 Windows 验证构建、标准 xUnit 行为/契约测试、Python 行为/启动测试和格式/lint。
CI 在本轮构建成功后独立收集行为、格式、原生适配器和发行包结果；任一失败仍使任务失败。构建失败或取消时不运行依赖产物的后续验证，避免使用旧产物。
`dev.ps1 pack` 生成真实自包含 ZIP，再运行分发与进程 E2E。该流程不装模型、不打开用户文档、不通过假翻译器伪造 ready 状态。
`dev.ps1 native-e2e` 需要已解锁桌面，验证受控 Win32 适配器的读写、选区、拒绝与零提交契约。它同时约束 Hook 测试依赖的字段后端；前置条件失败是未执行，不是通过。

`TypeLay.App.Tests` 的浏览器采集回归使用运行机已经安装的 Google Chrome、独立临时配置目录和本地普通搜索页。它走生产 Broker，实际注入不含标点的文字并检查源快照更新；不预先打开特殊可访问性开关，不下载模型，也不把采集成功当作真实译文、写回或物理输入法通过。运行机缺少 Chrome 是前置失败，不静默跳过。

浏览器前置必须先确认本次自有进程家族中的唯一测试页面窗口、前台身份与页面输入焦点，不能将启动进程的 `MainWindowHandle` 或任意当前焦点当成浏览器目标。前置失败不执行正文采集，也不计为输入框支持结论。

冷启动浏览器和临时配置的 fixture 准备使用整个回归共用的 20 秒总取消预算；两次正文观察各自限 6 秒并受同一总预算约束，保留单次 SendInput 点击及最终 DOM 焦点确认，这不放宽产品采集或预览性能验收。

新标签页另用独立配置启动 `chrome://newtab`，在自有 renderer 内通过 Document 的 URL 元数据确认内部页身份，只定位该 Document 中唯一可见、启用、可聚焦的编辑控件，再以真实矩形单击。此例先用 UIA 预热浏览器可访问性树，不证明冷启动无前置的首次自动捕获；内部页身份、唯一控件或同一字段/Document/前台连续性不足均失败，转入顶栏明确报告 `focus-transfer`。两段非敏感无标点合成输入只验证生产 Capture 的正文更新与 epoch，不按 Enter、不读取建议；不代表真实模型、两种翻译模式或物理输入法通过，也不保证浏览器不会请求网络建议。

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

`OverlayRenderingTests` 检查实际 HWND 位置/尺寸、源焦点、长词换行、字体变化、短文重显及主题/展开/手动草稿。320/520 宽度的浅深色回归保留简短状态、编辑入口的工具提示和 UI Automation 名称，检查正文完整可见、单条编辑焦点边框与中性底色；原生指针回归检查 hover 提示随编辑资格撤回而消失、源焦点不变，并将预览/编辑截图写入 CI 已收集的 `artifacts/test-results/rendering/`。这些检查须在 Windows 实跑；Linux 跨编译不是视觉验收。测量必须与编辑框的 `TextWrapping.Wrap` 一致：[WPF 10.0.12 的 `FormattedText.Trimming` 实现](https://github.com/dotnet/wpf/blob/v10.0.12/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/FormattedText.cs#L1342-L1360)表明 `None` 才强制拆分超宽单词，默认省略会低估无空格文本高度。保留连续长词反例，不能用插入空格让回归通过；这些合成会话只验证展示，真实模型连续更新由下方独立模式验证。

浮层只在首次出现时使用 140ms 透明度淡入，遵循 Windows 的 [ClientAreaAnimation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.systemparameters.clientareaanimation) 偏好；更新、撤销接受资格和安全隐藏不等待动画。隐藏及重新显示前移除旧时钟，最终基础透明度为 1，以 [FillBehavior.Stop 与移除属性动画](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-set-a-property-after-animating-it-with-a-storyboard) 避免旧 HoldEnd 值影响后续显示。淡入回归仅替换系统偏好读取，不修改全局设置或模拟动画时钟；通过真实渲染帧、HWND 可见性与源焦点验证禁用动画、显示/隐藏/再显示、最终透明度及更新不重播。

同一目标、源框锚点、工作区、DPI、宽度及上下避让侧保持不变时，展开/收起和正文自然高度变化使用约 280ms ease-out。通过 [CompositionTarget.Rendering](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget) 调整同一编辑框的视口高度，并以每帧至多一次 `SetWindowPos(SWP_NOACTIVATE)` 更新 HWND；文字不缩放，保留左锚和上方底锚/下方顶锚。新正文、提示和编辑资格立即生效，不创建旧正文副本、不延长接受许可。重复刷新不重启，反向变化从当前几何接续；换目标、源框移动、宽度/DPI或避让变化直接落位。减少动态效果立即到终点，安全隐藏清空正文并撤除帧回调。

高度回归在真实 WPF 渲染帧读取 HWND 几何，检查多个连续中间尺寸、锚点/源焦点、固定字号与字形高度、底栏完整、反向/更新不回弹、Esc/安全隐藏后不复活、人工草稿选区/撤销及减少动态效果。最多五张真实展开帧 `overlay-height-expand-*.png` 和上下锚几何序列 `overlay-height-{above,below}.json` 写入同一 `artifacts/test-results/rendering/`；需检查实际图像以区分视口变高与缩放文字。相关回归须在 Windows 实际运行，跨编译与纯几何计算不能计为动态视觉验收。

## 会话接受与退出按键回归

`SessionShortcutHookTests` 在自有 WPF 输入框上调用生产低级 Hook，验证 F8/F9 与 F6/F7 重新配置后的按住、重复及成对释放。已消费旧键的松开仍归旧操作，新的绑定不能抢走它；实际源字段收到的 F12 正向屏障用于确认消息已经分发。测试租约是明确的受控输入，不调用写入或模型，不能证明生产字段、物理输入法或真实写回安全。

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

验收覆盖标准页的自动接受、真实按需直接替换、已受理后原字段后台写入，以及命令前断连拒写。软件按键、显式实验开关和开发扩展属于该场景的前提，不代表物理输入法、品牌 Chrome 扩展安装或默认关闭门已经验收；真实结果以本轮 result.json 为准，未执行的后续阶段不得计作通过。
