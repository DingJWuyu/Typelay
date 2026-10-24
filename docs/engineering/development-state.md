# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 08:21 UTC** 按已公开提交与 CI 证据更新。

- **CI501 已终态失败，唯一失败是 Windows NativeFixture 编译缺少新增 Hook 分部源码链接。** Windows 行为、原生研究、格式、包装、Notepad 和 Hy 等后续步骤全部未跑；不能把上一轮结果算作本轮验收。
- **共享 Runtime 1314 项通过，新增 7 项回源间隙、默认拒绝、外部离开和取消后迟到 ACK 回归实际通过；Linux 完整产品链通过。** 新 Windows 真实回源尚未验证。两文件最小链接补齐已实现并双静态审查通过，编译未复验，生产与测试 C# 字节未再改动。
- **隔离 App 回执的最近实际基线仍是 CI500。** 真实派发正反、捕获屏障、Undo、零 submit、17 项源激活、Owned Ctrl/Tab 和 5 项 StartupPeers 当轮通过；人工稿回源与正式 Notepad 缺口仍在，不宣称旧终端遮挡、hover 或 Hy UIA 间歇根因已根治。
- **Linux 体验继续推荐完整核验的 CI498。** 本轮已核 GTK3/GTK4、Qt 编辑回源及 Hy 生命周期小原件和外包完整性，未重做内产品包采用评估；不关闭此前已回源异常丢稿或间歇 IME/投递延迟问题，模型质量与生命周期分别判断。

## 最近已核终态与实际结果

