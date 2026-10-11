# Verification protocol

修改测试、排障或声明验收结果时读本文件和 tests/AGENTS.md。检查范围按 `docs/agents/development.md` 选择。

## 自动化层级

`dev.ps1 check` 验证构建、标准 xUnit 行为/契约测试、Python 行为/启动测试和格式/lint。
`dev.ps1 shared-check` 在普通 .NET 与 Python 环境执行共享核心、生产会话编排、协议及 worker 回归，不运行 WPF、UIA、Hook 或模型推理。
`dev.ps1 pack` 生成真实自包含 ZIP，再运行分发与进程 E2E。该流程不装模型、不打开用户文档、不通过假翻译器伪造 ready 状态。
`dev.ps1 native-e2e` 需要已解锁桌面，验证受控 Win32 适配器的读写、选区、拒绝与零提交契约。它同时约束 Hook 测试依赖的字段后端；前置条件失败是未执行，不是通过。

测试失败时保留命令、提交 SHA、平台、退出码和最小失败输出。只使用合成测试文本。TRX 和临时包进入 artifacts/ 或 CI artifact，不将执行快照、日志或阶段报告提交到仓库。

## 受控桌面工具

以下命令从仓库根目录执行，使用已构建的 Release 项目和已解锁 Windows 桌面。它们是交互工具，不会自动完成全部断言。

```powershell
# 普通 Win32 测试窗口：由正式客户端和生产 Broker 观察，无进程内替身
# replay / focus-churn 操作直接改变测试控件，不代表物理输入或真实 IME
dotnet tests/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll

# 生产 Hook 的普通模式对照：软件注入 Tab 应交还控件，不接受候选
dotnet tests/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll --hook-test

# 生产 Hook 的注入模式对照：固定 HOOK TEST 候选，仅隔离按键行为
dotnet tests/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll --hook-test-automation
```

需要真实 WPF 浮层、SessionController、Hook 和模型时使用 DesktopFixture。先通过正常客户端明确同意安装组件/模型；发行目录由 `./dev.ps1 pack` 生成，也可使用 `./dev.ps1 run` 生成的 `artifacts/dev/TypeLay`。退出其它测试客户端后运行：

```powershell
$previousHome = $env:TYPELAY_HOME
try {
    $env:TYPELAY_HOME = (Resolve-Path 'artifacts/package/TypeLay').Path
    dotnet tests/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows/TypeLay.DesktopFixture.dll artifacts/desktop
} finally {
    $env:TYPELAY_HOME = $previousHome
}
```

TYPELAY_HOME 必须指向包含 worker/ 的完整发行根，不能指向源码根或 app/。DesktopFixture 使用独立 fixture-settings.json 和进程内 WPF 字段后端，不改变普通用户设置。它可以定位拖选、Delete、两阶段 Tab、连续追加、暂停/恢复和提交计数问题；这些结果不覆盖 UIA 或 Broker 管道。连续追加直接修改已提交文字，不模拟 IME。

生产 App 的 `--automation-test` 参数仅对该进程允许注入 Tab/Esc，界面明确标示，不写入设置。仅在退出已有客户端、设置好发行根 TYPELAY_HOME 后直接启动发行目录的 `app/TypeLay.App.exe --automation-test`；测试完成退出该进程，再用 TypeLay.exe 恢复普通模式。注入模式仍须遵守全部写回、焦点、IME 和期限检查，不能作为物理键盘验收。

## 真实产品验收

以下项目需在实际 Windows 桌面单独执行。默认只读优先，实验写回仅用备份的非敏感内容。开放风险及关闭条件以 `docs/agents/architecture.md` 为准。

1. 完整解压，双击 TypeLay.exe，首次安装需明确同意；重复启动只留一个托盘；退出后 App、Broker、worker 和 hook 全部退出。断网后已装模型仍能推理。
2. 在教程之外的浏览器普通输入框与桌面应用中输入合成文本，验证真实模型候选、无抢焦点/页面挤压。无标签输入不能预先排除；失败控件如实记录。
3. 人工修改候选，观察原生光标、选择、粘贴与换行。第一次 Tab 只回源字段，下一次 Tab 才尝试接受；间隔不限速度，按住键不能提前接受。原文末尾追加与原文重写按产品规格分别处理。局部选区替换保留范围外文字、emoji 和换行；提交计数必须有可递增的正向对照。
4. 使用真实微软拼音在源字段和浮层编辑器覆盖 composition、翻页、选词、Backspace、取消、提交、Shift 切换、组合期间 Tab 和候选窗口期间点击浮层。DOM composition 和 SendInput 均不等于物理 IME/Tab 通过。
5. 对焦点切换、字段关闭/复用、源文本并发修改、Broker 超时/崩溃做拒绝测试。不得覆盖新文本、串写其它字段或提交表单。B-01 未关闭前不能勾选安全写回通过。验证浏览器受控组件、撤销栈和事件同步，不能只比对最终文字。
6. 对照 docs/reference 中已确认原型检查浅/深主题、100%-200% DPI、多屏负坐标、窗口移动、滚动、长文本和展开状态。记录截图、实际时延及设备条件，发现明显异常也要处理。HTML/CSS 与 WPF 控件参数相似不等于视觉一致。

## 结果表述

在任务回复或 CI 结果中分别说明构建、行为/协议、分发/进程、模型推理、真实桌面交互与视觉结果，使用 passed/failed/not-run 并明确范围。结果属于执行时的提交，不能外推到其它版本；不新增仓库内验收报告或空白验收模板。
