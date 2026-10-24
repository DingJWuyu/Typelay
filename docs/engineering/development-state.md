# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 16:36 UTC** 按 CI510 完整终态原件与下一批已审组件更新；本地候选与实跑结果分开记录。

- **CI510 整体失败，Windows App 849 通过、0 失败、16 项显式研究未执行。** 独立 Notepad 的两份退出证明准入和最终三份证明均实际通过，前序 continuous 业务失败仍保红；原独占数据目录、身份、nonce 和预算未放宽。
- **真实 Notepad 的首次唤醒、取消、直接替换及原生 Undo 首次完整通过这条部署场景。** 单 F9、Esc 保源、单 Direct 和 Undo 于 1503ms 内完成；typed 等值等待实际遇到一次长度变化后成功。自动 Tab 仍按公开能力否定失败，编辑依赖未跑。随后退出场景耗尽原 operation deadline；旧 stage 未细分后续关闭/对话框等待，具体卡点待证，不能说原文复核卡住或正常 Quit 已通过。
- **本轮 continuous 首错是练习初始源读取超时。** Send/WM_GETTEXTLENGTH/1460，尚无目标绑定与练习进度；Chrome 冷页、新页自动/按需以及 browser 七场景本轮均通过，不能解释旧 capture-failed 或导航 aborted 的根因。练习候选仅把同一次基线读移到既有 native ready 前置之后，原输入、100ms 单消息与90秒总预算不变，真实 Windows 尚未验证。
- **后继来源切片只提供拒绝，不是正式自动接受。** 来源分类未知或不可信拒绝，类别合格但旧键新鲜性未证仍拒绝，并收紧实际嵌套 REMOVE 的单调撤权；与 Chrome 有界安全诊断、练习观察顺序、Notepad退出留证及Linux时耗观察一同待组合审查和实跑。UIAccess 产品选择已确认，Main/capability0 不变；正向来源、真实 IME、部署及人工稿异常交互仍各有边界。
- **Windows Hy 的15场景和 production五阶段通过；Linux Hy12阶段通过，但 Linux GTK4 multiline 与 Qt mode-switch 两场景失败。** Linux 推荐保持完整核验的 CI498；模型采用、默认和资源合同不变。

## 最近已核终态与实际结果

