# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 20:16 UTC** 按 CI515 完整终态、原件及实际包身份更新；通过、真实失败和未运行分别记录。

- **Notepad按需与普通退出已实际闭环。** 原单次F9、Esc保源、Direct与原生Undo1357ms通过；普通退出814ms通过，源进程、App和模型worker正常退出，原文件未改。真实Notepad丢弃回归明确匹配 `CommandButton_7` 并exit0，不能把此退出修复扩大成正式自动接受已开放。
- **正式自动Tab仍明确不具备能力。** 102ms在公开preview-only状态保留 `required-capability-unavailable`，不发接受键，编辑依赖未跑；Notepad总exit1与最终三proof成功并存。Main/capability0不变，物理/UIAccess、真实IME与部署资格仍缺。
- **App17个失败停在新的导航前置。** App859过/17失败/19未执行；Activation17均在RequestOwnedForeground返回SourceForegroundRefused，尚未到WaitVisible、Prepare或业务。前后同为非零外部root197068/PID2248/thread2404，当前原件不识别其进程种类，不能沿用CI514的terminal标签或断定创建者。
- **Hy有限诊断及生命周期已验证各自范围。** 新存储Unchanged七分支、Unconfirmed两分支及WPF有限投影两分支通过；Settings15场景与production五阶段全过。CI513候选hello后的PublicationUnchanged未复现、真实I/O原因仍未知；不改文件共享、发布/恢复结果或模型质量结论，不为采错另跑。
- **下一批只推进已审候选。** 源激活测试补真实caller自有WPF前台前置；隔离有序接受补冻结无修饰Tab/F1-F24及F10系统消息形态，两者已双轴静态GO，新增C#/Windows行为未跑。共享、编译、格式、native/包装、continuous与browser本轮通过；Linux按scope skipped，推荐仍CI498。

## 最近已核终态与实际结果

