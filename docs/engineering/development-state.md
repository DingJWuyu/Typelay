# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 19:48 UTC** 按 CI514 完整终态、原件及实际包身份更新；通过、真实失败和未运行分别记录。

- **本批两个目标边界都有实际进展。** 原有序来源 injected-edge 场景完整通过，dormant/same-tick/history回归仍过；Notepad owner locator六项真实窗口正反回归通过，正式退出也已匹配原生同进程owner对话框。不是物理/UIAccess正向或正式自动接受的通过证据。
- **Notepad正常退出仍未完成，但卡点已后移到按钮。** 按需F9、Esc保源、Direct与原生Undo1098ms通过；自动Tab50ms公开能力否定，编辑依赖未跑。退出510565ms在 `await-notepad-discard-button-or-exit` 耗尽原deadline：同进程对话框=true、按钮7=false、Invoke计数0；不能把已匹配对话框称普通Quit成功。
- **App17个失败全部是既有源准备终端遮挡。** App845过/17失败/19未执行。17条SourceClickObscured同指terminal PID2348/window131586，进程出生早于本run与App observer；这补齐了创建时间证据，但不能据非原子后采元数据确定创建者、父进程或前台根因。
- **Hy本轮完整通过，旧发布失败没有被解释。** Settings15场景、production五阶段通过，含切回Argos、中英预览及normalQuit。CI513的candidate hello后PublicationUnchanged未复现；不能把成功当根因修复，也不能把原ModelStart标签改说worker加载失败。
- **其余链与边界保持。** 共享302/1318/28/161、Windows编译/格式/native adapter/包装、continuous与browser均通过；原source-message两例及owned六例清理全真。Main/capability0关闭，Linux本轮按scope skipped并保持CI498推荐；没有模型或文件共享策略变更。

## 最近已核终态与实际结果

