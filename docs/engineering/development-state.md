# 当前开发状态与安排

这是当前开发的唯一现役入口，不是完成报告。**Windows 与模型线持续推进；Linux 暂无派工，保留已验证推荐包。** 负责人、证据或下一动作改变时更新对应行；已关闭事项删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。

## 先看结论

本页于 2026-10-23 核对。**Windows 普通源写入仍未支持，CI473 的准备失败已有本地修正，TSF 正从上下文观察推进到自然键路由与同步只读验证；Hy 中英独立16条已审，非公开兼容基础已集成，实际候选14的无模型构建与 Host 预检通过，正在准备真实产品链。** 编译、研究探针、采集和无模型预检都不能替代产品验收。

### 当前身份与最近 CI

- 本地代码集成基准 `b742ddc`，包括 `2877d6e` 提前上传 Windows 诊断、`300476e` 自有前台导航与 ordinary 初始化修正、`b742ddc` 非公开 Hy 中英兼容基础。**这三项尚未远端发布**，不得用 CI473 结果替它们背书。唯一发布者已只读确认远端仍为下述 `c1f242f`、无并发变更；下一候选先发已集成的37文件及本状态页，不等待 phase2 新实验。
- 最近终态为 [CI473](https://github.com/DingJWuyu/Typelay/actions/runs/39934831201)，单次自动运行、整体失败。远端源码 `c1f242fcd11a58603b6180e2a1004c4361dd18bc`、tree `e5f9c1b23e042d7c54a33dcf8267f928cdcecc19`，对应本地发布基准 `e742ae9`。原件、终态与包核验收据在上层工作区 `../artifacts/ci473-current/`。

| CI473 范围 | 实际结果 | 能证明与不能证明的边界 |
| --- | --- | --- |
| 共享检查 | 1641 项通过 | 只对应上述远端源码，不包含本地新增 Hy 基础 |
| Windows App | 已执行759项，743通过、16失败；16项均在首次 `StockFixture.Focus → Desktop.Prepare` 焦点断言失败 | 失败尚未到各自被测事务；本地修正仅 Compile，须新 Windows CI 复验，不写成已修复 |
| Windows 连续场景 | 失败于 `ordinary-start-source`，`EM_SETSEL` 返回 Win32 1460；前面的自有源场景有通过原件 | 不把自有源成功补作普通源成功；原失败及未运行后继保留 |
| 正式 Notepad | 仍为 `writeSupported=false`，接受正向门失败 | 预览、模型就绪和焦点有效不等于接受能力 |
| TSF phase1 | 共4次观察：1次 wrong-field 阴性，A→B→A 三次 exact existing context 正向；引用释放、正常退出通过 | 未测试自然键路由、同步读锁、composition 或生产写入 |
| 同步机制与既有产品回归 | 六模式5..10全部机制观察成立、输入配对与清理通过；browser7、Hy生产5、包生命周期10通过 | 裸机制不是普通源许可链；既有 Hy 场景不是候选14产品链 |
| Linux | 隔离 Fcitx/GTK 已过；实模 GTK 与 GTK4 两步骤失败 | 原件留存，不宣称根因已定或已修；推荐仍为 CI468 |

## 怎么安排，谁负责

**Windows 与模型主线不得因一批结束、发布等待、CI 等待或编译槽等待而停摆。** root 每批核证据后立即交接下一动作；需授权的模型执行保持未准入，同时推进实现、静态检查和独审。职责不是实时活跃人数，汇报人数只计工具确认的 `running`。

| 责任线 | 当前负责人及工作区 | 当前下一动作 | 本批关闭条件与后继 |
| --- | --- | --- | --- |
| Windows 集成与发布复验 | root；发布交唯一写者，集成区 `Typelay-ci468-recovered` | 组合 App.Tests/DesktopFixture Compile 与 Runtime1224 已过，交唯一发布者冻结的37文件与状态页，不等 phase2；发布后跟踪新 CI | 核对远端 commit/tree、原 App 失败、ordinary 初始化及诊断原件；Compile 不算复验。正式 Notepad 缺口仍由接入线继续，不因等待 CI 暂停它或模型线 |
| Windows 普通源接入 | `prepare_windows_tsf_admission`，独立 advisor；`Typelay-windows-tsf-keyprobe` | 实现 phase2 自有 stock Edit 的自然键路由与同步只读，交独审并完成本地 Compile；本地编译已通过；最终窄审修正另行核验 | 取得真实路由、同步只读、原键/文本保持及完整清理证据，逐项区分未准入与程序失败；仅满足研究必要条件，不开启生产写、不注入第三方 |
| Hy 中英实际产品链 | `implement_hy_english_candidate_support`，独立 advisor；`Typelay-hy-english-candidate` | 复用已通过的 stage-a4 实际候选；runner 已冻结并经 root 窄审，正在执行一次真实 Host/安装链 | 下一批为旧12激活 → 故障候选拒绝并保留旧代 → 成功扩到14 → 两条 smoke → 全部退出及锁复取；**当前已获一次模型执行准入，结果待核，不冒称通过**。失败保原件并停止余下阶段，不以 mock 或静态测试代替 |
| 候选裁决与资源协调 | root | 对实质变化做独审、分配编译/推理槽，按结果安排后继；保持公开 catalog、默认与预算不变 | 本地兼容 GO 不等于产品或质量放行；真实失败恢复、方向授权、原选择和退出均有原件后才裁决下一阶段，公开采用另行决定 |
| Linux | 暂不派工 | 保留 CI468 推荐包及 CI473 两项失败证据，不新增扩展或重复归因 | 用户要求恢复，或证据表明当前共享改动造成回归时由 root 明确安排 |

同机重编译与模型推理错开，模型推理串行。槽位由 root 按实际 owned 进程退出及所有权证据交接，不按代理结束消息释放；远端 CI 不占本地编译槽。代码、输入、身份、权限及资源合同未变时不反复全量审查，只复核实质变化与受影响边界。

## Windows：从当前失败推进到普通源必要能力

具体产品约束见 [Windows 待办](../platforms/windows/open-issues.md)。普通输入框接受译文、F10 替换与连续输入是核心场景；正式 Notepad 已证明仅预览，误写或丢稿影响高，日常发生频率未知。按[实际价值判断](testing.md#实际价值判断)优先闭合首键、原子写入、IME 与身份，而不是为归因本身重复跑已过场景。

- **准备修正待实际复验**：`300476e` 在源准备边界显式完成自有前台导航，并修正 ordinary 初始选择准备。App/桌面 fixture 托管 Compile 已过，日志在 `../Typelay-windows-source-next/artifacts/windows-source-next/ci473-*-compile.log`。原文本、焦点、选区、撤销与许可断言不放宽；未取得新 Windows 结果前不声称16项或1460已解决。
- **诊断先于后续失败留存**：`2877d6e` 提前上传源研究原始诊断，避免后续失败遮蔽已完成研究；它改善可观测性，不改变产品能力。
- **TSF 下一门是 phase2，不再重复 phase1**：CI473 已证实自有 stock Edit 的既有 context 与精确 view 可观察，且 wrong-field 被拒绝。当前实现自然键路由与同步只读研究，不造合成 context、不注册或注入第三方、不写文本；未取得能力不得用推测或降级绕过。phase1 原 JSON 明确 `keyRouting/synchronousReadLock/composition=not-tested`。
- **生产接受仍关闭**：六同步实验与 TSF context 阳性都不是完整许可见证。必须保留首键、同目标有效按键消费、原子文本事务、IME 与精确源身份边界；未知即拒绝。用户已决定接受后主动切换应用时，后续 repeat/up 交给新应用，跨应用尾键抑制不再是要求。
- 已过的几何、浏览器 metadata/Ready、源安装与退出回归保留，不作为现役排程再派工；旧故障未复现不等于根因已证实。新真实失败出现时按首因推进，不靠扩大期限、重试或跳过断言制造绿色。

## 模型：非公开 Hy 中英兼容基础已集成，真实产品链已准入待结果

### 当前已完成的证据

- **独立16条已审**：固定 Hy 与 Argos 默认各16翻译＋1 hello，整轮39.099秒、零重试，两个 owned 进程组退出并释放锁。Hy 为14条未见实质问题、1较小、1退款动作未决；Argos 为7重大、4较小、5未见问题，保守口径至少5对明确重大修复。只支持进入兼容/产品验收准备，未决不算通过，台灯错义及其他方向已知缺陷不消失。原件与审查在 `artifacts/paired-daily16-run-20261023-01/`，退出/结构复核在 `artifacts/paired-daily16-execution-20261023-01/`。评审先冻匿名判断再揭盲，但见过旧定性摘要，且不是专业译者验收，不宣称完全盲评或真实用户错误率。
- **兼容基础双独审 GO**：作者提交 `86b58f1`、集成提交 `b742ddc` 已审。Runtime1224、worker165、Linux setup108、supervisor/http各1、payload12、prepare11、ELF无模型ABI11通过；Windows仅 Compile。检查原件在 `../Typelay-hy-english-candidate/artifacts/`。这些不是实际 Windows 运行或模型安装恢复的替代证据。
- **stage-a4 是真实无模型通过**：`../Typelay-hy-english-candidate/artifacts/hy-candidate14-product-preparation/stage-a4/` 的 build/native/Host 均退出0，Host 准备视图真实读到嵌入候选14方向；空数据 `modelReady=false`，没有模型 worker、没有复制权重，所有 owned 进程退出，总13.806秒。前序 SDK 构建失败原件保留，是构建前置失败，不是模型失败；stage-a4 不补作那些失败通过。

### 必须保持的兼容边界

正式 catalog 仍12方向，ABI5、greedy、模型权重锁、默认与公开 UI 不变。新增是非公开 `english-v1` 能力及显式 format2 `Install/addDirections` 事务；旧 Repair 不扩方向。选中库存、manifest 与原生导出共同授予能力，不因库里有入口就宣称已安装；事务失败须保旧代与 active 选择。**跨旧版本恢复尚未完成，不承诺旧版本自动恢复14方向。**

下一次实际产品链只沿真实 Host、生产安装/激活和 editor snapshot 入口，验证旧12、预期故障后的旧代保留、成功14及两条冻结 smoke。候选构建物与原件分开，原安装证据只读，不手造 inventory/hash/current，不用研究 worker 或 mock Ready 顶替。只有实际观察到旧 worker 退休与替换时才声称覆盖恢复；preflight 拒绝只证明未切换。两条 smoke 不是质量复评，不重跑已消费16题；实际模型阶段获准后才执行，任何非预期失败立即停止余下阶段并保存已运行/失败/未运行及退出区别。

### 资源与采用条件

- SDK 构建与模型推理预算分开，不因构建前置失败扩大模型预算。模型保持两核 CPU affinity、硬地址空间2.5GiB、单请求及加载/hello60秒、单共享600秒、清理最多7秒、失败停止、零推理重试；推理与重编译不得重叠。两核 affinity 不等于只有两个线程，点采样不冒充连续资源证明。
- Hy/Qwen context1024、output192；Argos保留生产输入384 pieces、输出512拒绝界；Mozilla保留整请求192 token保护。原身份、模板、参数、原响应、失败与退出证据必须保留；冻结模型合同不因 SDK 独立构建而变化。
- 真实链准入前核实际二进制与模型身份、空间、锁、owned进程与资源槽；runner/合同已冻结并取得本次准入，后续新一轮仍须按结果裁决。兼容成功只证明该链，不自动证明高级操作、完整 GUI、物理输入或质量合格；不改默认、不上传文本、不启用云端，不无假设扫参或扩候选。

### 仍约束当前取舍的质量事实

以下是合成开发样本与 agent 复核，不是产品正确率；已见挑战与日常样本分开，旧缺失原件不能拼入本轮结果。

| 候选或比较 | 保留结论与当前用途 | 可读原件 |
| --- | --- | --- |
| Hy rep1.0 对默认1.05 | 风险8条仅1项明确角色修复；日常24条两臂各2重大，德语7点半及中韩 AND→OR 仍错，无替换默认依据 | `artifacts/hy-repetition-run-20261023-073958/`、`artifacts/hy-daily-run-20261023-01/` |
| Hy 中英首筛18条 | 日常12条为1重大/1较小/1未决/9未见问题，挑战6条为0/1/1/4；台灯摇晃错成灯光闪烁、严格20分钟上界未决，独立16条不抹除这些边界 | `artifacts/hy-english-run-20261023-01/`、`artifacts/hy-english-preparation/semantic-review-20261023.md` |
| Argos beam1 对 beam2 | 固定rep1.05，两臂日常各3重大、挑战各2重大，无重大修复，不采用beam1；成功轮退出和锁复取通过，不补作旧安装轮清理证明 | `artifacts/argos-beam-preparation/` |
| Mozilla 官方资产与有界CLI | 2次门＋18翻译全部exit0、退出清理确认，5.837秒；日常3/2/1/6、挑战0/2/1/3，保义不足，不采用。小而快不是公平性能胜出或语义合格，普通请求不证明极限拒绝分支 | `artifacts/translation-products-research/mozilla-official-assets-current/` |

其余旧 Hy instruction/Q6、Qwen3-1.7B、Liquid 对照无采用依据，旧原件缺失，不据摘要重判或重跑同配方。Liquid 条款待核；Qwen3.5-0.8B 无已确认发布方官方 GGUF，源转换缺预算内路径；MADLAD 初始化超预算，M2M100 转换路径未证明，TranslateGemma4B 资源/条款暂缓。未实测不写成质量失败，无新假设不扩研究范围。

## Linux 与恢复证据边界

- Linux 仍推荐 CI468 `091fd796` [体验包](https://github.com/DingJWuyu/Typelay/actions/runs/39904552393/artifacts/12480483952)，其完整检查、包身份、gzip/ZIP、293项清单及本包说明已有核验；仍需用户真机、物理键盘和输入法体验，不是所有应用验收。CI473 隔离已过、实模 GTK/GTK4 失败如上，当前不派修复或扩展任务。
- 按需唤醒后空捕获仍保守中止：恢复同一目标不证明期间未离开焦点家族；未取得有效顺序与屏障证据不恢复唤醒，见[架构约束](../platforms/linux/architecture.md#按需唤醒与缺失捕获)。可选富文本、非合作控件、Electron/VS Code 自动浮层扩展暂不开展。
- 已发布 CI468 源码从 shared artifact `12480153131` 完整恢复，tree `5dbdda82b1d415397db8580ca1878ab6dc7a2d95` 与发布一致；本地恢复基准 `88d8738` 是独立提交，不冒称远端历史恢复。收据在 `artifacts/ci468-source-recovery/verification.json`。
- 旧模型原始输出、资源/退出和独审原件，以及 Mozilla 未发布5文件补丁/33项测试原件仍缺失；可读的新证据不冒称旧证据恢复。未注明的 `artifacts/` 相对本集成工作区；模型兼容和 CI473 的跨工作区路径已分别明确。原始日志与模型留在被忽略目录，不提交或另建历史快照。

## 持续维护规则

1. 遵守 [AGENTS](../../AGENTS.md) 与产品基线；未知焦点、IME、身份或结果继续拒绝/隔离，外部准备不偷占或放宽产品许可期限。
2. 区分实现、本地检查、精确提交 CI、用户真机；失败、未测与环境限制分别报告，编译不等于 GUI，采集不等于质量合格。
3. 有效测试与有界诊断保留，过时假设或退役要求才删除；失效接口和引用同步清理，不为整理重写全库或新增无价值回归。
4. 相同失败必须换取新证据，不盲重跑；需要新授权时写清具体动作和所缺条件，继续独立的 Windows/模型工作，不把等待当完成。
5. 本页只维护当前事实、负责人、下一动作和关闭条件，已关闭排程直接删除；候选、CI与包逐项核一致，历史追溯交给 Git。
