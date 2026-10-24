# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 22:13 UTC** 按 CI518 完整终态与原件更新；通过、真实失败和未运行分别记录。

- **同键控制与测试对象链接修正已实际闭合。** 原F7控制回归完成原poison、事件身份与控制行为断言；冻结有效Ctrl/F23 scan修正通过，生产来源门未放宽。局部static快照也经Windows freestanding回归与WinEvent实跑通过，包装78项全过，关闭这两项修复排程。
- **当前App17项失败发生在新的准备阶段。** App869过/17失败/21未执行，全部为Activation的 `navigation.Activate()` 返回false；非空Content的ContentRendered已完成，精确前台检查与source导航尚未到达。不是先前30秒空Content等待，也不能凭此认定外部terminal身份或创建者。FirstWake12、WakeCapture13、Setup18本轮通过；前台资格原因仍待证。 后继准备只要求own HWND非零及实际前台匹配，未匹配才请求一次Activate；已双轴静态GO，修后Windows未跑，不称本轮根因已证。
- **正式Notepad独立链与两Hy链本轮均已验证。** Notepad单次wake、Esc、Direct与原生Undo2068ms通过，普通退出644ms通过；自动Tab仍在发接受键前58ms公开能力否定、编辑依赖not-run，总exit1不改。Hy Settings15与production五阶段通过，三个消费者的各自自然退出证明及最终退出门均通过，旧间歇原因不称根治。
- **真实runtime受控组装已准备，正式App仍关闭。** 内部显式开关默认false且要求隔离broker；既有Tab/F7阴性迁入真实factory、Router、Dispatcher与WPF，逐目标ACK投影及默认关闭回归分层。两轴静态GO，C#/Windows未跑；Main/CLI/automation、旧开关、cap0与物理包selector不变。
- **隔离物理Tab验证包已交付，物理按键尚未验证。** [本轮独立ZIP](https://github.com/DingJWuyu/Typelay/actions/runs/40134731709/artifacts/12576604148)包含三个framework-dependent输出及必要标记/许可/说明。Windows真实搬迁后原阴性两例2/2 Passed、唯一manual Tab发现成功，完整包身份已核；不是物理输入通过，也不开放Main/capability0或UIAccess/IME/正式部署。Linux完整链本轮通过，体验推荐保持CI498。

## 最近已核终态与实际结果

[CI518 / run 40134731709](https://github.com/DingJWuyu/Typelay/actions/runs/40134731709) 于 2026-10-24 21:33:16 UTC 由唯一push触发，attempt1，21:56:05 UTC终态 `failure`。冻结本地 `48d5b677b361747076fe8173e553d18309804b06` 对应远端 `c730092ec23c0eb4ec57b0c69e6ac089b3853b20`、tree `666afcf00e1127a34b0d3550fb13ab33bf113cbb`；21文件精确发布、731叶和非强推读回已核。失败步骤为App准备与正式Notepad自动接受，不把整体红等同于每条业务链失败。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。候选静态审查不算实跑，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与native | Core302、Runtime1318、Windows contracts166、Protocol28在shared与Windows均通过；shared worker168、Python shared29、Linux输入反馈181通过。Windows完整编译、format、worker168运行/1平台跳过、browser Python3、Ruff通过；native受控16项及包装78项通过，包含原freestanding guards与WinEvent | 本轮修复的 `__chkstk` 链接阻断已消除；受控native、编译和搬迁不代替物理输入或部署 |
| Windows App 与准备 | App869过/17失败/21未执行；17例均在ContentRendered之后的 `navigation.Activate()` 返回false，后继exact foreground/source导航未执行。FirstWake12、WakeCapture13、Setup18全过 | 本轮失败缺实际前台归属证据，原因仍未知；不回退为已修的空Content等待，也不把先前遮挡/点击后失焦归因于同一根因 |
| 隔离有序来源 | 原同键F7控制例完整通过，Tab/F7注入边缘拒绝两例、SourceWitnessWriter历史、Broker12与Acceptance11通过；一次性源MSG诊断已退役，共享观察保留 | 物理Tab/F7/F24三例在自动套件中NotExecuted；软件注入拒绝与同键行为不证明物理/UIAccess正向 |
| 隔离真实 Notepad 与回执 | source-message接受/撤回两例、owned同步六例runner exit0，hooks/process/input清理全真；App真实Notepad接受/撤回两例、receipt/fence及foreign回归通过，53owned与编排14、snapshot8通过 | 原28秒与输入不变；隔离消费者不开放正式Main/capability0 |
| 普通人工稿回源 | Windows六回源及foreign/router实际链通过，共享既有间隙保稿、默认拒绝、foreign与cancel/lateACK回归通过 | 不保证本轮命中0前台瞬态；原800ms、稿基线、fresh capture及撤权边界不变 |
| continuous 与浏览器 | continuous全链、正式browser7场景通过；前置两proof与最终三proof门均成功。三个消费者同run/attempt/head、各自nonce与原log匹配，natural exit0/0/1、kernel signaled与active0 | Notepad业务exit1与自然退出证明分开；旧1460/导航/capture-failed间歇原因仍未知 |
| 正式 Notepad | 自动Tab58ms明确preview-only且接受cue为0，在发接受键前失败；编辑依赖not-run。独立按需2068ms单wake/Esc/单Direct/原生Undo通过，普通退出644ms，sourceNormalExit/appNormalExit/modelWorkersExited/originalFileUnchanged全true | typed等值等待本轮记录一次末长度变动后完成，保原输入/期限；原自动接受能力仍未开放，不将总exit1抹绿或称所有普通输入框已支持 |
| Windows Hy | production五阶段与Settings15场景通过，包括真实普通源中英预览与正常退出663ms；存储Unchanged7、Unconfirmed2及WPF有限投影/清除2回归通过 | 有限诊断本轮未命中安装故障；CI513候选hello后的PublicationUnchanged原因仍未知。不推导质量或高级scope放行 |
| 隔离物理Tab包 | 原套件Tab/F7阴性准入通过；含空格和中文的真实搬迁路径上再次2/2 Passed、0skip、runner0；唯一manual Tab发现1条且无Skip。三份NativeInput同hash、三个.NET10 framework-dependent输出、原README/许可/lock与冻结源码一致 | CI没有执行物理Tab；须按包内说明由用户实际取得1Passed才建立该例物理证据，F键/UIAccess/真实IME另有边界 |
| Linux 产品链 | full job success；完整日志记录Settings10、教程9、GIMP7、browser7、GTK3 21、GTK4 23、Qt23及正式Hy12阶段通过 | 未下载本轮287MB最终ZIP或核新包身份，不据步骤绿升级推荐。旧时耗拒绝根因仍未知，保持实现与CI498推荐，不另跑或新增可选开发 |

核验入口为 `artifacts/ci518-independent/terminal-evidence-verified.json`，同目录保留完整两平台job日志、共享/Windows TRX、场景原件与发布收据。源码archive888成员、731文件与冻结树逐字/模式一致；18个source原件和5份早TRX与final逐字一致。五份已下载artifact均核metadata大小/SHA与完整CRC；Linux仅保存metadata与完整log，不冒称该包已核。Windows产品内包87,139,085字节、551项CRC及SHA `c6d3007b15bb978adc3a10c750e4770763ab5e2f152130e953bcea99cb363f42`已流式核，computed releaseIdentity与Notepad/Hy两链的三份正式加载报告一致。物理验证内ZIP为7,790,003字节、239文件，SHA `ce4164c3ffc27d19196c283740b149759a8616b3aa8ca6621c8c04d6bc21e22c`，BUILD精确对应本run/attempt/head；两内包均未另存重复副本。原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| Windows源导航前置 / triage_source_setup_ci498，root | 按非零own HWND与真实前台后置条件判断准备，已满足不重复请求，否则最多一次Activate；候选双轴静态GO，C#/Windows未跑。原ContentRendered/token/source导航及清理保持 | 原17例实际通过前不称修好；修后通过也不补造CI518的前台身份或根因，不加重试/延时或放宽源身份门 |
| 受控runtime组装 / connect_controlled_windows_runtime，root | 默认关闭的内部factory入口与逐目标ACK投影已双轴静态GO；原Tab/F7阴性迁真实Router/Dispatcher/WPF，新增automation两分支默认关闭及独立投影对照。纯folder与静态闭包已核，C#/Windows未跑 | 实际编译、新default/projection与迁移后两阴性均通过才关闭该片；必须核原grant安装/ACK/可见接受提示后唯一输入、来源拒绝/正常派发、零写和同owner Quiet。正式Main/cap0、100ms/30秒及物理manual不变 |
| 隔离物理Tab交付 / package_physical_tab_validation，root | 独立包已通过Windows搬迁阴性与唯一manual发现，提供同run包及包内说明；自动CI没有代替用户按物理键 | 用户真实单次Tab取得1Passed才算该例物理接受；其余UIAccess/IME/正式部署资格不由构建或软件阴性授予，不为等物理输入重跑CI |
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
