# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 17:16 UTC** 按 CI511 完整终态、共享原件及两平台完整日志更新；独立候选与实跑结果分开记录。

- **CI511 因 Windows 测试编译失败而整体失败。** SourceKeyboardBoundaryProbe.Message.cs 的 token 查询局部变量 `returned` 与原外层事件变量重名，CS0136；不是 setup-dotnet 故障。Windows App、source两例、Notepad、browser、continuous、Hy及新退出诊断均未跑，不能借上一轮结果称本轮通过。
- **共享新增诊断已实际通过。** Core302、Runtime1318、Protocol28、Contracts161全过；Runtime新增4项捕获失败/取消/不可写日志回归与Browser CDP投影、来源拒绝映射已有实际结果，不代替Windows App链。
- **Linux完整job通过，未根治旧延迟。** 原GTK4 multiline与Qt mode-switch本轮通过，完整日志含GTK4/Qt各23项、Hy12阶段。最终Linux ZIP首次超时后一次同只读动作重取成功，SHA/大小及466项CRC已核，新时耗覆盖两个原失败场景；本轮成功不解释旧延迟，没有新有证据的修复，不为采证据另跑，体验推荐仍CI498。
- **下一候选是最小编译修复加隔离有序来源接线。** 两行token变量改名保持断言与行为；source v5/49376、SourceWitness v2/16520、休眠准备/精确ACK/首次激活已实现，代码组合两轴静态GO，受控C边界通过，managed/Windows尚未编译或实跑。不是正式产品自动接受；Main/capability0和原安全期限不变。
- **正式Notepad最近实际证据仍是CI510。** 单F9、Esc保源、Direct及原生Undo完整通过；自动Tab公开能力否定保持红，编辑依赖未跑，退出场景耗尽原deadline且旧stage未细分关闭等待。新细stage/计数/终态冻结本轮未执行，不能把fixture清理证明称App正常Quit。

## 最近已核终态与实际结果

