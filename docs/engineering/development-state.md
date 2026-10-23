# 当前开发状态与安排

这是当前开发的唯一现役入口，不是完成报告。**Windows 与模型线持续推进；Linux 暂无派工，保留已验证推荐包。** 负责人、证据或下一动作改变时更新对应行；已关闭事项删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。

## 先看结论

本页于 2026-10-23 核对。**Windows 普通源写入仍有架构缺口：连续初始化修正已实测通过，前台准备及人工稿回源问题仍待首因证据；phase2 键路由/同步只读实验已在 CI475 获得 Tab/End 阳性原件，后续输入法与生产能力仍未证明。Hy 中英真实产品链已通过；手动预览启用已获批准并本地集成，正式目录可供14方向，新装仍12且默认不变，最终14方向GUI验收待后继CI。** 编译、研究观察与 smoke 不代替完整产品验收。

### 当前身份与最近 CI

- [CI475](https://github.com/DingJWuyu/Typelay/actions/runs/39961358090) 已于2026-10-23 13:49:56 UTC终态failure，唯一push、attempt1；远端 `139be7e77b524c6036a5d3cfa6a95575ddc3bd79`、tree `ee38ca403d1606c12272bc68866ac35387a4e1ae` 与本地冻结 `5f739da` 39文件完全一致，parent为CI474远端。当前共享1719/1719，Windows App775/776；唯一失败是新增候选English设置测试定位移除风格覆盖按钮失败，根因已定位为测试误查未规范化的saved.app，修正69eb38f本地Compile/纯文件格式与双轴独审通过，真实GUI待下一CI。原16前台失败及人工稿回源六变体本轮均通过，诊断变更不等于修复根因。phase2阳性见下；Linux完整job通过（含上轮失败的Qt），不据单次未复现声称根因已修；正式Notepad仍因接受提示超时失败，后3场景未跑。不含本页后续状态更新或中英公开预览启用改动。

- [CI474](https://github.com/DingJWuyu/Typelay/actions/runs/39948256660) 已于 2026-10-23 12:06:01 UTC 终态 failure，唯一自动 push、attempt1。远端 `796e81816079b13cf1f25a2d2bc3278e7936d1cd`、tree `5f525c4e44f9c4d19ac481886930868aca383a94`，精确对应本地冻结 `59d1218` 的38文件；用户明确批准后发布，没有夹带后继。
- 本地后继已集成 Windows phase2、模型安装探针修复、方向级能力与消费端、候选补方向动作，以及两处 Windows 失败的有界观察改进。前29文件组合 Runtime1287/1287、Protocol19/19、worker167/167、App.Tests/NativeFixture Compile、Ruff/YAML/格式通过；后加两诊断文件分别 Compile/格式通过，真实 Windows 尚未执行。诊断不等于故障修复。用户已明确批准本轮后续 Windows/Hy 源码、测试和文档继续在同一分支发布，以及手动启用 Hy 中英预览；不改 main/trunk，不上传模型或原始日志，保留 Argos 与既有默认。CI475的39文件另含Windows候选设置验收准备；其集成后Windows.Contracts111/111、App.Tests/DesktopFixture Compile、定点格式和冷启日志上传白名单检查通过。后继现已集成手动中英预览2fc49cc、设置回归修正69eb38f和自有Tab裁决实验7906966；各批双轴独审/对应检查通过，最终组合App.Tests Compile再次exit0，尚未发布。
- 本轮5份原ZIP及 head/SHA均核验；837条源码归档与冻结树逐项一致。证据根目录 `../artifacts/ci474-current/approved-publication-20261023/`，含 `terminal-verification.json`、`artifact-integrity-verified.json`、`verified-result-summary.json`。

| CI474 范围 | 实际结果 | 边界 |
| --- | --- | --- |
| 共享 | 1649/1649通过 | 对应远端59d1218内容，不是后继scope组合1287的远端证明 |
| Windows App | 758/775通过，17失败 | 原16项在唯一SetForegroundWindow返回false处拒绝，尚未进入被测写事务；第17项回源已focused但人工稿等待失败，尚未发送Ctrl/接受 |
| 连续场景 | 通过；ordinary焦点840853→840968，唯一选区设置840968→840970，原文/选区保持、writeRequests=0 | ordinary初始化修正已实测，ordinaryWritebackVerified=false，不证明普通写回 |
| 正式Notepad | 18条观测均writeSupported=false，90.009秒等待接受提示超时，后3场景未运行 | 仍仅预览，不宣称接受/撤销通过 |
| 六同步与TSF phase1 | 六模式5..10及清理通过；wrong-field阴性+A→B→A三次精确context阳性、引用释放/正常退出 | phase2不在本轮；键路由、同步锁、composition、生产写尚未证实 |
| Browser/Hy | 7/5/10场景通过；Hy normalQuit、exit0、allModelWorkersExited均真 | 既有12方向路径，quality not-reviewed，不证明新14 GUI或整体翻译质量 |
| Linux | GTK、GTK4、隔离Fcitx及Hy生命周期通过；仅Qt动画采样断言失败，观察高度仅100和280两档 | 未证实本批代码因果，不另开Linux线，推荐CI468不变 |

## 怎么安排，谁负责

**Windows 与模型主线不得因一批结束、发布等待、CI 等待或编译槽等待而停摆。** root 每批核证据后立即交接下一动作；需授权的模型执行保持未准入，同时推进实现、静态检查和独审。职责不是实时活跃人数，汇报人数只计工具确认的 `running`。

| 责任线 | 当前负责人及工作区 | 当前下一动作 | 本批关闭条件与后继 |
| --- | --- | --- | --- |
| Windows 集成与发布复验 | root；publish_ci471_with_approval 为唯一远端写入者 | CI474终态与原件已核；CI475已终态；后继7906966已集成三项变更并检查，冻结下一批发布，真实14 GUI及Tab裁决仍待实跑 | 原16行与人工稿回源要取得可区分原因的新事实；不盲重跑或以诊断通过冒称修好 |
| Windows 当前失败 | wire_direction_scoped_controls；Typelay-candidate-style-recovery | CI475唯一设置失败已按实际saved命名修测试，69eb38f保留删除/重置/术语回归并增切换前后偏好/按钮可见断言；待原Windows场景实跑 | 原断言不放宽，双轴独审/Compile与文件格式通过；project formatter因管道权限失败不伪称全检，真实场景通过才关闭 |
| Hy 交付接入 | root集成；模型作者与消费者按新证据续接 | 方向scope、设置动作与提示已集成/独审，组合检查通过；现有Windows设置验收已准备旧12原10场景/候选14追加5场景，目录门111项及Compile通过，实际GUI/模型未运行；用户已批准手动预览，作者007ce203已集成为2fc49cc；正式目录14、默认新装12，最终17文件双轴独审通过，待CI475结束后发布新包做真实15场景 | 旧12hello/能力保留，14中English高级请求明确拒绝且不清偏好、不普通降级；原active/语言保持，公开默认不自动改变 |
| 候选裁决与资源协调 | root | 对实质变化做独审、分配编译/推理槽，按结果安排后继；按已批准的手动预览范围调整 catalog，既有默认与预算保持不变 | 本地兼容 GO 不等于产品或质量放行；真实失败恢复、方向授权、原选择和退出均有原件后才裁决下一阶段，手动中英预览已获批准，完整质量放行仍需独立证据 |
| Linux | 暂不派工 | 保留 CI468 推荐包及 CI474 Qt失败证据，不新增扩展或重复归因 | 用户要求恢复，或证据表明当前共享改动造成回归时由 root 明确安排 |

同机重编译与模型推理错开，模型推理串行。槽位由 root 按实际 owned 进程退出及所有权证据交接，不按代理结束消息释放；远端 CI 不占本地编译槽。代码、输入、身份、权限及资源合同未变时不反复全量审查，只复核实质变化与受影响边界。

## Windows：从当前失败推进到普通源必要能力

具体产品约束见 [Windows 待办](../platforms/windows/open-issues.md)。普通输入框接受译文、F10 替换与连续输入是核心场景；正式 Notepad 已证明仅预览，误写或丢稿影响高，日常发生频率未知。按[实际价值判断](testing.md#实际价值判断)优先闭合首键、原子写入、IME 与身份，而不是为归因本身重复跑已过场景。

- **准备问题分开报告**：ordinary初始化已在CI474原JSON实测通过，原1460本轮未复现；原16项前台准备仍失败。`b390de9`仅补失败前后窗口/进程/线程、菜单状态及读取错误，不采标题/正文、不增加SetForeground重试或修改权限。下一轮观察“调用者无前台资格”等假设，不把唯一API返回false推为唯一根因。
- **人工稿回源并非Broker没有返回**：原CI474回源35ms内focused，之后同进程/同元素但目标lifetime变化，30秒等待的EditedTranslationPending未成立；Ctrl/accept尚未执行。旧trace丢失中间27.3秒，`cbe7060`/`3ed3861`保留首8条、全局与return后每类首次观察及末段，仍最多64条；Broker扫描最多1MiB/4096行，首次非native前后分列，截断明确unknown。只是补首因观察，不改焦点、捕获、期限、源生命周期或原断言。
- **诊断先于后续失败留存**：`2877d6e` 提前上传源研究原始诊断，避免后续失败遮蔽已完成研究；它改善可观测性，不改变产品能力。
- **TSF phase2 已有阳性，下一门按真实证据裁决**：CI473 已证实自有 stock Edit 的既有 context 与精确 view 可观察，且 wrong-field 被拒绝。CI475 原件中 Tab/End 均 selection-read-observed，各仅一次同步请求，request/session/selection HRESULT为0；默认按键行为、文本不变、零提交、所有权清理及正常退出已核。Tab默认切焦后身份失效符合观察。composition与queuedKeyIdentity仍未证明，消费始终false；独立advisor后root批准的自有fixture Tab自然OnKeyDown拒绝/消费差值实验已实现（作者39e58b0/root7906966）、两消费者Compile/格式及双轴独审通过，独立CI步骤最多2分钟，原job75和模型预算不变；真实Windows未跑。实验，每分支独立进程仅一down/up、一次SYNC只读、零写/提交；拒绝须A→B、消费须仍A，up不消费。缺回调或行为不符即止，不改窗口泵或调用键管理器补成功，不注册/注入第三方，不开放生产写回；旧键/IME等仍未准入。
- **生产接受仍关闭**：六同步实验与 TSF context 阳性都不是完整许可见证。必须保留首键、同目标有效按键消费、原子文本事务、IME 与精确源身份边界；未知即拒绝。用户已决定接受后主动切换应用时，后续 repeat/up 交给新应用，跨应用尾键抑制不再是要求。
- 已过的几何、浏览器 metadata/Ready、源安装与退出回归保留，不作为现役排程再派工；旧故障未复现不等于根因已证实。新真实失败出现时按首因推进，不靠扩大期限、重试或跳过断言制造绿色。

## 模型：手动 Hy 中英预览已本地集成，最终候选验收待运行

### 当前已完成的证据

- **独立16条已审**：固定 Hy 与 Argos 默认各16翻译＋1 hello，整轮39.099秒、零重试，两个 owned 进程组退出并释放锁。Hy 为14条未见实质问题、1较小、1退款动作未决；Argos 为7重大、4较小、5未见问题，保守口径至少5对明确重大修复。只支持进入兼容/产品验收准备，未决不算通过，台灯错义及其他方向已知缺陷不消失。原件与审查在 `artifacts/paired-daily16-run-20261023-01/`，退出/结构复核在 `artifacts/paired-daily16-execution-20261023-01/`。评审先冻匿名判断再揭盲，但见过旧定性摘要，且不是专业译者验收，不宣称完全盲评或真实用户错误率。
- **兼容基础双独审 GO**：作者提交 `86b58f1`、集成提交 `b742ddc` 已审。Runtime1224、worker165、Linux setup108、supervisor/http各1、payload12、prepare11、ELF无模型ABI11通过；基础的既有12方向已在CI474 Windows生产与生命周期实跑通过；后继scope及14 GUI仍未跑。检查原件在 `../Typelay-hy-english-candidate/artifacts/`。这些不是实际 Windows 运行或模型安装恢复的替代证据。
- **stage-a4 是真实无模型通过**：`../Typelay-hy-english-candidate/artifacts/hy-candidate14-product-preparation/stage-a4/` 的 build/native/Host 均退出0，Host 准备视图真实读到嵌入候选14方向；空数据 `modelReady=false`，没有模型 worker、没有复制权重，所有 owned 进程退出，总13.806秒。前序 SDK 构建失败原件保留，是构建前置失败，不是模型失败；stage-a4 不补作那些失败通过。

- **真实产品链首轮在安装前置失败**：B 阶段5.195秒退出2，尚未复制权重、启动 Host 或模型，零翻译，C–F 未运行；所有 owned 进程/组退出、drain 收尾，原代元数据不变且锁可重取。原件在同准备目录 `product-round-01/`。一次零模型依赖探针实际退出0，证实 `ctranslate2` 导入时 UCX 拒绝诊断污染 stdout 后才输出合法 JSON，不是 Python 重定位错误；保持 socket 权限拒绝不变，仅修探针协议输出隔离。精确原件在 `runtime-import-probe/`。修复 `6767f05` 仅分离探针的 JSON 输出，真实依赖 red/green、导入/退出原生 FD 污染及外来依赖路径拒绝回归通过，setup109/109 通过；原许可拒绝保持，不重跑无变化的失败轮。

- **真实产品链 round03 完整通过**：`product-round-03/result.json` SHA-256 `4b9a377fa08755c349ec84cbfd27ec2d64a4c3ef2a558821b8e788e3cda95623`，实际exit0、47.765秒。旧12真实激活后，故意移走新候选license使替换失败；旧worker25退役，恢复worker35使用同旧代12方向，current/active保持，并保留本次失败诊断。随后新14实际Ready，两条冻结中英smoke返回非空正确译文且文档/版本/选区绑定保持。Host正常退出0、双EOF、0外层清理信号、drain全部收尾/0取消、owned及组无残留，install及三use锁可重取，原seed完整性复核不变。3147原帧完整保存，不是质量评估、GUI或Windows验收。
- **运行环境限定与失败证据保留**：round02在Host激活阶段OOM，未翻译，旧失败不覆盖。相同硬AS下零模型对照显示Host虚拟地址接近2.5GiB而RSS约51MiB；仅给实验Host设置 `DOTNET_GCRegionRange=20000000`（512MiB预留）后，虚拟地址减少约783MiB，原 `DOTNET_GCHeapHardLimit=10000000`（256MiB提交）、AS2.5GiB及CPU0/1不变。round03使用这个明确环境，不证明未设置它的默认运行资源行为，也未定位旧OOM的具体失败分配点。模型、采样、token与原预算均未扩大。runner修正为持续排空，保留最多65536帧/16MiB完整原帧；原512帧门不再用于长轮，产品10ms事件行为不改。

### 必须保持的兼容边界

本地后继正式 catalog 可供14方向；ABI5、greedy、模型权重锁和默认保持，新装仍12方向。中英预览通过明确确认的 format2 `Install/addDirections` 事务启用；旧 Repair 不扩方向，既有选择和语言设置不变。选中库存、manifest 与原生导出共同授予能力，不因库里有入口就宣称已安装；事务失败须保旧代与 active 选择。候选14仍有原12高级能力的non-null作用域，English已有风格/术语请求必须明确拒绝而非普通翻译降级；保留Argos原有休眠偏好语义，重置/删除不受新增应用资格门阻挡。**跨旧版本恢复尚未完成，不承诺旧版本自动恢复14方向。**

已通过的产品链沿真实 Host、生产安装/激活和 editor snapshot 入口，不手造 Ready/current，不以研究worker代替。两条 smoke 不替代独立16条质量证据，也不抹除已知错义。方向级高级能力兼容、明确升级动作与禁用原因提示已实现并完成本地组合检查，不再重复扩方向/失败恢复实验或扫已消费样本；本地后继现有设置界面已提供明确补充动作，尚未发布新候选；默认保持不变。

- **候选设置验收代码已准备、未实跑**：`9b49b22`复用现有Windows Hy包生命周期。实际同包目录仅允许完整旧12或候选14；旧12保持原10场景，标明中英未提供且未运行。候选14追加真实取消/确认、同代正常退出冷启、两个普通预览，保留原语言/选择与所有权证明；目录不合法失败，不由worker自报决定跳过。公开catalog、默认和生产代码不改，原960秒预算不增加；冷启日志精确列入现有artifact白名单。Contracts111/111和两消费者Compile只证明本地检查，真实GUI、14冷启和本轮两句仍待最终候选运行。

- **手动预览启用实现与独审完成**：作者 `007ce203` 已集成为 `2fc49cc`，17文件；Runtime1287/1287、worker167/167、Linux setup109/109、Windows DesktopFixture/App.Tests Compile、格式/Ruff/diff检查实际通过。标准与需求两名独立审查均无待修问题，旧testing合同已同步；只是本地证明，不宣称最终14 GUI/冷启已过。启用确认明确高级能力、错漏及不可直接降级旧版本的限制；可以在当前版本使用Argos，未安装须另行确认下载。后继包使用现有15场景验收，不重复质量扫参。

### 资源与采用条件

- SDK 构建与模型推理预算分开，不因构建前置失败扩大模型预算。模型保持两核 CPU affinity、硬地址空间2.5GiB、单请求及加载/hello60秒、单共享600秒、清理最多7秒、失败停止、零推理重试；推理与重编译不得重叠。两核 affinity 不等于只有两个线程，点采样不冒充连续资源证明。
- Hy/Qwen context1024、output192；Argos保留生产输入384 pieces、输出512拒绝界；Mozilla保留整请求192 token保护。原身份、模板、参数、原响应、失败与退出证据必须保留；冻结模型合同不因 SDK 独立构建而变化。
- 真实链准入前核实际二进制与模型身份、空间、锁、owned进程与资源槽；round03 身份/环境/原始结果已冻结并核验，后续仅按实质新增风险安排验证。兼容成功只证明该链，不自动证明高级操作、完整 GUI、物理输入或质量合格；不改默认、不上传文本、不启用云端，不无假设扫参或扩候选。

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

- Linux 仍推荐 CI468 `091fd796` [体验包](https://github.com/DingJWuyu/Typelay/actions/runs/39904552393/artifacts/12480483952)，其完整检查、包身份、gzip/ZIP、293项清单及本包说明已有核验；仍需用户真机、物理键盘和输入法体验，不是所有应用验收。CI474 的 GTK/GTK4 本轮通过、Qt 动画采样失败如上，当前不派修复或扩展任务。
- 按需唤醒后空捕获仍保守中止：恢复同一目标不证明期间未离开焦点家族；未取得有效顺序与屏障证据不恢复唤醒，见[架构约束](../platforms/linux/architecture.md#按需唤醒与缺失捕获)。可选富文本、非合作控件、Electron/VS Code 自动浮层扩展暂不开展。
- 已发布 CI468 源码从 shared artifact `12480153131` 完整恢复，tree `5dbdda82b1d415397db8580ca1878ab6dc7a2d95` 与发布一致；本地恢复基准 `88d8738` 是独立提交，不冒称远端历史恢复。收据在 `artifacts/ci468-source-recovery/verification.json`。
- 旧模型原始输出、资源/退出和独审原件，以及 Mozilla 未发布5文件补丁/33项测试原件仍缺失；可读的新证据不冒称旧证据恢复。未注明的 `artifacts/` 相对本集成工作区；模型兼容和 CI473 的跨工作区路径已分别明确。原始日志与模型留在被忽略目录，不提交或另建历史快照。

## 持续维护规则

1. 遵守 [AGENTS](../../AGENTS.md) 与产品基线；未知焦点、IME、身份或结果继续拒绝/隔离，外部准备不偷占或放宽产品许可期限。
2. 区分实现、本地检查、精确提交 CI、用户真机；失败、未测与环境限制分别报告，编译不等于 GUI，采集不等于质量合格。
3. 有效测试与有界诊断保留，过时假设或退役要求才删除；失效接口和引用同步清理，不为整理重写全库或新增无价值回归。
4. 相同失败必须换取新证据，不盲重跑；需要新授权时写清具体动作和所缺条件，继续独立的 Windows/模型工作，不把等待当完成。
5. 派工按 `docs/agents/skills.md` 指定适用技能；执行前读原文及必需参考，验收核实际完成步骤。事后补读、两轮自审和独立双轴审查分别如实报告，不以读取文档冒称完成流程。
6. 本页只维护当前事实、负责人、下一动作和关闭条件，已关闭排程直接删除；候选、CI与包逐项核一致，历史追溯交给 Git。
