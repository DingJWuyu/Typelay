# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 02:27 UTC** 按已公开提交与 CI 证据更新。

- **CI494 已终态失败，当前先处理真实首错。** Windows App 的源点击准备、独立来源观察、continuous 源 Ctrl 退出和正式 Notepad 接受提示失败；Linux 唯一失败步骤是 Qt 真实模型链的 fresh F24 人工稿接受。
- **Linux 体验仍推荐 CI480，不升级。** CI494 的 DOM、GTK3/GTK4 术语与真实桌面链、Qt probe 及 Hy 生命周期通过，但 Qt 真实模型失败，整轮 Linux 未通过。
- **Windows 普通源生产接受仍未完成。** 自有受控源 owned 六例与同字段原生 source 两例通过，不等于正式 Notepad 或所有普通输入框具备接受能力。严格来源、真实 IME、精确身份与原子写入门保持；UIAccess 产品选择仍待用户明确。
- **模型生命周期和质量分开。** 本轮 Windows Hy 15 场景、Linux Hy 12 阶段通过；默认、质量、高级 scope、物理输入及所有应用支持没有因此放行。

## 最新发布与实际结果

[CI494 / run 40052238858](https://github.com/DingJWuyu/Typelay/actions/runs/40052238858) 于 2026-10-24 01:59:17 UTC 由唯一 push 触发，attempt 1，02:19:47 UTC 终态 `failure`。冻结本地 `38ec94f3e5fcd44e631908629173c2d66525a6b6` 对应远端 `4c5600a600f27f29724acc0410eb8218d4795c38`、tree `0bafdaf3412f58c91fda29e8abe6dde85a4fd7c8`；19 文件精确读回、同分支非强推及源码 artifact 身份已核。

本批包含 GTK3 drawable 时钟、Windows 退出回归的指针准备、GTK 完整会话身份与只读反馈及真实所有者验收、独立输入来源只读观察的一次 CI 接线，以及 DOM 动画等待失败留证。实现、实际通过、失败和未运行按下表分别记录，不以整体结果覆盖局部证据，也不把一次通过当作旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享与包装 | Core 302、Runtime 1297、Windows contracts 122、Protocol 28；worker 168、Python shared 29、Linux 输入反馈 181 通过。原生 foreground guards、实际 WinEvent 与包装 69 项通过 | 编译和受控检查不是桌面产品完成 |
| Windows App | 778 通过、17 失败、18 项显式研究未执行；17 项均在 `SourceClickObscured` 源点击准备失败。原指针准备对应的 Ctrl 退出两例实际通过 | 点击准备失败未进入待验机制；两例通过不解释其它准备失败 |
| 原生受控源 | owned 六例和 source 两例通过；source 保留首键、同字段尾键、Undo、Quiet 与实际清理证据 | 只覆盖自有受控源；生产接受仍关闭，未证明物理 IME 或跨应用能力 |
| 独立来源观察 | 已进入新 case，采到 4 条 F8 消息、2 阶段、两采样点共 8 次原始值，首段为 INJECTED、posted 段为 UNAVAILABLE；`sourceOrigin.error=origin-extra-input` 后第三阶段未发送，hook/进程已清理 | 整体 `inconclusive`，不是完全未采，也不是完整三段通过。`productionReadiness`、`hardwarePositiveObserved`、`bindingProven` 均为 false，不授予输入信任或写入能力 |
| consumption-order 研究 | 旧受控研究本轮通过 | 不据此宣布先前首消息前提失败的原因已修复，也不扩大为生产验收 |
| Windows continuous | 在普通 source Ctrl 退出时退出 lease 缺失、浮层未隐藏，整链失败；后续 5 场景未运行。消费者自然 exit 1，owned 残留为 0 | 未运行的持续预览、直接替换、按需练习、Chrome 冷页/新标签页不记通过；清理成功不抹去功能失败 |
| 正式 Notepad | 自动预览成功；接受提示在原 90 秒截止失败，实测 90.009 秒，后续三场景未运行 | 接受/Undo、人工稿回源、按需/直接替换不能记为通过 |
| Linux DOM / Qt probe | DOM 本轮通过，受控 Qt probe 通过 | DOM 未复现先前动画超时，不能把新增失败留证称为已修根因；probe 不替代 Qt 真实模型链 |
| Linux GTK | GTK3/GTK4 术语各 40/40；真实桌面结果分别列 21/23 项通过，含语言菜单保稿、新续约所有者及相应拒绝场景 | 仅代表本轮实际覆盖，非所有控件或应用验收 |
| Linux Qt 真实模型 | fresh independent F24 未接受回源人工稿；hook 入口时票已过期 80ms，尚未进入写入 | 深层原因未证，整轮 Linux 失败；不延票或重发输入补成功 |
| 双端 Hy | Windows 15 场景全过；Linux 12 阶段、88.28 秒通过，Host/worker 正常退出且无强制清理 | 生命周期与双向普通预览不证明质量、高级 scope 或物理输入 |

核验入口为工作区 `artifacts/ci494-independent/terminal-evidence-verified.json`，同目录的 `windows-results-verified.json`、`linux-results-verified.json`、`shared-results.json` 与 `publication-verified.json` 分别给出本轮结果和身份。Windows 包已核 SHA-256 与内外 CRC；Linux artifact 外层完整性已核，内产品包未另行核验采用。公开运行链接供查阅实际 job/artifact，原始日志不复制进本页。

## 当前责任与下一动作

root 对集成、证据核验和采用决定负责；下表是当前公开责任线，不是实时活跃进程清单。Agent 目标配置保持 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标记为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 当前失败与后继集成 / root | CI494 终态和所需原件已齐；按下列首错推进独立定位，候选变更按精确差异、审查和授权分别核验 | 可交付修复取得同路径实际证据后更新本页；失败、未运行和清理结果保持独立，不重复已过场景凑工作量 |
| Windows 准备与退出 / root | 分别定位 17 项 `SourceClickObscured` 前置和 continuous source Ctrl 退出 lease 缺失，保留两者不同的现场身份与时序 | 各自建立实际原因与最小回归；前置或期限问题不靠重试、延时、放宽门解决，不混称原指针修正失效 |
| Windows 来源观察 / root | 核 `origin-extra-input` 的额外输入来源及已有消息配对，保留两阶段原始采样和第三阶段未发事实 | 完整性未成立前结论保持 inconclusive；只有新证据支持具体下一动作才继续，不自动重复同一研究或授予生产资格 |
| Linux Qt 人工稿接受 / root | 沿本轮 fresh F24 的实际投递、票期限及入口观察定位过期原因；DOM/GTK 本轮通过项退出在修队列 | 根因与同路径实际回归齐备后关闭；不延票、不重发、不把 probe 通过代替真实模型验收 |
| Windows 产品闭环 / root | 普通源接受、原子写入、真实 IME 与身份资格保持独立；UIAccess 信任范围依赖用户决定。详细原问题与验收见 [Windows 待办](../platforms/windows/open-issues.md) | 同一真实输入的安全消费/写入、拒绝旁路、Undo、清理及正式应用正向验收齐备；受控研究不能替代这些完成门 |
| Linux 体验交付 / root | 维持 CI480 推荐，保留用户真机/物理键盘/输入法反馈；新包须独立建立完整 Linux 结果和包身份 | 满足采用依据后才更换推荐；不重打已验包、不自动发布 stable |
| 模型采用 / root | 保持下方默认、兼容和资源合同；只在新增产品故障或有实质新假设时安排最小验证 | 生命周期、质量和高级 scope 各凭自己的证据；无新依据不重跑已否定配方，不扩大候选或推理预算 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。UIAccess 选择未答前保持严格注入拒绝，不变更 OS 权限、注册或部署。

有界诊断只用于定位，不采用户标题/正文，不增加焦点重试或放宽原期限。真实 IME composition 正例仍缺，输入配置库存与明确 none 不能代替它；既有几何、浏览器与回源回归保留，但旧间歇故障未复现不等于根因已消失。

### Linux 推荐与支持范围

推荐 [CI480 体验包](https://github.com/DingJWuyu/Typelay/actions/runs/40007045279/artifacts/12525507850)，提交 `2342a747864b9694e3beebf1ca16c4aa44e078db`；内 tar 为 281,117,978 字节，SHA-256 `9a5415c95b4a0e285baee3cab6c1a4dfbda313f207a61b808097111f99acf4a2`。完整 Linux job、clean 提交身份与 293 项清单已独立核验。这是体验推荐，不是 stable、物理输入法或所有应用验收。

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
