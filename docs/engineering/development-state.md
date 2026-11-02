# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-02**，远端开发分支仍为 `work/linux-usability`，已核提交 [`7ae238cdc1e4a9b05c3e53bf810b3b31944da935`](https://github.com/DingJWuyu/Typelay/commit/7ae238cdc1e4a9b05c3e53bf810b3b31944da935)，tree `c84f74c7533bd9c980256cc8d9d222a6f8f251de`。新 12 文件修复/观察批已逐项核远端 blob/tree/parent/ref；Browser Editing 两端消费者、GIMP 导航校准等前序成果保持。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本次环境恢复时，未发布 17 文件的冻结 tree `05c838e0` 与独立 S09 候选再次不可读，远端旧 tree 查询为 404。当前重新 clone 精确 dd229750，按真实 CI575 失败重建必要修复；不把历史实现或验证摘要冒充原件，不继承丢失候选的通过。已成熟且审验的后继及时提交开发分支，再跟精确 CI，避免积累未保存源码；本地目录不承诺跨环境保留。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型；自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI576](https://github.com/DingJWuyu/Typelay/actions/runs/41383694261) 精确 head 7ae238cd 已于 2026-11-02 04:00 UTC 终态 failure。共享/范围与双端 Index 通过，两主平台失败。Windows contracts/完整包、Linux desktop/Qt probe 小原件及两端终态日志已取回并核官方大小/SHA；Linux 完整包超过大小边界未取。当前修复以本头事实为准，不继承旧失败标签或把未执行说成通过。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows contracts | App 实际执行 1446，1445 过、1 失败，22 未执行；Runtime 1556、Core 318、Protocol 28、Windows.Contracts 211 与独立 2 项通过 | 唯一失败是 GeneralMode `zh-CN/dark` 的采帧前断言：预期 ≤55.99，实际 56.5164。没有该例帧序列/终态，不能据合法超调断言初段已呈现；窄取证原 Select/布局/一次 Bounds 的时间，不松阈值或预算 |
| FirstWake / LocalDraft | FirstWake ordered 两分支、LocalDraft 16 项本头通过；失败专用 cancel 观察未触发 | 不解释旧超时/取消根因，不预热、延 150ms/500ms 或删源身份检查 |
| Appearance | retarget 实际 Passed，8 帧至 .5，末帧 pre/post 均 .5；最大相邻帧隙 140.625ms | 末态观察修正已有本头执行；不抹去 CI575 271.686ms 帧隙，也不称性能根因已解决 |
| S09 暂停 | 两参数 Passed，known/unknown 两张独立 920×700 有效 PNG 已逐图读；known 显示约75%与98.3/131.1KB，unknown仅98.3KB | 同名覆盖及该暂停静态子项已闭；不外推整页材质/所有状态和 DPI |
| Windows Browser | setup/automatic-preview-tab 过；新确认首次开菜单在1667ms失败，后6场未跑。Keep/Replace均0；trusted click 已打开菜单，同节点约12.6ms后隐藏，editor/document仍focused | 新阶段/菜单观察已排除“未送达点击”；沿原 view/焦点/关闭生命周期核原因，不再沿旧 Enter 90s 超时标签猜测，不重复输入或续期求绿 |
| Linux Browser / GTK3 | DOM 94/94 真实通过，旧两处输入/隐式token构造失败已闭；真实 Browser、Settings、Tutorial与GTK3流程通过 | 不等于Windows确认或完整IME/混版GUI；GTK3搜索是独立后继，未合入本头 |
| GIMP | 本头完整包前置因Qt probe失败未成，未执行；最近CI575六场/断连清理通过 | 保完整package依赖，未运行不计本头通过；原生Undo不支持边界保持 |
| Qt6 | 本头原async probe过；unmodified-reentry的second焦点真、generic press/release 1/1、队列0，但F23具名计数未出现，原负控失败，正式desktop因此未执行 | 窄键身份观察区分sender/Qt/provider；不以generic代替F23或修改输入求绿。评估既有显式unbundled独立用户流程，保发行包/完整probe失败门，不冒搬迁验收；主题settled前置尚未实际运行 |
| GTK4 | 首preview同实例map前后min均532×184，真实首paint/hover/trigger click已越过旧阻塞；unmap紧跟language-configuration主动hide。新配置begin=1/cancel=1/save=0，另popup mapped/search聚焦后top inactive回调取消 | 原CSS首map子项已闭，不把后继取消套旧shadow原因；核配置自己root/popup焦点生命周期与真实外窗区别，不恢复旧source或假active/grace |
| Notepad | 5场通过；接受/Undo两项因可信物理输入依赖未测 | 不以注入替代可信输入，不称整端通过 |