[CI514 / run 40123888197](https://github.com/DingJWuyu/Typelay/actions/runs/40123888197) 于 2026-10-24 19:07:59 UTC 由唯一push触发，attempt1，19:31:38 UTC终态 `failure`。冻结本地 `9644e3758263c8529d42db4e090a895a881dcf64` 对应远端 `49e3a5a536bdf199c92c7b07f490e40e74e9c94c`、tree `b72a601f88a1c927d4f001701bbf041809f17b1f`；6文件精确发布、718叶和非强推读回已核。失败步骤为App行为与正式Notepad，不把整体红等同于本批目标或全部业务均失败。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。候选静态审查不算实跑，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core302、Runtime1318、Windows contracts161、Protocol28通过；shared worker168、Python shared29、Linux输入反馈181通过。Windows完整编译、format、worker168运行/1平台跳过、browser Python3、Ruff/format133通过；native adapter与72项包装通过，真实WinEvent probe nativeStatus0 | 不把受控native/包装结果当物理输入或正式部署资格 |
| Windows App 与源准备 | App845过/17失败/19显式未执行；17个NativeSourceActivation均在SourceClickObscured准备拒绝。18setup、首次Wake11、snapshot8、编排14、53owned通过 | 17条均有geometry=same、terminal创建时间19:03:15.053，早于本run19:07:59与observer19:09:41.861；未核creator/parent，仍不能断定前台原因 |
| 隔离有序来源 | OrderedSourceRecordsTheRealInjectedEdgeButDoesNotConsumeOrWrite完整Passed，合法B前置、实际记录/eligible、原消息旁路、原文/零写/retire断言均已越过；SourceWitnessWriter初始化/历史回归通过 | 本轮通过不证明CI513唯一原因就是same-tick。手动物理Tab例NotExecuted，软件注入拒绝不代替物理/UIAccess正向、IME或部署，Main/capability0关闭 |
| 隔离真实 Notepad 与回执 | source-message接受/撤回两例、owned同步六例实际runner exit0且hooks/process/input清理全真；App真实Notepad两例、receipt/fence、foreign集成回归通过 | 原28秒与输入不变，旧机械消费者不授新来源资格；activation准备失败在其业务动作之前，不能称17项后续断言失败 |
| 普通人工稿回源 | Windows六回源及foreign/router实际链通过，共享既有间隙保稿、默认拒绝、foreign与cancel/lateACK回归通过 | 不保证本轮命中0前台瞬态；原800ms、稿基线、fresh capture及撤权边界不变 |
| Windows continuous 与浏览器 | continuous全链通过，含练习按需与Chrome cold/new-tab；browser setup及六业务场景全部通过 | 旧1460、导航aborted、capture-failed根因仍未知；不为重现另跑，不加等待重试或扩大采集，已完成主动验证排程不保留 |
| 退出证明与后继门 | before-notepad精确两proof门、最终三proof门实际通过；三个各自nonce与run/attempt/head、自然exit0/0/1、active0匹配 | fixture自然退出及清理不等于App普通Quit通过；业务失败仍保红 |
| 正式 Notepad | 自动Tab50ms公开能力否定、编辑依赖not-run；按需1098ms通过，一次长度变化观察后正文/Esc/Direct/Undo完成。owner locator六项正反回归通过；退出510565ms在await-notepad-discard-button-or-exit耗尽原520秒operation deadline | lastSameProcessDialogMatched=true、lastButtonId7Matched=false、Invoke started/returned=0。原对话框定位边界已越过，按钮查找仍待因；不推断按钮不存在，520+20与cleanup未改 |
| Windows Hy | production五阶段、Settings15场景全部通过；select-argos38967ms，ordinary-exit876ms且normalQuit/exit0/全部worker退出，实际加载身份相符 | 旧CI513 candidate hello后的PublicationUnchanged未复现、I/O原因未证；本轮不含新catch诊断，不推导质量或高级scope放行 |
| Linux 产品链 | 本轮full与可选Qt job均按scope skipped。最近CI511完整job success，GTK4/Qt各23项及Hy12阶段通过，新时耗覆盖原GTK4接受链和Qt首wake | 旧拒绝根因仍未知，无新有证据的可执行修复；保持实现及CI498推荐，不另跑或新增可选开发 |

核验入口为 `artifacts/ci514-independent/terminal-evidence-verified.json`，同目录保留完整job日志、共享/Windows TRX、场景原件与发布收据。源码archive867成员、718文件与冻结树逐字/模式一致；18个source原件和5份早TRX与final逐字一致。Windows outer89,370,757字节、112项CRC及SHA已核；内包87,136,594字节、551项CRC、SHA `9f2994b78f4dc5b43839ab2550aa07a15220c2f026632ba3715151999c420f52`，计算release identity `01e66b5df5bcfbc78c8863ac9408108f4b285629184f1585c53fba1903ff9fe2` 与Notepad及两Hy报告实际加载身份相符，未重复解出内包。CI511 Linux原件与CI498推荐证据不变，原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| Notepad正常退出 / root，diagnose_notepad_normal_exit | CI514已匹配精确owner对话框，但按钮7未匹配且未Invoke。独立候选将原IDNO选择限定为唯一 `7` 或 `CommandButton_7`，保owner/PID与Invoke前复核，新增真实控件正反及真实Notepad丢弃未保存合成改动回归；首Invoke后仅等退出的单次guard已补齐，最终两轴静态GO，编译与Windows未跑 | 随正常批次验证；歧义仍拒绝，不按标题或版本回退。原按需/Undo、精确一次丢弃及普通退出分别实跑通过前不称修复，不为采证据单独重跑 |
| 源激活测试准备 / triage_source_setup_ci498，root | 17条旧终端出生证据不识别创建者或前台原因。候选已双轴静态GO：仅Activation原用例在捕获/permit之前显式导航自有窗口并核可见，再执行原单次点击Prepare；普通Prepare及遮挡拒绝保持，编译与Windows未跑 | 原17项在真实Windows完成准备及后续业务断言、既有遮挡/焦点拒绝与清理仍过后关闭本候选排程；不把测试导航通过称终端创建根因已解决，不增加生产焦点权限或重跑采样 |
| Hy切回Argos诊断 / diagnose_windows_model_switch，root | 有限诊断候选已双轴静态GO：原发布catch保留首错操作与有限错误分类，经既有Settings诊断投影；补Confirmed/Unchanged/Unconfirmed与公开投影回归。编译/Windows未跑；不改文件共享、发布/恢复原语、结果或期限 | 随同行批次核真实存储及WPF回归；自然失败时核原有限诊断，未命中只记实机失败分支未验，不为采证据另跑。CI514成功仍不解释CI513 candidate hello后的PublicationUnchanged，AccessDenied不指认锁或读取者 |
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
