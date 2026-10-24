# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 08:44 UTC** 按已公开提交与 CI 证据更新。

- **CI502 已终态失败：Windows App 781 通过、17 失败、16 项显式研究未执行；另有浏览器扩展发现失败。** 17 项都是源点击前被同一 Windows Terminal 窗口遮挡，创建来源未证。浏览器在 setup 的 metadata evaluate 返回 ReferenceError，业务场景未跑；随后 Notepad/Hy 跳过，aggregate 退出证明检查实际执行并拒绝，不能据缺证明称进程泄漏。
- **NativeFixture 编译补链、普通人工稿回源六分支及新增 foreign/router 集成阴性回归实际通过。** 共享 Runtime 1314 项和新增 7 项回归通过；本轮没有证据保证真实 0 前台间隙恰被触发，不把集成通过扩大为所有间歇根因已消失。
- **隔离 App 回执正反、late ACK、expiry、Unknown、Owned Ctrl/Tab、5 项 StartupPeers、continuous、格式和包装本轮通过。** 普通正式接受、物理输入法与第三方部署仍未开放；原遮挡和 browser 问题按各自证据处理，不重跑刷绿。
- **Linux 本轮跳过，体验继续推荐完整核验的 CI498。** 最近完整 Linux 实跑 CI501 的 Qt/GTK/Hy 结果仍有效作基线，未改变推荐或模型采用；生命周期与翻译质量分别判断。

## 最近已核终态与实际结果