[CI511 / run 40113245780](https://github.com/DingJWuyu/Typelay/actions/runs/40113245780) 于 2026-10-24 16:48:20 UTC 由唯一push触发，attempt1，17:06:16 UTC终态 `failure`。冻结本地 `db3e33b989822d0cc4233fb78d02d25f2f8e11d2` 对应远端 `31a3f67b2c61cf327af44584d654c7622df9cb21`、tree `2267c80eb3121b57bc9ed282a8e80b4da8f761d4`；30文件精确发布、714叶和非强推读回已核。下述token变量修复及有序来源组合尚未发布，不算本轮运行内容。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行分别记录；一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core302、Runtime1318、Windows contracts161、Protocol28通过；shared worker168、Python shared29、Linux输入反馈181通过。Windows workspace在App.Tests编译处CS0136失败，后续格式、包装、原生适配器与桌面业务未跑 | 共享新增Runtime4项诊断回归和CDP/来源映射已实际通过；不拿共享编译替代完整Windows编译，修复尚未复验 |
| Windows App 与源准备 | 本轮没有App TRX，NativeSourceActivation、source setup、首次Wake、文本快照、编排/Save与Owned回归未跑。最近CI510为App849过/0失败/16显式未执行，首采307ms匹配 | 编译阻断优先修复；旧前台寿命失败分支本轮也未验，不为采诊断单独重跑 |
| 隔离真实 Notepad 消费者 | 本轮source-message接受/撤回两例未跑；CI510两例的一次Verified/零回执旁路、Undo、焦点、零LL activation/二次Write、Quiet与清理仍是最近基线 | 新source v5与有序构造不能借旧v4结果称通过；原28秒和输入不变，受控输入不补齐硬件/UIAccess、IME或部署资格 |
| 普通人工稿回源 | 本轮Windows六回源与foreign/router实际链未跑；共享既有间隙保稿、默认拒绝、foreign与cancel/lateACK回归随Runtime通过 | 原800ms、稿基线、fresh capture及撤权边界不变；Windows最近CI510通过，不外推本轮0前台瞬态 |
| 隔离 App 回执与原生源 | 本轮App receipt/fence、Owned Ctrl/StartupPeers及source两例/owned六例均未跑 | 当前只有共享来源拒绝映射与本地受控native证据；正向/拒绝/迟到ACK与原消费者需在新精确提交复验，Main/capability0关闭 |
| Windows continuous | 本轮未跑。CI510练习初读Send/WM_GETTEXTLENGTH/1460，progressAvailable/targetBound=false；已发布候选仅把同一初始读移到既有native ready后、F9 cue前 | 该重排未获Windows运行证据，不称底层stall已修；原100ms单消息、90秒总预算、输入次数不变 |
| 浏览器合作输入 | 本轮未跑；CI510 setup及六业务场景通过。新Browser CDP安全投影回归已在shared Contracts执行，实际通知采集仍未跑 | 旧导航aborted与捕获失败根因未证；新诊断不加CDP请求、订阅、重试或预算 |
| 退出证明与后继门 | 本轮三个消费者均未启动，没有新proof。CI510 before-notepad及最终三proof门实过，三个独立nonce与natural exit1/0/1、active0匹配 | 本轮门步骤未跑，不说缺证明是泄漏；编译失败不取消既有独立准入规则，fixture自然退出不是App ordinary-exit通过 |
| 正式 Notepad | 本轮未跑。最近CI510公开自动Tab126ms否定；按需链1503ms通过，含一次长度变化观察后完成正文、Esc、Direct和Undo；退出场景506973ms耗尽原520秒operation deadline | 旧stage涵盖原文检查及WM_CLOSE/保存对话框等待，具体卡点未证。细stage、原查询/Invoke计数和迟到快照冻结已发布但未运行；原selector、520+20预算不变 |
| Windows Hy | 本轮production与Settings均未跑；最近CI510 production五阶段、Settings15场景及实际加载身份完整通过 | 不是本轮Hy新失败；不解释旧UIA间歇根因，不推导质量或高级scope放行 |
| Linux 产品链 | 完整Linux job success；日志明确setup-settings10、tutorial9、浏览器桌面7、GTK3桌面21、GIMP7、GTK4桌面23、Qt桌面23及Hy12阶段通过，可选Qt job skipped | 新时耗已核原GTK4接受链及Qt首wake的发送、tick与工作；本轮未命中过期、clear等待或provider同步取消，旧拒绝根因仍未知，无新可执行修复，不另跑。未做新包采用评估，推荐CI498不变 |

核验入口为 `artifacts/ci511-independent/terminal-evidence-verified.json`，同目录保留共享TRX、完整两平台job日志与精确发布收据。源码archive863成员、714文件与冻结树逐字/模式一致；共享artifact的SHA/大小/CRC已核。Windows最终376字节ZIP只含原browser-profile/before-tests.json，单项CRC/SHA已核，没有TRX或产品包，不虚构包装证明。Linux完整日志结果与最终原件结果分别在 `linux-log-results.json` 和 `linux-results-verified.json`；首次下载超时及同动作一次重取收据均保留，outer466项CRC、SHA和大小已核，仅提小原件，inner tar未单独核采用身份。CI510真实Windows业务与退出原件仍在 `artifacts/ci510-independent/`；CI498推荐包证据不变。原始日志不复制进本页。

## 当前责任与下一动作

root 负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent 目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| Windows编译与有序来源组合 / root，publish_next_verified_fix_batch | CS0136仅tokenout/Assert变量改名已双轴GO；隔离有序源候选及初始化修订代码组合也已双轴GO，全部代码叶与作者对象一致。source v5/49376拒旧v4；Witness v2/16520休眠mapping-ready、精确attach ACK后首次激活，历史不重设。GCC1通过、WinEvent平台跳过；managed/Windows未跑 | 终态文档窄审与精确树授权后正常发布；先核Windows编译，再核新managed producer/LL拒绝/迟到ACK及旧机械消费者、native ABI与cleanup。受控C阳性不能授生产能力或充当物理/UIAccess实测 |
| Notepad正常退出 / diagnose_notepad_normal_exit | 准确关闭stage、原查找/Invoke计数和局部冻结快照已发布，CI511因编译失败未执行；CI510具体卡点仍未知，按需正文/Undo基线已通过 | 原selector、输入、deadline与cleanup不变；后续自然执行时核最后阶段及计数，再决定最小修复。不为采证据单独重跑，普通App正常退出仍须原场景通过 |
| 练习首读与Chrome诊断 / diagnose_practice_initial_read_timeout，diagnose_ci509_chrome_failures | 练习初读重排及Windows Broker/Chrome安全诊断已发布；共享4项捕获诊断和CDP投影回归通过，App/真实桌面未跑 | 编译恢复后核原场景；若未复现只记该轮通过，不称旧stall/aborted/capture失败根治，不加等待重试或扩大采集 |
| foreground等待首错 / triage_source_setup_ci498 | 已发布清理前寿命诊断，本轮因编译阻断未跑；CI510原用例过、失败分支未验 | 后续自然失败时核一次元数据及原取消/清理，核实后关闭排程；不单独重跑，非原子样本不证明前台根因 |
| Linux体验交付 / root | 保持CI498推荐，接收真机/物理输入法反馈 | 后继包须完整相关结果与身份才能替换推荐；未知IME保守拒绝，不自动发布stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认和资源合同；新增产品故障或实质新假设才安排最小验证 | 生命周期、质量、高级scope与许可各凭自身证据；不扩大候选、推理预算或公开范围 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；隔离有序候选已实现但尚未获得Windows实跑资格，按[正向接受剩余条件](../platforms/windows/development.md#正向接受的剩余条件)推进。新策略实际验证前保持当前严格边界及 Main/capability0，不变更 OS 权限、注册或部署。

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