[CI515 / run 40127283203](https://github.com/DingJWuyu/Typelay/actions/runs/40127283203) 于 2026-10-24 19:52:44 UTC 由唯一push触发，attempt1，20:12:45 UTC终态 `failure`。冻结本地 `91831cd411c1fb943d1117cb330144e05ce93d1e` 对应远端 `56591b10cbe6e0e9b23f407abe3d6ffc6a7c3f20`、tree `836e63dbd2a85ccddcccc19635703635742bf486`；13文件精确发布、718叶和非强推读回已核。失败步骤为App行为与正式Notepad，不把整体红等同于本批全部目标失败。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。候选静态审查不算实跑，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core302、Runtime1318、Windows contracts161、Protocol28通过；shared worker168、Python shared29、Linux输入反馈181通过。Windows完整编译、format、worker168运行/1平台跳过、browser Python3、Ruff/format133通过；native adapter与72项包装通过 | 不把受控native/包装结果当物理输入或正式部署资格 |
| Windows App 与源准备 | App859过/17失败/19显式未执行；Activation17均在RequestOwnedForeground拒绝。18setup、首次Wake11、snapshot8、编排14、53owned通过 | 原caller请求前后均外部PID2248/root197068；失败发生于可见性等待与点击之前，不称点击遮挡复现或业务断言失败。caller前台前置候选尚待实跑 |
| 隔离有序来源 | OrderedSourceRecordsTheRealInjectedEdgeButDoesNotConsumeOrWrite完整Passed，实际记录/eligible、原键派发、原文/零写/retire断言通过；SourceWitnessWriter初始化/历史回归通过 | 本轮通过不证明CI513唯一原因就是same-tick。手动物理Tab例NotExecuted；现有v5拒绝证据不等于后继v6功能键扩展已跑，不代替物理/UIAccess正向、IME或部署 |
| 隔离真实 Notepad 与回执 | source-message接受/撤回两例、owned同步六例runner exit0且hooks/process/input清理全真；App真实Notepad两例、receipt/fence、foreign回归通过 | 原28秒与输入不变，旧机械消费者不授新来源资格，Main/capability0关闭 |
| 普通人工稿回源 | Windows六回源及foreign/router实际链通过，共享既有间隙保稿、默认拒绝、foreign与cancel/lateACK回归通过 | 不保证本轮命中0前台瞬态；原800ms、稿基线、fresh capture及撤权边界不变 |
| Windows continuous 与浏览器 | continuous全链通过，含练习按需与Chrome cold/new-tab；browser setup及六业务场景全部通过 | 旧1460、导航aborted、capture-failed根因仍未知；不为重现另跑，不加等待重试或扩大采集 |
| 退出证明与后继门 | before-notepad精确两proof门、最终三proof门通过；三个各自nonce与run/attempt/head、自然exit0/0/1、active0匹配 | fixture自然退出不自行证明业务成功；本轮App普通Quit另有Notepad原场景证据，自动Tab能力否定仍保红 |
| 正式 Notepad | 自动Tab102ms公开能力否定、编辑依赖not-run；按需1357ms正文/Esc/Direct/Undo通过。真实丢弃回归414ms匹配CommandButton_7并exit0，四selector与六owner正反回归全过；正式普通退出814ms通过，sourceNormalExit/appNormalExit/modelWorkersExited/originalFileUnchanged全true | 已关闭按钮定位与普通退出修复排程；未知或歧义仍拒绝，首Invoke后只观察退出，原520+20与cleanup未改。此证据不开放正式接受或产品权限 |
| Windows Hy | production五阶段、Settings15场景全过；select-argos70835ms，ordinary-exit689ms且normalQuit/exit0/全部worker退出。新Unchanged7、Unconfirmed2及WPF有限投影/清除2回归实际通过，实际加载身份相符 | 新诊断真实安装故障分支本轮未命中；旧PublicationUnchanged未复现、I/O原因未证。AccessDenied只分类系统结果，不指认锁/读取者；不推导质量或高级scope放行 |
| Linux 产品链 | 本轮full与可选Qt job均按scope skipped。最近CI511完整job success，GTK4/Qt各23项及Hy12阶段通过，新时耗覆盖原GTK4接受链和Qt首wake | 旧拒绝根因仍未知，无新有证据的可执行修复；保持实现及CI498推荐，不另跑或新增可选开发 |

核验入口为 `artifacts/ci515-independent/terminal-evidence-verified.json`，同目录保留完整job日志、共享/Windows TRX、场景原件与发布收据。源码archive867成员、718文件与冻结树逐字/模式一致；18个source原件和5份早TRX与final逐字一致。Windows outer89,295,552字节、112项CRC及SHA已核；内包87,137,989字节、551项CRC、SHA `b1bc6918b93d493f4f98d58af7b130f20eb9f585d39012358548417eaa10e456`，计算release identity `157ebdd234145a41702311bad86b21973f57faad76d0d4258d2b36b80ddb2eec` 与Notepad及两Hy报告实际加载身份相符，未重复解出内包。CI511 Linux原件与CI498推荐证据不变，原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 源激活测试前置 / triage_source_setup_ci498，root | 候选已双轴静态GO：第一组Desktop测试自行建立真实caller自有WPF前台，再执行原单次自有源导航和Prepare；关闭准备窗口后只观察精确源焦点，不重试或放宽拒绝。新编译/Windows未跑，CI515首因仍待验证 | 原17项在真实Windows完成准备及后续业务断言、既有遮挡/焦点拒绝与清理仍过后关闭本候选排程；失败按实际阶段处理，不把该前置假设当已证终端根因 |
| 隔离接受功能键 / implement_ordered_function_key_acceptance，root | 完整候选已双轴静态GO：冻结无修饰Tab/F1-F24主键贯穿原Broker及source owner，v6/49384同步全部消费者含WinEvent probe，Witness v2不变；保留认领键尾债和同主键Wake完成语义。作者受控C红绿通过，新C#/Windows、真实物理/UIAccess未跑 | 随正常批次核完整编译、真实managed/Broker/FirstWake及原source2/Notepad消费者；实际F10 SYS与错误键/修饰键负控分别验证。保持100ms/原来源IME拒绝和Main/capability0，受控C不冒物理接受或部署资格 |
| Linux体验维护 / root | 保持当前实现与CI498推荐，接收真机/物理输入法反馈，不增加可选开发 | 新实际故障或用户反馈再判断最小必要动作；未知IME保守拒绝，不自动发布stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认和资源合同；新增产品故障或实质新假设才安排最小验证，与Windows独立推进 | 生命周期、质量、高级scope与许可各凭自身证据；不扩大候选、推理预算或公开范围 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；隔离有序候选的初始化与软件注入拒绝已获得Windows实跑证据，但物理/UIAccess正向及正式部署资格未建立，按[正向接受剩余条件](../platforms/windows/development.md#正向接受的剩余条件)推进。新策略实际验证前保持当前严格边界及 Main/capability0，不变更 OS 权限、注册或部署。

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
