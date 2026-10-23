# 当前开发状态与安排

这是当前开发的唯一现役入口，不是完成报告。**Windows 与模型线持续推进；Linux 可选扩展暂无派工，保留 CI468 推荐包。** 负责人、证据或下一动作改变时更新对应行；已关闭事项删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。

## 先看结论

本页于 2026-10-23 18:18 UTC 核对。**冻结的 28 文件已发布为 CI478，18:07:03 UTC 终态 failure。Windows 源 hook 同步只读与原 Tab 转发已通过；本轮读到明确无 composition，但不是真实 IME 正例，旧键资格、第三方部署与生产写入仍未证明。Hy 中英真实产品链已通过，手动预览已经发布；Windows Hy 完整15场景已通过；Linux冷启14通过，英文语言选择断言失败，双向预览仍未运行。** 正式目录可供 14 方向，新装仍 12，默认不变；编译、研究观察与 smoke 不代替完整产品验收。

### 当前身份与最近 CI

- [CI478](https://github.com/DingJWuyu/Typelay/actions/runs/39997840353) 是本批唯一 push 运行，2026-10-23 17:46:34 UTC 开始、18:07:03 UTC completed/failure，attempt1。发布提交 `77559f91a68a0ef793d6938c12e116ee9dc5b74b`、tree `a44d67bf9cbff6b2b60fcdc3204a6f390882caf8`、parent `bad749060987ccd3f53a51e0a89eb1178c9b6cd4`；源码 archive 847 项已精确核对冻结树。唯一 publisher `monitor_ci478_to_completion` 已核终态与原件。
- **当前没有待答的发布授权。** 此前针对具体状态/路径的授权允许一次已确认未执行的重试，现已成功。用户 17:50 再次给予 GitHub 授权，并要求离开 8 小时期间持续推进；这不是 UIAccess 产品范围的明确选择。本轮源/IME 观察、前台准备、设置 UIA、模型说明与 Linux 取消断言修复均在 CI478；各责任批双轴审查与适用本地检查完成，集成后 NativeFixture/DesktopFixture/App.Tests 三消费者 Compile 均 exit0，实际 GUI 结果以本轮原件为准。
- [CI477](https://github.com/DingJWuyu/Typelay/actions/runs/39990193024) 于 17:20:31 UTC 终态 failure，唯一 push、attempt1；提交 `bad749060987ccd3f53a51e0a89eb1178c9b6cd4`、tree `d597e1ab8425e296faf2032c71725029cd581580`，对应本地冻结 `ede25c4` 的 19 文件。5 份 ZIP、845 条源码、TRX/JSON 与完整日志已核，原件在 `../artifacts/ci477-current/approved-publication-20261023/`。下表保留其仍待关闭的边界，不把后继修复记作已通过。

| 范围 | 已确认事实 | 本轮结果与后继验证 |
| --- | --- | --- |
| 共享 | CI478 1720/1720 通过 | 不替代平台 GUI 或模型验收 |
| Windows App | CI477 761/777；LanguagePicker 两项、CandidateEnglish 规范名及包状态 UIA 回归通过；16 项 NativeSourceActivation 被同一前台 PID7156 阻在初始准备 | CI478 为 771/780：原 16 Activation、14 Setup（含新回执）、新模型说明/导航 WPF 均过。另 9 项失败：6 NativeReturn barrier、1 multiline 点击准备、1 root-hover 消费、1 ConfiguredControl 人工稿回源。后继按原件修复，不扩大期限 |
| 源 hook READ / IME 观察 | CI478 同栈一次 TSF READ、原 Tab 转发阳性；`none-in-exact-context-at-read`，Query/Find=0、Next=S_FALSE/0；完整库存 30 项，仅 en-US 键盘 Active/Enabled，其余未启用，未切换；清理正常 | 无真实 composition 正例，仍是 `positive-not-run`；SendInput 研究不证明旧键同一性、第三方部署或生产写。原件在 `../artifacts/ci478-current/source-diagnostics/` |
| 连续/浏览器/Hy 生产链 | CI477 通过 | 不补齐普通 Notepad 写入 |
| 正式 Notepad | CI478自动预览通过；接受提示90.003秒deadline，0接受提示/1 preview-only，后续场景未跑 | 接受/撤销未通过，生产安全门仍关闭 |
| Windows Hy 设置 15 场景 | CI477 前 10 通过，含安装、取消、补 14 与同代冷启；原 App=0、旧 workers 退出、新 worker=1、实载 14。preview-zh-en 在同语言页误等 peer 重建，622.983 秒到 deadline，未到语言选择/源启动/模型请求，后 4 场景未跑 | CI478完整15全部通过，含双向真实预览、回Argos、移除及正常App/worker退出；englishAcceptance=passed，quality=not-reviewed |
| Linux Hy | CI477 原 73 协议全过；Hy14 取消后实际 idle、modelReady/12 方向保持，旧测试误断言 ready；Host/worker 正常退出，无强制清理 | CI478取消、补14、同代冷启与Ready14均过；首次英文语言选择精确名称断言失败，53.567秒，双向预览未到。Host/worker正常退出，无强清理；首因修复由Linux Hy owner负责 |

## 怎么安排，谁负责

**Windows 与模型主线不得因一批结束、发布等待、CI 等待或编译槽等待而停摆。** root 按现役缺口和实际依赖交接下一动作；职责不是实时活跃人数，汇报人数只计工具确认的 `running`。所有任务目标模型/思考强度为用户指定的 **`gpt-6-astra` + `xhigh`**；当前工具没有独立确认实际运行配置，不能把请求值当成已核实值。

| 责任线 | 当前负责人 | 下一动作与关闭条件 |
| --- | --- | --- |
| CI478 发布后验证 | `monitor_ci478_to_completion`；root 按原件分派 | CI478终态及5份artifact、Windows551文件/Linux293项清单已核。后继发布由publish_next_verified_fix_batch负责，限冻结14文件，同分支非强推，发布后跟精确新CI到终态 |
| Windows 源输入设计 | root；`assess_windows_acceptance_contract` 独立核需求/部署边界 | READ 阳性只关闭机械读取门；继续核事件资格、RW、IME 与部署。17:34 向用户提出的系统认可辅助输入（含 UIAccess）问题尚未答复；现严格策略保持，决定不阻塞已有修复验证 |
| Windows 回源与前台回归 | `finish_foreground_handoff_fix` | 550563e集成027cf4b，7文件双轴GO：6个NativeReturn测试补真实前台fixture向Broker定向交接，原800ms/断言保持；multiline/root-hover仅补已有原因/拒绝trace，不宣称修复。C#与Windows本地未执行，后继CI验证 |
| Windows 人工稿回源 | `fix_windows_draft_return_identity` | 作者45c8481集成35917fa，普通 GetFocus 用同次前台样本分类，消除 editor→source 两次采样拼成 Other 的可达竞态；真 Other/精确字段末尾复核保持。双轴 GO，既有6场景补保原目标身份断言；与CI478轨迹一致但非唯一因果证明，修后Windows未运行 |
| Windows TSF 写机制验证 | `finish_tsf_write_validation`，advisor 已有条件 GO | 已核六同步实验仅 native 写，不是 TSF RW。准备仅自有 stock Edit 的完整快照、同 session 复核/一次写、写前拒绝原 Tab 旁路与 Undo 四场景；首次副作用后失败记受理失败/未知，不能冒充零写拒绝。生产门、UIAccess 决定、第三方部署与 IME 配置不变；代码/Windows实际结果尚未完成 |
| Hy 最终交付验收 | root；模型作者按 CI478 新证据续接 | Windows完整15已通过，保留回归；继续修Linux真实14后继预览路径。保留库存/Ready/方向/进程/退出门，没有新质量假设不重复扫参 |
| Hy 输入过长错误反馈 | root 集成；作者 `/root/fix_hy_length_error_feedback` | 作者工作区 `../Typelay-hy-input-length-feedback`、base `70456d7`，提交 `73fd001` 已无冲突集成为 `06b60ce`，仅 3 文件，不在 CI478。两个 Hy 字节上限错误码归一到既有 segment-too-long；worker 168 项通过，root整合树复跑168/168 exit0，标准/规格独审均 GO。新增 Runtime 回归先被 MSBuild 命名管道 socket 拒绝，正式 `require_escalated` 重跑又在 shell 前因 bwrap 只读路径启动失败 exit1，Runtime/Protocol 均未执行。该本地动作停止，不再重试或触碰受限路径；待 CI478 终态后决定后继批，用已授权 CI 共享检查验证，不并发发布，不宣称 Direct 模式扩大反馈 |
| Linux Hy 后继失败 | `fix_ci478_linux_hy_failure` | Hy旧标签修正e925b3d集成f5c793f、双轴GO、11项准备/Ruff过；正式标签英语，原日志未打印实值，不能排除叠加UI问题，真实GUI待下轮。Qt控制键进入后安全拒绝，相关代码本轮未改、CI477曾过；正补现有trace无正文拒绝分支，不重发/延时/放宽门，不拖当前发布 |
| Linux 可选扩展 | 暂不派工 | 保留 CI468 推荐包；Hy 必要跨平台验收不扩大为新应用/控件支持 |

同机重编译与模型推理错开，模型推理串行。槽位由 root 按实际 owned 进程退出及所有权证据交接，不按代理结束消息释放；远端 CI 不占本地编译槽。代码、输入、身份、权限及资源合同未变时，只复核实质变化与受影响边界。

## Windows：仍需闭合的普通源能力

具体产品约束见 [Windows 待办](../platforms/windows/open-issues.md)。普通输入框接受译文、F10 替换与连续输入是核心场景；正式 Notepad 仍仅预览，误写或丢稿影响高，日常发生频率未知。按[实际价值判断](testing.md#实际价值判断)优先闭合首键、原子写入、IME 与身份，不为归因本身重复跑已过场景。

- **当前失败与观察边界**：CI477 的 16 项前台准备失败已在 CI478 全部通过；当前另 9 项桌面失败按上表分工处理。已保留的有界焦点/回源诊断不采标题/正文、不增加 SetForeground 重试、不修改焦点期限、源生命周期或权限。原人工稿回源曾在 35ms 内 focused，后续 target lifetime 变化，不能解释为 Broker 没有返回；trace 最多 64 条，Broker 扫描最多 1MiB/4096 行，截断明确 unknown。源诊断提前上传，避免后续失败遮蔽已完成观察。
- **只读证明与已否决机制**：自有 stock Edit 的既有 context、精确 view 与 wrong-field 拒绝已有实证；当前同线程 `WH_KEYBOARD(HC_ACTION)` 栈一次同步 READ 后原样 `CallNextHookEx` 保住 Tab，零写/消费/提交，不订 context key sink。相反，CI476 已证明 `OnTest=true` 占位后 `OnKeyDown=false` 不恢复原 Tab，该机制与专用自动 CI 已退役，不再排程 Consume 或重注入补成功。第三方 DLL 加载/TIP 注册未授权也未实施。
- **事件资格与信任范围待裁决**：冻结要求是不换输入法、不要求目标应用主动适配、安全明确接受；“排除全部软件注入”仍受已接受 ADR 约束。公共 `WH_GETMESSAGE` 可持有并修改同一真实 MSG，是尚未被反证的候选；来源信息是否绑定该 MSG、RW 与 IME 仍待证明。不能把缺跨 hook cookie 推为所有公共 API 不可行，也不能把候选当成闭合方案；待答 UIAccess 决定前保持严格生产策略，不改 OS 权限/注册/部署。
- **生产接受仍关闭**：同步只读与 TSF context 阳性不是完整许可见证。必须保留首键、同目标有效按键消费、原子文本事务、IME 与精确源身份边界；未知即拒绝。用户已决定接受后主动切换应用时，后续 repeat/up 交给新应用，跨应用尾键抑制不再是要求。
- 已过的几何、浏览器 metadata/Ready、源安装与退出回归继续保留，不作为现役排程再派工；旧故障未复现不等于根因已证实。真实 IME composition 正例仍缺，输入配置库存与明确 none 不能代替它。

## 模型：已发布手动 Hy 中英预览，最终 GUI 验收未闭合

### 仍有效的交付证据与兼容边界

- **真实产品链 round03 完整通过**：原件根 `../Typelay-hy-english-candidate/artifacts/hy-candidate14-product-preparation/`，`product-round-03/result.json` SHA-256 `4b9a377fa08755c349ec84cbfd27ec2d64a4c3ef2a558821b8e788e3cda95623`，实际 exit0、47.765 秒。旧 12 激活后故意缺新候选 license，替换失败保旧 current/active 与失败诊断；旧 worker25 退役、恢复 worker35 仍用旧代 12。随后新 14 实际 Ready，两条冻结中英 smoke 非空正确且文档/版本/选区绑定保持。Host 正常退出0、双 EOF、0 外层清理信号、drain 全收尾/0 取消、owned 及组无残留，install 与三 use 锁可重取，原 seed 完整性不变；3147 原帧完整保存。这是生产安装/激活和 editor snapshot 链，不是质量评估或完整 GUI 验收。
- **证据的环境限制**：round02 的 Host 激活 OOM 未被覆盖或补算通过。round03 仅为实验 Host 增设 `DOTNET_GCRegionRange=20000000`（512MiB 预留）；原 `DOTNET_GCHeapHardLimit=10000000`（256MiB 提交）、AS2.5GiB 与 CPU0/1 不变，不证明默认资源行为，也未定位旧 OOM 失败分配点。runner 持续排空，最多 65536 帧/16MiB，产品 10ms 事件行为不变。依赖探针已隔离 UCX stdout 污染与合法 JSON；原 socket 拒绝不变，真实依赖/原生 FD 污染/外来依赖路径拒绝回归及 setup109 已通过，原件 `runtime-import-probe/`。
- **实现与本地检查已完成**：兼容基础、方向级高级能力、明确升级动作、禁用原因与手动确认已集成并双轴独审。手动预览批 Runtime1287/1287、worker167/167、Linux setup109/109 与两 Windows 消费者 Compile 通过；Linux 最终包准备的 99 项无模型邻近检查通过。原件在 `../Typelay-hy-english-candidate/artifacts/` 及相应作者工作区。它们不是本轮 Windows/Linux GUI 通过证据；实际已跑范围以上方 CI 表为准，不再把补 14/Windows 冷启写成从未运行。
- **能力与默认**：正式 catalog 可供 14 方向；ABI5、greedy、模型权重锁与默认保持，新装仍 12。中英预览经明确确认的 format2 `Install/addDirections` 事务启用，旧 Repair 不扩方向，既有选择和语言设置不变。选中库存、manifest 与原生导出共同授予能力；事务失败保旧代与 active 选择。候选 14 保留原 12 高级能力的 non-null 作用域，English 风格/术语请求明确拒绝而非降级普通翻译。保留 Argos 休眠偏好，重置/删除不受新增应用资格门阻挡。**跨旧版本恢复未完成，不承诺旧版本自动恢复 14 方向。** 当前版本可改用 Argos，未安装须另行确认下载。
- **最终验收门**：Windows 现有 15 场景预算仍 960 秒，Linux 240 秒工作/260 秒外限与 quality 请求数不变；核真实安装、取消/确认、同代冷启、双向普通预览、设置保持与正常退出。普通响应不证明高级 scope、源请求或质量；本地未发现 niri/Xvfb/weston 或 DISPLAY/WAYLAND 桌面，以实际 CI 原件建立 GUI 结果。

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

- Linux 仍推荐 CI468 `091fd796` [体验包](https://github.com/DingJWuyu/Typelay/actions/runs/39904552393/artifacts/12480483952)，完整检查、包身份、gzip/ZIP、293项清单及本包说明已核；仍需用户真机、物理键盘和输入法体验，不是所有应用验收。后继 CI477 GTK/GTK4/Qt 检查已通过，含 Qt 动画采样；这不证明 CI474 旧失败根因已定位。Qt 原件在 `../artifacts/ci477-current/approved-publication-20261023/linux/linux/qt-desktop/` 的 `desktop-result.json` 与 `qt-motion-*.json`。当前不派可选修复/扩展任务。
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

本地待发批包含 Hy 长度反馈、Windows 人工稿焦点取样、回源测试交接/既有失败诊断、Linux Hy 精确语言标签与本状态页，共14文件；TSF写机制研究与Qt新增诊断尚未就绪，不纳入本批。C#编译与Windows/GTK修后验证未在本地执行，后继CI承担对应检查，不能预报通过。
