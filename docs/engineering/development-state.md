# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-02**，远端开发分支仍为 `work/linux-usability`，已核提交 [`a217c077a6ab3148ae136b6a9da84f9c802abdb1`](https://github.com/DingJWuyu/Typelay/commit/a217c077a6ab3148ae136b6a9da84f9c802abdb1)，tree `b3a1666cd7a4f6b16aa5c09e321c8d3a8e658110`。新 19 文件术语读取因果/菜单刷新/回归观察批已逐项核远端 blob/tree/parent/ref；Browser Editing 两端消费者、GIMP 导航校准等前序成果保持。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本次环境恢复时，未发布 17 文件的冻结 tree `05c838e0` 与独立 S09 候选再次不可读，远端旧 tree 查询为 404。当前重新 clone 精确 dd229750，按真实 CI575 失败重建必要修复；不把历史实现或验证摘要冒充原件，不继承丢失候选的通过。已成熟且审验的后继及时提交开发分支，再跟精确 CI，避免积累未保存源码；本地目录不承诺跨环境保留。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型；自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI579](https://github.com/DingJWuyu/Typelay/actions/runs/41403063780) 精确 head a217c077 已于 2026-11-02 07:23:40 UTC 终态 failure。范围、共享与双端 Index 通过，两主平台失败。Windows contracts/完整包、Linux small desktop 与两端终态日志已核大小/SHA；Linux 完整包 701,012,853 bytes 超读取上限，未取。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows contracts | App 实际执行 1446，1443 过、3 失败，22 未执行；Runtime 1565、Core 318、Protocol 28、Windows.Contracts 211 与独立 2 项通过。此前 AppSheet hide 第二 opening 本头通过 | 三红为 Hy receiver 未收到输入、owned 空稿 Replace 回源后 fresh 门拒绝、Appearance hide-show 尚未触发。Desktop collection 明确非并行，各失败时段无另一 App test 重叠，不能据此改全局调度 |
| Owned Replace / Appearance | 空稿 Replace 在 75ms 完成回源证明、98ms 因 fresh-source-refused 退休，尚未 Bind，具体拒绝谓词缺证；Appearance 的窄中段前置未命中，`changed=false`，未实际 Hide/Show | 仅保留现 Broker 拒绝记录；Appearance 改用已有严格真实 in-flight 前置，保原曲线、预算与退休后置。未有可证生产修复，不松 500ms/新鲜度 |
| Hy 鼠标 / Settings | 鼠标单次 pair 后 receiver 0；失败时 foreground/hit 非 receiver，尚无同刻身份可证谁抢焦点。Hy Settings 前 9 场通过，cold-start-english 实际卡托盘图标发现，后 5 场未跑 | 保真实输入拒绝；托盘导航与模型冷启分开，不把找图标失败称推理失败，不重试/延时或扩模型研究 |
| Windows Browser | setup+8 场真实全部通过，包括 4 种 Keep/3 种 Replace、后继 glossary 2.348s、按需、直接选择/换焦及断连负控 | 新独立读取因果字段已越过原排序拒绝，snapshot/permit/lease 未改。完整物理 IME、混版 GUI、safe-false 重试及 rich 变更体验仍未全部验齐 |
| Linux Browser / GIMP | Browser 96 个 DOM 回归与正式 desktop 7 场通过；GIMP 正式包六场及清理通过 | 不冒全部普通应用/物理输入覆盖 |
| GTK3 | 本头在完整生产链第二次编辑点击被 Host 精确 revision 门拒：native 96，当前 97；真实 candidateHit、peer、target 已匹配，第一次编辑/接受已过 | 不直接松 revision。先核 96→97 的实际 view 差量，区分真实新 view 退休与无关刷新；前头目标搜索整链通过不代表本头整个合作链通过 |
| GTK4 配置 | 当前 popup 同 map 的真实 paint 已就绪；内容 min 252、PopoverContent 264、含阴影外层 342，原 0×0 前置已越过。测试把外层宽与内容 264 比较而止 | tests-only 公开测同实例内容 border bounds，保持 264 与完整不裁切；不改 CSS/阴影/原焦点 IME/期限。外点、自然 Tab/Esc 四生命周期后置仍未到达 |
| Qt6 | 正式 probe/合作包及 Ctrl/F7/Undo 通过；expand/reverse/reduced 已过。safety-hide 的敏感 metadata 在真实按钮 click 前处理，输入最终命中背景，未启动记录 | 原同一 wait 内先确认本次真实 click 和中间尺寸，再一次失效；保隐藏/零源写及预算。语言新正负只在显式入口、未接正常 CI，desktop 更晚菜单链也未到达，必须补实际接线，不冒 probe 已验 |
| Linux tutorial | 同第二句失败。新 ID164 发送/Host处理入口/回复入队同毫秒，调用恢复晚 80ms；本轮耗时在回复入队后的传输/调度集合，非已证 Host policy 慢。continuity-lost 早于 native 返回 | 收束旧服务耗时假说；公开 state 已复现跨 150ms 时界混合发布 ready=true/expired=true/focus=Other；单次构造共用局部时间已同测试红绿，后继 151ms 仍拒读取。尚不能认定为本头唯一因果，不重绑或延时 |
| Notepad / Continuous | Continuous 全链通过；Notepad 5 场通过，接受/Undo 两项可信物理输入依赖未测 | 不以软件注入代替可信输入；Notepad 套件总体未通过 |
| S09 / S08 静态 | 已核 known/unknown 独立暂停图及同偏好 S08 指定图，未发现新的可独立静态修补 | S08 end 为真实组合、quit 为无 owner Content 图；材质、透明边缘、中帧与多 DPI 仍未闭 |

