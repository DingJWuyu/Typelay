# 当前开发状态与安排

这是当前开发的唯一现役入口。负责人、证据与下一动作改变时更新；已关闭排程删除，历史查 Git。产品要求以[产品基线](../product/README.md)为准。未获准公开的内容不纳入本页。

## Index 默认后端切换 - 当前实施

用户已确认切换 Index。此开发线从 `82f53d6cab539d41443d835674b7148ed5e926ac` 在 `work/linux-usability` 实施，已获准发布到既有开发分支进行 CI，但尚未推送或获得双端验收。新用户明确确认准备时推荐 Index；已有 Argos/Hy 库存、active 选择、方向子集和设置保持，启动不下载、不自动迁代。Index 使用独立包身份、锁与原生适配，不改写 Hy ABI5 或旧模型身份。Repair 保留当前选择与已装方向，失败保旧代。

实际价值：用户在普通输入框使用翻译，希望采用已选定的 Index 配方；修改前生产默认为 Argos，当前实现正接入 Index。实际频率未知，影响每次使用默认模型的翻译结果；需要共享 worker、两端安装与原生分发接线，成本明显高于改文件名，但复用既有代际事务与输入安全路径，不重构平台输入。验证沿现有 catalog/库存、真实管道握手、准备/激活公开边界和真实模型推理开展，替身仅证明合同，不证明质量。

| 责任 / 负责人 | 当前动作与依赖 | 完成或停止条件 |
| --- | --- | --- |
| 共享与双端产品接线 / `implement_index_model_switch` | catalog、worker、Runtime 与 Windows/Linux 安装/设置默认；保留输入、焦点、预览、写回与 Undo 安全 | 聚焦回归与共享检查通过；实际 Linux native 推理证据；Windows 原生生命周期由精确 Windows CI 单独验证，未跑不称通过 |
| 独立 Index native / `index_native_adapter` | 独立模型 ABI、token/template 校验与 b11913 构建；消费官方 pinned 原件 | whole-source / plain 或真实要求的 instTrans、thinking=false、greedy、真实 EOG 与截断拒绝；原硬资源门不放宽 |
| 官方身份 / `verify_index_runtime_provenance` | 官方模型、许可证、模板与 runtime revision 原件核验，证据仅在 ignored artifacts | 明确实际哈希、许可证及特殊 token；缺证不靠摘要补造 |
| 独立迁移审视 / `review_index_migration_design`，root | 审核旧库存、默认与资源门，root 保留发布权 | root 单独核精确冻结候选后发布既有开发分支；不合并、不更新 stable，不上传模型或原始日志 |

上述职责目标配置均请求 `gpt-6-astra` + `xhigh`；派发工具未独立确认实际运行配置，实际值未核实。Index 明确采用独立 context4096 / output1024，与所选配方一致；Hy 仍1024/192。保留两核、2.5GiB 地址空间、单次60秒等硬资源限制；实际不适配即报告失败，不缩配置、截断、放宽硬门或回退。质量和耗时仍须实际验证，不由配置相同推定。

当前本地检查：共享 worker 186 项、共享 Python/CI 选择 29 项、实际 Index ELF/资源 3 项、Linux setup 110 项、发行包构成 30 项、desktop session 20 项、startup diagnostics 1 项及全仓 Ruff check/format 已通过。新增 Index 原生安全审计与真实模型协议验证见上方/下方范围；managed 与双端正式宿主 CI 尚未执行，不能据此宣称整个平台通过。构建包始终不含模型，原始证据仅在 ignored artifacts；发布由 root 取冻结提交与远端新鲜状态后统一执行。

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
| 模型采用 / root | 执行上方已授权 Index 默认切换，保持旧 Argos/Hy 库存与显式选择；与 Windows 输入任务独立 | 以精确 Index 载荷与两端实际 owner 验证，不以旧 Hy 结果或配置相同代替 |

最终集成候选的8个变更C#已通过获准的folder whitespace；全仓Ruff check/format（135文件）、包装Python单测8项及diff check通过。本地未运行被拒绝的managed build/test；新Windows、物理、UIAccess及真实IME验证均未执行。下一步由root审核精确冻结身份后决定开发分支CI，不由集成作者自行发布。

## 仍约束交付的产品边界

### Windows 普通源

普通输入框接受译文、F10 替换和连续输入仍是核心目标；代表性应用不是支持名单。首键、同目标有效按键消费、原子文本事务、IME 与精确源身份必须同时成立，未知即拒绝。用户主动切换应用后的 repeat/up 正常交给新应用，跨应用尾键抑制不再是要求。

