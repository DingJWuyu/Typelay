# Windows 测试与桌面验收

按[共享验证协议](../../engineering/testing.md)表述证据；本页只适用于 Windows，Linux 入口见其[开发与验证](../linux/development.md)。

## 自动化层级

`dev.ps1 check` 在 Windows 验证构建、标准 xUnit 行为/契约测试、Python 行为/启动测试和格式/lint。
CI 在本轮构建成功后独立收集行为、格式、原生适配器和发行包结果；任一失败仍使任务失败。构建失败或取消时不运行依赖产物的后续验证，避免使用旧产物。
`dev.ps1 pack` 生成真实自包含 ZIP，再运行分发与进程 E2E。该流程不装模型、不打开用户文档、不通过假翻译器伪造 ready 状态。
`dev.ps1 native-e2e` 需要已解锁桌面，验证受控 Win32 适配器的读写、选区、拒绝与零提交契约。它同时约束 Hook 测试依赖的字段后端；前置条件失败是未执行，不是通过。

`TypeLay.App.Tests` 的浏览器采集回归使用运行机已经安装的 Google Chrome、独立临时配置目录和本地普通搜索页。它走生产 Broker，实际注入不含标点的文字并检查源快照更新；不预先打开特殊可访问性开关，不下载模型，也不把采集成功当作真实译文、写回或物理输入法通过。运行机缺少 Chrome 是前置失败，不静默跳过。

浏览器前置必须先确认本次自有进程家族中的唯一测试页面窗口、前台身份与页面输入焦点，不能将启动进程的 `MainWindowHandle` 或任意当前焦点当成浏览器目标。前置失败不执行正文采集，也不计为输入框支持结论。

定位时可单独选择真实场景，避免一个桌面失败挡住其它结果。在 Windows 已完成 Release 构建后运行：

```powershell
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~BrowserCaptureTests
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~UnsavedShortcutDraftSurvivesSettingsRefreshAndPageNavigation
dotnet test tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ControllerDesktopScenario
```

设置渲染、快捷键草稿和控制器桌面场景共用测试套件的单个 STA/WPF Application，但分别报告结果。单场景失败会清理自有窗口；真正阻塞 STA 的系统调用仍可能令整套件超时，不能将后续未执行项算作通过。

`OverlayRenderingTests` 检查实际 HWND 位置/尺寸、源焦点、长词换行、字体变化、短文重显及主题/展开/手动草稿。测量必须与编辑框的 `TextWrapping.Wrap` 一致：[WPF 10.0.12 的 `FormattedText.Trimming` 实现](https://github.com/dotnet/wpf/blob/v10.0.12/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/FormattedText.cs#L1342-L1360)表明 `None` 才强制拆分超宽单词，默认省略会低估无空格文本高度。保留连续长词反例，不能用插入空格让回归通过；这些合成会话只验证展示，真实模型连续更新由下方独立模式验证。

## 受控工具的边界

`tests/windows/support/TypeLay.Platform.Windows` 提供只访问已登记 fixture 控件的 Win32 适配器，服务适配器契约和键盘 Hook 回归，不在生产依赖链。其受控 IME 状态不等于外部应用的真实输入法状态。

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

随后复用同一模型和原字段切到按需模式，等待生产原生后端实证直接替换能力，在原文/选区/焦点完整时仅注入一次 `Ctrl+Shift+F10`。生产 Hook 和 Broker 仍独立检查 IME、身份、版本与期限；只有真实 Verified 回执对应的成功状态、原字段匹配真实模型结果、焦点保持、浮层始终隐藏且没有额外提交，直接替换阶段才通过。最后点击自有提交按钮验证计数器确实工作。该阶段只覆盖 x64 stock Edit 与显式自动化按键，不代表物理 IME 或其他应用可写；`continuousPreview`、`directTranslation` 和 `submitCounterVerified` 分开记录，任一步或清理失败都会返回失败，不重试 F10。

整个模式有 20 分钟取消期限，执行者仍须设置外部进程/步骤上限处理不可取消的系统调用。成功返回 0，前置参数错误返回 2，其余失败返回 1；失败不重试或延长安全观察期限。结果、`trace*.jsonl` 和安装 CPU 自检可进入对应 CI artifact，模型、下载缓存、原始输入及译文不上传。`TYPELAY_TRACE` 开启时，浮层最多记录 32 条耗时至少 50ms 的同步渲染阶段样本到 `.overlay.jsonl`，默认关闭。

生产 App 的 `--automation-test` 参数仅对该进程允许注入 Tab/Esc，界面明确标示，不写入设置。仅在退出已有客户端、设置好发行根 TYPELAY_HOME 后直接启动发行目录的 `app/TypeLay.App.exe --automation-test`；测试完成退出该进程，再用 TypeLay.exe 恢复普通模式。注入模式仍须遵守全部写回、焦点、IME 和期限检查，不能作为物理键盘验收。

## 真实产品验收

产品要求统一从 [README 产品验收入口](../../../README.md#产品验收入口)读取冻结规格及已接受 ADR-0002。以下项目需在实际 Windows 桌面单独执行，Linux 的对应入口见 [Linux 验证](../linux/development.md#验证)。默认只读优先，实验写回仅用备份的非敏感内容。共用风险见工程架构，Windows 写回风险及关闭条件见[本平台架构](architecture.md#b-01非原子写回与按键所有权)。

1. 完整解压，双击 TypeLay.exe，首次安装需明确同意；重复启动只留一个托盘；退出后 App、Broker、worker 和 hook 全部退出。断网后已装模型仍能推理。
2. 在教程之外的浏览器普通输入框与桌面应用中输入合成文本，验证真实模型候选、无抢焦点/页面挤压。无标签输入不能预先排除；失败控件如实记录。
3. 人工修改候选，观察原生光标、选择、粘贴与换行。第一次 Tab 只回源字段，下一次 Tab 才尝试接受；间隔不限速度，按住键不能提前接受。原文末尾追加与原文重写按产品规格分别处理。局部选区替换保留范围外文字、emoji 和换行；提交计数必须有可递增的正向对照。
4. 使用真实微软拼音在源字段和浮层编辑器覆盖 composition、翻页、选词、Backspace、取消、提交、Shift 切换、组合期间 Tab 和候选窗口期间点击浮层。DOM composition 和 SendInput 均不等于物理 IME/Tab 通过。
5. 对焦点切换、字段关闭/复用、源文本并发修改、Broker 超时/崩溃做拒绝测试。不得覆盖新文本、串写其它字段或提交表单。B-01 未关闭前不能勾选安全写回通过。验证浏览器受控组件、撤销栈和事件同步，不能只比对最终文字。
6. 对照 docs/design 中已确认原型检查浅/深主题、100%-200% DPI、多屏负坐标、窗口移动、滚动、长文本和展开状态。记录截图、实际时延及设备条件，发现明显异常也要处理。HTML/CSS 与 WPF 控件参数相似不等于视觉一致。
