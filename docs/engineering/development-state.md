# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-25 08:18 UTC** 更新。最近已核 Windows 终态仍为 CI527；新增正式 ordered 源接受实现候选已冻结并完成独立静态审查，尚未执行 managed/Windows 验证，也未发布或变更推荐。

- **App903过/0失败/22未执行。** `ProductionForegroundWriteTests.NativeReturnPreservesTheExactFieldRestoredByRootActivation` 的success、cookie-lost、timeout三行均通过，失败专用全局/源线程观察未触发。CI526曾在completion屏障释放前读到全局焦点 `(0, 0)`，本轮未复现，原因仍未知，不称根因已修。
- **最近已核 CI527 的唯一失败仍是正式Notepad自动接受cap0拒绝。** 按需Direct/Undo与普通退出通过，无业务deadline或cleanup问题；自动接受及依赖未跑独立保留，不能称整条Notepad或普通输入框自动接受通过。
- **隔离物理验证包继续推荐已核CI526。** 本轮打包通过不触发推荐轮换；**物理按键尚未执行**。产品开发预览保持CI518，Linux体验保持CI498。
- **正式接线候选已实现，尚无对应 CI 结果。** 代码冻结于 `aef04ed193bc97b7247a09d142fd703eee656c8a`，从 `2dc08de` 独立实施；候选 factory 与 Broker/Main 已唯一接通 ordered 路线，不再声称候选 Main 关闭。root 已授权在当前开发分支安排 CI 验证，不等于用户产品发布或推荐晋升。
- **NativeReturn 诊断已实跑且未命中。** 原全局精确焦点断言及失败时源线程观察保留；不以局部状态替代全局条件，不为未复现的理论原因新增开发或重复 CI。

## 最近已核终态与有效证据

