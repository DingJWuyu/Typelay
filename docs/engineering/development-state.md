# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-25 06:18 UTC** 按 CI526 完整终态与已核交付更新；失败、未运行与后继诊断候选分别记录。

- **编译阻断已闭合，App仍有一个真实失败。** 五处测试取消token修正已在Windows编译验证；App902过/1失败/22未执行。唯一失败是 `ProductionForegroundWriteTests.NativeReturnPreservesTheExactFieldRestoredByRootActivation(change: "success")` 在completion屏障释放前读取全局焦点为 `(0, 0)`，不等于原root/field；原因仍未知。
- **Notepad按需Direct与普通退出实际通过，自动接受仍失败。** 新准备与命令关联回归已执行；按需不再空等原业务截止，但正式cap0拒绝及依赖未跑独立保留，不能称整条Notepad或普通输入框自动接受通过。
- **隔离物理验证包推荐更新为CI526。** 原套件安全门、人工用例发现与搬迁默认关闭检查已通过，包身份已核；**物理按键尚未执行**。产品开发预览保持CI518，Linux体验保持CI498。
- **下一候选仅补失败诊断，不是根因修复。** 保留原全局精确焦点断言，失败时另读已绑定源线程的局部GUI状态与错误码；不以局部焦点替代全局条件，不改生产行为、屏障、输入次数或期限。

## 最近已核终态与有效证据

[CI526 / run 40167116453](https://github.com/DingJWuyu/Typelay/actions/runs/40167116453) 于 2026-10-25 05:52:48 UTC创建，attempt1，06:13:13 UTC终态 `failure`。冻结本地 `d63418f060a2db78acaaea6c68c04b672243c7ab`、tree `73c0f0c3d9848de1ea8dd9d2a3035641ff900655` 对应远端 `148612977651c5e40937cd719fa4dbe1905dea21`。失败步骤为Windows行为检查与正式Notepad，shared成功；Linux产品链按范围跳过。

| 范围 | CI526实际结果 | 证据边界 |
| --- | --- | --- |
| shared与Windows编译 | shared的Core302、Runtime1318、Windows.Contracts169、Protocol28合计1817项通过；Windows编译及原五处xUnit1051修正通过 | 编译通过不抹去后续App失败或物理未执行 |
| Windows App | 上述NativeReturn的success参数行在原中间全局焦点门失败；Notepad场景编排19项通过，源接受及原Tab9/F7118阴性安全门通过 | 未取得源线程局部状态，不能断言实际焦点已恢复、只是全局采样瞬态或产品已正确完成；新诊断尚未实跑 |
| 正式Notepad | 自动接受130ms公开cap0否定，编辑回源依赖not-run；单wake/Esc/单Direct及原生Undo2043ms通过，ordinary-exit791ms通过。无业务deadline触发或cleanup错误 | ordinary-exit有自身源/App/model退出与原文件不变证据，不借消费者退出代替；原自动接受失败及总exit1保持 |
| 其它Windows链 | continuous、browser7、Hy production五阶段与Settings15通过；source两行与owned六行清理成功，三个消费者natural exit0/0/1及完整退出门已核 | 不推导语言质量、UIAccess、真实IME或正式部署完成 |
| 隔离物理Tab包 | 原Tab9/F7118两Passed前置、唯一未跳过manual Tab发现、搬迁defaultfactory false/true两Passed全部通过，CI526包可交付 | 搬迁验证默认cap0/不安装/不提示/零写，不重跑源安装拒绝或物理阳性；没有原suite到最终包逐字samebits证明 |
| Linux产品链 | skipped | 保持既有实现与CI498推荐，不新增本轮Linux验收声明 |

核验入口为 `artifacts/ci526-independent/terminal-evidence-verified.json`。五份artifact、冻结源码733叶、23份早期/最终原件、产品包551文件与三份正式加载身份、消费者退出证明均已核。新隔离包的精确下载、内层摘要与环境要求统一见[CI526物理Tab验证包说明](../platforms/windows/README.md#隔离物理-tab-验证包不是产品体验包)。原始日志、模型与测试输出不复制到源码文档。

原CI524搬迁Tab到期失败不因改用默认关闭搬迁检查而改判；本轮安全前置仍来自原套件的完整Tab/F7阴性。旧子进程早退、覆盖窗及设置保存异常没有因未复现而获得根因结论。

## 当前责任与下一动作

root负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| NativeReturn失败观察 / integrate_windows_next_candidate，root | 集成已审窄诊断：capture后绑定源PID/TID；原全局焦点采样不匹配时，记录该次全局结果与源线程GUI局部状态，再执行原严格等值断言。无生产改动，不等待、重试或释放屏障补成通过；集成后单文件folder whitespace与diff检查通过，新managed/Windows未跑 | 授权CI验证精确候选；若同边界自然失败，凭新观察区分未知状态而非预设根因。局部sourceActive/sourceFocus不能代替原全局门；本地.NET/MSBuild/tests拒绝不重试 |
| 正式Notepad与已过回归 / root | Direct/Undo、普通退出及新编排回归已实际通过；自动cap0拒绝和编辑依赖未跑保持，原Ctrl、源接受、期限、所有权及清理回归继续保留 | 新实际失败再最小诊断；不把成功Direct提升为自动接受或所有普通输入框支持，不因未复现称旧问题根因修复 |
| 隔离物理Tab交付 / root | 当前提供已核CI526包；CI518产品开发预览不变。原安全门与搬迁默认关闭职责分开，下载身份以Windows说明为准 | 用户在包内提示后亲手一次Tab取得1Passed才算物理阳性；不补按、不重跑，UIAccess/IME/第三方部署各凭自身证据 |
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
