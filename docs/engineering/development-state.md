# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-25 05:01 UTC** 按 CI524 完整终态与后继本地集成状态更新；通过、真实失败和未运行分别记录。

- **Windows Ctrl 切片已实跑，物理正向仍未跑。** App898过/0失败/22未执行；Broker v4/source v7 的六个新增有序 Ctrl 场景、旧读真实 RenderCount 保稿回归、原Tab/F7阴性均过。源导航17、原有子进程所有权及退出码对照、continuous也通过；旧早退、覆盖窗身份及设置保存异常根因仍未证。
- **共享检查被 Ruff 格式失败阻断。** `test_distribution.py` 的协议版本/鉴权元组格式使 shared 与 Windows 格式步骤失败；shared 行为套件未运行，不能借 Windows 的 Core302、Runtime1318、Contracts169、Protocol28 通过补绿。后继仅展开该元组格式，不改协议行为。
- **正式 Notepad 有两个独立失败。** 自动接受仍在输入前公开 cap0 否定，39ms失败，人工稿接受依赖未跑；按需 Esc 后14ms即触发 Direct，命令因 no-lease 后以 StaleObservation 拒绝，没有启动翻译。fixture仍空等509033ms至原520秒业务截止，普通退出场景未运行；消费者自然退出证明不能替代它。
- **原套件安全门通过，搬迁末次输入前期限门失败。** 原Tab/F7两阴性完整过；搬迁Tab在 ordered-edge 准备之后、唯一键对之前的最终expiry检查失败，F7通过，没有新物理ZIP。当前仍推荐[已核CI521独立包](https://github.com/DingJWuyu/Typelay/actions/runs/40143179984/artifacts/12580160695)；物理按键、UIAccess及正式Main/cap0接受均不由这些结果开放。
- **连续、浏览器与两Hy链通过。** 三个消费者退出门通过；Linux产品链按范围跳过，体验推荐保持CI498。后继Notepad测试准备与包装职责修复已完成静态审查，新的managed/Windows执行仍待授权CI，不把旧失败改绿。

## 最近已核终态与实际结果

[CI524 / run 40160855034](https://github.com/DingJWuyu/Typelay/actions/runs/40160855034) 于 2026-10-25 04:09:33 UTC 由唯一 push 触发，attempt1，04:32:53 UTC终态 `failure`。冻结本地 `0091a95895dbd40aca1d3843459c501711e2cc4f` 对应远端 `ffceddcd9c080b0fb3bf64eae55b67a7b99a78c2`、tree `bb7523b8e31434712d0f217aadeb23f744085873`。失败包括 shared/Windows Ruff格式、正式Notepad及物理包搬迁；App零失败不等于22个未执行场景完成。

本轮确实运行了 source v7/49416、Ctrl切片与输入前准备压缩，不再沿用旧v6结果；它不包含后继Notepad测试准备、Ruff修正或包装职责调整的Windows验证。后继验证以 [Actions入口](https://github.com/DingJWuyu/Typelay/actions) 中精确提交及原件为准；静态审查不是实跑，单次通过也不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| shared 与 Windows基础 | shared因Ruff格式失败未进入行为检查；Windows实际Core302、Runtime1318、Contracts169、Protocol28通过，Windows格式步骤同样失败 | Windows结果不回填shared；后继本地Ruff通过也不改写本轮失败 |
| Windows App 与Ctrl | App898过/0失败/22未执行；六个新增ordered Ctrl场景及旧读保稿回归通过，既有pre-Control两例与fresh-source六例通过；源导航17、原有子进程所有权及退出码对照均过 | 人工physical Ctrl+F7、Tab/F7/F24未执行；旧早退、覆盖窗身份及保存异常原因未知。根退出码不代替完整所有权退出证明 |
| 隔离有序来源与回执 | 原套件Tab/F7两阴性完整通过，包含来源拒绝、零正文写/零完成与退休；默认factory及原隔离接受/撤回回归保持通过 | 搬迁Tab另有真实最终输入前到期失败，不能被原套件通过覆盖；隔离消费者不开放正式Main/capability0 |
| continuous 与浏览器 | continuous与独立browser通过；三个消费者的run/attempt/head与nonce、natural exit0/0/1、kernel signaled、active0及退出门均已核 | 旧设置保存异常未复现，不据此宣称根因修复；普通Notepad消费者自然退出不是ordinary-exit场景通过 |
| 正式Notepad | 自动Tab场景39ms公开cap0否定；按需Esc在692791，14ms后Direct无lease，692845给出StaleObservation，692850才见fresh lease。没有翻译启动，原文长度无变化，fixture在await-exact-direct-translation空等509033ms至业务截止；ordinary-exit为not-run | 自动接受失败独立保留，人工稿接受依赖未跑；不能把未开始翻译或消费者清理当Direct/Undo/普通退出通过，也不据随后lease追认旧键 |
| Windows Hy | production五阶段与Settings15场景通过，三份正式加载报告与本轮产品包releaseIdentity一致 | 不推导语言质量、高级scope或旧安装异常根因放行 |
| 隔离物理Tab包 | 原Tab/F7两Passed准入与唯一manual Tab发现通过；搬迁Tab在Ordered.cs行285最终输入前expiry门失败，F7通过 | 没有实际超期数值，仍未调用Key；不推断各阶段耗时，不续期/换grant。无新ZIP，CI521推荐保持 |
| Linux产品链 | 本轮按Windows变更范围skipped；最近完整链CI518通过，体验推荐保持CI498 | 不是本轮Linux通过，不新增可选开发或升级推荐 |

核验入口为 `artifacts/ci524-independent/terminal-evidence-verified.json`。源码archive889成员、732文件与冻结树逐字/模式一致，18个source原件与final一致；三份artifact已核metadata大小/SHA与完整CRC。Windows内产品包87,145,235字节、551文件，SHA `2416e523fe49288b5ab08f434332484a601dec6f0d36325f5c3a8a6dfd6ee5b7`，computed releaseIdentity与Notepad/Hy两链三份加载报告一致。原始日志与模型不复制进本页，CI521独立物理包身份与操作仍见[验证包说明](../platforms/windows/README.md#隔离物理-tab-验证包不是产品体验包)。

后继集成本地检查：包装8项通过，当前validator接受CI524原TRX中的精确Tab9/F7118两Passed；全仓Ruff检查及135文件格式通过，4个C#文件夹whitespace通过，3个变更Python语法通过，native受控边界1过/Windows WinEvent1平台跳过，diff检查通过。以上不含新的.NET/MSBuild/managed测试、Windows桌面或物理验收，既有权限拒绝未重试。

## 当前责任与下一动作

root负责集成、证据核验和采用决定；下表是现役责任线，不是实时活跃进程清单。Agent目标配置为 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| Notepad测试准备与Ruff / integrate_windows_next_candidate，root | 已集成完整后继：Esc后核同代新源publication与现有公开Direct-ready，再发唯一Direct；拒绝按精确generation/sequence关联，不依赖trace行序，固定末尾读取有界快照。补Compile Link与窄回归，生产零改；Ruff仅修元组格式。最终两轴静态GO，新managed/Windows未跑 | 下一动作在授权CI核真实Notepad与窄回归；不借旧ready、无lease或后到观察追认键，不新增输入、不延长520秒。原自动cap0失败独立保留 |
| 物理包职责 / connect_controlled_windows_runtime，integrate_windows_next_candidate，root | 包装链静态GO并集成：保留原套件恰Tab9/F7118两Passed安全前置，搬迁改用既有factory默认关闭false/true两行；唯一manual发现、目录、依赖、清理不变。没有原suite测试时快照，不称最终包逐字samebits；新Windows搬迁未跑 | 下一动作在授权CI核原安全门、实际搬迁及包证据；不放宽生产或原有序测试100ms期限，不把旧搬迁失败改绿，CI521推荐保持至新证据齐全 |
| 已过回归维护 / root | Ctrl新增六例/旧读保稿、原有序阴性、子进程所有权、源导航17与continuous已实际通过；保留有效回归与有限失败观察 | 新实际故障再分派最小诊断；物理/UIAccess/IME仍各凭自身证据，不因未复现追加专门CI或宣称旧根因已修 |
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
