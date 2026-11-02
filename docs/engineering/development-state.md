# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-02**，远端开发分支仍为 `work/linux-usability`，已核提交 [`dd2297500d556136b5f1344c919f18b2daeff166`](https://github.com/DingJWuyu/Typelay/commit/dd2297500d556136b5f1344c919f18b2daeff166)，tree `b5e33b451dd6d8456b5dd1a95ef342026f6c9e5c`。Browser Editing 两端完整消费者、GIMP 导航校准与 Qt/GTK4 窄观察已保存在该提交，不是未发布半链。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本次环境恢复时，未发布 17 文件的冻结 tree `05c838e0` 与独立 S09 候选再次不可读，远端旧 tree 查询为 404。当前重新 clone 精确 dd229750，按真实 CI575 失败重建必要修复；不把历史实现或验证摘要冒充原件，不继承丢失候选的通过。已成熟且审验的后继及时提交开发分支，再跟精确 CI，避免积累未保存源码；本地目录不承诺跨环境保留。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型；自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI575](https://github.com/DingJWuyu/Typelay/actions/runs/41281349343) 精确 head 为 dd229750，终态 failure。scope/shared 与双端 Index 通过，Windows/Linux 主链失败。Windows contracts、完整 Windows 包、Linux desktop 小诊断与两端终态日志已重新取回并核官方 SHA；以下区分实际通过、失败和未执行。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows contracts | App 实际执行 1446，1442 过、4 失败；Runtime 1556、Core 318、Protocol 28、Windows.Contracts 211 与独立 2 项通过 | 四个失败分别处理；不以测试通过率估算产品完成度 |
| FirstWake | ordered 两分支超原 150ms，首次 managed-focus 是主要耗时但非已证独因；相关源码与先前通过头一致 | 暂无已证安全替代，不预热、延预算或删源身份检查；不重复泛性能取证 |
| Appearance | 第二选择已发生；末 Rendering observer 先读 .598368，同帧 product 后为 .5；另有真实 271.686ms 帧隙 | tests-only 改最终读取相位，严格 .5/原中间断言/预算保留；不称修复动效性能 |
| LocalDraft | unarmed-prefix 在首 anchor 断言失败，未到后继 claim；实际取消原因未证 | 用现公开 cancel callback 的首个时间/身份/前台分类与失败末态做有界观察，非原 WinEvent、非原子快照，不增输入或等待 |
| Windows Browser | setup、automatic-preview-tab 过；新 Editing 确认场 90s 超时，后 6 场未跑。顺序 fixture 与 trace 只支持四种 Keep/click Replace 已完成，Enter 轮前半具体断点未知 | 复用原 Observe 响应记录阶段、计数及布尔/长度/选区，不额外读或输出正文，不新增 CDP/输入/等待；下一原件定位后最小修验 |
| Linux Browser | DOM 94 项中 92 过、2 失败：测试输入追加了初稿；隐式新 token 却期待旧确认 Esc | 真实 Ctrl+A/精确稿观察；显式区分同 token refresh 保稿/选区/焦点与真新 token 退休/零旧操作。tests-only 修正不冒新 DOM 已通过 |
| GIMP | 六场及正常退出、取消/确认 Disconnect 与清理通过 | 严格首屏/fullclip 校准已有实际闭环；原生 Undo 不支持边界保持 |
| Qt6 | capsule 语言 Auto/English 与编辑态 More Copy 已越过旧阻塞；后继主题几何失败前没有本次 collapse 的 spring settled | 原总界内关联实际 attempt/started/continuation/settled，再以同对象目标高度的 fresh 快照冻结 baseline；不松尺寸、稿/选区/焦点/Undo 断言 |
| GTK4 | 同 PID/address/代次 map→unmap 14ms，minimum 532×184 对 surface 448×100；无源 FocusOut、renew 更晚。官方 shadow 缓存路径与两轴 `532−90+6=448`、`184−90+6=100` 吻合 | 同 provider 完整 CSS 在 Popover 构造/挂 parent 前加载的新候选已双轴静审，CSS/阴影/宽度不变；未 C 编译/GUI。下轮核同实例首 map、尺寸、paint 与配置链 |
| Notepad | 5 场通过；接受/Undo 需可信物理输入未测，编辑回源/Undo 因依赖未测 | 不以注入替代可信输入，不称整端通过 |

