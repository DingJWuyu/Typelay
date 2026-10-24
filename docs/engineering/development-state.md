# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 15:44 UTC** 按 CI509 完整终态原件、独立 Notepad 准入候选和已确认的辅助输入产品选择更新；本地候选与实跑结果分开记录。

- **CI509 整体失败，但 Windows App 849 通过、0 失败、16 项显式研究未执行。** 首次 Wake 11 项、文本快照 8 项、Notepad 编排与 Save 14 项全部实过，末门测试修订与 typed 观察基础回归已交付；这不代替真实 Notepad 尾段验收，也不解释旧 sibling 空 Snapshot 的唯一原因。
- **新的阻断在两条 Chrome 路径，不是本轮 Notepad 或 Hy 失败。** continuous 新页按需已唤醒并完成翻译，随后 capture-failed 退休会话，最终等待截止；browser 在 Page.navigate 返回 aborted，尚未连接 loopback 页面或开始扩展发现。两者原件已交只读诊断，不延时或重跑求绿。
- **正式 Notepad 与 Hy 因原依赖未运行。** continuous/browser 均自然 exit1、activeProcesses=0；aggregate 实际运行后因缺少 Notepad 证明拒绝，不是泄漏实证。下一候选以独立的 before-notepad 两份退出证明准入 Notepad，保留前序业务红、最终三份证明、独占数据目录拒覆盖及原预算；本地检查通过，Windows 尚未实跑。
- **Windows 辅助输入兼容范围已获用户确认，不再等待该产品选择。** 认可 Windows 分类的辅助输入不等于开放普通模拟键、给 TypeLay 提权或开放生产接受。来源门另线只有部分实现准备，旧队列新鲜性与实际安全接线未闭，Main/capability0 不变；真实 IME 与人工稿异常交互选择仍分别处理。
- **Linux 体验继续推荐完整核验的 CI498。** 本轮 Linux 未运行；模型采用、默认和资源合同不变。

## 最近已核终态与实际结果

