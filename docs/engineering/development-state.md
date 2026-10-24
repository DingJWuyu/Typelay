# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 20:52 UTC** 按 CI516 完整终态与原件更新；通过、真实失败和未运行分别记录。

- **新前台准备有一个确定的测试构造错误。** Activation17均在无Content窗口的ContentRendered等待耗尽30秒，未到Activate或源导航；空Content不触发该事件。补固定非空TextBlock的最小修正已双轴静态GO，修后前台资格及原17业务仍待Windows实跑，不改预算或把此原因套到其他失败。
- **其余失败分开处理。** App843过/43失败/21未执行；FirstWake/WakeCapture另24例在注入前被同一Terminal遮挡，Setup正例1例在点击后焦点等待超时，均未定位根因。新F7同键控制例已记录四个事件，但witness poison为1而非0；后续记录身份/控制断言未执行，不能凭前半链放行。
- **本轮后段因原预算门未运行。** continuous全部通过，随后剩3417秒低于package chain固定要求3600秒，预算准入失败；打包、正式browser/Notepad、aggregate退出门及两Hy链均skipped。没有新产品ZIP，也不是新Notepad/Hy业务失败或已证泄漏，不延长75分钟job或后段预算。
- **最近正式Notepad与Hy证据仍是CI515。** Notepad按需正文/Esc/Direct/原生Undo及普通退出实际通过，自动Tab公开能力否定仍保留总exit1；Hy Settings15与production五阶段通过。CI516未重验这些正式链，旧Hy I/O、遮挡及焦点间歇原因不称已根治。
- **编译和受控来源验证有进展但不等于部署。** v6/49384完整编译、Contracts166、原source2/owned6、Tab/F7注入拒绝回归通过；物理Tab/F7/F24均未执行。Main/capability0不变，物理/UIAccess、真实IME与部署资格仍缺。Linux按scope skipped，推荐仍CI498。

## 最近已核终态与实际结果

