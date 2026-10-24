# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 14:06 UTC** 按 CI508 完整终态原件与 Notepad 文本观察窄修进度更新；本地未发布修订与该轮实际结果分开记录。

- **CI508 已核 Windows App 为838通过、3失败、16项显式研究未执行。** 原26项首次 Wake 回归23过、3失败；真实冷单F9、旧read不得借用、Broker字段门与队列回归通过。旧末门theory的两项往返在排空后采样155/184ms，不能据此确定生产交付采样时刻；sibling在变更前空Snapshot的具体原因仍未知。确定性末门替换已实现并获两轴静态 GO，尚未实跑或发布。
- **CI508 真实 Notepad 已走到首 Wake、预览、Esc 与 Direct Verified，但未完成原场景。** 唯一 F9 的新捕获以 age51ms 唤醒，译文与取消后的源文不变已到达，F10 入队且 Broker/App 均记 Verified；按需场景 1229ms 后为裸 InvalidOperationException，具体失败读点仍未知，Undo 与正常退出未证。现有三消息严格读取在正文异步变化时可拒绝本轮样本；窄修只让等值等待中的 LengthChanged 视为未取得完整观察，保留其它致命异常、严格源文断言、唯一按键和原预算，不将这一观察语义修复冒称 CI508 唯一根因。
- **新 retrieved-MSG 路线的隔离真实 Notepad 接受/撤回两例完整通过。** 精确提交实际验证了接受恰一次 Verified 正文回执、撤回零正文回执且原键旁路，以及各自原路径的 Undo、焦点、零 LL activation/二次 Write、Quiet 与清理。正式 Main/capability0 不变，不能把隔离通过写成普通发行路径已开放。
- **本轮回源、隔离 App 回执、Owned Ctrl/Tab、StartupPeers、continuous 与 Hy 生命周期通过。** 回源六分支及 foreign/router 集成阴性回归通过；Windows Hy production 五阶段与 Settings 15 场景通过，正常退出与 worker 清理有实证。实际 0 前台间隙是否命中、物理输入法、第三方部署和翻译质量仍分别判断。
- **Linux 体验继续推荐完整核验的 CI498。** CI508 未运行 Linux 产品链。最近完整 Linux 实跑 CI501 的 Qt/GTK/Hy 结果仍有效作基线，未改变推荐、模型采用或产品权限。

## 最近已核终态与实际结果