仍有效的邻近依据：Onboarding 退休 opening 的旧 Show 复活及 nested/inherited/属性策略/Close/异常已有真实 WPF 回归通过；owned Replace click/Enter/Space、空稿、held/repeat 后新动作与 Keep 稿/选区/Undo 已有 CI574 证据。Queue 后续通过未解释原超时根因。Settings 深色标题窄修已有有效九图与样式节点通过，不等于整页材质/动效 1:1。

## 当前候选、owner 与恢复动作

| Owner | 当前工作 | 收束条件 |
| --- | --- | --- |
| 集成 / resume_typelay_ui_delivery | 已重取必要原件、恢复锁定工具并完成 12 文件成熟批适用检查；冻结发布与精确 CI 跟踪 | 新源码绑定独审与适用检查；回读 blob/tree/commit/ref，不继承旧 tree 或旧 GREEN |
| GTK4 / nov2_gtk4_initial_css | 单文件首 map CSS 顺序修已形成新固定候选，Spec/Standards 静审通过 | 原首次 paint/点击和后继配置链实际通过，诊断模式不作性能验收 |
| Linux / nov2_linux_followup | Browser 两测试构造修正与 Qt 稳定态前置已合当前批；独立只读核 GTK3 搜索边界 | parser/格式/语法检查与真实 DOM/Qt 分开；旧失败保留，不改生产 spring 或源权限 |
| Windows / nov2_windows_followup | Browser 阶段观察、Appearance 末帧读取、LocalDraft 窄取证及 S09 捕获分名 | 本批托管编译/格式已过，真实 Windows 原场景再验；诊断不是产品修复 |
| Standards / nov2_standards；Root | 固定源码独立工程标准审与独立 Spec/advisor；Root 负责用户里程碑沟通 | 后继修改另审差量，不让一轴通过代替另一轴或 GUI |

S09 两个 knownTotal 参数在 CI575 均 Passed，但 `File.Create` 共用 `model-paused.png` 只留下单图。新候选仅把两个既有截图按 known/unknown 分名，无新增截图、输入、等待或断言；取得两张新图前不关闭 known 暂停视觉。

GTK3 目标搜索不在本轮优先修复小批：丢失候选尚未恢复，现役源/目标选择与 Apply 保留；Qt 搜索壳亦未恢复。先让已证失败的必要修正得到下一次真实平台结果，再独立推进有用户收益的菜单搜索，不把等待当全局停止，也不为忙碌扩底层研究。

Agent 目标配置为 `gpt-6-astra` / `xhigh`，工具仅确认任务标识，实际配置未独立核实。作者只写 ignored 候选，集成唯一修改 live、文档和发布。标准检查由集成单 owner 串行，记录输入/预算/退出/清理；先快速格式再重编译，新权限、预算扩张或不确定并发再判断。

## 验证环境与未闭产品边界

本批 9 个代码/测试文件与 3 份产品/状态文档已分别完成 Standards 与 Spec 静审。最终 Linux 15 项观察契约、真实 CI575 trace 对照、三文件 Ruff/AST 通过；最后仅 test 格式机械调整，AST 等价。并集 diff、5 个 C# 文件的 folder-only whitespace 与两段现有 Browser JS 语法通过。App.Tests 首次 C# 产物已生成，但末尾 Copy 缺正式 native DLL 输出路径而失败；按项目路径放置已核同 SHA 的正式 DLL 后，仅重跑 App.Tests/Fixture Release build，均通过（8.07s，986 输入无漂、owned 清理为空）。原失败日志保留，不把恢复的 DLL 称本机 native 编译，也不把托管构建称 WPF/DOM/Qt/GTK 实测。

- 私有 SDK 10.0.401 已按官方元数据核完整 SHA512 恢复，Ruff 0.14.0/fontTools 4.61.1 按现锁安装；从已核正式包恢复 32 面字体并通过官方 verify，native DLL 核 SHA。工具恢复不是产品测试。两项目 locked restore 已过；原 NuGet 默认只读缓存失败保留，改用官方环境变量指定私有缓存，没有调整权限。
- 当前没有可用 DISPLAY/WAYLAND 或 GTK/Qt 开发 SDK、真实 WPF。既有 socket、系统安装、project-based formatter 与超 512MiB artifact 拒绝不换路线绕过；只用独立 folder 格式检查，未请求过大 Linux 完整包。
- 完整双端逐页/状态/真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭；S08 材质/中间组合像素、多 DPI 与跨页 Full 动效仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒原型完整呈现；下一真实结果决定后继修复。
