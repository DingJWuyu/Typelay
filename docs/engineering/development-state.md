# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## 先看结论

本页于 **2026-10-25 09:10 UTC** 按 CI528 完整终态与后继候选状态更新。正式 ordered 接线已在开发分支执行，不再是“尚无CI”的候选；它没有获得产品推荐或物理接受资格。

- **App896过/19失败/22未执行，失败须按实际边界拆分。** 其中17条在调用者点击前置看到同一遮挡HWND `262612`，尚不知其所有者，不能计作17个独立产品缺陷；另有浏览器500ms回执等待失败与一项新owned capture返回null。后两项原因也未由本轮原件确认。
- **正式Notepad不再停在cap0提示，而是在一次软件Tab后精确写回未成立。** 公开接受cue已出现；随后trace源长度由32到33，但没有源端origin或终态拒绝码证据。自动接受等到原520秒业务截止，编辑、Direct及普通退出均未运行，不能沿用CI527通过结果。
- **旧source-message诊断mode11也真实失败。** 原始runner为1，mode11前置失败；mode12及清理成功不能抹绿。重复GETMESSAGE消费者是静态强假设，尚未证明唯一根因；owned模块身份不一致同样只是静态解释，仍须精确Windows验证。
- **后继修正与诊断均未实跑。** 仅集成已审的测试准备/模块一致性修正、有界失败观察与headless验收范围纠正，保留原安全门与期限；静态GO不能改写CI528失败。
- **推荐不变。** 继续提供CI526隔离物理Tab包，产品预览CI518、Linux体验CI498；本轮新包不晋升推荐。物理、UIAccess及真实IME仍未获新证据。

## 最近已核终态与有效证据