[CI501 / run 40075982893](https://github.com/DingJWuyu/Typelay/actions/runs/40075982893) 于 2026-10-24 07:54:40 UTC 由唯一 push 触发，attempt 1，08:17:18 UTC 终态 `failure`。冻结本地 `1302f0a1d9ff3eea4870fa2118e10de6e7f427ff` 对应远端 `8ed0d7dfabaf78d258589c2088036cb0326e8484`、tree `b9e77c3c822090cd8ea1f1f6aeb2a0f89e162e5b`；14 文件差异，精确读回、同分支非强推及源码 artifact 身份已核。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行按下表分别记录；旧轮结果不代表新提交，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享检查 | Core 302、Runtime 1314、Windows contracts 123、Protocol 28 通过；shared job 的 worker 168、Python shared 29、Linux 输入反馈 181 通过，Ruff 与 133 文件格式通过。新增回源 7 项全部通过 | shared 检查不覆盖 Windows 编译闭包或桌面交接；本轮 Windows 格式/Python/包装均未跑 |
| Windows 编译与 App | NativeFixture 链接的 `KeyboardHook.cs:243` 调用 `ObserveSourceReturnForeground`，项目未纳入新增 `KeyboardHook.SourceReturn.cs`，报 CS0103；行为步骤未跑，仅有测试前浏览器 profile 原件 | 两文件补齐已双静态审查，XML 与唯一显式 Hook 消费者 8/8 分部引用闭包已核，编译仍未复验。不能沿用 CI500 的 796/1/16 当本轮结果 |
| 隔离 App 回执基线 | 最近 CI500 的完整接受与拒绝对照、late ACK、expired installation、Unknown fence、shipping capability=0 通过；17 项 NativeSourceActivation、Owned Ctrl/Tab、5 项 StartupPeers 通过 | CI501 未运行这些 Windows 用例；原终端创建来源与 hover 消息来源仍无唯一根因证明，不授予正式第三方生产能力 |
| 人工稿回源 | CI500 原件在 611206ms 的 `ReturningToSource` 同次前台采样为 0，触发 `focus-left` 并停止会话；原已排队 return 随后完成，clear 后同元素 target lifetime 轮换。当前窄修已实现，CI501 新增 7 项共享回归通过 | 前台为何短暂为 0 未证；return 默认 Other/null 不是捕获读取失败，尚未进入该失败用例的 Ctrl/revoke/Shift 动作。新增 Windows 真实回源、router 与 foreign 阴性回归被编译阻断，不能称产品修复已验 |
| 原生受控源与研究退役 | 最近 CI500 的 owned 六例、source 两例和 dialog / peek / forged 三例通过，退出与 hook/输入清理完整；来源分类自动采集保持退役 | CI501 所有 Windows 研究步骤未跑。受控合成输入不授予严格物理来源、真实 IME 或第三方部署资格，不恢复旧研究凑运行 |
| Windows continuous 与浏览器 | 最近 CI500 两条链通过，各自自然 exit 0、activeProcesses 0 | CI501 未运行；不证明此前 source Ctrl 退出的间歇原因已修 |
| 正式 Notepad | 最近 CI500 自动预览成功，接受提示在原 90 秒截止失败，实测 90.005 秒，后续三场景未运行 | CI501 未运行，不是新增 Notepad 失败；接受/Undo、人工稿回源、按需/直接替换仍未完成 |
| Linux 产品链 | 完整 Linux job 通过：DOM、设置/教程、GIMP、GTK3/GTK4、Qt 与 VS Code 有实际结果；Qt 真实模型编辑回源、独立新键接受/Undo 与默认 Tab 均通过。Hy 12 阶段 90.705 秒，14 方向握手及双向 editor snapshot 通过，host/worker 退出且未强制清理 | Qt 输入为嵌套 niri 的 XTest，不是物理键盘；Hy 高级 scope 与质量未由此验证。外 ZIP SHA/CRC 已核，未复做内 tar 采用评估，体验推荐保持 CI498 |
| Windows Hy | 最近 CI500 独立 owner/hello 五阶段与设置 15 场景通过，normalQuit=true、exitCode=0、所有模型 worker 退出，`cleanupErrors=[]` | CI501 未运行。安装前展开超时未复现不等于根因修复；生命周期不等于翻译质量或高级 scope 放行 |

核验入口为工作区 `artifacts/ci501-independent/terminal-evidence-verified.json`。同目录的 `shared-results.json`、`windows-results-verified.json`、`linux-results-verified.json`、完整两平台 job 日志、`source-archive-verified.json` 与 `publication-verified.json` 给出实际结果和身份。源码 archive 的 853 个成员、704 个文件与冻结树逐字/模式相符；Windows 最终 artifact 为 377 字节，仅含测试前 profile，没有 TRX 或产品包。Linux 外包与小原件已校验，内 tar 未另解副本或升级推荐。上一轮仍相关的 Windows 实际基线在 `artifacts/ci500-independent/terminal-evidence-verified.json`，推荐包证据保持在 `artifacts/ci498-independent/linux-package-verified.json`。公开运行链接供查阅实际 job/artifact，原始日志不复制进本页。

## 当前责任与下一动作

root 对集成、证据核验和采用决定负责；下表是当前公开责任线，不是实时活跃进程清单。Agent 目标配置保持 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标记为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 编排与后继集成 / root | CI501 终态及原件已齐；下一最小批次仅已审 NativeFixture 链接补齐、现役维护说明和本页实际状态，当前仍本地待最终核审 | 精确差异、独立双审、身份与授权齐备后发布；不为文档单独触发整轮 CI，不取消或重跑旧轮 |
| Windows 人工稿回源 / triage_ci500_manual_return、root | 窄修已实现，共享 7 项回归通过；Windows 缺失分部链接的两文件补齐已实现、双静态审查及 XML/引用闭包检查通过，生产/测试 C# 未改，编译未复验 | 先通过真实 Windows 编译，再实跑原回源失败及 router/foreign/cancel 阴性边界；保持原 800ms、身份、原稿基线与撤权约束，原回源失败及 router、foreign、cancel 回归全部实际通过前，不称产品修复 |
| Linux 体验交付 / root | 保持 CI498 已核完整包推荐；本轮 Linux 全链及 Qt/Hy 小原件已核，无新增失败。保留用户真机、物理键盘/输入法反馈，旧 IME/投递延迟未获唯一根因证据 | 后继包须完整相关结果与身份才能替换推荐；未知 IME 仍保守拒绝，不延票、不重发唤醒、不自动发布 stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认、兼容和资源合同；只在新增产品故障或有实质新假设时安排最小验证 | 生命周期、质量、高级 scope 和资产许可各凭自己的证据；许可调查本身不授予新采用或公开权限，不扩大候选或推理预算 |
| 权重许可链 / verify_minisbd_asset_provenance | 只读核查官方权重来源与对应许可链，区分代码许可证与权重许可证；不加载模型或运行推理 | 给出可复核的官方依据、缺口及使用边界；缺失依据保持未确认，不把许可推断写成已获授权 |

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。UIAccess 选择未答前保持严格注入拒绝，不变更 OS 权限、注册或部署。

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
