# 当前开发状态与安排

这是当前开发的唯一现役入口，不是完成报告。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。

## 先看结论

本页于 2026-10-23 19:17 UTC 核对。**CI480 的完整 Linux job、共享检查、Windows 常规行为与双端 Hy 生命周期均已通过。Linux 体验推荐已决定升级为 CI480；Windows 普通源接受仍未完成，正式 Notepad 接受失败，新 TSF RW 研究只证明获得同步锁后零写拒绝。** 模型质量、高级 scope、真实 IME、物理输入和所有应用仍不能由这些结果推出。

### 当前发布身份与证据

- [CI480](https://github.com/DingJWuyu/Typelay/actions/runs/40007045279) 为唯一 push、attempt1，18:53:43 UTC 创建，19:15:36 UTC completed/failure。冻结本地 `6e9f5463b5b993064cb0dc3f2d2be105fc7ca38f` 的17文件精确对应远端 `2342a747864b9694e3beebf1ca16c4aa44e078db`、tree `e46dc73d0d5d78c50f7813ed285e1369026f9b1b`、parent `f9ce8b5a`，非强推及读回已核。
- 原件入口 `../artifacts/ci480-current/terminal-evidence-verified.json`：4份ZIP、源码849项、Windows551文件身份与Linux293项清单均已核。Linux完整成功不是setup-only/Qt-only/quality-only；Windows总体失败不能抹去Linux结果，也不能由常规绿色掩盖研究失败。
- 发布授权仍覆盖本轮Windows/Hy后续源码、测试与含进度/结果/路径的文档，同分支非强推，无待答发布确认。UIAccess产品选择尚未明确，严格生产输入策略保持；不得借用户离开8小时的持续推进要求改变安全边界。

| CI480 范围 | 已核结果 | 不能据此宣称 |
| --- | --- | --- |
| 共享 | Core302、Runtime1289、Protocol19、Contracts111共1721全过；worker168、Python shared21、Linux输入反馈178全过 | 受控端口/worker测试不是模型质量或桌面验收 |
| Windows常规 | App784/784全过，8个A/B点击场景与原multiline路径实际通过；另16个显式研究在普通套件NotExecuted。全部共享套件与worker通过 | 普通源生产写入已支持，或物理输入已测 |
| Windows生产链/Notepad | 连续、浏览器合作、Hy生产链通过；Notepad仅预览，接受提示90.011秒失败、后3场景未跑；消费者自然退出、残留0 | 接受/撤销、后续人工稿回源或按需/直接场景已过 |
| TSF自有RW研究 | 首个success场景一次同步RW请求/session HR0，完整读取及composition明确none后changed-body拒绝；0 SetText、无副作用，正常清理/退出 | 实际写入、Undo、提交正控、后三例或原Tab默认效果已核。CallNextHookEx/Next0只证明调用旁路；研究status=stopped-not-validated |
| Windows Hy | 15场景全过，取消/补14、同代冷启、双向普通预览、Argos/移除与App0/worker正常退出 | quality已评、真实键盘/所有方向质量合格 |
| Linux | 完整构建/搬迁安装/合作组件/设置/教程/DOM70/协议73/GTK3/GTK4/Qt/GIMP/VS Code等通过，GTK4glossary40场景全过 | 旧间歇投递延迟唯一根因已解决、所有应用均支持 |
| Linux Hy | 79.677秒完整12阶段，取消/补14、同代冷启、双向translateEditorSnapshot文档/版本/选区绑定、语言恢复、Argos/移除；Host/worker正常退出，无强清理 | advancedScope/source dispatch已验（仍明确not-run）、quality已评、物理桌面或12方向质量已验 |

## 当前编排

一批结束不是整个项目暂停点。按现役缺口推进、及时处理真实CI首错；没有有价值的新独立动作时跟踪待定结果，不造工作量或无假设扫参。职责不是实时活跃人数。Agent目标配置保持`gpt-6-astra`+`xhigh`，工具未独立确认实际配置时不得冒称已核。

| 责任线 | 负责人 | 当前动作与完成条件 |
| --- | --- | --- |
| TSF拒绝差值证据 | root集成；finish_tsf_write_validation | 作者0c2146a已集成67725ff，四文件双轴GO、folder格式/YAML/diff通过。只从既有合成字符串记录长度/选区/首差码元/固定类别及hook完成后的只读原生效果；不归一化、不改比较/100ms/一次请求，Undo/Submit仍在verified终态后。实际差值待下一CI，根因未知，未编译/Windows实跑 |
| 下一批发布/验证 | publish_next_verified_fix_batch，root冻结 | CI480原件已齐。发布已审差值证据与当前文档后跟精确CI至终态，首例结果决定下一修复，不拿continue-on-error颜色当研究通过 |
| Windows输入信任与部署 | root | UIAccess范围待用户明确裁决；保持严格注入拒绝。同步RW局部证据不授予旧键身份/IME/第三方部署/原子产品写入资格 |
| Linux体验交付 | root；assess_linux_candidate_adoption | 采用CI480已独立核验的包，在线推荐更新；不重打已验包、不发布stable。保留用户真机反馈与模型/高级scope限制 |
| 模型后继 | root | 保持双端已过生命周期回归及原默认。高级scope/source dispatch与质量证据独立处理；没有新假设不重复扫参或扩预算 |

原Runtime前置、Windows回源/人工稿/多行点击、DOM采样、GTK4菜单准备均已有CI480通过证据，不再挂成在修队列。GTK/Qt投递诊断保持有界：配置F1曾迟178ms而正确拒绝100ms过期票，Qt曾初始StaleObservation拒绝；本轮通过不证明旧原因消失。下次真实失败再用原始跨度/basis定位，不加热身、续期或输入重试。

本地MSBuild/测试曾被socket权限拒绝，正式审核路径启动失败；未绕过或重试这些动作。独立`dotnet format whitespace --folder`不加载项目，已实际运行并通过，不代表编译恢复。同机重编译/模型推理错开，模型推理串行，按实际owned进程退出交接资源槽。

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
- **实现与本地检查已完成**：兼容基础、方向级高级能力、明确升级动作、禁用原因与手动确认已集成并双轴独审。手动预览批 Runtime1287/1287、worker167/167、Linux setup109/109 与两 Windows 消费者 Compile 通过；Linux 最终包准备的 99 项无模型邻近检查通过。原件在 `../Typelay-hy-english-candidate/artifacts/` 及相应作者工作区。本地检查不是 GUI 证据；当前双端生命周期与双向预览的实际结果以上方 CI480 原件为准。
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

- Linux 推荐 CI480 `2342a747` [体验包](https://github.com/DingJWuyu/Typelay/actions/runs/40007045279/artifacts/12525507850)，内tar 281,117,978字节、SHA-256 `9a5415c95b4a0e285baee3cab6c1a4dfbda313f207a61b808097111f99acf4a2`；完整Linux检查、clean提交身份与293项清单独立核验。仍需用户真机/物理键盘/输入法体验，不是stable或所有应用验收；包内“14 GUI未验证”旧保守句不作为当前事实，在线说明已按CI480实际范围更新。可选扩展不派工。
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