[CI508 / run 40098022629](https://github.com/DingJWuyu/Typelay/actions/runs/40098022629) 于 2026-10-24 13:24:48 UTC 由唯一 push 触发，attempt 1，13:45:03 UTC 终态 `failure`。冻结本地 `7ef64d6aa259c58b54a0de777ec22103ce27ebe0` 对应远端 `abbdd3e1de442bbbeb8e5e9a60e3f8a273f88525`、tree `d632b79c8d6a36eb89bc5ebc0e0281b89cb88f92`；精确读回、同分支非强推及源码 artifact 身份已核。本地后继末门测试修订与文本观察窄修尚未发布，不算本轮已验证内容。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行按下表分别记录；旧轮结果不代表新提交，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core 302、Runtime 1314、Windows contracts 144、Protocol 28 通过；shared worker 168、Python shared 29、Linux 输入反馈 181 通过。Windows build 与格式通过，worker 运行 168 项为 167 通过、1 跳过，browser profile Python 3 项、Ruff 与 133 文件格式通过；包装 69 项通过，含 native foreground guards 与 WinEvent | Windows worker 的 1 项跳过单独保留；共享 Runtime 通过不代替真实平台场景 |
| Windows App 与源准备 | 838 通过、3 失败、16 项显式研究未执行。首次 Wake 26 项中 23 过、3 失败；旧 NativeSourceActivation 17 项、source setup 18 项及 foreground 准备通过，首次焦点观察 275ms 即匹配源 root/field | 末门两项旧测试于排空后测得155/184ms，sibling变更前空Snapshot原因未知；替换测试未实跑。取消后寿命诊断的失败分支未触发，不据此解释旧焦点失败，不为诊断单独重跑 |
| 隔离真实 Notepad 消费者 | 新 source-message Broker/adapter 的接受与撤回两例实际通过；一次 Verified/零正文回执旁路、Undo、焦点、零 LL activation/二次 Write、Quiet 与清理按各自原断言闭环 | 迁移验证排程关闭；原 28 秒和按键不变。受控软件输入不补齐严格来源、真实 IME 或第三方部署资格，不等于下方正式产品接受通过 |
| 普通人工稿回源 | 原六分支（含 shift）全部通过；新增已收到 foreign 前台后回原窗仍不得被迟到 ACK 恢复的真实 router 集成回归通过。共享 7 项间隙保稿、默认拒绝、foreign 与三类 cancel/late ACK 回归通过 | 本轮未证明真实 0 前台瞬态再次出现并命中过例外。保留原 800ms、原稿基线、fresh capture 和撤权边界，不授予普通生产写入 |
| 隔离 App 回执与前置 | SourceMessageReceipt 完整用例及拒绝派发对照、late ACK、expired installation、Unknown fence 通过；Owned Ctrl/Tab 和 5 项 StartupPeers 消费者回归通过 | 原始 hook 观察与真实派发仍分开；受控实跑不证明第三方部署资格 |
| 原生受控源与研究退役 | owned 六例、source 两例及现役受控 native adapter 通过，hook/进程/输入清理完整；来源分类自动采集保持退役 | 受控合成输入不授予严格物理来源或真实 IME 资格，不恢复旧研究凑运行 |
| Windows continuous 与退出证明 | continuous 整条链通过；continuous、browser、Notepad 三份同 run/attempt/head、各自 nonce 已核的自然退出证明均确认 activeProcesses=0，aggregate 实际通过 | 三个消费者根进程退出码依次为 0、0、1；Notepad 业务失败后的消费者退出，不等于其未运行的 ordinary-exit 场景通过 |
| 浏览器合作输入 | setup 与六业务场景通过；两个 candidate 均 chromePresent/runtimePresent/getManifestCallable=true，metadataRead=2、matchingManifests=1，无 exception，异常行列为 null。既有 JSON 投影类 35 项通过 | 同一次 guarded evaluate 诊断已交付；本轮正常返回不能解释 CI502 ReferenceError。仍为 unpacked 开发扩展、软件合成输入，不扩大部署或物理 IME 资格 |
| 正式 Notepad | 前三前置通过；自动接受 67ms 报 required-capability-unavailable，唯一当前结果、previewOnlyCue=1/cue=0及原 facts 保留；编辑为 not-run/dependency-unavailable。独立准入后首Wake age51ms、译文、Esc与F10均到达，Broker/App direct-write均Verified；按需场景1229ms报InvalidOperationException/operation-failed，facts=null，ordinary-exit未跑；既有13项编排/Save回归全过 | 原件无细stage及异常reason，无法唯一定位direct等待、Undo等待或严格复核，Undo/正常退出未证。新窄Matches仅接入既有四处等值等待；细stage、changed计数及安全数值诊断待精确提交实跑。消费者exit1、active0、cleanupErrors=[]只证明故障后清理；生产能力、原输入、WaitAsync与520+20秒预算不变 |
| Windows Hy | production 五阶段、Settings 15 场景全部通过；英文补充 64.103 秒，14 方向冷启和中英普通源双向预览通过；ordinary-exit 明确 normalQuit=true、exitCode=0、全部 model workers 退出，cleanupErrors=[] | 不把本轮通过当旧 UIA 间歇超时根因已修；质量 not-reviewed，高级 scope 与跨旧版本恢复仍有各自缺口 |
| Linux 产品链 | 本轮完整 Linux 与可选 Qt job 按未改动的 Windows-only 范围 skipped；最近 CI501 完整桌面链和 Hy 12 阶段通过，Qt 实际编辑回源/独立新键接受与 Undo 通过 | 本轮没有新 Linux 包；CI501 未重做内产品包采用评估，体验推荐仍是 CI498。XTest 不是物理键盘，Hy editor snapshot 不是高级 scope 或质量验收 |

核验入口为工作区 `artifacts/ci508-independent/terminal-evidence-verified.json`。同目录的 `contracts-results.json`、`first-wake-tests.json`、`notepad-trace-progress-verified.json`、`foreground-preparation-result.json`、`owned-startup-results.json`、`windows-results-verified.json`、`shared-results.json`、`source-results-verified.json`、完整 job 日志与 `publication-verified.json` 给出实际结果和身份。源码 archive 的 860 个成员、711 个文件与冻结树逐字/模式相符；early/final 18 份 source 与5份contracts原件逐字一致。Windows 内外包 SHA/CRC 与 551 个文件已核，计算的 releaseIdentity 与 Notepad、Hy production、Hy Settings 三份实际加载回执一致；内 ZIP 未另留重复副本。最近 Linux 实跑证据在 `artifacts/ci501-independent/linux-results-verified.json`，推荐包证据仍在 `artifacts/ci498-independent/linux-package-verified.json`。公开运行链接供查阅实际 job/artifact，原始日志不复制进本页。

## 当前责任与下一动作

root 对集成、证据核验和采用决定负责；下表是当前公开责任线，不是实时活跃进程清单。Agent 目标配置保持 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标记为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 首次 Wake 末门回归 / root 集成与CI核验 | 本地 c205fcb 已冻结且两轴静态 GO；保留真实冷单F9与旧read阴性，末门改为真实Hook/UI加本次请求显式合成pending回包，仍调用实际TryRead；只控制最后交付now，保留不变阳性与独立过期阴性。生产时钟、原150ms及Notepad时序不变，sibling空Snapshot原因仍未知 | 本地纯folder格式、git diff及XML/Compile静态检查通过；MSBuild/socket仍被拒绝，修订用例与诊断未实跑。与下行窄修合并后，由root按原授权执行下一次正常push及精确CI；不加sleep/内部lease等待/重试/延期限 |
| Notepad 预期文本观察 / fix_notepad_text_observation，root 集成与CI核验 | 独立worktree基于c205fcb实现：严格ReadText保留三消息、count短路及限量，只给LengthChanged的窄Matches返回false；接受/人工稿/direct/共享Undo使用原外层等待，SourceUnchanged仍严格。真实STATIC双向变化与fatal边界回归、细stage和独立数值诊断保留原Facts；仅snapshot_regressions负责测试文件，目标配置gpt-6-astra/xhigh，工具确认请求已接受，运行时配置未另核实 | 纯folder格式、git diff及63项显式Compile链接/XML静态检查通过，冻结后由root派两轴审查；本地行为测试因既有MSBuild/socket拒绝不重试。后续精确CI须证明完整译文、焦点、Undo与正常退出；新诊断只能定位下一次失败，CI508具体读点仍unknown，不宣称已根治 |
| foreground 等待首错 / triage_source_setup_ci498 | 取消后的清理前寿命诊断已发布，CI508 编译与原用例通过，失败分支未验；保留已持有进程 kernel 退出信号及窗口存在布尔的单次诊断，异常不替换原取消 | 等后续自然失败时核一次清理前元数据及原取消/清理保留，核实后关闭本诊断排程；不单独重跑。非原子样本不证明截止前退出、窗口身份或前台根因，原输入/循环/10 秒截止不变 |
| Linux 体验交付 / root | 保持 CI498 已核完整包推荐；最近完整 CI501 无新增 Linux 失败。保留用户真机、物理键盘/输入法反馈，旧 IME/投递延迟未获唯一根因证据 | 后继包须完整相关结果与身份才能替换推荐；未知 IME 仍保守拒绝，不延票、不重发唤醒、不自动发布 stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认、兼容和资源合同；只在新增产品故障或有实质新假设时安排最小验证 | 生命周期、质量、高级 scope 和资产许可各凭自己的证据；许可调查本身不授予新采用或公开权限，不扩大候选或推理预算 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。UIAccess 选择未答前保持严格注入拒绝，不变更 OS 权限、注册或部署。

有界诊断只用于定位，不采用户标题/正文，不增加焦点重试或放宽原期限。真实 IME composition 正例仍缺，输入配置库存与明确 none 不能代替它；既有几何、浏览器与回源回归保留，但旧间歇故障未复现不等于根因已消失。

### Linux 推荐与支持范围

推荐 [CI498 体验包](https://github.com/DingJWuyu/Typelay/actions/runs/40067927744/artifacts/12550995369)，提交 `3152a7f74b4022dfd18d34fe9534441a621d992c`；内 tar 为 281,118,175 字节，SHA-256 `0b9fc84e9d7036f046935e6620dbe6635732ad3b144cb9570281b801b9b11d31`。完整 Linux job、内外包、clean 提交身份、293 项文件清单与启动入口已核验。这是体验推荐，不是 stable、物理输入法或所有应用验收。

按需唤醒后的空捕获仍保守中止；恢复同一目标不证明期间未离开焦点家族，见 [Linux 架构约束](../platforms/linux/architecture.md#按需唤醒与缺失捕获)。可选富文本、非合作控件及 Electron/VS Code 自动浮层扩展暂不开展。

### 模型：生命周期通过不等于质量放行

- **兼容与默认**：正式 catalog 可供 14 方向，新装仍为 12；ABI5、greedy、模型权重锁与默认参数不变。中英预览须明确确认的 format2 `Install/addDirections`，旧 Repair 不扩方向，失败保旧代和 active 选择。库存、manifest 与原生导出共同授予能力；English 风格/术语请求明确拒绝，不降级为普通翻译。Argos 休眠偏好保留；跨旧版本自动恢复 14 方向尚未完成。
- **实际范围**：Windows Hy 已覆盖受控 stock Edit 双向普通源预览与 15 场景生命周期；Linux 为 editor snapshot 与 12 阶段生命周期。双端高级 scope 尚未实机验收，质量未由这些 smoke 评估。已有独立产品安装/激活链证明失败保旧代与正常清理，但实验 Host 的资源配置不能作为默认资源行为证明。
- **质量比较**：原独立 16 条合成日常样本中，Hy 为 14 条未见实质问题、1 较小、1 退款动作未决；Argos 为 7 重大、4 较小、5 未见问题，保守至少 5 对明确重大修复。未决不算通过；评审见过旧摘要且非专业译者，不称完全盲评或用户错误率。原件仍在 `artifacts/paired-daily16-run-20261023-01/` 与 `artifacts/paired-daily16-execution-20261023-01/`。
- **采用结论**：Hy repetition 1.0 无替换默认 1.05 的依据；中英首筛仍有台灯错义和严格时间上界未决。Argos beam1 未证明重大改善，不采用；Mozilla 有界 CLI 能运行但保义不足，不采用。其它旧候选无采用依据；缺失旧原件不以摘要重判，未实测不写成质量失败。没有新假设不扩研究范围。
- **资源合同**：模型推理串行并与重编译错开；两核 CPU affinity、硬地址空间 2.5GiB、加载/hello 与单请求 60 秒、共享 600 秒、清理最多 7 秒、失败停止且零推理重试。Hy/Qwen context 1024、output 192；Argos 保留生产输入 384 pieces、输出 512 拒绝界；Mozilla 保留整请求 192 token 保护。双端生命周期既有预算为 Windows 15 场景 960 秒、Linux 240 秒工作/260 秒外限，quality 请求数不变。两核 affinity 不等于只有两个线程，点采样不冒充连续资源证明。

## 维护与恢复

1. 收到交接、实质进展或新失败时，先核精确提交与原件，再替换当前结论、责任和下一动作；关闭的排程直接删除，不另建历史页或替代日志。
2. 区分实现、本地检查、精确提交 CI 和用户真机；失败、未测、环境限制分别报告。本地 MSBuild/测试的 socket 权限限制不由独立格式检查解除，拒绝的动作不换路径绕过。
3. 执行前核二进制/模型身份、空间、锁和 owned 进程；清理须有实际退出证据。原件存入被忽略的工作区 `artifacts/` 或 CI artifact，不提交模型、原始日志或来源摘要清单；新证据不冒称旧原件恢复。
4. 按 [AGENTS](../../AGENTS.md) 与 [技能规则](../agents/skills.md) 做必要验证和独立审查；同一失败必须换取新证据，不以重试、延时或放宽断言掩盖。等待结果或授权期间继续有价值且独立获准的工作。
