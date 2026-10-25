# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-25 05:50 UTC** 按 CI525 完整终态更新；本轮结果、上次有效Windows证据与后继未验证修正分开记录。

- **共享检查通过，Windows被编译错误阻断。** shared的Ruff检查及135文件格式通过，Core302、Runtime1318、Windows.Contracts169、Protocol28合计1817项通过。Windows在 `OrdinaryNotepadScenarioTests.cs` 五处异步文件写入报告 `xUnit1051`：调用缺少 `TestContext.Current.CancellationToken`，编译失败。
- **本轮Windows行为与交付全部未运行。** App、源路径、continuous、浏览器、Notepad、两Hy链、产品包及物理验证包均未进入执行；没有App TRX、桌面结果或新包。不能沿用上一轮结果补绿，也不能从编译失败推导产品行为退化。
- **下一候选只补五处测试取消参数。** 不改变生产行为、Notepad准备/命令关联、包装职责、输入次数或期限；新的managed编译与Windows执行须由授权CI验证。本地格式检查不替代编译。
- **推荐保持。** [CI521隔离物理Tab包](https://github.com/DingJWuyu/Typelay/actions/runs/40143179984/artifacts/12580160695)、CI518产品开发预览与CI498 Linux体验推荐不变；物理/UIAccess/真实IME与正式Main/cap0接受未获新证据。

## 最近已核终态与有效证据

[CI525 / run 40166590275](https://github.com/DingJWuyu/Typelay/actions/runs/40166590275) 于 2026-10-25 05:43:57 UTC 创建，attempt1，05:45:35 UTC终态 `failure`。冻结本地 `df89a4bea9d3ee60f736038d3557759b472629e8`、tree `ff27928601cb1f10fd474fb6519faed4351e5d97` 对应远端 `97d5ca80335c3ebc8dc4c9f3d1906e96835f3aa0`；shared成功、Windows编译失败，Linux产品链按范围跳过。

| 范围 | CI525实际结果 | 证据边界 |
| --- | --- | --- |
| shared | Ruff check与135文件format通过；Core302、Runtime1318、Windows.Contracts169、Protocol28全部通过 | 共1817项.NET测试；不是Windows桌面、模型或物理输入验收 |
| Windows编译 | `OrdinaryNotepadScenarioTests.cs` 行125、127、130、142、145共五个xUnit1051，零warning，编译失败 | 五处测试异步文件写入遗漏取消token；不降低分析器或警告门，不以本地whitespace称编译通过 |
| Windows行为与桌面 | App、源接受、native、continuous、browser、Notepad与两Hy链均not-run | 没有App TRX或桌面场景结果；不把CI524通过或失败重新记成本轮结果 |
| Windows交付 | 产品打包、搬迁、物理包准备与上传均not-run；无新产品或物理ZIP | Windows artifact仅含测试前浏览器进程观察；不能作为可用包或场景验收 |
| Linux产品链 | skipped | 保持既有实现与推荐，不因shared通过新增Linux验收声明 |

核验入口为 `artifacts/ci525-independent/terminal-evidence-verified.json`。733叶与冻结源码及12个发布变更字节一致，源码archive890成员逐项核验；shared与Windows两份artifact已核metadata、大小、SHA与CRC。原始日志、模型及测试输出不复制到源码文档。

最近一次实际Windows产品执行仍是 [CI524](https://github.com/DingJWuyu/Typelay/actions/runs/40160855034)，不属于CI525：

- 当时App898过/0失败/22未执行，v7的六个新增ordered Ctrl场景、旧读真实RenderCount保稿、原Tab/F7阴性、源导航17、子进程所有权/退出码对照、continuous、browser及两Hy链通过。物理Ctrl+F7、Tab/F7/F24未跑；旧早退、覆盖窗及保存异常根因未证。
- 正式Notepad自动cap0在输入前失败；按需Esc后14ms触发Direct，no-lease后StaleObservation拒绝，没有启动该次翻译，fixture仍等待至520秒业务截止，ordinary-exit未跑。搬迁Tab在最终输入前expiry门失败、F7通过，无新物理包。这些失败不因后继测试准备、包装职责调整或本轮编译失败改绿。

## 当前责任与下一动作

root负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| Windows测试编译 / implement_windows_ctrl_acceptance，integrate_windows_next_candidate，root | 从冻结df89独立集成五处异步文件写入的 `TestContext.Current.CancellationToken`，不改断言、测试输入或生产代码；root静态GO，单文件folder whitespace、全仓Ruff检查与135文件格式、diff检查通过，managed未跑 | 授权CI在精确候选上完成Windows编译后才能确认修正；不重试被拒绝的本地.NET/MSBuild/tests，不屏蔽xUnit1051 |
| Notepad与物理包验证 / root | 前轮已集成Esc后新源publication与公开Direct-ready、唯一命令身份关联和有界trace读取；包装保留原Tab9/F7118两Passed安全门，搬迁改既有defaultfactory false/true两行。CI525编译阻断，全部新Windows验证仍未跑 | 编译通过后继续原Windows链，核Notepad与窄回归、原安全门、实际搬迁及包证据；保持520秒/100ms、唯一输入与清理，不宣称原suite与最终包逐字samebits，不改旧失败 |
| 已过回归与交付 / root | 保留CI524有效行为证据及其未闭合失败；提供CI521隔离物理包，产品开发预览保持CI518 | 用户实际单次物理Tab取得1Passed才算该例通过；新实际故障再最小诊断，不将未复现称根因修复，不以包交付代替物理/部署验收 |
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