[CI527 / run 40168855271](https://github.com/DingJWuyu/Typelay/actions/runs/40168855271) 于 2026-10-25 06:22:25 UTC创建，attempt1，06:43:13 UTC终态 `failure`。冻结本地 `45bd9f12c3f77917de470b59940fb10345eea7cc`、tree `e5a3b3d58ea5ce0648f24301d953f5e6b93625e9` 对应远端 `b8ca3ec44501a0adccd57a8f93bfdd21120907af`。唯一失败步骤为正式Notepad，shared与Windows行为检查通过；Linux产品链按范围跳过。

| 范围 | CI527实际结果 | 证据边界 |
| --- | --- | --- |
| shared与Windows编译 | shared的Core302、Runtime1318、Windows.Contracts169、Protocol28合计1817项通过；Windows编译及原五处xUnit1051修正通过 | 编译与受控测试通过不替代物理输入或正式部署验收 |
| Windows App | NativeReturn三参数行、Notepad场景编排及原Tab9/F7118阴性安全门通过；App零失败，22项未执行 | 失败专用观察未命中，不能判定旧全局0的原因，也不把未执行计为通过 |
| 正式Notepad | 自动接受113ms公开required-capability-unavailable，编辑回源依赖not-run；单wake/Esc/单Direct及原生Undo1797ms通过，ordinary-exit873ms通过。无业务deadline触发或cleanup错误 | ordinary-exit有自身源/App/model退出与原文件不变证据，不借消费者退出代替；原自动接受失败及总exit1保持 |
| 其它Windows链 | continuous、browser7、Hy production五阶段与Settings15通过；source两行与owned六行清理成功，三个消费者natural exit0/0/1及完整退出门已核 | 不推导语言质量、UIAccess、真实IME或正式部署完成 |
| 隔离物理Tab包 | 原Tab9/F7118安全前置、唯一manual Tab发现、搬迁defaultfactory两行与打包通过；当前交付推荐仍为已核CI526 | 搬迁验证默认cap0/不安装/不提示/零写，不重跑源安装拒绝或物理阳性；没有原suite到最终包逐字samebits证明 |
| Linux产品链 | skipped | 保持既有实现与CI498推荐，不新增本轮Linux验收声明 |

核验入口为 `artifacts/ci527-independent/terminal-evidence-verified.json`。四份artifact、冻结源码733叶、18份早期/最终原件、产品包551文件与三份正式加载身份、消费者退出证明均已核。当前推荐隔离包的精确下载、内层摘要与环境要求统一见[CI526物理Tab验证包说明](../platforms/windows/README.md#隔离物理-tab-验证包不是产品体验包)。原始日志、模型与测试输出不复制到源码文档。

原CI524搬迁Tab到期失败不因改用默认关闭搬迁检查而改判；本轮安全前置仍来自原套件的完整Tab/F7阴性。旧子进程早退、覆盖窗及设置保存异常没有因未复现而获得根因结论。

## 未发布正式接线候选

独立树 `Typelay-formal-source-candidate` 的代码候选为 `aef04ed193bc97b7247a09d142fd703eee656c8a`，不是 CI527 已测源码。相对 CI527 对应本地 `45bd9f12c3f77917de470b59940fb10345eea7cc` 共30叶，包括 `2dc08de` 引入的本状态页；相对实现起点 `2dc08de` 的代码及关联文档共29叶。本页修正单独冻结，不改已核原件。

| 实施阶段 | 冻结提交与当前结果 | 验证边界 |
| --- | --- | --- |
| 逐 capture 资格与兼容 | `f95010b` 增加可选五字段 eligibility 和 Broker v5 双向旧 peer 拒绝；`8b50619` 保留 Direct 原两写能力但 cap0；`9afbf2f` 修正 legacy Notepad 显式旧设置及有效 Direct 计划拒绝回归 | 缺/错证明不能回落旧勾选；native transaction v2/source v7 不变。新增 managed/真实 Broker 回归尚未执行 |
| owned 共存与寿命 | `bbfa6d3` 分离 owned Bind/op6 与 ordinary source hook，隔离 LL owned FIFO，按精确 PID/start 去重活进程 fence；`f9035c9` 保留原注册命令优先并删除无实泵计数断言 | 仅 signaled 原句柄可清理，不提前卸模块；重复 capture/clear 回归验证的是寿命，不宣称量化句柄数。Windows 未执行 |
| 正式唯一组合与交付门 | `aef04ed` 删除 factory opt-in，Broker/Main 固定 ordered；旧 sent seam 仅保留现役安全回归，正式 CLI/环境/设置不可选择。原 ordered Tab/F7/manual/Ctrl 使用真实 Broker 入口，always-cap0 测试换成精确资格缺失拒绝，包装 smoke 与文档同步 | 原安装/ACK/Arm、来源、IME、焦点、版本、100ms、Undo、撤权及清理门不放宽；推荐 CI518/CI526 不变 |

`assess_formal_windows_wiring` 与 `review_formal_compatibility_tests` 对精确最终代码均给出独立静态 GO，先前 Direct 能力丢失、legacy 消费者和命令优先问题已逐项修正。允许的 folder whitespace、全仓 Ruff check/format（135文件）、包装 Python 单测8项和 diff check 均通过；没有执行本地被拒绝的 managed build/test，也没有本候选 Windows、物理、UIAccess、真实 IME 或部署阳性。下一步由 root/publisher 在原开发分支安排精确候选 CI；作者不自行发布。

## 当前责任与下一动作

root负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 正式源接受候选 / connect_controlled_windows_runtime，root，publisher | 三阶段代码与独立双轴静态审查已完成；root 负责精确候选整合与 CI 决定，publisher 才能执行远端写入。当前尚无对应 managed/Windows 结果 | 在精确源码的 CI 结果后区分回归、物理与部署证据；开发分支验证不等于产品发布，禁止以静态 GO 冒称正式接受可用 |
| NativeReturn维护 / root | 三参数行本轮通过，原严格全局门与失败专用源线程观察保留；诊断未触发，旧原因未知，无待定CI | 新实际失败或用户反馈产生有效证据后再最小诊断；不为理论原因新增开发、等待或重复CI，局部状态不能替代全局门，本地被拒绝的构建/测试不重试 |
| 正式Notepad与已过回归 / root | CI527 的 Direct/Undo、普通退出及编排回归已实际通过；该版自动cap0拒绝和编辑依赖未跑仍保持。新正式接线候选需重新验证，原Ctrl、源接受、期限、所有权及清理回归继续保留 | 精确候选实际失败再最小诊断；不把成功Direct提升为自动接受或所有普通输入框支持，不因未复现称旧问题根因修复 |
| 隔离物理Tab交付 / root | 当前提供已核CI526包；CI518产品开发预览不变。已发布包的原安全门与搬迁默认关闭职责分开；新候选的搬迁 smoke 改验缺 eligibility 时拒绝，尚未运行。下载身份以Windows说明为准 | 用户在包内提示后亲手一次Tab取得1Passed才算物理阳性；不补按、不重跑，UIAccess/IME/第三方部署各凭自身证据 |
| Linux体验维护 / root | 保持当前实现与CI498推荐，接收真机/物理输入法反馈，不增加可选开发 | 新实际故障或用户反馈再判断最小必要动作；未知IME保守拒绝，不自动发布stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认和资源合同；新增产品故障或实质新假设才安排最小验证，与Windows独立推进 | 生命周期、质量、高级scope与许可各凭自身证据；不扩大候选、推理预算或公开范围 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；隔离有序候选的初始化与软件注入拒绝已获得Windows实跑证据，但物理/UIAccess正向及正式部署资格未建立，按[正向接受剩余条件](../platforms/windows/development.md#正向接受的剩余条件)推进。当前推荐产品 CI518 与隔离验证包 CI526 保持原已发布边界；这不表示未发布候选的 Main 仍关闭。候选已实现唯一 ordered 正式组合，只能在本次精确 capture 资格、实际安装和原安全条件成立时提出接受。root 授权的开发分支 CI 不等于正式产品发布、用户默认推荐晋升或物理验收；不变更 OS 权限、注册或部署。

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