仍有效的邻近依据：Onboarding 退休 opening 的旧 Show 复活及 nested/inherited/属性策略/Close/异常已有真实 WPF 回归通过；owned Replace click/Enter/Space、空稿、held/repeat 后新动作与 Keep 稿/选区/Undo 已有 CI574 证据。Queue 后续通过未解释原超时根因。Settings 深色标题窄修已有有效九图与样式节点通过，不等于整页材质/动效 1:1。

## 当前候选与恢复动作

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| 发布与集成 | a217c077 与 CI579 原件已核；后继 9 个代码/测试文件及 4 份现役产品文档已完成适用检查 | 冻结本批并核远端未变后发布，跟踪新精确 CI；不追加未成熟 GTK3/Hy 假说 |
| Browser glossary | 两端独立因果字段及唯一 Runtime 消费者已发布，Windows 原故障真实越过 | 保既有身份/时刻/许可门，继续剩余完整 GUI 验收，不扩大该字段用途 |
| GTK4 内容几何 | 已合 tests-only 公共内容 bounds，同实例 paint 关联与严格内容盒断言保留 | 同实例/同 map/已绘制的内容严格 264，后继完整配置行为下一真实运行 |
| Qt 安全收展 | 已合真实 click/中间尺寸之后才失效的 tests-only 前置；默认 session 已接现有语言正负项，原 180s 总界不变 | 原单次输入/同等待预算/严格隐藏零写不变，不改生产 spring |
| Windows 回归 | Appearance 前置与 owned 既存拒绝记录保留已审验并合入；Hy 仍缺证 | 无证不改生产或全局桌面策略，不补输入/放宽期限 |
| GTK3 view | 96→97 的实际 DTO 差量仍缺，未放宽精确 revision 门 | 取得真实 view 差量后再决策；未知、真实失焦/新 view 仍退休 |
| Tutorial state | 单次同步 state 的局部时间一致性修复已合；最终同测试 baseline 失败、候选通过，InputFeedback 217/217 | 下一真实教程链验证；不以受控反例冒 CI579 唯一因果，过期后读取仍拒绝 |

Qt 目标搜索旧壳仍未恢复；现只读比较现有 child 补搜索与复用 Host 配置退休链的最小范围，未开始新实现。GTK3 当前只补现有语言子层的目标搜索与本地键盘选择，不冒完整原型方向头/交换/即时保存。后继按实际原件补有价值缺口，不重启模型研究或为忙碌扩诊断。

## 验证环境与未闭产品边界

本批 9 个代码/测试文件已完成独立工程与需求审查。Windows 三文件 folder-only whitespace、五个 Python 文件 Ruff/格式/AST 与 diff 检查通过；App.Tests Release 增量构建 0 warnings / 0 errors，57.10s 检查窗口的 985 个输入无漂移。Tutorial 最终同测试字节在原生产上实际失败，候选公开 state 1/1、完整 InputFeedback 217/217 通过；恰 150ms 合法、后继 151ms 状态和 capture 退休/零新请求均保留。各检查进程已退出，合并后文件与已审验候选逐字相同。

当前共享 C# 生产与 a217c077 相同，可沿用 CI579 的 Runtime 1565、Core 318 等真实结果，但本批未重跑这些无改动套件。GTK4 C fixture 与 Qt/GTK/WPF 新行为未在本机编译或运行；真实内容宽度、动画失效前置、语言执行接线、Appearance 与 owned 拒绝记录需下一目标 CI。受控 state 红绿不能替代教程整链或证明 CI579 的唯一根因。

- 本地 SDK 10.0.401 已按官方元数据核完整 SHA512 恢复，Ruff 0.14.0/fontTools 4.61.1 按现锁安装；从已核正式包恢复 32 面字体并通过官方 verify，native DLL 核 SHA。工具恢复不是产品测试。本次 Runtime.Tests 与 LinuxHost 的 locked restore 已过，未改依赖或锁文件；NuGet 使用官方环境变量指定工作区缓存。
- 当前没有可用 DISPLAY/WAYLAND 或 GTK/Qt 开发 SDK、真实 WPF。本地可执行正式共享测试、受控 Python/stdio、folder 格式检查与托管编译；DOM、GTK/Qt 与 WPF 运行依赖目标平台。Linux 完整 artifact 超过可读取的大小上限，当前采用独立 desktop/probe 原件。
- 完整双端逐页/状态/真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭；S08 材质/中间组合像素、多 DPI 与跨页 Full 动效仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒原型完整呈现；下一真实结果决定后继修复。