[CI510 / run 40109457874](https://github.com/DingJWuyu/Typelay/actions/runs/40109457874) 于 2026-10-24 15:59:20 UTC 由唯一 push 触发，attempt 1，16:28:13 UTC 终态 `failure`。冻结本地 `5ef7723485a6fd7808a8001be4a49f36a66797a8` 对应远端 `57576aa621a4e58f551f96dc9d843076313b5655`、tree `8b358d1a4393e368976f29ca719f65c4d762589d`；8 文件精确发布、同分支非强推及源码身份已核。下述来源拒绝、Chrome 诊断和练习观察顺序组合尚未发布，不算本轮运行内容。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行分别记录；一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core 302、Runtime 1314、Windows contracts 144、Protocol 28 通过；shared worker 168、Python shared 29、Linux 输入反馈 181 通过。Windows build、格式通过，worker 167 过、1 跳过，browser profile Python 3 项、Ruff 与 133 文件格式通过；包装 72 项通过，含退出证明 CLI、native foreground guards 与 WinEvent | 共享或受控回归不代替真实平台业务验收 |
| Windows App 与源准备 | 849 通过、0 失败、16 项显式研究未执行。首次 Wake 11、PublicNoticeSnapshot 8、Notepad 编排/Save 14、NativeSourceActivation 17、source setup 18 项通过；foreground 首次观察 307ms 匹配源 root/field | 清理前寿命诊断失败分支未触发；不据此解释旧焦点或空 Snapshot 根因，不为它单独重跑 |
| 隔离真实 Notepad 消费者 | source-message Broker/adapter 接受与撤回两例完整通过；一次 Verified/零正文回执旁路、Undo、焦点、零 LL activation/二次 Write、Quiet 与清理依各自原断言核验 | 原28秒和按键不变；受控输入不补齐严格来源、真实 IME 或第三方部署资格，不等于正式产品接受开放 |
| 普通人工稿回源 | 原六分支及已收到 foreign 前台后不得被迟到 ACK 恢复的 router 集成阴性通过；共享7项间隙保稿、默认拒绝、foreign 与 cancel/late ACK 回归通过 | 未证明本轮再次命中真实0前台瞬态；原800ms、原稿基线、fresh capture 和撤权边界不变 |
| 隔离 App 回执与原生源 | SourceMessageReceipt、拒绝派发、late ACK、expired installation、Unknown fence、Owned Ctrl/Tab 和5项 StartupPeers 通过；owned六例、source两例及现役 native adapter 通过，hook/进程/输入清理完整 | 来源分类自动采集保持退役；局部回执不授予普通生产接受或物理 IME 资格 |
| Windows continuous | owned 接受/编辑/回源、普通 control、continuousPreview、direct、Chrome 冷页和新页自动/按需通过。onDemandPractice 在 practice-current-mode-entry 失败：初始读706263–706367ms，Send/WM_GETTEXTLENGTH(14)/1460；进程与窗口身份仍匹配，progressAvailable/targetBound=false | 这是已观察超时，不证明底层线程为何停顿。候选把完整初始读块移到已有 DirectReplace 与 PracticeProgress.Target 前置之后、可见 F9 ready cue 之前；不加读、sleep、重试或期限，尚未 Windows 实跑 |
| 浏览器合作输入 | setup 与六业务场景均通过；包含按需替换、原字段后台完成、移除连接无写和提交计数正控 | 本轮未复现旧导航 aborted/metadata 问题，不称根因已修；有界诊断候选不改变 CDP 请求、事件订阅、重试或预算 |
| 退出证明与后继门 | before-notepad 两证明门实际通过，continuous 失败但已退出仍可进入原 Notepad；最终 all 三证明门通过。continuous/browser/Notepad 同 run/attempt/head、各自 nonce 已核，natural exit1/0/1、active0、kernel-signaled | 两证明只授权独立测试准入，不把前序红洗绿；fixture 自然退出及其清理不等于被测 App 的 ordinary-exit 场景成功。当前准入接线排程关闭 |
| 正式 Notepad | 公开自动预览通过；自动 Tab 在126ms明确否定，不发接受键，编辑依赖 not-run。独立按需链1503ms passed：一次 F9、Esc 保源、一次 Direct、nativeUndoRestoredSource=true；typed 等值等待记录一次 LengthChanged(9→10→32)后成功。ordinary-exit 506973ms失败并耗尽原520秒 operation deadline，cleanupErrors=[] | 旧 stage verify-original-source-before-exit 同时覆盖后续 WM_CLOSE、进程退出/保存对话框等待，不能据字段定位卡点；正常 Quit 尚无通过证据。原输入、520+20预算和 Main/capability0 不变 |
| Windows Hy | production五阶段和 Settings15场景均通过，包含中英确认/取消、同代冷启、双向普通预览、切回、移除及 ordinary-exit；加载身份与原包相同，清理无错误 | 不解释旧 UIA 间歇根因；真实模型 smoke 不代替质量或高级 scope 放行 |
| Linux 产品链 | setup-settings10、tutorial9、浏览器桌面7、GTK3桌面21、GIMP7项及 Hy12阶段通过。step47 GTK4 的 multiline write 缺 verified receipt，step48 Qt mode-switch 丢 configured acceptance route；完整 Linux job失败，可选 Qt job skipped | 两首错由原 trace 分诊，不延150/100ms或重跑求绿；本轮包未作采用评估，推荐保持 CI498。XTest 不是物理输入，editor snapshot 不是高级 scope 或质量验收 |

核验入口为 `artifacts/ci510-independent/terminal-evidence-verified.json`。同目录保存 `windows-results-verified.json`、`linux-results-verified.json`、`shared-results.json`、`source-results-verified.json`、完整两平台日志及发布收据。源码 archive 860 成员、711 文件与冻结树逐字/模式相符；early/final source18份与 continuous2份逐字一致。Windows outer112项、Linux outer433项 CRC 与metadata SHA/大小已核；Windows inner551文件 CRC及校验和已核，计算 release identity 与 Notepad、Hy Settings与Hy production三份报告的实际加载身份相同，未另留inner重复副本。Linux inner未另解包或采用；推荐包证据仍在 `artifacts/ci498-independent/linux-package-verified.json`。原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 现役候选组合 / root，publish_next_verified_fix_batch | Chrome安全诊断、来源拒绝/嵌套撤权、练习基线读顺序三原片已独立双轴GO；另合入Notepad退出留证与Linux时耗观察，本页一起待完整组合双审。全部代码叶保留作者对象，唯一平台文档冲突按当前实现与剩余条件收束。组合GCC1通过、Windows WinEvent跳过、GTK观察19通过、XML21与diff通过；新增C#/Qt/Windows桌面未跑 | 授权精确树后核全部消费者编译、原source/Notepad事务邻近回归及实际练习场景；受控拒绝不是正向来源接受，诊断不是修复，未复现旧失败只报本轮事实，不为采诊断单独重跑 |
| Notepad 正常退出 / diagnose_notepad_normal_exit | 已实现准确关闭stage、原查找/Invoke计数及局部冻结快照，防迟到UIA回调改写终态；随当前组合审查，未编译/桌面运行。旧原件未给具体关闭卡点，独立按需正文/Undo已通过 | 原选择器、输入、deadline与清理不变；自然执行时核最后到达阶段及计数再决定是否修复。不能把新增诊断称作退出已修，真实正常退出仍须原场景通过 |
| Windows 正向来源门 / implement_source_origin_gate，root | UIAccess选择已确认；当前严格入口仅拒绝，正向初始化/有序见证另线实现中，未纳入此候选，旧键与严格构造LL交接仍须完成 | 代码、受控API、Windows阴性、物理/UIAccess和IME证据分别核；未知零写拒绝，Main/capability0不变，不把技术缺口写回等待同一产品选择 |
| foreground 等待首错 / triage_source_setup_ci498 | 清理前寿命诊断已发布，本轮原用例通过，失败分支未验 | 后续自然失败时核一次元数据及原取消/清理，核实后关闭排程；不单独重跑，非原子样本不证明前台根因 |
| Linux 实际拒绝分诊 / diagnose_linux_desktop_regressions，instrument_linux_shortcut_delays | GTK既有phases/keytiming及Qt终态聚合时耗已本地实现，纳入当前组合待审和CI；组合GTK观察19项通过，Qt尚未编译或真实桌面运行 | 自然复现时以时耗区分原等待/发送路径，再决定修复；保原150/100ms、25ms timer/原sleep、唯一输入与调度，不从总红推全部桌面失败，不为采证据单独重跑 |
| Linux 体验交付 / root | 保持CI498推荐，接收真机/物理输入法反馈 | 后继包须完整结果与身份才能替换推荐；未知IME保守拒绝，不自动发布stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认和资源合同；新增产品故障或实质新假设才安排最小验证 | 生命周期、质量、高级scope与许可各凭自身证据；不扩大候选、推理预算或公开范围 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；来源机制尚未就绪，按[正向接受剩余条件](../platforms/windows/development.md#正向接受的剩余条件)推进。新策略实际验证前保持当前严格边界及 Main/capability0，不变更 OS 权限、注册或部署。

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
