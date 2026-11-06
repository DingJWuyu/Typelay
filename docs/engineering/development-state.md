# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与最近 CI

截至 **2026-11-06**，最近已核平台验收对应开发分支 `work/linux-usability` 的提交 [`ff5394f70391745e8e6e06549482485f79007b01`](https://github.com/DingJWuyu/Typelay/commit/ff5394f70391745e8e6e06549482485f79007b01)。[CI587](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940) 于 2026-11-06 03:58:09 UTC 终态 failure，Linux 和 Windows 均已执行。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本头已验证前批 Qt 正式 fixture 生命周期、两项 Unicode 查询输入与 GTK4 焦点拒绝诊断。现役后续限定为三个已有失败边界的有界诊断：Qt Composing 点按回执、GTK4 租期耗时与焦点生命周期、Windows 两项既有 WPF 回归。诊断候选已准备，本地检查已完成，独立审查进行中；后继目标平台验证待完成，尚无已验证的产品修法，也未启动新的 CI。

实施范围保持原型 I-III；IV 的 N06-N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。不增加产品功能，不实施 O02 hover 或 O04 几何、材质、动效候选。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。

## 最近真实平台结果

以下均为 ff5394f7 的 CI587 原始证据。原始 ZIP 已核 SHA-256；JSON、TRX 与日志从下列 CI artifact 入口获取，不提交模型或原始日志。下表通过、失败和未执行分开记录。

### Linux

证据入口：[Linux job 127695797543](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940/job/127695797543)、[正式桌面 artifact](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940/artifacts/13475449422)。Qt 完整 probe 失败后，正式桌面使用已校验的未封装构建收集独立交互证据；`qt-desktop-admission.json` 明确为 `unbundled-build-input-interaction`、`distributionAcceptance: false`，不能抵消 probe 失败或冒发行包通过。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| 共享 / Linux Browser | Core 318、Runtime 1565、Protocol 28、Windows Contracts 211 全过；正式 DOM 100/100 与 Browser 七项通过 | 保留 pending echo、后来稿/ABA、中断与 focus 恢复重入边界；不外推 Windows 可恢复拒绝重试、混版 GUI、非 Editing 确认或 rich 正文变更后 bookmark 体验 |
| Settings / 教程 / VS Code / GTK3 | 三条桌面链通过，GTK3 正式 21 项完成 | 目标搜索、源选择与显式 Apply 保持；Unknown 专项仍未测。各项交互不替代完整视觉、可信物理输入或全部应用验收 |
| Qt 正式桌面 | 正式桌面 artifact 的 Qt `desktop-result.json` 记录全部 26 项通过，包括真实 Host 目标搜索、先前未到达的 multiline UTF-16 选区替换/原生 Undo/redo、QPlainTextEdit 实际 pinyin、只读/特殊文档拒绝及未知回执不重试 | 这是本头未封装构建的完整已记录交互链。停用正式 caller 的固定 60s 自退后越过原失败，不证明自退是旧失败的排他原因；原外层 240s、进程退出观察与 cleanup 保持 |
| Qt 受控搜索 | 真实 MOC/C++ 构建通过；两项 Unicode 查询、保存/交换、provider 恢复、Tab/ShiftTab/Escape、外点取消与旧 opening 退休均已越过。随后 Composing 日语行点按的 `clicked` 回执等待失败：实际日语行收到新 press，未增加 clicked | [Qt probe artifact](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940/artifacts/13475533531) 已保留 `language-options-native-state.json` 与日志；旧事件记录达到上限，不能据此断言 release 接收者或几何根因。后续 Composing 零保存断言、Return/held/fresh 和配置主题截图未执行；有界点按事务诊断候选已准备，待目标平台验证。Unicode 通过来自真实剪贴板 Ctrl+V，不是物理 Unicode 键盘验收 |
| GTK4 早期术语 | [早期 GTK artifact](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940/artifacts/13476255484) 的 `gtk4-glossary-result.json` 首例 entry / ValidTranslation / save 在 capture-more→retain-more 间因 fixture abort 退出，`cases=[]`，40 项中完成 0 项；最后 800ms lease 已过期，最后快照源仍持焦，未证实源焦点丢失 | 具体慢阶段未知，不能先归因 GTK 截图、IPC 或产品焦点。仅记录既有操作耗时、到期点与 fixture 状态，不加 renew、retry 或延长租期 |
| GTK4 正式配置 | 前七个独立场景通过，包括 Enter 保存、实际 pinyin、held repeat、跨 row 与释放后 fresh 保存。第八个 window-focus 仍在 Begin 前 readiness 失败，Begin/Save/Cancel 均为 0；TRACE 新证实服务器焦点为 0、本地焦点为 1、local flags=2 | flags=2 是本地标志，不是 IME Known/Clear。分歧何时产生仍未知；不是已经进入配置壳后的失焦。后续场景未执行，完整外点/自然越界/Esc 与原 Composing 行点按接收目标未闭，焦点生命周期诊断候选已准备但尚未目标平台验证，原资格门保持 |
| 发行附件 / GIMP / 正式 Hy | Qt probe 失败阻断原发行附件/准入，GIMP 和正式 Linux Hy 本头均为 SKIPPED | 在新头满足原发行准入后执行；不能以旧头结果或 Qt 未封装交互替代 |

### Windows

证据入口：[Windows job 127695797538](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940/job/127695797538)、[Contracts artifact](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940/artifacts/13475757602) 与[桌面 artifact](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940/artifacts/13476750238)。Contracts 中 App 的 `runneradmin_runnervmhao91_2026-11-06_03_42_09_net10.0[1].trx` 共 1470 项，实际执行 1448，1446 通过、2 失败，其余 22 未执行；共享四套计数见上表。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Onboarding 进度重定向 | `ANewDownloadReportRetargetsFromTheLastPaintedWidthRatherThanJumpingAhead` 在原 3s 观察期限超时，停在 `ObserveOnboardingFill`，尚未到达重定向断言；本头无帧轨迹 | 既有 Rendering/进度阶段及失效状态诊断候选已准备，待目标平台区分未观察到目标帧与产品轨迹错误；不改原时限、断言或动效 |
| LocalDraft 过期 claim | `AReleasedReplaceThatExpiresBeforeClaimDoesNotConsumeTheFirstFreshRetry` 在旧过期 claim 后的 anchor 仍有效断言失败，位于第一组 fresh retry 输入之前 | 现有原件不能确定 anchor 失效来源，也不能称 fresh retry 被吞。现有取消/claim 时间线诊断候选已准备，尚待目标平台验证；不续原 down+500ms，不改变接受资格 |
| 构建 / 主链 / 原 sheet-hide | 构建、Continuous、搬迁发行包、Hy ABI5 与 Settings 生命周期通过；CI584 失败的 `ApplicationSheetRetirementClearsOnlyItsBackgroundAndCannotReuseOldConsent(retirement: "hide")` 在本头 TRX 为 Passed | 旧 red 项不继续写成当前失败；单次通过不能证明旧故障排他根因。源确认 Keep 与 General 动效已有回归仍保留，不外推全部页面流畅或逐像素一致 |
| Browser | setup + 8 场通过；Editing 确认 4 Keep / 3 Replace（click、Enter、Space），原源不变、submit=0；术语生命周期、按需/直接/断连与正向 submit 控制通过 | physicalIme 未测，safe-false 后重试、混版及非 Editing 完整确认仍未闭；旧头缺 render 身份不由本头成功补成因果证明 |
| Notepad / 物理输入 | 桌面 artifact 中 Notepad 的 `result.json` 五场通过：预检、模型设置、自动预览、按需取消/直接替换/Undo、退出。自动接受/Undo 为 `trusted-source-input-required`，`acceptanceAttempted=false`；编辑回源/Undo 为 `dependency-unavailable`。原文件不变，cleanupErrors 为空 | 总结果为 failed，但不是已验证的写回回归；两项可信源输入仍未执行。软件注入或 physical-Tab 包准备不能替代实际输入验证 |

## 模型能力边界

CI587 两个平台独立 Index 包生命周期均完成全部 12 阶段，Linux 用时 107.309s；质量仍为 `not-reviewed`，两项任务均未运行 Index 桌面验收。生命周期通过不等于翻译质量、桌面体验或先前断连根因已闭合。

Index 仍是新用户明确准备时的默认，保留旧 Argos/Hy 安装与选择，不扩模型。固定 Index profile 的中英双向 Glossary 登记范围保持；其他方向、缺失或不匹配 profile、风格/组合/另述仍不由该证据放行。自然/正式风格和换种说法仍是未完成功能，不归 N06-N11 前端例外，也不重启优化研究。

## 当前工作与收束条件

本批诊断对应用户搜索目标语言、打开术语菜单、首次设置进度及重译重试路径的已观察阻塞。日常发生频率未知，实际影响与根因需分开验证。只在既有测试和失败点保留有界状态与时序，成本和维护范围有限，收益是让下一次失败能区分产品行为、观察缺口和测试调度；没有新假设或新证据时不重复重跑。

| 责任 / 模块 | 当前工作与依赖 | 收束条件 |
| --- | --- | --- |
| Qt测试 | Composing 单次点按的有界 geometry / press / release 诊断候选已准备，本地检查已完成，独立审查进行中，目标平台验证待完成；依赖现有 `language-options-native-state.json`、fixture 和[受控输入边界](../../tests/linux/qt_cooperative/run.py) | 原输入次数、等待、clicked 断言与 IME 门不变；查明 press/release/命中和布局关系后再决定修法。未取得下一同头 Qt GUI 结果前保持失败未闭 |
| GTK4合作组件 | 早期 capture-more→retain-more 的阶段耗时及第八场本地/服务器焦点生命周期诊断候选已准备，本地检查已完成，独立审查进行中，目标平台验证待完成；依赖现有术语、[配置测试](../../tests/linux/gtk_session_reentry.py)和合作组件 TRACE | 保原 800ms、readiness 时限与焦点/IME 门；不加 renew、retry、抢焦点或补发 Begin。分别找出耗时阶段与首次焦点分歧，再决定是否需要产品修改 |
| Windows桌面测试 | [Onboarding](../../tests/windows/TypeLay.App.Tests/SetupRenderingTests.OnboardingProgressMotion.cs) Rendering/进度阶段与 [LocalDraft](../../tests/windows/TypeLay.App.Tests/LocalDraftTapInputTests.cs) 取消/claim 诊断候选已准备，本地检查已完成，独立审查进行中，目标平台验证待完成；依赖同头 WPF 原生运行 | 不改 3s、动画时长、原 down+500ms 或断言；分别定位首个未到阶段与 anchor 失效来源，不以测试名推断未执行的重试结果 |
| 集成与文档 | 三个诊断候选本地检查已完成，独立审查进行中，目标平台验证待完成；同步维护本页及两份设计边界，尚未发布诊断或启动新 CI | 先核精确 head、未覆盖他人提交及原发行门，再按已有授权推进验证。没有目标平台条件时继续静态/局部检查，写清依赖；CI 等待不代表项目完成 |

Qt 目标搜索沿用 config-only Begin→退休 ACK→配置保存链，原 source/target/交换保留；仅按需快捷键已启用的源态开放，自动/禁快捷键及编辑态禁用。Host 原 generation/owner 门仍在 StopSession 前拒绝陈旧请求。GTK4 原次 Return 的 capture/IME/held claim 已有真实证据，Unknown/Composing 不借后续 Clear 升级，同硬件 held 跨 row 不升级，release 后才可接受 fresh 保存。GTK3 保源选择与显式 Apply，不冒完整原型方向头/交换/即时保存。

## 验证环境与未闭边界

CI587 已提供本头 Qt/GTK 原生构建与实际桌面、WPF 及模型生命周期证据，范围如上。后继诊断候选已准备，本地检查已完成，独立审查进行中，目标平台验证待完成；CI587 结果不能替代这些候选的新头平台结果。文档检查只覆盖事实、UTF-8/换行、精确基线差异与引用，不冒原生构建、GUI 或模型验收。

后继候选最终代码已完成仓库 Ruff 0.14.0 lint/format（181 文件）、受影响 Python compileall、既有 Qt 候选观察 18/18、GTK geometry 79 项与 preview 3 项合计 82/82、input-feedback 218/218，以及新增 Qt 日志 helper 4 项测试（含 6 次真实子进程操作）。状态发布场景已计入 79 项 geometry，不另计套件。GTK 补丁与受控 C/C++ 的严格局部检查只证明相应局部边界；本地没有原生 Qt/GTK 开发 SDK，也没有 .NET/WPF 编译与运行环境，仍需目标 CI 验证。

- Qt 本头正式桌面已生成六张主题、两张中文编辑、两张暂停图；产出不等于逐图复核完成。此前已阅 More 三点、Expand 箭头与 paused 采色证据保留，英文 UI 与展开态 Collapse 实图仍未齐。受控配置失败后的新主题图未运行，不能混作正式桌面已有图。
- “暂时隐藏”的既有受控单次 escape/旧 view 拒绝及 More 全部 12 项可见证据保留；完整 Host 隐藏、再次聚焦恢复用户链仍未闭。
- 完整双端逐页、真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭。
- S08 材质/中间组合像素、多 DPI、跨页 Full 动效及 O02/O03/O04 余项仍有差距。26px/15px 等控件断言不代替逐像素 1:1；当前没有整端视觉完成结论。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒完整原型呈现。