[CI502 / run 40078040870](https://github.com/DingJWuyu/Typelay/actions/runs/40078040870) 于 2026-10-24 08:26:24 UTC 由唯一 push 触发，attempt 1，08:38:17 UTC 终态 `failure`。冻结本地 `604776419eb7a5d308220d9152c86d44eb3b6493` 对应远端 `96d3d18cc55c54a256b0ac52c32c5297db5a42a5`、tree `732eeb7dd04a93ecf9ddc819af196228d89cf7c2`；3 文件差异，精确读回、同分支非强推及源码 artifact 身份已核。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行按下表分别记录；旧轮结果不代表新提交，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享、编译与包装 | Core 302、Runtime 1314、Windows contracts 123、Protocol 28 通过；shared worker 168、Python shared 29、Linux 输入反馈 181 通过。Windows build 与格式通过，worker 运行 168 项为 167 通过、1 跳过，browser profile Python 3 项、Ruff 与 133 文件格式通过；包装 69 项通过，含 native foreground guards 与 WinEvent | NativeFixture 缺失分部链接已通过实际编译，不再保留待编译排程；Windows worker 的 1 项跳过单独保留 |
| Windows App 与源准备 | 781 通过、17 失败、16 项显式研究未执行。17 项 NativeSourceActivation 均在点击前报告 SourceClickObscured，同一 Terminal PID 4716/HWND 196962，为源/Broker 以外进程，后采几何一致 | 原件不证明终端由谁创建或激活。后继仅在原拒绝分支增加固定进程创建时间元数据与原 covered 回归，已双静态审查，Windows 未跑；这是诊断，不是遮挡修复 |
| 普通人工稿回源 | 原六分支（含 shift）全部通过；新增已收到 foreign 前台后回原窗仍不得被迟到 ACK 恢复的真实 router 集成回归通过。共享 7 项间隙保稿、默认拒绝、foreign 与三类 cancel/late ACK 回归通过 | CI500 的 0 前台导致退休链已定位；本轮未证明那个真实瞬态再次出现并命中过例外。保留原 800ms、原稿基线、fresh capture 和撤权边界，不授予普通生产写入 |
| 隔离 App 回执与前置 | SourceMessageReceipt 完整用例及拒绝派发对照、late ACK、expired installation、Unknown fence 通过；Owned Ctrl/Tab 和 5 项 StartupPeers 通过 | 原始 hook 观察与真实派发仍分开；这些受控实跑不证明 Terminal 来源或第三方部署资格 |
| 原生受控源与研究退役 | owned 六例、source 两例和 dialog / peek / forged 三例通过，hook/进程/输入清理完整；来源分类自动采集保持退役 | 受控合成输入不授予严格物理来源或真实 IME 资格，不恢复旧研究凑运行 |
| Windows continuous | 整条链通过，消费者自然 exit 0、activeProcesses 0 | 不证明此前 source Ctrl 退出的间歇原因已修 |
| 浏览器与退出证明 | setup 9095ms 在 discover-loaded-extension 失败；5 targets、2 service workers、1 candidate，metadataRead=0，evaluate 返回 reference-error/valuePresent=false。后六业务场景未跑，快捷键/替换/submit 均为 0；browser 自然 exit 1、activeProcesses 0、cleanupErrors=[] | 同一次 guarded evaluate 的固定能力布尔值及安全异常行列诊断已实现、独立双静态审查通过，C# 与 Windows 未跑，精确原因仍未证；原认证、调用数、等待/重试与期限不变。Notepad 未运行且无退出证明；aggregate 检查实际拒绝，不把缺证明等同实际泄漏 |
| 正式 Notepad 与 Windows Hy | 两者本轮均 skipped，没有新的场景结果。最近 CI500 Notepad 接受提示 90.005 秒超时、后续三场景未跑；当轮 Windows Hy 五阶段及设置 15 场景通过 | 不把旧 Notepad 失败或 Hy 通过冒充 CI502 结果。Hy UIA 间歇超时、质量与高级 scope 不因旧生命周期通过而关闭 |
| Linux 产品链 | 本轮完整 Linux 与可选 Qt job 按未改动的 Windows-only 范围 skipped；最近 CI501 完整桌面链和 Hy 12 阶段通过，Qt 实际编辑回源/独立新键接受与 Undo 通过 | 本轮没有新 Linux 包；CI501 未重做内产品包采用评估，体验推荐仍是 CI498。XTest 不是物理键盘，Hy editor snapshot 不是高级 scope 或质量验收 |

核验入口为工作区 `artifacts/ci502-independent/terminal-evidence-verified.json`。同目录的 `contracts-results.json`、`contracts-failures.json`、`activation-obscuration-verified.json`、`owned-startup-results.json`、`windows-results-verified.json`、`shared-results.json`、`source-results-verified.json`、完整 job 日志与 `publication-verified.json` 给出实际结果和身份。源码 archive 的 853 个成员、704 个文件与冻结树逐字/模式相符；early/final 23 份 TRX/source 原件逐字一致。Windows 内外包 SHA/CRC 与 551 个文件已核；本轮没有 Hy/Notepad 的加载身份回执，计算出的 releaseIdentity 不冒充实际加载核对，内 ZIP 未另留重复副本。最近 Linux 实跑证据在 `artifacts/ci501-independent/linux-results-verified.json`，推荐包证据仍在 `artifacts/ci498-independent/linux-package-verified.json`。公开运行链接供查阅实际 job/artifact，原始日志不复制进本页。

## 当前责任与下一动作

root 对集成、证据核验和采用决定负责；下表是当前公开责任线，不是实时活跃进程清单。Agent 目标配置保持 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标记为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 编排与后继集成 / root | CI502 终态与原件已齐；编译补链和回源集成排程关闭，后继准备仅已审遮挡诊断及 browser 调查形成的必要变更，状态随实质批次同行 | 最终差异、独立双审、身份与授权齐备后再发布；不为文档单独触发整轮 CI，不重跑旧轮或放宽期限 |
| 源点击遮挡 / triage_ci499_activation_failures | 原身份诊断仍只定位 Terminal 遮挡；已有两文件固定创建时间候选和原 covered 回归，双静态审查通过，格式/diff 通过，Windows 未跑，原准备/点击/安全判断不变 | 下一实跑核元数据及原拒绝保持，再区分终端出生时段；创建时间也不能证明谁激活旧窗口，不先改更多启动 flag 或宣称根治 |
| 浏览器扩展发现 / triage_ci502_browser_discovery | 三文件诊断已实现并经独立双静态审查；folder format、diff/XML 与同表达式 Node 9 场景通过，C#、真实 CDP 与 Windows 未跑，原调用数和期限不变 | 下一实跑用固定能力布尔值/安全异常行列区分原 metadata 失败；原因仍未证，不把诊断称修复，不加等待、重试或预算，不把 setup 失败当业务投递失败 |
| Linux 体验交付 / root | 保持 CI498 已核完整包推荐；最近完整 CI501 无新增 Linux 失败。保留用户真机、物理键盘/输入法反馈，旧 IME/投递延迟未获唯一根因证据 | 后继包须完整相关结果与身份才能替换推荐；未知 IME 仍保守拒绝，不延票、不重发唤醒、不自动发布 stable |
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
