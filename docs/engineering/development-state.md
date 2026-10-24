# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 18:55 UTC** 按 CI513 完整终态、原件及实际包身份更新；本轮通过、真实失败和未运行分别记录。

- **Windows 完整编译已恢复，隔离有序来源仍有一个真实测试失败。** 两条 NativeFixture Compile 链接在本轮编译通过，旧 CS0136/CS0246 阻断已消除。App855过/1失败/19未执行；唯一失败为 injected-edge 场景的 witness 计数未增长断言。原许可、mapping与消息pair派发完成已越过，后续记录、正文不变、零写和retire断言尚未执行，不能称完整拒绝链通过。
- **新初始化回归与既有消费者实际通过。** SourceWitnessWriter 的 dormant/same-tick/history 回归通过；source-message两例、owned六例和原App receipt、回源、准备回归通过。物理Tab显式用例未执行；受控结果不授生产能力，Main/capability0仍关闭。
- **正式 Notepad 按需链完整通过，正常退出卡点已缩窄。** 单F9、Esc保源、Direct及原生Undo1447ms通过；自动Tab101ms公开能力否定，编辑依赖未跑。退出507961ms耗尽原deadline，细stage为 `find-notepad-close-dialog`，未匹配同进程对话框、未观察按钮7、Invoke计数为0；不再归因于原文检查，但对话框发现失败的原因仍未证。
- **Hy 设置切回 Argos 的发布失败仍待因。** production五阶段通过；Settings的select-argos候选hello已成功，随后PublicationUnchanged、公开状态failed-runtime，后10场景未跑。顶层ModelStart标签不表示worker加载失败；当次I/O原因未知。故障清理完成不等于普通退出或质量通过。
- **下一就绪组合只改测试/fixture与本页。** source合法B前置及有限观察、Notepad原生owner对话框定位均已两轴静态GO，组合保留作者代码叶；修后C#/Windows尚未运行。没有生产来源门、ABI、期限、模型或文件共享策略变更，Hy后继诊断另线，不纳本批。
- **其余本轮进展已核。** 共享302/1318/28/161、Windows格式/native adapter/包装通过；continuous含练习和Chrome、browser七场景均通过，旧间歇根因未因此解决。Linux本轮按scope skipped，保持实现与CI498体验推荐，不加可选开发。

## 最近已核终态与实际结果

