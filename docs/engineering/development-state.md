# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-24 06:20 UTC** 按已公开提交与 CI 证据更新。

- **CI498 已终态失败，当前失败为 Windows 单一 foreground 准备测试的点击后焦点等待和正式 Notepad 接受提示超时。** Windows App 796 项通过、1 项失败、16 项显式研究未执行；continuous 整链通过。首末焦点样本为 `none`，757 次观察均未匹配，不代表全部样本都是 `none`，根因尚未证。
- **Windows source 两例的真实后端安装/回收生命周期继续通过，下一片 App 回执接线尚未实跑。** 隔离 Prepared/Arm 与完成回执候选已完成独立静态双审，正式 Main、能力位和严格来源门不变，Notepad 不能记为接受通过。
- **Hy 设置链本轮 15 场景通过，新增两项真实 WPF 截止/取消回归通过。** 原安装前 UIA 展开超时未复现；本轮只增加有界阶段诊断，不称旧超时根因已修，质量结论不变。
- **Linux 完整产品链通过，体验推荐升级为 CI498。** 真实 GTK3/GTK4、Qt 编辑回源和 Hy 生命周期均有原件，完整包身份已核。新编辑焦点交接的 10 项 Runtime 回归通过；这不证明此前已回源后的异常丢稿根因已修，也不等于 stable 或物理输入验收。

## 最近已核终态与实际结果