[CI528 / run 40176516580](https://github.com/DingJWuyu/Typelay/actions/runs/40176516580) 于 2026-10-25 08:24:54 UTC创建，attempt1，08:54:08 UTC终态 `failure`。冻结本地 `aaaa21422c848663f163a7430243977a5fa7a265`、tree `e7137de1ff88cf7b2c444253b644771ebc47f314` 对应远端 `152ee0e04dc470b4bb95ed84f9dceb6eb8cd8418`。Windows行为检查与正式Notepad步骤失败；另有包装成成功步骤的旧source-message诊断，其原始runner仍失败。

| 范围 | CI528实际结果 | 证据边界 |
| --- | --- | --- |
| shared与编译 | Core302、Runtime1318、Windows.Contracts171、Protocol28合计1819项通过；正式Broker v5/逐capture资格接线已编译执行 | 不是正式接受、物理或部署验收 |
| Windows App | NativeSourceActivation17条同一preclick遮挡失败；BrowserContract一条500ms回执门失败；OwnedPracticeQueue一条capture-owned为null；其它896项通过、22项未执行 | 不知遮挡窗owner，不从调用前置推导17个产品缺陷；浏览器时序及owned动态模块身份仍未知 |
| 正式接线合同 | 缺失/不匹配eligibility拒绝、Direct写权独立、逐目标ACK、Ctrl保稿与原ordered Tab/F7阴性等已通过；owned其余54条通过 | cue/资格呈现与软件阴性不授予实际接受；新混合owned场景失败独立保留 |
| source诊断 | mode11在预期1/实际0的前置断言失败，原runner exit1；mode12通过。原owned同步六行runner0，hooks/process/input清理均已核 | 包装步骤绿不等于mode11通过；legacy双GETMESSAGE静态假设须验证，不认作唯一已证根因 |
| 正式Notepad | 公开cue后唯一软件Tab已走到await-exact-source-translation；trace源长度32→33，后有新翻译请求。该场景509424ms至原520秒业务截止失败，edit-return、on-demand Direct与ordinary-exit均not-run；cleanupErrors为空 | 没有native origin/terminal reason或source receipt；textLengthChangeCount不是变更次数，不能据长度判定具体来源拒绝或物理输入结果 |
| 其它Windows链与包 | continuous、独立browser7、Hy production五阶段与Settings15通过；消费者自然退出与包加载身份已核。原Tab9/F7118安全前置、唯一manual发现及搬迁缺eligibility拒绝两行通过 | 独立browser通过不覆盖App中的500ms失败；新产品/物理包仅属开发候选，不替换CI518/CI526推荐，不宣称原suite至最终包逐字samebits |
| Linux产品链 | skipped | 保持既有实现与CI498推荐，不新增本轮Linux验收声明 |

原件核验入口为 `artifacts/ci528-independent/terminal-evidence-verified.json`，App失败与source详情分别由其指向的 `windows-results-verified.json`、`source-results-verified.json` 保留。冻结源码733叶、发布变更字节、包与加载身份均已核；原始日志、模型和测试输出不复制进源码文档。当前推荐包身份仍见[CI526物理Tab说明](../platforms/windows/README.md#隔离物理-tab-验证包不是产品体验包)。

## 后继最小候选与下一动作

root负责采用与最终核验，唯一publisher负责获准的远端写入。下表是现役责任线，不是实时活跃进程清单。Agent目标配置为 `gpt-6-astra` + `xhigh`；工具未确认的实际配置不以目标值冒充。

| 责任线 / 负责人 | 当前候选与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 旧source-message准备 / integrate_windows_next_candidate，root | 已审09c80ec仅将旧诊断捕获显式选入sent-only Broker，避免随后手装native诊断再引入第二个GETMESSAGE消费者；不改生产路由或原mode11/12断言 | 后继精确Windows执行确认是否消除同边界失败；当前仍是假设，不放宽来源、100ms或清理门 |
| 混合owned模块 / integrate_windows_next_candidate，root | 已审3769dd8+b306d9d复用App侧唯一生产Broker/Main与普通StockFixture，实际核Broker/Practice加载同一路径NativeInput；原capture/owned/再普通capture与零写断言保留 | Windows未跑；动态模块不一致未由CI528证明，不能把静态修正写成根因已修；不增加输入重试或释放未知owner |
| 前台与浏览器失败观察 / integrate_windows_next_candidate，root | 已审0ca856d在原失败门增加遮挡窗身份元数据和浏览器回执时序观察；原点击前置、500ms截止及结果断言不变 | 新观察须由自然失败取得，不采标题/正文、不延时换绿；未知仍未知，诊断不是修复 |
| Notepad headless范围 / assess_formal_windows_wiring，root | 精确34e03d8四叶已独立静态GO并集成：无人值守观察到真实接受cue后保留not-run / trusted-source-input-required，不再生成接受Tab或人工稿接受输入；明确cap0产品拒绝仍failed。仅经原同token只读准入才继续独立Direct/Undo及退出 | 新managed/Windows未跑；聚合对not-run仍保持failed/exit1，不能称接受通过。原520秒、唯一Direct、source/focus/worker检查与清理不变；可信来源正向须另有实际证据 |
| 隔离物理Tab与已过回归 / root | CI526包、CI518产品预览推荐不变；原安全门、源身份、Direct独立写权、Ctrl人工稿和清理回归继续保留 | 用户一次物理Tab取得1Passed才算该例阳性；UIAccess/IME/部署各凭自身证据。旧NativeReturn全局0、子进程早退、覆盖窗及保存异常不因未复现称根因修复 |
| Linux体验维护 / root | 保持当前实现与CI498推荐，接收真机/物理输入法反馈，不增加可选开发 | 新实际故障或用户反馈再判断最小必要动作；未知IME保守拒绝，不自动发布stable |
| 模型采用 / root | 保持下方既有公开采用结论、默认和资源合同；新增产品故障或实质新假设才安排最小验证，与Windows独立推进 | 生命周期、质量、高级scope与许可各凭自身证据；不扩大候选、推理预算或公开范围 |

最终集成候选的8个变更C#已通过获准的folder whitespace；全仓Ruff check/format（135文件）、包装Python单测8项及diff check通过。本地未运行被拒绝的managed build/test；新Windows、物理、UIAccess及真实IME验证均未执行。下一步由root审核精确冻结身份后决定开发分支CI，不由集成作者自行发布。

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；隔离有序候选的初始化与软件注入拒绝已获得Windows实跑证据，但物理/UIAccess正向及正式部署资格未建立，按[正向接受剩余条件](../platforms/windows/development.md#正向接受的剩余条件)推进。当前推荐产品 CI518 与隔离验证包 CI526 保持原已发布边界；这不表示当前开发候选的 Main 仍关闭。候选已实现并在CI528运行唯一 ordered 正式组合，只能在本次精确 capture 资格、实际安装和原安全条件成立时提出接受。root 授权的开发分支 CI 不等于正式产品发布、用户默认推荐晋升或物理验收；不变更 OS 权限、注册或部署。

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
