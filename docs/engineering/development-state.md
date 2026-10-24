# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 06:52 UTC** 按已公开提交与 CI 证据更新。

- **CI499 已终态失败：Windows App 778 通过、19 失败、16 项显式研究未执行；另有格式错误和正式 Notepad 接受提示超时。** 19 项分为 17 项源点击被终端遮挡、1 项新回执用例的原始 Tab 计数断言、1 项 Owned Ctrl 首键拒绝，不归为同一原因。
- **新隔离 App 回执用例已越过 Verified、捕获屏障与单次结算断言，但整个用例未通过。** 首错是把下游原始 GETMESSAGE 观察值 1 当成派发泄漏；其后 key-up 计数、Undo、零 submit 断言尚未执行。真实派发计数与拒绝对照的后继测试修订已静态双审，Windows 尚未复验；正式能力仍为 0。
- **Hy 设置 15 场景、独立生产 owner/hello、continuous 与浏览器本轮通过。** 安装前 UIA 超时没有复现，不称根因已修；生命周期与翻译质量仍分别判断。
- **Linux 本轮按既有范围选择跳过，体验仍推荐完整核验的 CI498。** 该包的 Qt 编辑/回源、GTK3/GTK4 和 Hy 链已有实际证据；本轮没有新的 Linux 产品验收，也不关闭此前已回源异常丢稿或间歇 IME/投递延迟问题。

## 最近已核终态与实际结果