[CI498 / run 40067927744](https://github.com/DingJWuyu/Typelay/actions/runs/40067927744) 于 2026-10-24 05:52:00 UTC 由唯一 push 触发，attempt 1，06:14:37 UTC 终态 `failure`。冻结本地 `39bf0649bd756ffad51a601147ed2f686871bc8e` 对应远端 `3152a7f74b4022dfd18d34fe9534441a621d992c`、tree `d9c18d720367bfdc311659649e2531b608adb94f`；7 文件差异，精确读回、同分支非强推及源码 artifact 身份已核。

后继验证以 [Actions 入口](https://github.com/DingJWuyu/Typelay/actions) 中当前分支最新运行及原件为准。实现、实际通过、失败和未运行按下表分别记录；旧轮结果不代表新提交，一次通过不证明旧间歇问题根因已修。

| 范围 | 已核结果 | 剩余边界 |
| --- | --- | --- |
| 共享与包装 | Core 302、Runtime 1307、Windows contracts 122、Protocol 28；worker 168、Python shared 29、Linux 输入反馈 181 通过。原生 foreground guards、实际 WinEvent 与包装 69 项通过 | 新编辑交接 1 正例/9 负例实际通过；编译和受控检查不是全部桌面产品完成 |
| Windows App | 796 通过、1 失败、16 项显式研究未执行；17 项 NativeSourceActivation 与 BrowserSourceClick 三例通过。唯一失败是 `NativeSourceSetupTests` 的 foreground 场景：可见性/几何已通过且单次点击调用返回，点击后原 10 秒内焦点未匹配 | 首末样本 `root/control=none`；757 为观察次数，不代表全部样本为 none。1400 来自对 0 做线程查询，不证明源销毁；新 Hy WPF 用例在失败之后执行，非此前污染。只读调查未定根因，不放宽等待 |
| 原生受控源 | owned 六例通过；新 source 两例实跑 2/2。早 Dispose 未提前拆除未清尾键，原 repeat/up 仍被消费，Undo/Quiet 断言通过；两例退出后 `sourceRouteClosed=true`，hook/进程/输入清理完整 | 只覆盖隔离构造、自有受控源及合成输入。安装/卸 hook 失败注入、自然 WM_CLOSE、App 完成回执、严格同消息来源及真实 IME 未由本轮证明 |
| 来源边界与研究退役 | 来源分类自动采集保持退役；原 dialog / peek / forged 三例仍实际通过，hook 已关闭、进程已退出、输入配对且正文未变 | 手动维护入口不授予事件绑定、硬件来源或生产资格；无实质新依据不自动重采 |
| Windows continuous | 本轮整链通过，消费者自然 exit 0、activeProcesses 0 | 不证明此前 source Ctrl 退出的间歇原因已修；普通源生产接受仍独立 |
| 正式 Notepad | 自动预览成功；接受提示在原 90 秒截止失败，实测 90.005 秒，后续三场景未运行 | 接受/Undo、人工稿回源、按需/直接替换不能记为通过 |
| Linux 产品链 | 完整 Linux job 通过，实际设置/教程、GIMP、DOM、GTK3、GTK4、Qt6 与 VS Code 检查通过。Qt 原件证实首次 Tab 回源零写、取得新人工稿许可后第二次 Tab 精确 Verified；配置键、provider 丢失/恢复拒绝及新源恢复检查亦完成 | Qt 编辑焦点交接已获当前回归证据；不把通过扩为已回源异常丢稿、物理输入或所有应用保证。GTK3 pause 本轮通过，不证明此前 IME 探测超时的深层原因已修 |
| Windows Hy | 独立生产 owner/hello 五阶段与设置 15 场景通过；中英补充 43.698 秒，普通退出 `normalQuit=true`、exit 0、模型 worker 退出，`cleanupErrors=[]`。新增缺按钮局部截止与调用方预取消 WPF 两例通过 | 本轮安装前展开超时未复现；诊断不是根因修复，成功设置链没有触发新失败阶段分支。生命周期不等于翻译质量或高级 scope 放行 |
| Linux Hy | 正式 12 阶段通过，89.783 秒；14 方向 handshake、取消保留/补充中英、冷启动、返回 Argos 与删除通过；host/worker 退出确认，无强制 worker 清理 | 双向预览仍为 editor snapshot，不改称普通源或物理桌面接受；高级 scope 和质量未由此评估 |

核验入口为工作区 `artifacts/ci498-independent/terminal-evidence-verified.json`。同目录的 `windows-results-verified.json`、`contracts-results.json`、`shared-results.json`、`editor-handoff-runtime-results.json`、`linux-results-verified.json`、`linux-package-verified.json` 与 `publication-verified.json` 给出实际结果和身份。Windows 包已核 SHA-256、内外 CRC 与 551 个文件的 releaseIdentity，和实际加载包一致；源码 archive 的 851 个成员、702 个文件与冻结树逐字/模式相符。Linux 外包完整 CRC、内 tar 的 293 项文件清单、clean 源码身份与启动入口均已核。公开运行链接供查阅实际 job/artifact，原始日志不复制进本页。

## 当前责任与下一动作

root 对集成、证据核验和采用决定负责；下表是当前公开责任线，不是实时活跃进程清单。Agent 目标配置保持 `gpt-6-astra` + `xhigh`，工具未确认的实际配置标记为未核实。

| 责任线 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 编排与后继集成 / root | CI498 终态及所需原件已齐，Linux 体验推荐已按完整包和产品链证据更新；后继 Windows 候选按当前公开基合成，状态差异独立审查后再形成精确冻结 | 新候选审查、身份与授权齐备后再发布；不取消、重跑旧轮，不等待未来 CI 编号来改当前事实 |
| Windows App 回执 / advance_windows_source_integration | 隔离 Prepared/Arm、exact ACK 与单次 poll/retire 完成回执接线已静态双审，GCC 反例修后通过；C#/GUI 未实跑，下一批核真实 App 消费者。正式能力仍为 0，UIAccess 产品选择仍待用户明确 | 同一真实输入的回执归属、准备期输入撤权、拒绝旁路、Undo 与清理需精确提交 CI 证据；受控结果不授予 Notepad/第三方生产能力 |
| Windows foreground 准备 / triage_source_setup_ci498 | 已核失败发生在单次点击返回后的 ReadFocus 等待；相同前序在上一轮通过，现有首末样本不足定位为何失去前台。只读核实际 runner/进程证据，不新增重试或扩大诊断 | 有直接故障证据或高价值最小定位点再修；若原件不足则保留未决，独立开发继续，不单独重跑求绿 |
| Linux 体验交付 / root | 推荐本轮 CI498 已核完整包；GTK3/GTK4/Qt/Hy 全链通过。保留用户真机、物理键盘/输入法反馈，旧 IME/投递延迟未获唯一根因证据 | 后继包须完整相关结果与身份才能替换推荐；未知 IME 仍保守拒绝，不延票、不重发唤醒、不自动发布 stable |
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