正式 Notepad 的仅预览不能改成接受通过；源事件与事务的受控阳性也不能授予第三方部署资格。既有只读 TSF、原始消息和来源观察各有范围，见 [Windows 开发说明](../platforms/windows/development.md)；已退役机制不恢复凑运行。[UIAccess 辅助输入兼容范围已获用户确认](../product/README.md#windows-接受译文的辅助输入兼容范围)，不再等待产品选择；隔离有序候选的初始化与软件注入拒绝已获得Windows实跑证据，但物理/UIAccess正向及正式部署资格未建立，按[正向接受剩余条件](../platforms/windows/development.md#正向接受的剩余条件)推进。当前推荐产品 CI518 与隔离验证包 CI526 保持原已发布边界；这不表示当前开发候选的 Main 仍关闭。候选已实现并在CI528运行唯一 ordered 正式组合，只能在本次精确 capture 资格、实际安装和原安全条件成立时提出接受。root 授权的开发分支 CI 不等于正式产品发布、用户默认推荐晋升或物理验收；不变更 OS 权限、注册或部署。

有界诊断只用于定位，不采用户标题/正文，不增加焦点重试或放宽原期限。真实 IME composition 正例仍缺，输入配置库存与明确 none 不能代替它；既有几何、浏览器与回源回归保留，但旧间歇故障未复现不等于根因已消失。

### Linux 推荐与支持范围

推荐 [CI498 体验包](https://github.com/DingJWuyu/Typelay/actions/runs/40067927744/artifacts/12550995369)，提交 `3152a7f74b4022dfd18d34fe9534441a621d992c`；内 tar 为 281,118,175 字节，SHA-256 `0b9fc84e9d7036f046935e6620dbe6635732ad3b144cb9570281b801b9b11d31`。完整 Linux job、内外包、clean 提交身份、293 项文件清单与启动入口已核验。这是体验推荐，不是 stable、物理输入法或所有应用验收。

按需唤醒后的空捕获仍保守中止；恢复同一目标不证明期间未离开焦点家族，见 [Linux 架构约束](../platforms/linux/architecture.md#按需唤醒与缺失捕获)。可选富文本、非合作控件及 Electron/VS Code 自动浮层扩展暂不开展。

### 模型：采用 Index，生命周期仍不等于质量放行

- **默认与迁移**：新且可信的空库存经明确确认默认准备 Index 官方 Q4_K_M；固定 revision、1,312,164,352 bytes、SHA 与 Apache 许可见现役 `native/index/model.lock.json`。已有 schema1/2 Argos 和 schema3 active 不重解释、不自动下载或重选；Repair 保已装方向，失败保旧代。Index 限于已有十四方向；Hy 保留 ABI5/b11717、新装十二方向与显式中英补充接口，中英高级门不变。
- **Index 配方**：独立 ABI1/b11913、完整原源一次调用、native plain 或真实约束 instTrans、thinking=false、greedy、4096 context / 1024 最大新 token，不重试/回退/截断。经过严格 topology、字面项、内部空白与逐槽术语校验后，仅以原源序列化全请求 SP/TAB 及原 CRLF/CR/LF；不是语义修补或 raw benchmark 输出等价。
- **当前实际证据**：Linux 正式 setup 已准备私有 CPython3.13.16、完整依赖和真实 Index 候选，未手造 current/active；完整 worker 的十四方向 hello、同 worker 中英双向及 URL/LF 请求通过，最终 native 的控制字面量 roundtrip、禁止 ID、decode 前取消、实际 decode 中取消零输出及下一请求隔离通过。真实风格/术语/组合各有成功响应，但含长重复输入的批次在60秒请求外门失败，不能将整批次记绿。空白+CRLF原始模型曾改变布局，被正确拒绝；一次授权诊断确定是CRLF类型与尾padding丢失，窄源布局适配后的最终 worker 七帧已通过：布局、三种高级请求、9000字节明确拒绝及同 worker 下一普通请求，总9.137秒、自然exit0；记录精确payload/module哈希。原失败证据不被新候选抹去。
- **未完成的门**：本地 managed build/test 未进入编译，受 MSBuild socket 和执行封装限制；没有换路径绕过。新增 Windows/Linux Index 独立 CI 将分别执行正式宿主与进程 owner 的安装、激活、连续请求、取消/失败保旧代、冷启、切换/移除与退出，目前未运行。旧 CI528 输入失败保留，不因 Index 检查改变推荐或计为新实现通过。质量/GUI/物理输入/IME 各凭实际证据。
- **资源**：Index Linux 已实际设置并核验最多两核 affinity 与硬地址空间2.5GiB；Windows 新接线设置 affinity 并沿用2.5GiB/单进程 Job，尚待真实 Windows 验证，Job内存不是Linux地址空间计量。载入/hello 与单请求60秒、独立生命周期600秒及退出7秒不放宽。Hy 仍1024/192，不将原研究外层限制倒写为此前产品已有的资源门。

## 维护与恢复

1. 收到交接、实质进展或新失败时，先核精确提交与原件，再替换当前结论、责任和下一动作；关闭的排程直接删除，不另建历史页或替代日志。
2. 区分实现、本地检查、精确提交 CI 和用户真机；失败、未测、环境限制分别报告。本地 MSBuild/测试的 socket 权限限制不由独立格式检查解除，拒绝的动作不换路径绕过。
3. 执行前核二进制/模型身份、空间、锁和 owned 进程；清理须有实际退出证据。原件存入被忽略的工作区 `artifacts/` 或 CI artifact，不提交模型、原始日志或来源摘要清单；新证据不冒称旧原件恢复。
4. 按 [AGENTS](../../AGENTS.md) 与 [技能规则](../agents/skills.md) 做必要验证和独立审查；同一失败必须换取新证据，不以重试、延时或放宽断言掩盖。等待结果或授权期间继续有价值且独立获准的工作。