[CI499 / run 40070339399](https://github.com/DingJWuyu/Typelay/actions/runs/40070339399) 于 2026-10-24 06:28:25 UTC 由唯一 push 触发，attempt 1，06:48:35 UTC 终态 `failure`。冻结本地 `aa353e8412c38068d5968f9456358a626fbe5c1c` 对应远端 `06901b9ef1ac9c5471d639dd54912714b50a76d1`、tree `b41541501ddd84472d550b1bd75e4bbfd45d106b`；23 文件差异，精确读回、同分支非强推及源码 artifact 身份已核。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行按下表分别记录；旧轮结果不代表新提交，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享与包装 | Core 302、Runtime 1307、Windows contracts 123、Protocol 28 通过；shared job 的 worker 168、Python shared 29、Linux 输入反馈 181 通过。Windows 包装另实跑 69 项，含 native foreground guards 与 WinEvent | Windows 步骤 19 先在 `NativeSourceControllerTests.cs:188` 报三处 WHITESPACE，随后该步骤的 worker、Python 与 Ruff 未运行；不以 shared job 的通过覆盖未运行 |
| Windows App | 778 通过、19 失败、16 项显式研究未执行。17 项 NativeSourceActivation 在点击前被同一 Windows Terminal 窗口遮挡；新回执用例原始 Tab 计数断言失败；Owned Ctrl 首键返回未消费 | 遮挡原件区分了源/Broker/其它进程，但终端创建来源未证；不是上一轮点击后焦点 `none` 失败。Owned trace 在 786622ms 以 `source-hover` 撤销 prefix，不猜为超时 |
| 隔离 App 回执 | 新用例已越过 Verified、延迟响应期间捕获未交付、释放后 NoSession/Retired、精确捕获正文、CompletionTransfers=1/BrokerWrites=0、选区及焦点断言。late install ACK 与 expired installation 回归通过 | `SourceTabDown.v1` 预期 0 实际 1 是首错。原始下游 hook 看见 Tab 不等于应用消息泵收到 Tab；后继以实际派发/拒绝正控验证，当前 key-up 计数、Undo、零 submit 断言尚未执行，不能称完整接受验收 |
| 原生受控源 | owned 六例通过；新 source 两例实跑 2/2。早 Dispose 未提前拆除未清尾键，原 repeat/up 仍被消费，Undo/Quiet 断言通过；两例退出后 `sourceRouteClosed=true`，hook/进程/输入清理完整 | 只覆盖隔离构造、自有受控源及合成输入。安装/卸 hook 失败注入、自然 WM_CLOSE、App 完成回执、严格同消息来源及真实 IME 未由本轮证明 |
| 来源边界与研究退役 | 来源分类自动采集保持退役；原 dialog / peek / forged 三例仍实际通过，hook 已关闭、进程已退出、输入配对且正文未变 | 手动维护入口不授予事件绑定、硬件来源或生产资格；无实质新依据不自动重采 |
| Windows continuous | 本轮整链通过，消费者自然 exit 0、activeProcesses 0 | 不证明此前 source Ctrl 退出的间歇原因已修；普通源生产接受仍独立 |
| 正式 Notepad | 自动预览成功；接受提示在原 90 秒截止失败，实测 90.002 秒，后续三场景未运行 | 接受/Undo、人工稿回源、按需/直接替换不能记为通过 |
| Linux 产品链 | 完整 Linux 与可选 Qt job 按未改动的 Windows-only 范围 `skipped`；本轮无 Linux 产品包 | 体验推荐仍是 CI498，历史已核范围见下方推荐入口，不用 shared 输入反馈测试冒充新桌面验收 |
| Windows Hy | 独立生产 owner/hello 五阶段与设置 15 场景通过；中英补充 49.891 秒，普通退出和模型 worker 清理通过，`cleanupErrors=[]` | 安装前展开超时未复现；诊断不是根因修复。生命周期不等于翻译质量或高级 scope 放行 |

核验入口为工作区 `artifacts/ci499-independent/terminal-evidence-verified.json`。同目录的 `windows-results-verified.json`、`contracts-results.json`、`contracts-failures.json`、`shared-results.json`、`source-results-verified.json` 与 `publication-verified.json` 给出实际结果和身份。Windows 包已核 SHA-256、内外 CRC 与 551 个文件的 releaseIdentity，和实际加载包一致；源码 archive 的 852 个成员、703 个文件与冻结树逐字/模式相符，early/final TRX 与 source 原件逐字一致。推荐 Linux 包证据保持在 `artifacts/ci498-independent/linux-package-verified.json` 与同目录终态/实际结果。公开运行链接供查阅实际 job/artifact，原始日志不复制进本页。

## 当前责任与下一动作

root 对集成、证据核验和采用决定负责；下表是当前公开责任线，不是实时活跃进程清单。Agent 目标配置保持 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标记为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 编排与后继集成 / root | CI499 终态及所需原件已齐；后继仅组合已审测试观察边界、既有启动合同与当前状态，Owned Ctrl 修订按实际 trace 单独核审 | 精确差异、审查、身份与授权齐备后再发布；不取消、重跑旧轮，不等待未来 CI 编号来改当前事实 |
| Windows 回执测量 / advance_windows_source_integration | raw GETMESSAGE 与实际消息派发分开记录，接受 actual-dispatch=0、拒绝对照=1，保原业务/期限；单行 try 格式已实际重现三错并用 folder whitespace 修正复核，最终差异双审通过，C#/Windows 未复验 | 下轮同一真实 App 用例必须继续跑到 key-up 计数、Undo、零 submit 断言与清理；部分断言通过不授予正式 Notepad/第三方生产能力 |
| Windows 源启动 / triage_ci499_activation_failures | 两处已有重定向 stdio 的启动点显式 CreateNoWindow 已双审，原进程身份/点击/几何/超时不变；最终日志没有新增终端归属证据 | 实际复验原 17 项；修正启动合同不冒称 7984 终端来源或旧间歇遮挡根因已证 |
| Owned Ctrl 前置 / triage_ci499_owned_ctrl | 原 trace 已定位 source-hover786622 撤 prefix；Ctrl 正例已复用现有 root 指针准备并在 finally 恢复，普通 Tab 分支不变。候选独立双审通过，尚未实跑 Windows，不更改生产资格或预算 | 下轮实际复验原 Ctrl/Tab 正例与相邻回归；在新实跑前保留失败，不延时、重试或新增无依据的诊断 |
| Linux 体验交付 / root | 保持 CI498 已核完整包推荐；GTK3/GTK4/Qt/Hy 全链通过。保留用户真机、物理键盘/输入法反馈，旧 IME/投递延迟未获唯一根因证据 | 后继包须完整相关结果与身份才能替换推荐；未知 IME 仍保守拒绝，不延票、不重发唤醒、不自动发布 stable |
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