[CI509 / run 40102218114](https://github.com/DingJWuyu/Typelay/actions/runs/40102218114) 于 2026-10-24 14:22:40 UTC 由唯一 push 触发，attempt 1，14:35:16 UTC 终态 `failure`。冻结本地 `892de7e3771a7ba6cf89dd223a4bb1842ec8690a` 对应远端 `c97002b357d6d38ede4871e81695c5c57fc2e066`、tree `b16f4653000e25d29c7eae540bce54b8ecb6172b`；12 文件精确发布、同分支非强推及源码身份已核。下述独立退出准入与产品选择文档整合尚未发布，不算本轮运行内容。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行分别记录；一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core 302、Runtime 1314、Windows contracts 144、Protocol 28 通过；shared worker 168、Python shared 29、Linux 输入反馈 181 通过。Windows build、格式通过，worker 167 过、1 跳过，browser profile Python 3 项、Ruff 与 133 文件格式通过；包装 69 项通过，含 native foreground guards 与 WinEvent | 共享或受控回归不代替真实平台业务验收 |
| Windows App 与源准备 | 849 通过、0 失败、16 项显式研究未执行。首次 Wake 11 项包含冷真实 F9、旧 read 阴性与末门 9 对照；PublicNoticeSnapshot 8 项及 Notepad 编排/Save 14 项通过。NativeSourceActivation 17、source setup 18 项通过，foreground 首次观察 264ms 匹配源 root/field | 修订后的末门与 typed 基础回归排程关闭；取消后寿命诊断失败分支未触发，不据此解释旧焦点或空 Snapshot 根因，不为它单独重跑 |
| 隔离真实 Notepad 消费者 | source-message Broker/adapter 接受与撤回两例完整通过；一次 Verified/零正文回执旁路、Undo、焦点、零 LL activation/二次 Write、Quiet 与清理依各自原断言核验 | 原 28 秒和按键不变；受控输入不补齐严格来源、真实 IME 或第三方部署资格，不等于正式产品接受开放 |
| 普通人工稿回源 | 原六分支及已收到 foreign 前台后不得被迟到 ACK 恢复的 router 集成阴性通过；共享 7 项间隙保稿、默认拒绝、foreign 与 cancel/late ACK 回归通过 | 未证明本轮再次命中真实 0 前台瞬态；原 800ms、原稿基线、fresh capture 和撤权边界不变 |
| 隔离 App 回执与原生源 | SourceMessageReceipt、拒绝派发、late ACK、expired installation、Unknown fence、Owned Ctrl/Tab 和 5 项 StartupPeers 通过；owned 六例、source 两例及现役 native adapter 通过，hook/进程/输入清理完整 | 来源分类自动采集保持退役；局部回执不授予普通生产接受或物理 IME 资格 |
| Windows continuous | owned 接受/编辑/回源、普通 control、continuousPreview、direct、practice 与 Chrome cold page 通过；Chrome 新页自动模式通过，按需最终 desktop-deadline。原 trace 先 wake-awakened、translate-done，再 capture-failed 与 NoSession | 不再归因为按键没唤醒或没有开始翻译；捕获失败的底层原因尚未证，当前只读诊断，不放宽捕获失败退休规则或期限 |
| 浏览器合作输入 | setup 中 CDP get-targets、attach、Page.enable、DOM.enable 已到达；Page.navigate 阶段失败，pageNavigationFailure=aborted，loopback 三项连接/请求/响应均 false，扩展发现计数均 0 | 扩展发现及六业务场景未跑，不归成旧 metadata ReferenceError，不把此前正常轮结果当本轮通过 |
| 退出证明与后继门 | continuous/browser 各自同 run/attempt/head、独立 nonce 的 natural exit1、active0 已核；step34 aggregate 实际失败，Notepad nonce/证明缺失 | 缺证明不是泄漏实证。before-notepad 候选只核前两份精确证明，最终 all 仍须三份；前序失败仍保红，取消/未知/缺失/冲突与共享用户数据目录继续拒绝 |
| 正式 Notepad | step33 skipped，没有本轮 result、退出证明或新尾段证据。最近 CI508 首 Wake age51ms、译文、Esc、Broker/App Direct Verified 已到达，随后 1229ms InvalidOperationException，具体读点未知、Undo/正常退出未证 | typed Matches 仅处理等值等待中的 LengthChanged，其它 fatal 与严格 SourceUnchanged 保留；基础回归本轮通过，完整译文、焦点、Undo、普通退出仍须部署实跑，原输入与 520+20 秒预算不变 |
| Windows Hy | production step35 与 Settings step36 skipped，不记为新 Hy 失败；最近 CI508 的 production 五阶段与 Settings 15 场景、normalQuit=true、exitCode=0/全部 worker 退出仍是既有基线 | 本轮无新加载或生命周期证明；旧 UIA 间歇根因、质量与高级 scope 缺口没有因此关闭 |
| Linux 产品链 | 完整 Linux 与可选 Qt job skipped；最近 CI501 完整桌面与 Hy 12 阶段通过，体验推荐仍为已完整核包的 CI498 | 本轮无新 Linux 包；XTest 不是物理键盘，editor snapshot 不是高级 scope 或质量验收 |

核验入口为 `artifacts/ci509-independent/terminal-evidence-verified.json`。同目录 `windows-results-verified.json`、`shared-results.json`、`source-results-verified.json`、`foreground-preparation-result.json`、`owned-startup-results.json`、完整 job 日志与 `publication-verified.json` 保存身份与结果。源码 archive 860 成员、711 文件与冻结树逐字/模式相符；early/final source 18 份与 continuous 2 份逐字一致。Windows outer SHA/CRC 与 inner 551 文件 CRC/校验和已核；Notepad/Hy 未运行，没有其 loaded-release 回执，不冒充实际加载验证。内 ZIP 未另留重复副本。最近 Linux 原件与推荐包证据分别在 `artifacts/ci501-independent/linux-results-verified.json`、`artifacts/ci498-independent/linux-package-verified.json`；原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 独立 Notepad 部署验证 / root，publish_next_verified_fix_batch | before-notepad 两证明门原片已双轴 GO；13 项 CLI、admission/profile/YAML 与旧真实 proof 重放本地通过，整合窄审待完成。前序业务失败但退出已证时继续原 Notepad；最终三 proof 与预算不变，独占用户数据仍拒覆盖 | 授权精确提交后，核 Windows 接线及真实 Notepad 预期正文、焦点、Undo、正常退出；公开 Tab 能力否定仍失败，不能以独立尾段通过冒充产品接受开放。若未准入按真实原因停止，不改 proof 或重跑求绿 |
| Chrome 两处首因 / diagnose_ci509_chrome_failures | 只读 CI509 与既有可比原件：新页已唤醒/译完后 capture-failed，合作浏览器导航 aborted；不重复下载、不修改 native 或 Chrome 源码 | 只对现有证据能区分的原因给下一最小动作；无新证据不加重试、sleep 或扩预算，不阻已就绪的独立 Notepad 验证 |
| Windows 来源门 / implement_source_origin_gate，root | 用户辅助输入选择已确认；另线部分实现已准备，旧队列新鲜性与实际安全接线尚未闭合，不含本批。既有受控机械消费者仍保留各自边界 | 完成机制审查、原消费者反例及 Windows 实跑后再判断采用；真实 IME 与部署仍须独立证据，Main/capability0 保持关闭，不把技术未决写回等待用户同一选择 |
| foreground 等待首错 / triage_source_setup_ci498 | 清理前寿命诊断已发布，CI509 编译与原用例通过，失败分支未验 | 后续自然失败时核一次元数据及原取消/清理，核实后关闭排程；不单独重跑，非原子样本不证明前台根因 |
| Linux 体验交付 / root | 保持 CI498 推荐，接收真机/物理输入法反馈 | 后继包须完整结果与身份才能替换推荐；未知 IME 保守拒绝，不自动发布 stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认和资源合同；新增产品故障或实质新假设才安排最小验证 | 生命周期、质量、高级 scope 与许可各凭自身证据；不扩大候选、推理预算或公开范围 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；来源机制尚未就绪，按[下一来源门切片](../platforms/windows/development.md#源接受来源门的下一实现切片)推进。新策略实际验证前保持当前严格边界及 Main/capability0，不变更 OS 权限、注册或部署。

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
