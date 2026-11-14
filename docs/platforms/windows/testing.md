# Windows 测试与桌面验收

Windows 验收要回答用户能否在真实输入框中持续预览、安全接受、修改译文并正常退出。共享合同、受控原生测试、真实模型和物理输入分别提供证据，任何一层都不能替代其它层。

先按[共享验证协议](../../engineering/testing.md)说明场景价值，再选择最接近故障的现有入口。任务进展与验证证据由 [GitHub Issues](https://github.com/DingJWuyu/Typelay/issues)维护。已有发行包另见[包身份与有限结果](README.md#当前可验证候选)。

## 验证入口

| 待验证边界 | 本页入口 |
| --- | --- |
| 完整产品行为与视觉 | [真实产品验收](#真实产品验收)使用实际发行包、物理输入和真实输入法。 |
| 工程检查与测试环境 | [自动化层级](#自动化层级)、[计时隔离](#托管合同的计时隔离)和[受控工具边界](#受控工具的边界)。 |
| 原生捕获、接受与写回 | [普通外部应用](#普通外部桌面应用验收)、[首次 Wake](#首次无租约-wake-回归)、[会话按键](#会话接受与退出按键回归)和[系统 Edit 事务](#系统-edit-前台事务回归)。 |
| Chrome 字段与连接 | [浏览器合作链](#windows-浏览器合作链)区分真实产品通道与 UIA 采集。 |
| WPF 设置、浮层与暂停 | [暂停入口](#暂停入口回归)和[视觉回归](#设置与浮层的视觉回归)。 |
| 真实模型与组件生命周期 | [持续翻译](#无标点持续翻译的真实模型检查)、[Hy 验证](#可选-hy-隔离-adapter-验证)及[开发页的 Index 入口](development.md#index-默认载荷与独立检查)。 |
| 同次按需命令的无正文诊断 | [快捷键诊断](#按需快捷键诊断)说明关联字段、队列与观察限制。 |

## 自动化层级

| 入口 | 可以证明的范围 | 需要另行验证的范围 |
| --- | --- | --- |
| `./dev.ps1 check` | 构建、标准托管行为/合同、worker、格式与 lint。App 套件使用实际 WPF 和自有桌面资源。 | 真实模型、物理键盘、用户输入法和全部外部应用。 |
| `./dev.ps1 pack` | 实际自包含 ZIP、摘要、中文空格目录搬迁及生产进程入口。 | 用户安装体验、模型质量与普通输入框完整行为。 |
| `./dev.ps1 native-e2e` | 已登记 Win32 fixture 的读写、选区、拒绝和提交计数。 | 生产 UIA、外部应用和物理 IME。 |
| 生产 C 的 API 边界回归 | 同一 native 源码在受控系统响应下的事务与拒绝规则。 | Windows 调度、真实 Hook 链、系统重入和物理来源。 |
| DesktopFixture 显式模型入口 | 指定真实模型、生产组件和自有场景的实际结果。 | 未运行阶段、其它控件与语言质量放行。 |
| 真实产品验收 | 指定环境中的物理输入、应用行为和视觉。 | 未覆盖的平台、版本或设备。 |

CI 构建成功后分别收集行为、格式、原生与分发结果，任一失败仍使任务失败。构建失败或取消时，依赖该产物的阶段不能使用旧输出继续。前置失败、未运行、产品拒绝和通过分别报告。

## 真实产品验收

产品要求从[产品验收入口](../../../README.md#产品验收入口)读取冻结规格与已接受 ADR。对照[已确认原型](../../design/README.md)检查实际界面。默认只读优先，实验写回只用备份的非敏感内容。

1. 完整解压并双击 `TypeLay.exe`。首次准备须明确同意，重复启动只有一个托盘；正常退出后 App、Broker、worker 和 Hook 都应退出。已装模型在断网后仍可推理。
2. 在教程之外的浏览器和桌面普通输入框输入合成文字。检查无标点时持续预览、原焦点保持、页面不被挤压，并记录失败控件。无标签字段不能预先排除。
3. 修改候选，检查原生光标、选择、粘贴和换行。第一次 Tab 只回源，第二次才尝试接受；按住键不能提前接受。局部替换须保留范围外文字、emoji 与换行，Undo/redo 和事件同步须真实验证。
4. 使用物理键盘与真实微软拼音，覆盖组合、翻页、选词、Backspace、取消、提交、Shift 切换、组合期间 Tab 和候选窗口期间点击浮层。DOM composition 与 SendInput 不能替代这些观察。
5. 切换焦点、关闭或复用字段、并发改文，并让 Broker 超时或崩溃。检查不串写、不覆盖新文、不提交表单。已派发操作按实际回执收尾，未知结果不得重试。提交计数必须有可递增的正向对照。
6. 在浅深主题、100%-200% DPI、多屏负坐标、移动、滚动、长文和展开状态中核对实际截图。记录设备条件和时延；控件参数相似不等于原型视觉一致。

[B-01](architecture.md#b-01非原子写回与按键所有权)未关闭前，不能把通用安全写回标为通过。每个场景都须记录源版本、实际发行包身份、输入方式、通过/失败/未测范围和自有资源退出证据。

## 托管合同的计时隔离

标准 test/check 与 CI 使用 [test_contracts.py](../../../eng/windows/test_contracts.py)。主 solution 只排除 `ModelInstallerTests.DownloadTimeoutAlsoBoundsAnArchiveBodyAfterHeadersAndPartialBytes`；主套终态后，独立 testhost 执行该方法的两个参数场景各一次。这是预先分组，不是失败重试。

两个组各自保留 TRX 和退出码。主套普通失败后仍执行独立组，取消则停止。独立组缺失、重复或跳过都使整体失败。下载 100ms、外层 3s、64 字节前缀和真实取消断言保持；未进入流暂停不能冒充正控。显式单项目或过滤调用不自动附加该组。

## 受控工具的边界

[NativeFixture](../../../tests/windows/fixtures/TypeLay.NativeFixture/Program.cs)默认提供真实 Win32 输入框供生产 Broker 观察。Hook 模式链接生产 KeyboardHook/Core，并使用固定候选隔离按键问题，不调用模型。[受控适配器](../../../tests/windows/support/TypeLay.Platform.Windows)只访问已登记字段，测试 IME 状态不代表外部应用的实际状态。

[DesktopFixture](../../../tests/windows/fixtures/TypeLay.DesktopFixture/Program.cs)普通交互模式使用生产浮层、控制器、Hook 和 ModelClient，但用进程内 WPF 字段后端替代源访问。该模式用于编辑与焦点定位，不能证明 UIA 或 Broker 管道。显式模型入口另选真实源和 Broker，须按各节范围解释。

`ModelInstallerTests` 通过正式安装入口验证超时、用户暂停、损坏缓存、文件锁和失败恢复，替换的是 HTTP 与子进程边界。真实 ZIP、哈希、文件锁和自有进程仍参与检查。小包可证明恢复规则，不能证明真实模型可用。`SetupRenderingTests` 从 WPF 按钮检查失败原因、诊断、关闭后重试与主动暂停后的继续。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ModelInstallerTests|FullyQualifiedName~SetupRenderingTests'
```

原生辅助调用失败只记录固定操作、消息 ID 与错误码，不记录正文。`SendMessageTimeoutW` 返回零可能是失败或超时，不能仅凭异常认定原因。只有持续输入生成器的单次合成 `EM_REPLACESEL` 允许 1 秒等待；产品观察和接受期限保持，输入超时后先确认自有源退出。

## 受控桌面工具

完成 Release 构建后，在已解锁 Windows 桌面从仓库根运行。以下是交互工具，不会自动完成全部验收。

```powershell
# 真实 Win32 源窗口，由正式客户端和 Broker 观察。
dotnet tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll

# 普通 Hook 对照，软件 Tab 应交还控件。
dotnet tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll --hook-test

# 固定候选的显式软件输入对照，只验证按键行为。
dotnet tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll --hook-test-automation
```

需要交互式 WPF 与真实模型时，先经正常客户端明确同意准备模型，再退出其它测试客户端并运行：

```powershell
$previousHome = $env:TYPELAY_HOME
try {
    $env:TYPELAY_HOME = (Resolve-Path 'artifacts/package/TypeLay').Path
    dotnet tests/windows/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows/TypeLay.DesktopFixture.dll artifacts/desktop
} finally {
    $env:TYPELAY_HOME = $previousHome
}
```

`TYPELAY_HOME` 指向包含 `worker/` 的完整发行根，也可使用 `run` 生成的 `artifacts/dev/TypeLay`。该工具使用独立 `fixture-settings.json`。连续追加直接修改已提交文字，不模拟 IME。

### 无标点持续翻译的真实模型检查

`--verify-continuous` 使用独立 stock Edit、生产 Broker、控制器、浮层与真实模型。它会联网调用正式 ModelInstaller 准备运行组件和双向模型，只能用于已授权模型安装的临时环境；普通 check/xUnit 不隐式调用它。

```powershell
# 结果目录必须尚不存在。
dotnet tests/windows/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows/TypeLay.DesktopFixture.dll `
  --verify-continuous artifacts/continuous-translation `
  src/shared/TypeLay.Worker/run_worker.py `
  tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll
```

持续输入场景要求译文在原生源仍追加时可见，随后更新到完整最新源的真实模型结果，原文、焦点与零提交保持。输入是合成已提交中文，没有句末标点。最终完整源的参考推理在首译观察之后执行，不能预热首译。最多 300 次约 100ms 追加和 90 秒桌面截止只用于识别停滞，不能当成可接受延迟保证。

独立直接替换场景在生产后端确认能力后，只注入一次 `Ctrl+Shift+F10`。必须取得 Verified，原框与同一模型结果相符，焦点保持、浮层隐藏且没有提交。每场景最后点击自有提交按钮，证明计数器可递增。前场景失败保留首错，只有清理已确认、模型可用且总预算尚余时才继续独立场景。

同一入口按七条独立旅程各执行一次：自有自动练习、普通控件快捷键、持续预览、直接替换、按需练习、普通网页冷态预览和 NTP 预览。每条旅程有独立的 90 秒预算，并创建自己的源窗口、会话和浮层；模型只准备一次。前一旅程失败不会耗尽后一旅程的预算，清理失败则阻断后续旅程。自有自动练习内的接受、编辑回源和完成仍按真实依赖执行，前置未完成时记录 `not-run / dependency-unavailable`。

整个模式保留 20 分钟取消期限，外部执行器另设进程上限。独立预算增加了桌面总执行上限，原按键次数、从 keydown 起算的 500ms 期限和许可条件保持。每条旅程只尝试一次，未知写入结果不重试。返回 0 表示全部要求通过，参数错误为 2，其余失败为 1。

上传只包含结构化结果、有界无正文 trace 和自检结果，不上传模型、缓存、输入或译文。`TYPELAY_TRACE` 的浮层慢渲染采样最多 32 条，只记录耗时至少 50ms 的阶段，默认关闭。

### 按需练习的生产入口

同一个模型入口通过真实 WPF 与设置提交启动发行用 TypeLay.Practice，再用生产会话/Hook/Broker 执行一次 F9 和一次 F10。预览须保留原文，替换须有真实 Verified，关闭不能覆盖用户后来选择的模式，也不能标记完整自动教程完成。

源正文核对在实际能力观察和目标绑定成功后执行，期望仍是预先固定的示例文字。就绪提示与唯一 F9 之间不插入额外同步读取。模型参考、原目标、100ms 消息上限与共同截止保持。搬迁包只验证 Practice 加载、普通 Edit、正常关闭和设置不变，不替代这条翻译链。

## 普通外部桌面应用验收

`--verify-ordinary-notepad` 从完整发行目录启动真实 App，以系统已安装的 `System32/notepad.exe` 和合成 `.txt` 为源。必须确认原进程镜像、原生 x64 与唯一严格 Edit。现代记事本、进程转交、已有 Notepad 或未知控件均是前置失败，不回退 fixture。

入口复用同轮 continuous 已校验的 Argos 文件，不另下载模型。只有 TypeLay 用户数据目录原本不存在且没有运行中的客户端时，才独占创建数据。真实设置负责工作模式及实验配置，产品授权仍按实际能力判断。

```powershell
$fixture = 'tests/windows/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows'
foreach ($assembly in @('TypeLay.App', 'TypeLay.Runtime', 'TypeLay.Core', 'TypeLay.Protocol', 'TypeLay.Windows.Contracts')) {
  Copy-Item "artifacts/package/TypeLay/app/$assembly.dll" "$fixture/$assembly.dll" -Force
}
dotnet "$fixture/TypeLay.DesktopFixture.dll" --verify-ordinary-notepad artifacts/ordinary-notepad artifacts/package/TypeLay artifacts/continuous-translation
```

自动接受前先观察同一 App 唯一浮层的当前真实译文和公开能力。明确“仅预览”时，接受记录为 `not-run`，保留 `required-capability-unavailable` 原因与实际观察，不发送接受键。出现真实接受提示也不能用普通 SendInput 作来源阳性；自动接受记录 `not-run / trusted-source-input-required`，人工稿接受记录依赖未就绪。

只有这两类明确的发键前边界，才允许继续独立按需场景。继续前只读重核原预算、App/worker、原源身份、正文和精确焦点，不补焦点或改文。随后一次 F9、Esc、一次 F10、Undo 成功后才检查普通退出；自动接受的未验范围仍保留，整体不会变成通过。

Esc 后先确认撤权后的新观察与公开 Direct 就绪，再发送唯一 Direct。trace 只用于排除页面缓存的旧 ready，不授写权。拒绝立即记录为原命令失败，不补发、不续时。

业务共用 520 秒，另保留 20 秒清理，外部 owner 最多 585 秒。`result.json` 记录逐阶段结果与系统应用身份，`consumer-exit.json` 单独证明进程族退出。只有全部自有 owner 都确认退出，才删除自有数据；root 退出或空 cleanupErrors 不足以授权删除。当前 CI 编排和跨消费者退出门以 [check.yml](../../../.github/workflows/check.yml)为准。

## 首次无租约 Wake 回归

[FirstWakeCaptureTests](../../../tests/windows/TypeLay.App.Tests/FirstWakeCaptureTests.cs)用真实 stock Edit、Hook、Router、Service 和认证 Broker 验证冷态一次 F9。它要求新的真实正文、原选区不变、零写能力、零许可和零提交。旧普通 read 在按键后才返回时，也必须独立发 `capture-wake`，不能借旧正文或旧输入版本。

实际交付末门另用受控 IInputBroker 响应，完成后制造字段切换、往返、新输入、Esc、配置或新命令，再调用 `TryRead`。该层证明异步失效，不证明冷 UIA 能在 150ms 内完成。Broker 队列回归另外核过期未发送、不终止别的调用，以及迟到只读帧排空后仍拒绝交付。

裸 Hook 的 no-lease、Direct 的 no-lease 和旧/未来版本拒绝继续保留。当前源码和旧包的实际通过范围分别查对应运行；不能因确定性末门通过宣称冷读或用户 F9 故障已经根治。

## Windows 浏览器合作链

`--verify-browser-cooperation <结果目录> <发行根> <CfT路径>` 使用实际 App、BrowserHost、包内扩展和本次已校验模型。Chrome for Testing（CfT）版本与摘要来自[固定来源](../../../tests/windows/support/chrome-for-testing.json)，缓存命中仍校验。

真实注册仅在明确的 `TYPELAY_BROWSER_REGISTRATION_E2E=1` 环境启用。生产发现器读取标准 Chrome User Data，测试不改 `connection.json` 或注入另一个 profile 路径。一般运行要求根目录不存在并由 fixture 原子创建。

只有 workflow 明确启用 `TYPELAY_HOSTED_EMPTY_BROWSER_CONTAINER_E2E=1` 的一次性 hosted VM，才允许使用预存在且完整枚举为空的普通目录。无 reparse、无既存 Chrome、精确目录 handle、卷/文件 ID 与 nonce 都须匹配。该分支不删除原根或浏览器产生的内容；原自建目录则在精确退出和所有权证明后清理。未知或非空 profile 一律拒绝。

设置接入后，只在本次受管页中唯一 `A message` 字段满足身份、完整边界、命中和无遮挡条件时，注入一次真实桌面点击。之后仍须确认 DOM trusted 点击、前台与字段焦点，再进入翻译。遮挡或取消零输入，不换点重试。

验收包括自动接受、前台选区 F10、已受理后仅原框后台写入，以及正式 UI 移除后的无写负控。每阶段单独报告。CfT 开发加载和软件输入不代表品牌 Chrome 商店安装、物理按键或真实 IME。

### 浏览器普通页与新标签页采集

[BrowserCaptureTests](../../../tests/windows/TypeLay.App.Tests/BrowserCaptureTests.cs)使用运行机已装 Chrome、独立 profile 和生产 Broker，检查无标点正文与版本更新。缺 Chrome 是前置失败，不静默跳过。

普通页先确认本次进程族的唯一窗口、前台与 DOM 焦点。冷启动和页面准备上限为 120 秒，之后才进入原 20 秒产品采集预算与 6 秒观察循环；xUnit 上限 160 秒，外层仍负责不可取消阻塞。一次准备失败不重启或重试。

NTP 使用原 20 秒共用预算，先从 UIA Document 的 URL 确认内部页，再单击唯一合格编辑框。该定位预热了 UIA 树，不能证明无预热冷态发现。转入顶栏记 `focus-transfer`，不替换为普通网页通过。

真实模型的普通页冷态首译使用 DOM 准备，首译前不访问测试 UIA。NTP 两模式则保留 UIA 预热条件。没有受控提交计数器时，只能报告零 Enter 动作、零产品写请求及观察到的原字段保持，不能报告已经证明零提交。浏览器停止或目录清理失败仍是整例失败。

## 会话接受与退出按键回归

[SessionShortcutHookTests](../../../tests/windows/TypeLay.App.Tests/SessionShortcutHookTests.cs)在自有 WPF 字段验证配置键、按住、重复、释放和未用许可退休。已消费旧按次的 release 仍归原操作，新配置不能抢走。受控租约与固定候选不证明物理来源或写入安全。

[NativeSourceControllerTests](../../../tests/windows/TypeLay.App.Tests/NativeSourceControllerTests.cs)连接真实控制器、Broker 和源 DLL，分别测试接受、撤回、原键旁路、回源、人工稿及结果结算。来源与物理入口见[有序源开发说明](development.md#正向接受的剩余条件)。回源前置失败时，后续 Ctrl 接受或拒绝仍属未运行。

普通回源必须确认原 root、精确字段、PID/TID、cookie 与真实新捕获。短暂 NULL 前台不是 Source，回源 ACK 也不是新的 IME 样本。术语保存的在途返回证明仅属于本次 form/token/basis，普通编辑不能借用。Windows 焦点回归保留同 root 换字段与另一进程返回两类实际前提，不强制造出某种 child foreground 形态。

[OverlayRenderingTests](../../../tests/windows/TypeLay.App.Tests/OverlayRenderingTests.cs)验证 Tab/Esc、功能键、IME 事件及旧键释放。第一次接受键只请求回源，之后必须重新观察并等待第二次接受。迟到按钮和菜单携带原目标、配置与会话身份，不能借新视图获得权限。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~SessionShortcutHookTests|FullyQualifiedName~OverlayRenderingTests'
```

修改术语或设置反馈时，在 Windows 执行[术语浮层反馈](../../../tests/windows/TypeLay.App.Tests/OverlayGlossaryTests.cs)、[设置提交反馈](../../../tests/windows/TypeLay.App.Tests/TranslationSettingsTests.cs)及受影响的编辑回源与关闭结算。WPF 编译不证明桌面行为通过。

## 系统 Edit 前台事务回归

[ProductionForegroundWriteTests](../../../tests/windows/TypeLay.App.Tests/ProductionForegroundWriteTests.cs)通过真实 Broker 与独立 Edit 验证前台写入，并用生产 DLL 私有 wire 区分 Broker 提前拒绝和源 GUI 最后拒绝。正例覆盖折叠当前选区与原替换范围不同，保留范围外文字、源焦点、零提交和 Undo。

负例覆盖另一字段、另一 root、仅选区变化、过期许可、资格或寿命变化，以及请求在队列等待期间过期。后者必须在私有 500ms 事务预算仍有效时由 native 拒绝，不能用 Broker 拒绝代替。缺 DLL 与旧 wire 不得获得能力。后台 F10 的原字段绑定另由 ProductionDirectWriteTests 验证。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ProductionForegroundWriteTests|FullyQualifiedName~ProductionDirectWriteTests'
python -m unittest discover -s tests/windows -p test_native_foreground_guards.py -v
```

第二条命令直接编译生产 C，并在系统 API 边界改变焦点、选区、时间、HIMC/HKL 和 MSG，验证最后拒绝、Prepared/Arm、同消息置空、尾键与退休。这些替身响应不代表真实 Windows 已出现相同交错。

Windows 下另有测试专用 WinEvent DLL，只加载到自有 fixture。它观察真实 `EM_SETSEL` 栈内的 caret/selection 回调；无事件是有限阴性，不能推断永不重入。`OwnedRetirementInsideSelectionCallbackRejectsThePendingWriteBeforeTextMutation` 在该栈内退休 owned 寿命，要求拒绝正文且保留真实选区副作用。

该场景的历史 DLL 清理失败及原件由 [Issue #16](https://github.com/DingJWuyu/Typelay/issues/16)承接。有效拒绝子项不抵消清理失败，也不证明其它重入、物理键或第三方部署通过。

## 暂停入口回归

[PausedInputTests](../../../tests/windows/TypeLay.App.Tests/PausedInputTests.cs)通过生产 Broker 的 `observe-pause` 验证暂停期间 UIA Value getter 为零，并用普通 capture 作真实读取正控。密码、只读、敏感、排除、缺 pattern 和不合格 ComboBox 均拒绝。

真实会话场景只点击一次恢复按钮，核持久化、原焦点和按需模式保持。视图回归检查几何、文字与撤销状态清除、旧代次点击和恢复。共享 Runtime 另核迟到、过期、旧目标与保存失败。

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~PausedInputTests|FullyQualifiedName~OverlayRenderingTests'
```

## 设置与浮层的视觉回归

设置渲染、快捷键草稿与控制器场景共用一个 STA/WPF Application，但分别报告。单场景失败清理自有窗口，系统调用阻塞仍可能导致后续未执行。可按原消费者分别定位：

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~UnsavedShortcutDraftSurvivesSettingsRefreshAndPageNavigation
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ControllerDesktopScenario
```

当前[设置窗口](../../../src/windows/TypeLay.App/UI/SettingsWindow.cs)默认 920×700，最小 800×560。检查实际七页入口、搜索、滚动、长文、浅深主题、键盘焦点和失败反馈。文字和选中导航按实际背景检查 4.5:1 对比度，不能沿用旧尺寸截图作为当前证据。QuickPanel 的写锁负例须保留已确认设置和错误，解除锁后用原控件重试；后台刷新不触发保存。

浮层的 ItemStatus 区分当前译文、人工稿、待返回稿和状态文字。观察器不能把忙碌提示或旧 revision 当当前译文。高度动画回归读取真实 WPF 帧与 HWND 几何，检查连续中间尺寸、固定字号、底栏、反向变化、焦点及隐藏后不复活。减少动态效果应直接到终点。截图位于 `artifacts/test-results/rendering/`，必须查看实际图像，纯几何和交叉编译不能证明视觉通过。

## 按需快捷键诊断

`TYPELAY_TRACE` 用 `commandGeneration` 与正数 `commandSequence` 关联同次命令；Broker 请求 ID 不是该序号。租约字段描述发布时的观察与版本，不新增权限。`lastCommand*` 只是上下文，文件写入顺序也不是跨组件因果顺序。

Hook 保留原 64 条有界队列并报告溢出。capture、Broker 调用与源焦点诊断只输出固定类别、版本、布尔和时刻；没有 token 或进程状态时保留 null。只读既有 handle，不能为日志重新查询焦点或正文。可选 CDP 诊断保留最近 16 条已收到事件，不增加 RPC 或订阅；缺事件不能证明事件未发生。

日志用于区分采集、调度、入口拒绝与显示失败，不能靠日志刷新、等待新许可或重按隐藏故障。当前无正文诊断用法见[用户故障取证](input-diagnostics.md#新标签页实机复现)。

生产 App 的 `--automation-test` 只为该进程提供明确的软件输入测试模式，不写设置。使用前退出旧客户端、设置完整发行根，直接启动 `app/TypeLay.App.exe --automation-test`；完成后退出，再用 `TypeLay.exe` 恢复普通模式。该参数不能绕过有序源的来源、IME、焦点或期限门，更不能充当物理验收。

## 可选 Hy 隔离 adapter 验证

手动隔离入口用于 C ABI、控制 token 和 worker 生命周期，不改变 catalog、默认模型、设置或已有安装。

```powershell
./dev.ps1 build
./dev.ps1 hy-adapter-prepare
./dev.ps1 hy-adapter-e2e
```

prepare 在 600 秒内下载并完整校验固定 GGUF、CPU ZIP 与许可，构建隔离 adapter；e2e 在 180 秒内检查取消、恢复、一次真实 EOG 与已有字面 token 审计。每请求 60 秒，ctx1024、2 个计算线程、greedy、192 输出 token；只有真实 EOG 才成功，失败不输出部分结果。

worker 挂起创建，确认 Job 归属、单进程与 2.5 GiB committed-memory 限制后恢复。取消后须同时确认 Job active=0 和根句柄退出，再启动下一实例。正文只经有界 stdin，不能进入 argv 或临时 prompt 文件。该限额不是 Linux RLIMIT_AS 或 RSS。

正式检查使用实际 ABI5 发行包的 `--verify-hy-production` 与 `--verify-hy-packages`。五份生产程序集在消费者启动前与 ZIP 对齐并核实际加载 SHA；不改发行包或运行中程序集。前者核加载与退出，后者通过真实设置完成安装、选择、预览、移除和退出。

十四方向候选另验证补充中英的取消与确认、正常退出后同代冷启、普通中英双向预览，再恢复原语言设置继续原场景。默认新装仍十二方向，冷启不准备或修复。设置链保持 960 秒总限和 20 秒清理预留，部分或未知方向目录失败。GUI 生命周期通过仍不代表中英质量或高级能力放行。

所有模型消费者必须串行，并提交同 run/attempt/head 与 nonce 的独立进程族退出证明。业务失败保留为失败，清理未知阻止后续消费者。只上传列明的小结果、有界诊断和发行 ZIP，模型、下载缓存及整个 artifacts 目录不上传。