[CI513 / run 40117770129](https://github.com/DingJWuyu/Typelay/actions/runs/40117770129) 于 2026-10-24 17:48:04 UTC 由唯一push触发，attempt1，18:12:12 UTC终态 `failure`。冻结本地 `9b483849a421296cd3a09f3bc3d7546907e0cc56` 对应远端 `b186f8a416d64b64b754facbd00add411cf0a852`、tree `f6abc78b734fb83442b927ee6e10e765fc38fee1`；2文件精确发布、717叶和非强推读回已核。失败步骤为App行为、正式Notepad和Hy设置，不把整体红等同于各链均失败。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。下一候选的静态审查不算本轮运行内容，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core302、Runtime1318、Windows contracts161、Protocol28通过；shared worker168、Python shared29、Linux输入反馈181通过。Windows workspace含NativeFixture完整编译通过，format通过，worker168运行/1平台跳过、browser Python3、Ruff/format133通过；native adapter与72项包装通过，真实WinEvent probe nativeStatus0 | 两条链接修复已有编译证据；不把native/包装受控结果当物理输入或正式部署资格 |
| Windows App 与源准备 | App855过/1失败/19显式未执行；17activation、18setup、首次Wake11、文本snapshot8、编排14、53owned全部通过 | 唯一witness失败见下；旧前台寿命失败分支未自然命中，不为采诊断单独重跑 |
| 隔离有序来源 | SourceWitnessWriterHasNoDormantOrSameTickOrphansAndKeepsActiveHistory通过。OrderedSourceRecordsTheRealInjectedEdgeButDoesNotConsumeOrWrite在line120的witness计数增长断言失败，实际SourceDispatchCompleted已为2 | 尚不能据TRX判same-tick为唯一根因；后半record/eligible、正文/零写/retire未验。物理Tab例NotExecuted，Main/capability0关闭 |
| 隔离真实 Notepad 与回执 | source-message接受/撤回两例、owned同步六例实际runner exit0，hooks/process/input清理全真；App真实Notepad两例、receipt/fence与foreign集成回归通过 | 有序来源新增失败不由旧机械消费者代替；原28秒和输入不变，硬件/UIAccess、IME与部署资格未补齐 |
| 普通人工稿回源 | Windows六回源及foreign/router实际链通过，共享既有间隙保稿、默认拒绝、foreign与cancel/lateACK回归通过 | 不保证本轮命中0前台瞬态；原800ms、稿基线、fresh capture及撤权边界不变 |
| Windows continuous 与浏览器 | continuous全链通过，含原练习首读后的按需链与Chrome cold/new-tab；browser setup与六业务场景全部通过。初读重排和安全诊断已获得本轮运行证据 | 旧1460、导航aborted、capture-failed根因仍未知；不为重现另跑，不加等待重试或扩大采集，相关主动验证排程关闭 |
| 退出证明与后继门 | before-notepad精确两proof门、最终三proof门实际通过；三个各自nonce与run/attempt/head、自然exit0/0/1、active0匹配 | fixture自然退出及清理不等于App普通Quit通过；业务失败仍保红 |
| 正式 Notepad | 自动Tab101ms公开能力否定；编辑依赖not-run；按需1447ms通过，一次长度变化观察后正文、Esc、Direct、Undo完成。退出507961ms在find-notepad-close-dialog耗尽原520秒operation deadline | lastSameProcessDialogMatched=false、lastButtonId7Matched=null、Invoke started/returned=0；新细stage/终态冻结已实跑，具体发现失败原因待证，原selector与520+20不变 |
| Windows Hy | production五阶段通过；Settings前4场景通过，select-argos76605ms public-preparation-failed-runtime。候选hello成功后PublicationUnchanged，原worker已退出、当前仍Hy；后10场景not-run | 失败位于候选hello后的发布阶段，顶层ModelStart标签不是加载失败证据，当次I/O原因仍未知；cleanupErrors为空表示故障清理完成，ordinary-exit未跑，不推导质量或高级scope放行 |
| Linux 产品链 | 本轮full与可选Qt job均按scope skipped。最近CI511完整job success，GTK4/Qt各23项及Hy12阶段通过，新时耗覆盖原GTK4接受链和Qt首wake | 旧拒绝根因仍未知，无新有证据的可执行修复；保持实现及CI498推荐，不另跑或新增可选开发 |

核验入口为 `artifacts/ci513-independent/terminal-evidence-verified.json`，同目录保留完整job日志、共享/Windows TRX、场景原件与发布收据。源码archive866成员、717文件与冻结树逐字/模式一致；18个source原件和5份早TRX与final逐字一致。Windows outer89,323,117字节、108项CRC及SHA已核；内包87,136,592字节、551项CRC、SHA `04eb5d1a0d0ea4905bc7e47f12336ace8b32d908879c6eab727097ae684945b1`，计算release identity `c991ab809e768215beddedacbe168993493d2f340f21d54edf087c441015407c` 与Notepad及两Hy报告实际加载身份相符，未重复解出内包。CI511 Linux原件与CI498推荐证据不变，原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 有序来源失败 / implement_ordered_source_witness，root | 测试/fixture候选已独立合成：同一permit内确认active/B与真实GetTickCount64跨界，保原期限、唯一Tab pair及全部原断言；现有mapping/选定MSG仅安全元数据。作者两轴静态GO，修后未编译或实跑；CI513唯一根因仍未知 | 精确批次获准后核原失败及dormant/same-tick阴性实际结果；不得换票、续期、造时间或重发。完整record/零写/retire与原消费者实跑前，不称来源链修好或授生产资格 |
| Notepad正常退出 / root，diagnose_notepad_normal_exit | 原生owner locator候选已两轴静态GO：从原root的enabled popup取同PID、精确owner、可见可用#32770，再绑定UIA与按钮7，Invoke前复核身份/取消。新增真实窗口回归未跑；不宣称原UIA树是CI513唯一原因 | 整合窄审与精确授权后随就绪批验证；保原输入、按钮7、deadline和cleanup，核locator正反回归及真实Notepad按需/Undo/正常退出。未匹配不等于不存在，不为采证据单独重跑 |
| Hy切回Argos / root | 已核候选hello成功后的PublicationUnchanged；当次I/O原因未知。仅原catch有限诊断另线实现中，未纳本批，没有文件共享策略更改或已证根因修复 | 不等待该诊断阻塞就绪Windows验证；沿原件与自然后续证据定位原因，后10未跑保持未验，不加模型调用或把production hello替代Settings切换 |
| foreground等待首错 / triage_source_setup_ci498 | 清理前寿命诊断已发布，本轮原用例通过、失败分支未验 | 后续自然失败时核一次元数据及原取消/清理，核实后关闭排程；不单独重跑，非原子样本不证明前台根因 |
| Linux体验维护 / root | 保持当前实现与CI498推荐，接收真机/物理输入法反馈，不增加可选开发 | 新实际故障或用户反馈再判断最小必要动作；未知IME保守拒绝，不自动发布stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认和资源合同；新增产品故障或实质新假设才安排最小验证，与Windows独立推进 | 生命周期、质量、高级scope与许可各凭自身证据；不扩大候选、推理预算或公开范围 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；隔离有序候选已有部分Windows实跑证据，但新增来源拒绝场景仍失败，正式资格未建立，按[正向接受剩余条件](../platforms/windows/development.md#正向接受的剩余条件)推进。新策略实际验证前保持当前严格边界及 Main/capability0，不变更 OS 权限、注册或部署。

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
