# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-31**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。本批恢复基线为 [`94c1c2f9`](https://github.com/DingJWuyu/Typelay/commit/94c1c2f9ac3665985dbe4d7493f2b6b156a8c200)。`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`，`main` 保持 `09fe613a606073876483b51446211030a66a9597`；本开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 恢复状态与验证边界

执行环境替换后，已从远端重新 clone 并核对上述提交与 clean 工作树。旧环境中未提交的候选、工具、截图和模型原始证据没有恢复，不能用旧路径、摘要或旧审查冒充现有原件。仍需的候选从当前已提交源码重新构造，在被忽略的独立目录中验证后集成；不复活仅在旧记录中存在的修改。

[CI570](https://github.com/DingJWuyu/Typelay/actions/runs/41085150084) 对应精确提交 `94c1c2f9ac3665985dbe4d7493f2b6b156a8c200`，所有 job 已终态。共享检查、范围选择和双端 Index 独立生命周期成功；Windows 与 Linux 主检查失败。以下仅陈述已恢复原件覆盖的边界，不继承前一提交结果。

- Windows：早期诊断与完整 artifact 已下载、核 SHA 并安全解压，TRX、场景 JSON、日志和截图可读。
- Linux：早期与桌面小诊断 artifact 已下载、核 SHA 并安全解压，另有终态 job 日志。完整 artifact 为约 701 MB，超过工具 512 MiB 上限，未下载；不换路径规避限制，也不将小诊断称完整产物。
- 当前执行机已恢复官方 .NET SDK 10.0.401、校验官方 SHA 的 PowerShell 7.6.6 和仓库锁定的 Ruff/fontTools 私有环境。PowerShell 使用工作区标准 XDG 缓存。系统 Python 有 GTK3/Gdk/Atk/cairo，缺 GTK4、AT-SPI typelib、Xvfb 与原合作构建 SDK；私有 Xorg dummy 单次启动因本地/Unix socket 无法建立而终态失败，自有进程已退出；未改用 TCP 或其它路径。现有 GTK 库不等于已经恢复桌面验收能力，Windows 实际运行仍依赖后续精确提交 CI。
- 系统包只读检查受到 `/etc/apt/apt.conf.d/80-applied-apt-retries` 权限限制；一次正式提权尝试被执行器的只读 sessions 挂载前置阻止，命令未执行。不重试该路径或绕过访问限制，继续用户态工具、现有能力与 CI。

### CI570 Windows 实测结果

App TRX 共 1444 项，执行 1422，1413 通过、9 失败；独立 2 项另行通过。Runtime 1507、Core 310、Protocol 28、Windows.Contracts 210 全通过。9 个失败分别是：

1. 五个 owned 重译确认场景停在初始鼠标交接的测试观测。原件均记录同 HWND、计划坐标的唯一 `WM_LBUTTONUP`，低位 nonce 为 1 至 5，生产 hook 轨迹到 `Complete`；fixture 期望的高 32 位签名未出现在返回值中。当前最小候选只将发送标签放入正 32 位范围，继续完整 `nuint` 精确比较和所有后续安全断言。这是已观察链路的标签兼容问题，不是 Windows API 一概只有 32 位，也尚未证明后续产品流程通过。
2. FirstWake 两种参数的真实冷路径总时长为约 155/158 ms，超过原 150 ms 而安全拒绝；managed-focus 约 64/68 ms。原因和优化仍未闭，不预热、跳检查或延长期限求绿。
3. Appearance retarget 采样跨过要求的中段，第二次选择未触发；此红不证明重定向行为已经退化，也不能删除实际中帧要求。
4. Onboarding 旧 Show 尾部用例已观察原窗口隐藏，失败的是目标窗口未 active；不能说成旧尾重新显示。需要精确阶段证据，不令生产无条件抢焦。

Browser 的 setup、automatic-preview-tab、browser-editor-glossary-lifetime 三场实际通过；首失败是第四场 `on-demand-settings/open-real-settings` 的 `preview-settings-hidden`，剩四场未运行。原术语等待 enabled 与新源退休链本头已走通，不再把它当本轮首阻塞。Notepad 五个可执行场景通过，两项 `trusted-source-input-required` 未运行，cleanup 为空；软件注入不代替可信物理输入。双端 Index 的成功不等于桌面、完整 UI 或语言质量认证。

### CI570 Linux 实测结果

严格原生构建、托盘、真实下载进度、浏览器 Chromium/Firefox 生产模型与写回、隔离 GTK 事务等已取得通过子项。六个真实桌面首失败各自归因，不能混称翻译不可用：

| 场景 | 首失败与已知边界 | 下一动作 |
| --- | --- | --- |
| Settings | `gtk_settings_combo.py:31` 报 combo clipped；原错误未给双方几何，截图不足以证明完整 clip | 保留 full-clip 门，补最小当前几何/实例诊断，不猜修布局 |
| Tutorial | 恢复源语言时控件不能 focus/scroll fully into view；失败图中练习窗口仍 active | 核已有显式窗口激活的公开用户路径，不让生产自动抢焦 |
| GIMP | 正常退出阶段目标 rect 为 `(257,177,1,1)`、viewport 为 `(258,178,456,613)`；此前真实模型预览、接受/编辑、按需写入五项通过 | 区分未分配与隐藏旧 peer，保完整 clip 和输入资格 |
| GTK3 | 精确语言控件焦点前置未 detach；首次 selector 操作后 `languageTransitions=[]`、source `focusOut=0`，尚未到 authority 负控 | 取得该点击的身份与 receipt，不能称安全拒绝门失效 |
| GTK4 | trigger 首次 map/layout 后约 20 ms 内 unmap，缺 current painted geometry；Host 直到测试清理才 hide，begin/cancel/save 均为 0 | 区分 toolkit popover layout 与生产主动隐藏，不延时或续租 |
| Qt6 | 最后 Settings 组合键保存未完成；F1 接受/F24 退出仍显示 F1 风险确认 | helper 消费真实整组确认，不按本次 chord 猜测；原期限内显式唯一确认并核磁盘与 draft |

Qt 的真实模型、Accept/Undo、Exit 零写、editor return/fresh accept/default Tab/Undo 子结果均为 true；最后设置组合仍未完成，不能据此前子项把整场算通过。已检查的失败状态均 modelReady=true、modelFailureCode=null。合作与设置失败分别由各 owner 收敛。

## 当前候选验证

本批成熟并集包含测试侧 owned 鼠标标签、Onboarding/Browser 既有失败输出接线、Qt 整组风险确认消费者与显式 CI 回归、Tutorial 原预算内显式切窗确认、Settings 裁剪字段、GTK3/GTK4 固定观察，以及仅 opt-in 的 FirstWake 分段诊断。均已按精确差量完成规范/安全与产品 Spec 审查；诊断不是产品修复，不改变焦点、IME、输入许可或期限。

首轮完整构建在 App.Tests 托管输出后因 Linux 缺少 Windows native DLL 的后置复制而失败，未报告完整通过。现从已校验 CI570 同头发行 ZIP 中白名单恢复真实 32768 字节 DLL，SHA `b9ce5e6446c6fcb948b85e544d8d192a645f361d2ca775c1d49709d2ad47a85b`；其 C 源与构建脚本仍与94c1相同，仅供托管构建后置复制，不冒称新 Linux native 编译或 Windows 执行。完整字体已通过官方锁校验。最终13文件并集的 App.Tests 与真实 DesktopFixture full 托管构建均0警告/错误，119项 Python、全 Ruff/format、portable Contracts 211/211和7份受影响 C# 的官方 folder whitespace 校验通过，972源码输入与完整字体前后无漂。Browser 的机械格式经 Roslyn C#12 全 token/非布局 trivia逐字等价后才采用，并重新编译 DesktopFixture。首轮缺 native 输入与 formatter 参数错误保留为失败记录，后续只修对应工具/输入条件。上述均不是 Windows 实际行为或 GTK4 严格 native 编译/真实桌面通过，完整 solution格式与真实平台仍待本提交的正式 CI。

## 当前负责人及下一动作

所有责任线的目标配置为 `gpt-6-astra` / `xhigh`。本次新代理已显式按目标请求，但工具仅返回任务标识，实际模型/思考强度没有独立确认，均标为未核实。

| 责任线 / 当前 owner | 当前动作与依赖 | 完成依据 |
| --- | --- | --- |
| 集成 / resume_typelay_ui_delivery | 恢复官方工具与现役证据；唯一负责 live 合入、状态文档和发布。旧候选不继承 | 新源码身份、双轴审查、适用检查、远端精确提交回读及其 CI |
| Windows / windows_recovery | owned 标签、Onboarding输出及FirstWake opt-in诊断已双轴审查，最终并集托管编译与portable合同通过，等待精确CI；Appearance按原件继续归因 | 五条原公开路径及完整 fresh Bind/Accept/Undo/零误写，另三红分别闭环 |
| Linux 设置 / linux_recovery | Qt 整组风险确认 helper 已同边界 RED→5/5、邻近116/116及双轴审查，CI显式消费新增回归；Xorg反馈环受阻，设置/教程/GIMP不猜同根因 | 同 helper RED/GREEN 与真实设置路径分层验证；保原期限/确认/full-clip |
| 合作浮层 / cooperative_recovery | GTK3 首点击 receipt 与 GTK4 固定信号上下文/版本窄诊断已双轴审查；严格GTK4编译和真实行为待CI，不碰设置 owner 的 helper | 实际正反路径、当帧几何与原输入安全门，诊断不冒修复 |
| 独立规范/安全 / independent_review | 对重建候选的精确 diff 独审，Windows 标签已无阻断；后续检查 Linux 确认与合作诊断 | 实际源码、现役规范、既有消费者和未验边界 |
| 根主控 / Root | 产品/Spec 决策与独审；Windows Browser 第四场设置入口诊断已入本批，完整确认功能由独立 browser-confirmation-candidate 重建，半套不合 | 精确场景原件与冻结规格，不据摘要猜失败顺序 |

本提交只包含已经达到当前适用验收边界的成熟差量，真实平台尚未走到绿色，仍标明未验，不自动将诊断写成已修；CI 等待期间继续现役独立缺口，不追加无新假设的重复运行。

## 仍未关闭的交付边界

完整双端 25 项与同偏好逐页验收尚未完成。S08 整体模态材质与入场实际组合像素、owned 完整确认/Replace、Browser 完整键盘确认、GTK4 O04 和其他页面 Full 动效仍有缺口。旧未提交 shader 接线/渲染、Linux pulse、Qt HFW、O03 与浏览器确认候选均不可按旧摘要直接合入；需要时从现基线重新准备。CI570 的两次官方 FXC shader 字节原件已恢复且各352字节一致，状态仍为 generated-twice-identical-not-rendered；不称饱和度材质已交付。字体包已有 32 面锁定资源，不重新发明字体体系，当前已按官方prepare从锁定缓存重建并验证完整本地bundle，首轮不完整输出未用于编译；实际跨平台页面/状态仍逐项核对。[原型对照](../design/prototype-parity.md)维护产品关闭条件。

B01 固定 Index profile 的中英双向 Glossary 发布行已在提交源码中，仍仅按已批准 feature-delta 条件准入。B02 自然/正式风格与换种说法此前有界裁决未达标准，属于本轮未可靠交付功能，不归入第四类仅前端范围；保持门禁，不新增 prompt、采样、模型研究或质量试验。旧模型原始证据当前本地缺失，不声称重新评审或恢复；实现与既有裁决不据此自动扩权。

## 持续约束

- 输入来源、焦点、IME、租约、接受资格、写回及 Undo 不因重构放宽。原 down+500 ms、FirstWake 150 ms 等预算保持；诊断不产生新的许可。
- 动态桌面/浏览器与重 CPU 构建统一单 owner：READY 不是运行，fresh GO 后立即 START，终态先 END 并清理再分析。记录命令、独立 cwd/源码身份、session、期限、日志与 exit；不确定结果先查，不盲重跑。
- 字体只按固定来源和校验和在构建时准备，产品离线；GTK3 语言菜单没有背景模糊时用对应浅深实底保证可读，继续披露材质差异，不扩大为交互降级。
- 保留 Argos/Hy 显式兼容与方向，新空库存默认 Index；不自动下载/替换模型，失败保旧代。模型权重不进入源码。
- 唯一发布分支是 `work/linux-usability`，非 force、推送前核远端身份；不更新 main/trunk、不自动 PR、不发布稳定版或体验推荐。
