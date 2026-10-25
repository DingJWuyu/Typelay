# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-25 04:01 UTC** 按 CI523 完整终态与本地集成状态更新；通过、真实失败和未运行分别记录。

- **源导航17例与continuous实际通过。** App890过/0失败/21未执行；采用源已在前台则直接准备的候选后，17例全过；未统计各分支命中，也未倒推出旧覆盖窗身份。练习关闭前保存、关闭保留后续设置与重置进度本轮均过；旧设置保存异常未复现，有限内层错误观察没有命中，不称根因已修。
- **子进程原所有权场景与新退出码对照通过。** `GenerationStaysProtectedUntilEveryOwnerHasConfirmedExit` 两个owner都取得退出证明，并完成排他回收与清理；已有原生根句柄的活进程/null、自然退出23及Dispose后拒绝对照也通过。旧first.assert-alive早退未复现，故障退出码观察没有命中，原因仍未知，不称根因已修。
- **搬迁F7在后续ordered-edge准备时到期，CI521物理交付推荐保持。** 原套件Tab/F7两阴性完整过；搬迁首例F7通过初始真实提示、同permit及未过期检查，却在后续ordered-edge等待检查时晚4ms，尚未发送该键；次例Tab通过。没有各轮或映射打开耗时，不能把全部时差归因于轮询，更不能放宽期限或换grant掩盖；B 是 uptime，permit 是 interrupt time，不跨域相减。当前仍使用[已核CI521独立包](https://github.com/DingJWuyu/Typelay/actions/runs/40143179984/artifacts/12580160695)，物理按键从未执行，正式Main/cap0不开放。
- **正式Notepad独立链、浏览器与两Hy链通过。** Notepad按需单次wake/Esc/Direct/原生Undo1253ms、普通退出798ms通过；自动Tab仍在输入前公开能力否定，场景80ms失败，编辑依赖not-run，总exit1保留。browser7、Hy Settings15与production五阶段、三个消费者自然退出证明及退出门通过。Linux产品链按范围跳过，推荐仍为CI498。

## 最近已核终态与实际结果

[CI523 / run 40158384733](https://github.com/DingJWuyu/Typelay/actions/runs/40158384733) 于 2026-10-25 03:29:16 UTC 由唯一 push 触发，attempt1，03:49:56 UTC终态 `failure`。冻结本地 `76ed013cc37c247d51a9f6cd1c4afd5ecaf41bfa` 对应远端 `48837fc1c3902283e8527f6645c053517c4cb446`、tree `cec55b3d3218916f68d4b1db20868569168d365c`；5文件精确发布、732叶与非强推读回已核。失败步骤仅为正式Notepad自动接受及物理包搬迁阴性；App没有失败不等于21个未执行场景已完成，物理ZIP未发布。

上述结果属于原 source v6/49384 的冻结源码，不包括当前本地 Broker v4/source v7 的 Ctrl 切片或后继准备压缩候选；新候选的 .NET/Windows/physical 均未运行。后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。候选静态审查不算实跑，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与native | Core302、Runtime1318、Windows contracts166、Protocol28在shared与Windows均通过；shared worker168、Python shared29、Linux输入反馈181通过。Windows完整编译、format、worker168运行/1平台跳过、browser Python3、Ruff通过；native受控16项及包装78项通过 | 受控native、编译和产品包搬迁不代替物理输入或部署 |
| Windows App 与准备 | App890过/0失败/21未执行；Activation17、FirstWake12、Setup18全过，鼠标接收72.8ms通过。原代次所有权189.5ms通过，first/secondExitProven均true；新真实根退出码对照69.6ms通过 | 旧早退没有复现，不能推导其退出码或根因；根退出码不替代job/stderr/代次退出证明。旧导航覆盖窗身份与鼠标超时原因仍未知 |
| 隔离真实runtime与有序来源 | 默认factory两例、逐目标ACK投影、原同键F7控制、SourceWitnessWriter历史、Broker12、Acceptance11及原Tab/F7两阴性完整通过，包含精确拒绝回执、OriginUntrusted、零完成/零写与清理 | 搬迁F7另有后续ordered-edge准备到期失败，不能用原套件通过覆盖；物理Tab/F7/F24三例仍NotExecuted |
| 隔离真实Notepad与回执 | source-message接受/撤回两例、owned同步六例runner0，hooks/process/input清理全真；App真实Notepad接受/撤回两例、receipt/fence及foreign回归通过，53owned与编排14、snapshot8通过 | 原28秒与输入不变；隔离消费者不开放正式Main/capability0 |
| 普通人工稿回源 | Windows六回源及foreign/router实际链通过，共享既有间隙保稿、默认拒绝、foreign与cancel/lateACK回归通过 | 不保证本轮命中0前台瞬态；原800ms、稿基线、fresh capture及撤权边界不变 |
| continuous 与浏览器 | continuous完整通过，练习resetProgress、closePreservedLaterSettings等八项均true，有限innerExceptions为空且未截断。独立browser7通过；前置两proof与最终三proof门成功，三个消费者run/attempt/head及nonce匹配，natural exit0/0/1、kernel signaled与active0 | 旧关闭前SettingsStore.Update异常未复现，本轮无内层故障身份；保留失败时有限观察，不据此修改生产保存逻辑 |
| 正式Notepad | 自动Tab场景80ms在接受键前明确preview-only且接受cue为0，编辑依赖not-run；按需1253ms单wake/Esc/单Direct/原生Undo通过，普通退出798ms，sourceNormalExit/appNormalExit/modelWorkersExited/originalFileUnchanged全true | 原自动接受能力仍未开放，不将总exit1抹绿或称所有普通输入框已支持 |
| Windows Hy | production五阶段与Settings15场景通过，包括真实普通源中英预览与正常退出712ms；存储Unchanged7、Unconfirmed2及WPF有限投影/清除2回归通过 | 有限诊断本轮未命中安装故障；旧候选hello后的PublicationUnchanged原因仍未知。不推导质量或高级scope放行 |
| 隔离物理Tab包 | 原两阴性准入及唯一manual Tab发现1通过；搬迁F7先跑失败、Tab后跑通过。F7已通过初始实际提示、同permit及到期门，在ordered-edge准备谓词行256发现now=1338069、expires=1338065、B=1337953，晚4ms，仍未调用Key | 无初始失败时序JSON，不能区分映射开销、首轮或后续轮询耗时；B 与 now/expires 分属 uptime/interrupt time，不相减，不称为初始cue读取到期。物理ZIP未发布，保持CI521已核包推荐；原100ms、30秒、同grant与输入次数不变 |
| Linux产品链 | 本轮按Windows变更范围skipped；不是本轮通过。最近完整链CI518通过，体验推荐保持CI498 | 无新Linux包验收，不因共享检查绿升级推荐或新增可选开发 |

核验入口为 `artifacts/ci523-independent/terminal-evidence-verified.json`，同目录保留完整Windows与共享job日志、TRX、场景原件及发布收据。源码archive889成员、732文件与冻结树逐字/模式一致；18个source原件与final逐字一致。三份已下载artifact均核metadata大小/SHA与完整CRC。Windows外ZIP89,306,492字节、120项，内产品包87,140,137字节、551文件，SHA `5156b55543eddcb2d7d682e57b9dd46ac13fb00a8706513352bcde7c27c90a44`；computed releaseIdentity与Notepad/Hy两链三份正式加载报告一致，未另存重复内包。CI521物理推荐的独立身份与操作仍见[验证包说明](../platforms/windows/README.md#隔离物理-tab-验证包不是产品体验包)。原始日志不复制进本页。

## 当前责任与下一动作

root负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 隔离 Ctrl 源接受 / integrate_windows_next_candidate，root | 完整 Ctrl 切片及实际 RenderCount 旧读回归已在独立候选集成：Broker v4/source v7 显式 observer 与 Ctrl+F1-F24，原 FIFO/ManualAcceptanceModifier复用。native/source 与 managed 两轴静态 GO，C/Python 检查已跑；新 .NET 编译、managed/runtime 与 Windows/physical 均未跑 | 下一动作是在授权渠道对精确候选执行 CI 验证；observer不呈现接受，不借Ctrl前票/旧读/延长期限，正式Main/cap0不变。CI523 的 v6 结果不外推至本候选 |
| 搬迁输入前到期 / connect_controlled_windows_runtime，root | 真实WPF提示事件等待已在 CI523 的 v6 实跑；后继准备压缩候选静态 GO，输入前仍持有原 nonce MMF handle，仅把 source diagnostic view 与固定 version/key 断言后移到输入派发后，补有限同域阶段检查点。集成保留 v7/49416，尚未实跑或证明根因 | 下一动作与 Ctrl 候选一起在授权 CI 验证；witness/FIFO、cue、焦点、原100ms、同grant、唯一键对与终态门不变。不把 B uptime 与 permit interrupt time 相减，不据现有失败猜首次谓词或哪项 mapping 耗时；CI521物理包推荐保持 |
| 已过回归维护 / root | CI523 的原有子进程所有权场景及退出码对照、源导航17与continuous均已实际通过；保留有限失败观察供同边界自然失败时使用，旧早退、覆盖窗身份和保存异常内层原因仍未知 | 不因未复现新增专门CI；新实际故障再分派最小诊断，不以通过冒称旧原因已证 |
| 隔离物理Tab交付 / root | 提供CI521已核独立包；产品开发预览仍独立推荐CI518 | 用户实际单次Tab取得1Passed才算该例物理接受；其余UIAccess/IME/正式部署各凭自身证据，软件阴性/包交付不代替物理按键 |
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