仍有效的邻近依据：Onboarding 退休 opening 的旧 Show 复活及 nested/inherited/属性策略/Close/异常已有真实 WPF 回归通过；owned Replace click/Enter/Space、空稿、held/repeat 后新动作与 Keep 稿/选区/Undo 已有 CI574 证据。Queue 后续通过未解释原超时根因。Settings 深色标题窄修已有有效九图与样式节点通过，不等于整页材质/动效 1:1。

## 当前候选与恢复动作

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| 发布与集成 | 已合 GTK3 搜索、Browser More 稿回显修正、Qt 独立流程资格及三处窄观察，共 15 个代码/测试文件；已完成组合检查 | 冻结这批及 5 份现役文档，核远端仍为 7ae238cd 后逐项发布；跟新头 CI，不继承旧 tree 或旧 GREEN |
| GTK3 语言菜单 | GTK3 真实目标搜索已重建并完成审查，保源选择/Apply、code/目录身份与原 IME 门；Unknown/Composing 的原搜索键不会落入通用 Escape 命令 | 下一真实 GTK 编译、搜索/空态/导航/同 opening/重开与 Composing 回归；Unknown 仍未实测，不把静态结论当平台通过 |
| Browser 编辑菜单 | More 仅在同合法 Editing、同 field/view/token 且 text 精确回显当前稿时保显示；原 control/regenerate 退休不变，新增 1 正向/7 负向公开场景 | 下一真实 DOM/Windows 验证，不称已证明 CI576 唯一根因；本地未运行 DOM，使用下一目标平台结果验证 |
| GTK4 配置窗口 | 原首 map 已越过；仅给原 inactive 信号附当前 controller/event surface/root/popup 身份及已有异步 compositor 观察，不改关闭条件 | 一份真实证据区分合法配置转焦与外窗切走；缺失/截断标 unknown，非原子观察不作性能验收 |
| Qt 验证流程 | 保原 F23 断言，附两 owned 字段最多 4 个键身份事件；完整 probe 失败时仅精确当前 build/default async 证明可进原显式 unbundled 用户流程 | 下一真实 Qt 证据；缺证明不运行，发行 pack/finalize/GIMP 与整体失败门保持，不把独立交互当发行包验收 |
| Windows GeneralMode | GeneralMode 只在原失败附 Select/布局/原一次 Bounds 的时间；新 App.Tests Release build 和单文件格式已过 | 下一真实 WPF 判定原采样前失败；不新增几何读取或放宽 55.99、300ms、3s |

Qt 目标搜索壳仍未恢复；GTK3 本批只补现有语言子层的目标搜索与本地键盘选择，不冒完整原型方向头/交换/即时保存。新 CI 期间按实际原件补有价值缺口，不重启模型研究或为忙碌扩诊断。


## 验证环境与未闭产品边界

当前 20 文件候选（15 代码/测试、5 文档）已完成源码审查。最终组合 5 个 Python 文件 Ruff/格式/AST、Browser JS 语法与 diff 检查通过；`test_build_bundle` 完整 51/51 通过（含新增 10 项资格/早失败回归），40.290s，983 个输入文件未变，检查进程已退出。General 单文件 folder-only whitespace 与 App.Tests Release build 新执行通过，57.25s；其源码末字节未变，不重跑同一构建。两处公共 runner 合并已核未覆盖各自新增行为。以上不等于 GTK/Qt native 编译或真实 WPF/DOM 验收。

- 本地 SDK 10.0.401 已按官方元数据核完整 SHA512 恢复，Ruff 0.14.0/fontTools 4.61.1 按现锁安装；从已核正式包恢复 32 面字体并通过官方 verify，native DLL 核 SHA。工具恢复不是产品测试。两项目 locked restore 已过；原 NuGet 默认只读缓存失败保留，改用官方环境变量指定工作区缓存。
- 当前没有可用 DISPLAY/WAYLAND 或 GTK/Qt 开发 SDK、真实 WPF。本地只执行 folder 格式检查和托管编译；DOM、GTK/Qt 与 WPF 运行依赖目标平台。Linux 完整 artifact 超过可读取的大小上限，当前采用独立 desktop/probe 原件。
- 完整双端逐页/状态/真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭；S08 材质/中间组合像素、多 DPI 与跨页 Full 动效仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒原型完整呈现；下一真实结果决定后继修复。