[CI516 / run 40129359917](https://github.com/DingJWuyu/Typelay/actions/runs/40129359917) 于 2026-10-24 20:20:46 UTC 由唯一push触发，attempt1，20:39:06 UTC终态 `failure`。冻结本地 `23ee6389255abda2a6a23d22a3783af537c2b5a1` 对应远端 `4913847573f7913c7ed0da0e7ff4f3c169c7e80c`、tree `7511689df2ac517e1451d20bacc72604a175d645`；22文件精确发布、718叶和非强推读回已核。失败步骤为App行为与后段预算准入，不把整体红等同于每条业务链失败。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。候选静态审查不算实跑，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与native | Core302、Runtime1318、Windows contracts166、Protocol28在shared与Windows均通过；shared worker168、Python shared29、Linux输入反馈181通过。Windows完整编译、format、worker168运行/1平台跳过、browser Python3、Ruff/format133通过；native受控16项通过 | 本轮产品包装跳过，不沿用旧72项包装结果冒称新包通过；受控native不代替物理输入或部署 |
| Windows App 与准备 | App843过/43失败/21未执行。Activation17在ContentRendered等待失败；FirstWake12与WakeCapture12在SourceClickObscured拒绝；Setup18项中17过、foreground正例点击后焦点等待失败 | 24例遮挡原件同Terminal PID4056/HWND262230，创建时间早于run与testhost；不证明创建者或与Setup同因。修正空窗口只针对17项，不降低命中、身份或焦点门 |
| 隔离有序来源 | Tab/F7注入边缘拒绝两例与SourceWitnessWriter历史回归通过；Broker12、Acceptance11通过。新同键控制例在四记录计数通过后，poison预期0实际1失败 | 后续逐记录身份和控制断言未跑；当前原因未知，先有限对账，不放宽poison。物理Tab/F7/F24三例NotExecuted，软件注入拒绝不证明物理/UIAccess正向 |
| 隔离真实 Notepad 与回执 | source-message接受/撤回两例、owned同步六例runner exit0，hooks/process/input清理全真；App真实Notepad接受/撤回两例、receipt/fence及foreign回归通过，53owned与编排14、snapshot8通过 | 原28秒与输入不变；这些隔离消费者不开放正式Main/capability0，也不代替本轮未运行的shipped Notepad链 |
| 普通人工稿回源 | Windows六回源及foreign/router实际链通过，共享既有间隙保稿、默认拒绝、foreign与cancel/lateACK回归通过 | 不保证本轮命中0前台瞬态；原800ms、稿基线、fresh capture及撤权边界不变 |
| continuous 与浏览器 | continuous全链通过，含练习按需、Chrome cold/new-tab和原控制键；其own consumer原件记录natural exit0、kernel signaled、active0，与本run/attempt/head一致 | 后继正式browser未跑。continuous nonce未在后继门日志取得独立expected值，不能声称最终nonce校验或三proof门通过；旧1460/导航/capture-failed间歇原因仍未知 |
| 后段准入与产物 | 原75分钟job已耗1083秒、剩3417秒，低于固定3600秒而拒绝。步骤28至36全部skipped，最终仅上传小原件 | 本轮没有产品ZIP、正式Notepad/Hy结果或aggregate退出门执行；不把缺后继proof说成泄漏，不延预算或重跑刷绿 |
| 正式 Notepad 最近基线 | CI515自动Tab102ms公开能力否定、编辑依赖not-run；按需1357ms正文/Esc/Direct/Undo和普通退出814ms通过，sourceNormalExit/appNormalExit/modelWorkersExited/originalFileUnchanged全true。CI516 App丢弃及4 selector/6 owner回归通过 | 已关闭按钮定位与普通退出修复排程；本轮正式链skipped，不冒新验证。未知/歧义仍拒绝，首Invoke后只观察退出，原520+20及cleanup不变 |
| Windows Hy 最近基线 | CI515 production五阶段与Settings15场景通过；CI516存储Unchanged7、Unconfirmed2及WPF有限投影/清除2回归通过，正式两Hy链skipped | 新诊断真实安装故障未命中；CI513候选hello后的PublicationUnchanged原因仍未知。AccessDenied只分类系统结果，不指认锁/读取者，不推导质量或高级scope放行 |
| Linux 产品链 | 本轮full与可选Qt job均按scope skipped。最近CI511完整job success，GTK4/Qt各23项及Hy12阶段通过，新时耗覆盖原GTK4接受链和Qt首wake | 旧拒绝根因仍未知，无新有证据的可执行修复；保持实现及CI498推荐，不另跑或新增可选开发 |

核验入口为 `artifacts/ci516-independent/terminal-evidence-verified.json`，同目录保留完整job日志、共享/Windows TRX、场景原件与发布收据。源码archive867成员、718文件与冻结树逐字/模式一致；18个source原件和5份早TRX与final逐字一致。四份artifact均核metadata大小/SHA与完整CRC；最终Windows outer2,285,011字节、87项，SHA `24db9b95aca427e8760678b22ac2b476d77767e643e82328d117c6bfc5042091`，无内产品包。CI515正式Notepad/Hy与包身份、CI511 Linux及CI498推荐原件保持；原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 源激活测试前置 / triage_source_setup_ci498，root | 固定非空Content窄修已双轴静态GO，保持原事件等待、单次Activate/源导航及关闭后精确焦点观察。新C#/Windows未跑，不等待更大功能片才验证 | 原17项完成准备和业务，既有遮挡/焦点拒绝与清理保持后关闭；失败按新实际阶段处理，不能以渲染通过冒称前台资格成立 |
| 隔离接受功能键 / implement_ordered_function_key_acceptance，root | v6主键及全部消费者已编译；真实同键控制回归停在poison。现有四记录与Ctrl/F23源MSG的有限对账已实现并获双轴静态GO，新C#/Windows未跑；它不是已证生产根因或已验修复。物理验证包另线准备，不作为本窄修发布前置 | 原poison/事件身份/控制行为整链通过前不称该回归修好。保持原来源、IME、期限与Main/capability0；受控软件证据不冒物理或部署资格 |
| 其余源准备失败 / root | 分开保留24例注入前遮挡与1例点击后失焦事实；本轮原件不足以把它们归为一个根因，不推广尚未成功的导航前置 | 有实际定位证据再裁定最小修复或复用既有点击回执；不增加激活绕行、点击重试或预算，不为重现单独跑CI |
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
