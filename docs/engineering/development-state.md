# 当前开发状态与安排

这是当前开发的唯一现役入口，不是完成报告。**Windows 与模型线持续推进；Linux 可选扩展暂无派工，保留 CI468 推荐包。** 负责人、证据或下一动作改变时更新对应行；已关闭事项删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。

## 先看结论

本页于 2026-10-23 18:50 UTC 核对。**CI479 于 18:40:13 UTC 终态 failure，但 Windows Hy 15 场景与 Linux Hy 生命周期/双向预览均已通过。当前失败是新增 Runtime 回归的前置条件、Windows 多行点击准备及五类 Linux 桌面验证；已准备的修复与 TSF RW 四场景进入下一批，不等待所有 Linux 修复。** 源 hook 同步 READ 与原 Tab 转发通过，明确无 composition 不是真实 IME 正例；事件资格、第三方部署与生产接受仍未闭合。正式 catalog 可供 14 方向，新装仍 12，默认不变；双端链路通过不是质量、高级 scope 或物理输入验收完成。

### 当前身份与最近 CI

- [CI479](https://github.com/DingJWuyu/Typelay/actions/runs/40002549822) 是本批唯一 push、attempt1，18:20:57 UTC 创建、18:40:13 UTC completed/failure。冻结本地 `afce5af161b0235b00a35c4e00aaa33f7e690bd6` 的 14 文件对应远端 `f9ce8b5a152437f97ff7c1ab129bc4aacc85e51e`、tree `80aac4bb7937940066a47bad01fd1aa1b2a1fa72`、parent `77559f91`，已精确读回。6 份 artifact、冻结源码 archive、Windows 551 文件与 Linux 293 项包清单均已核；原件和终态收据在 `../artifacts/ci479-current/`，入口为 `terminal-evidence-verified.json`、`suite-scope-verified.json`。
- **当前没有待答的发布授权。** 用户 17:50 授权继续源码、测试、状态文档与结果发布，并要求离开 8 小时期间持续推进。发布仍限已核范围、同分支非强推；UIAccess 产品选择尚未答复，严格生产策略不变，不阻塞已有修复及自有控件验证。

| CI479 范围 | 已核结果 | 尚未证明或后继动作 |
| --- | --- | --- |
| 共享 job | Core 302/302；Runtime 1288/1289，唯一失败为新增长度反馈回归的错误前置条件 | **仅此 job** 的后续 Protocol、Contracts、worker 因 fail-fast 未运行；测试修正待下一 CI |
| Windows job | Core 302/302、Protocol 19/19、Contracts 111/111、Python worker 168/168；Runtime 同一项失败；App 已执行 779/780 通过，另 15 项显式 NotExecuted | App 唯一失败是 multiline=true 的 `SourceClickControlDocument` 前置校验。原 6 项 NativeReturn 与 6 项 ConfiguredControl 均过；root-hover 本轮过，不据此声称旧失败根因已证 |
| 源 hook READ / IME | 同栈一次 TSF READ、原 Tab 转发通过；`none-in-exact-context-at-read`，Query/Find=0、Next=S_FALSE/0；30 项库存仅 en-US 键盘 Active/Enabled，未切换，正常清理 | `realImePositive=positive-not-run`、旧键同一性未证；SendInput 研究不证明物理键、第三方部署或生产写入。原件 `source-diagnostics/tsf-source-key-read/` |
| 连续/浏览器/Hy 生产链、正式 Notepad | 连续翻译、浏览器合作与 Hy 生产链通过；Notepad 自动预览通过，接受提示等待 90.010 秒失败，0 接受提示/1 preview-only | Notepad 接受/撤销未通过，后续回源、按需/直接及普通退出场景未运行，生产安全门仍关闭 |
| Windows Hy | 15 场景全部通过，含取消/补 14、同代冷启、双向普通预览、回 Argos、移除及 App/worker 正常退出；`englishAcceptance=passed` | `quality=not-reviewed`；原件 `windows/hy-packages/result.json` |
| Linux Hy | 91.899 秒，全部生命周期阶段通过：取消/补 14、同代冷启、双向 `translateEditorSnapshot` 文档/版本/选区绑定、语言恢复、回 Argos、移除；Host/worker 正常退出，无强制清理 | `advancedScopeAcceptance=not-run-raw-scope-and-source-dispatch-unobserved`，双向 `quality=not-reviewed`，无物理桌面/12 方向质量声明；原件 `linux/linux/hy-package/hy-package-result.json` |
| Linux 桌面 | DOM resize 宽度 480≠520；GTK reentry 未到 mutation callback；GTK4 语言弹层重开 `KeyError: typelay-source-language`；Qt F10 未证实 CPU 直接翻译；独立 GTK4 glossary 把 frame0 未绘制误判为永久几何拒绝 | 五类分别处理，不混为 Hy 失败，不以重试或放宽安全门消除；具体负责人和已准备修复如下 |

## 怎么安排，谁负责

**Windows 与模型主线不得因一批结束、发布等待、CI 等待或编译槽等待而停摆。** root 按现役缺口和实际依赖交接下一动作；职责不是实时活跃人数，汇报人数只计工具确认的 `running`。所有任务目标模型/思考强度为用户指定的 **`gpt-6-astra` + `xhigh`**；当前工具没有独立确认实际运行配置，不能把请求值当成已核实值。

当前已集成下列 READY 变更，冻结身份由本次发布清单核对；下列已准备变更均**不在 CI479**。双轴 GO 不等于编译或 GUI 通过，具体未跑范围保留到下一 CI 验证。

| 责任线 | 当前负责人 | 下一动作与关闭条件 |
| --- | --- | --- |
| 下一批发布与精确 CI | `publish_next_verified_fix_batch`；root 核范围 | CI479 终态原件已齐，冻结下一批 READY 变更并核远端后发布，跟精确提交 CI 至终态；本批已就绪修复和定点诊断一起发，后续按新原件继续，不把研究等待当停工 |
| Windows 源输入设计 | root；`assess_windows_acceptance_contract` 独立核需求/部署边界 | READ 只关闭机械读取门，继续核事件资格、RW、IME 与部署；UIAccess 待用户裁决期间保留严格策略 |
| Windows TSF RW 四场景 | `finish_tsf_write_validation` | `9a2626a` 五文件已集成、双轴 GO，替换旧两分钟 READ 步骤：仅自有 stock Edit 验完整快照、同 session 复核/一次写、写前拒绝原 Tab 旁路及 Undo。即时撤权、range 所有权、本事件消费绑定、已验证终态后 Undo、零提交递增正控五项审查问题已修；合并仅保留独立 ASFW 分支，其余四文件逐字一致。未编译/Windows执行；root已运行不加载项目的folder whitespace格式检查，修正两文件纯空白后复验通过（非编译替代）；首次副作用后失败为受理失败/未知，不能报零写拒绝 |
| Runtime 长度反馈回归 | root；`fix_length_feedback_runtime_regression` | `96777bf` 已集成、双轴 GO，仅修测试：先建立“甲”再追加，避免旧默认 hello 整段改写按既有合同清空预览；保严格断言并加可接受正控/错误前只读门，无生产改动。修后 Runtime 未跑。旧本地 MSBuild/socket 与 bwrap 只读路径拒绝不再重试，交下一 CI；worker 已过不代替 Runtime，既有错误归一不扩大 Direct 模式反馈 |
| Windows 多行源点击 | `fix_multiline_source_click_preparation` | `1ec221e` 两文件已集成、双轴 GO；仅精确 owned Edit + `ES_MULTILINE` 允许合法 Document 映射，其余门保持，原点击 4 场景扩 A/B 8 项。未编译/Windows 执行，待下一 CI |
| GTK4 glossary 绘制准备 | root；`diagnose_ci479_linux_desktop_failures` 已交付 | `7eeaf27` 两文件已集成、双轴 GO；frame0 是未 ready，不是永久 viewport 错误，在原 deadline 内等待同一实例完成 frame 后用原几何断言。root 几何 43/43 通过，GUI 未跑 |
| Linux DOM resize | `diagnose_ci479_linux_desktop_failures` | 8e1585a集成fc14efc，双轴GO，复用既有trusted同按钮click启动360ms采样，保520宽/中间帧/截图等全部断言；静态/Ruff过，Chromium启动socket拒绝后未重试，真实DOM未跑，旧记录不足证明唯一根因 |
| Linux GTK reentry | `fix_gtk_reentry_verification` | 发送时原100ms许可约余99ms，但到GTK实际迟178ms、已过期79ms，安全拒绝正确，非1秒回调等待太短。2a6084c集成8e7b82c，双轴GO，仅既有32跨度按实际配置接受键冻结，TRACE关闭不新增解析；无F8热身/续期，native未编译，延迟根因待新跨度 |
| Linux GTK4 语言弹层重开 | `fix_gtk4_language_popup_reopen` | 573d6f7集成eb7bea8，双轴GO；只修GTK4菜单mapped但尚无surface/frame时错误旧坐标点击，保同控件/菜单instance、800ms、无renew/单click及disabled Apply断言。root组合55/55通过，C/真实GTK未跑 |
| Linux Qt 直接翻译 | `fix_qt_direct_translation_verification` | F10在queueShortcut初始StaleObservation拒绝，未进入capture/model，不能归为模型失败或仅过期。4223f3集成db9db15，双轴GO，记录原判断已用observed/current区分missing/future/expired，不新采样/不改150ms；与47bf981同享128行上限，默认关闭、无正文，未编译/GUI，仍是诊断不是功能修复 |
| Hy 后继验收 | root | 双端生命周期与中英普通预览已闭合，保留回归；高级 scope/源分发与质量仍按现有证据分开判断，不无新假设重跑质量或把普通响应算高级验收 |
| Linux 可选扩展 | 暂不派工 | 保留 CI468 推荐包；当前修复限已有失败，不扩新应用/控件支持，不把清理 -15 写成正常退出 |

同机重编译与模型推理错开，模型推理串行。槽位由 root 按实际 owned 进程退出及所有权证据交接，不按代理结束消息释放；远端 CI 不占本地编译槽。代码、输入、身份、权限及资源合同未变时，只复核实质变化与受影响边界。

## Windows：仍需闭合的普通源能力

具体产品约束见 [Windows 待办](../platforms/windows/open-issues.md)。普通输入框接受译文、F10 替换与连续输入是核心场景；正式 Notepad 仍仅预览，误写或丢稿影响高，日常发生频率未知。按[实际价值判断](testing.md#实际价值判断)优先闭合首键、原子写入、IME 与身份，不为归因本身重复跑已过场景。

- **诊断与观察边界**：有界焦点/回源诊断不采标题/正文、不增加 SetForeground 重试、不修改焦点期限、源生命周期或权限。原人工稿回源曾在 35ms 内 focused，后续 target lifetime 变化，不能解释为 Broker 没有返回；trace 最多 64 条，Broker 扫描最多 1MiB/4096 行，截断明确 unknown。源诊断提前上传，避免后续失败遮蔽已完成观察。
- **只读证明与已否决机制**：自有 stock Edit 的既有 context、精确 view 与 wrong-field 拒绝已有实证；当前同线程 `WH_KEYBOARD(HC_ACTION)` 栈一次同步 READ 后原样 `CallNextHookEx` 保住 Tab，零写/消费/提交，不订 context key sink。相反，CI476 已证明 `OnTest=true` 占位后 `OnKeyDown=false` 不恢复原 Tab，该机制与专用自动 CI 已退役，不再排程 Consume 或重注入补成功。第三方 DLL 加载/TIP 注册未授权也未实施。
- **事件资格与信任范围待裁决**：冻结要求是不换输入法、不要求目标应用主动适配、安全明确接受；“排除全部软件注入”仍受已接受 ADR 约束。公共 `WH_GETMESSAGE` 可持有并修改同一真实 MSG，是尚未被反证的候选；来源信息是否绑定该 MSG、RW 与 IME 仍待证明。不能把缺跨 hook cookie 推为所有公共 API 不可行，也不能把候选当成闭合方案；待答 UIAccess 决定前保持严格生产策略，不改 OS 权限/注册/部署。
- **生产接受仍关闭**：同步只读与 TSF context 阳性不是完整许可见证。必须保留首键、同目标有效按键消费、原子文本事务、IME 与精确源身份边界；未知即拒绝。用户已决定接受后主动切换应用时，后续 repeat/up 交给新应用，跨应用尾键抑制不再是要求。
- 已过的几何、浏览器 metadata/Ready、源安装与退出回归继续保留，不作为现役排程再派工；旧故障未复现不等于根因已证实。真实 IME composition 正例仍缺，输入配置库存与明确 none 不能代替它。

## 模型：双端手动 Hy 生命周期通过，质量与高级 scope 保持独立

### 仍有效的交付证据与兼容边界

- **真实产品链 round03 完整通过**：原件根 `../Typelay-hy-english-candidate/artifacts/hy-candidate14-product-preparation/`，`product-round-03/result.json` SHA-256 `4b9a377fa08755c349ec84cbfd27ec2d64a4c3ef2a558821b8e788e3cda95623`，实际 exit0、47.765 秒。旧 12 激活后故意缺新候选 license，替换失败保旧 current/active 与失败诊断；旧 worker25 退役、恢复 worker35 仍用旧代 12。随后新 14 实际 Ready，两条冻结中英 smoke 非空正确且文档/版本/选区绑定保持。Host 正常退出0、双 EOF、0 外层清理信号、drain 全收尾/0 取消、owned 及组无残留，install 与三 use 锁可重取，原 seed 完整性不变；3147 原帧完整保存。这是生产安装/激活和 editor snapshot 链，不是质量评估或完整 GUI 验收。
- **证据的环境限制**：round02 的 Host 激活 OOM 未被覆盖或补算通过。round03 仅为实验 Host 增设 `DOTNET_GCRegionRange=20000000`（512MiB 预留）；原 `DOTNET_GCHeapHardLimit=10000000`（256MiB 提交）、AS2.5GiB 与 CPU0/1 不变，不证明默认资源行为，也未定位旧 OOM 失败分配点。runner 持续排空，最多 65536 帧/16MiB，产品 10ms 事件行为不变。依赖探针已隔离 UCX stdout 污染与合法 JSON；原 socket 拒绝不变，真实依赖/原生 FD 污染/外来依赖路径拒绝回归及 setup109 已通过，原件 `runtime-import-probe/`。
- **实现与本地检查已完成**：兼容基础、方向级高级能力、明确升级动作、禁用原因与手动确认已集成并双轴独审。手动预览批 Runtime1287/1287、worker167/167、Linux setup109/109 与两 Windows 消费者 Compile 通过；Linux 最终包准备的 99 项无模型邻近检查通过。原件在 `../Typelay-hy-english-candidate/artifacts/` 及相应作者工作区。本地检查不是 GUI 证据；当前双端生命周期与双向预览的实际结果以上方 CI479 原件为准。
- **能力与默认**：正式 catalog 可供 14 方向；ABI5、greedy、模型权重锁与默认保持，新装仍 12。中英预览经明确确认的 format2 `Install/addDirections` 事务启用，旧 Repair 不扩方向，既有选择和语言设置不变。选中库存、manifest 与原生导出共同授予能力；事务失败保旧代与 active 选择。候选 14 保留原 12 高级能力的 non-null 作用域，English 风格/术语请求明确拒绝而非降级普通翻译。保留 Argos 休眠偏好，重置/删除不受新增应用资格门阻挡。**跨旧版本恢复未完成，不承诺旧版本自动恢复 14 方向。** 当前版本可改用 Argos，未安装须另行确认下载。
- **验收边界与预算**：双端已通过的生命周期回归保持 Windows 15 场景 960 秒、Linux 240 秒工作/260 秒外限，quality 请求数不变。普通预览不证明高级 scope、原生源请求、质量或物理输入；本地未发现 niri/Xvfb/weston 或 DISPLAY/WAYLAND 桌面，以实际 CI 原件建立 GUI 结果。

### 仍约束采用的质量事实

以下是合成开发样本与 agent 复核，不是产品正确率；已见挑战与日常样本分开，旧缺失原件不能拼入本轮结果。

- **独立 16 条比较**：固定 Hy 与 Argos 默认各 16 翻译＋1 hello，39.099 秒、零重试，两个 owned 进程组退出并释放锁。Hy 为 14 条未见实质问题、1 较小、1 退款动作未决；Argos 为 7 重大、4 较小、5 未见问题，保守至少 5 对明确重大修复。未决不算通过，台灯错义和其他方向缺陷不消失。原件/审查在 `artifacts/paired-daily16-run-20261023-01/`，退出/结构复核在 `artifacts/paired-daily16-execution-20261023-01/`。先冻匿名判断再揭盲，但评审见过旧定性摘要且非专业译者，不宣称完全盲评或真实用户错误率；两条产品 smoke 不能替代此质量证据。

| 候选或比较 | 保留结论与当前用途 | 可读原件 |
| --- | --- | --- |
| Hy rep1.0 对默认1.05 | 风险8条仅1项明确角色修复；日常24条两臂各2重大，德语7点半及中韩 AND→OR 仍错，无替换默认依据 | `artifacts/hy-repetition-run-20261023-073958/`、`artifacts/hy-daily-run-20261023-01/` |
| Hy 中英首筛18条 | 日常12条为1重大/1较小/1未决/9未见问题，挑战6条为0/1/1/4；台灯摇晃错成灯光闪烁、严格20分钟上界未决，独立16条不抹除这些边界 | `artifacts/hy-english-run-20261023-01/`、`artifacts/hy-english-preparation/semantic-review-20261023.md` |
| Argos beam1 对 beam2 | 固定rep1.05，两臂日常各3重大、挑战各2重大，无重大修复，不采用beam1；成功轮退出和锁复取通过，不补作旧安装轮清理证明 | `artifacts/argos-beam-preparation/` |
| Mozilla 官方资产与有界CLI | 2次门＋18翻译全部exit0、退出清理确认，5.837秒；日常3/2/1/6、挑战0/2/1/3，保义不足，不采用。小而快不是公平性能胜出或语义合格，普通请求不证明极限拒绝分支 | `artifacts/translation-products-research/mozilla-official-assets-current/` |

其余旧 Hy instruction/Q6、Qwen3-1.7B、Liquid 对照无采用依据，旧原件缺失，不据摘要重判或重跑同配方。Liquid 条款待核；Qwen3.5-0.8B 无已确认发布方官方 GGUF，源转换缺预算内路径；MADLAD 初始化超预算，M2M100 转换路径未证明，TranslateGemma4B 资源/条款暂缓。未实测不写成质量失败，无新假设不扩研究范围。

### 资源与验证条件

- SDK 构建与模型推理预算分开。模型保持两核 CPU affinity、硬地址空间2.5GiB、单请求及加载/hello60秒、单共享600秒、清理最多7秒、失败停止、零推理重试；推理与重编译不重叠。两核 affinity 不等于仅两个线程，点采样不冒充连续资源证明。
- Hy/Qwen context1024、output192；Argos 保留生产输入384 pieces、输出512拒绝界；Mozilla 保留整请求192 token保护。原身份、模板、参数、原响应、失败与退出证据保留；冻结模型合同不因 SDK 构建变化。
- 真实链执行前核二进制/模型身份、空间、锁、owned 进程与资源槽；仅按实质新增风险安排验证。兼容成功不自动证明高级操作、完整 GUI、物理输入或质量合格；默认、本地执行与隐私边界保持，不无假设扫参或扩候选。

## Linux 与恢复证据边界

- Linux 仍推荐 CI468 `091fd796` [体验包](https://github.com/DingJWuyu/Typelay/actions/runs/39904552393/artifacts/12480483952)，完整检查、包身份、gzip/ZIP、293项清单及本包说明已核；仍需用户真机、物理键盘和输入法体验，不是所有应用验收。CI479 的既有桌面失败按当前责任表修复，不能用某轮通过反推旧失败根因；可选应用/控件扩展仍不派工。
- 按需唤醒后空捕获仍保守中止：恢复同一目标不证明期间未离开焦点家族；取得有效顺序与屏障证据前不恢复唤醒，见[架构约束](../platforms/linux/architecture.md#按需唤醒与缺失捕获)。可选富文本、非合作控件、Electron/VS Code 自动浮层扩展暂不开展。
- CI468 源码从 shared artifact `12480153131` 完整恢复，tree `5dbdda82b1d415397db8580ca1878ab6dc7a2d95` 与发布一致；本地恢复基准 `88d8738` 是独立提交，不冒称远端历史恢复，收据 `artifacts/ci468-source-recovery/verification.json`。
- 旧模型原始输出、资源/退出和独审原件，以及 Mozilla 未发布5文件补丁/33项测试原件仍缺失；新证据不冒称旧证据恢复。未注明的 `artifacts/` 相对本集成工作区；原始日志与模型留在被忽略目录，不提交或另建历史快照。

## 持续维护规则

1. 遵守 [AGENTS](../../AGENTS.md) 与产品基线；未知焦点、IME、身份或结果继续拒绝/隔离，外部准备不偷占或放宽产品许可期限。
2. 区分实现、本地检查、精确提交 CI、用户真机；失败、未测与环境限制分别报告，编译不等于 GUI，采集不等于质量合格。
3. 有效测试与有界诊断保留，过时假设或退役要求删除；同步清理失效接口和引用，不为整理重写全库或新增无价值回归。
4. 相同失败必须换取新证据；需要新授权时写清具体动作和所缺条件，继续独立的 Windows/模型工作，不把等待当完成，也不换路径绕过拒绝。
5. 派工按 `docs/agents/skills.md` 使用适用技能；核实际完成步骤，事后补读、两轮自审与独立双轴审查分别如实报告。
6. 本页只维护当前事实、负责人、下一动作和关闭条件，已关闭排程删除；候选、CI 与包逐项核一致，历史追溯交给 Git。
