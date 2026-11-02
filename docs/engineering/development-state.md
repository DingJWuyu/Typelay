# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-02**，远端开发分支仍为 `work/linux-usability`，已核提交 [`5f096fc073f6fd9167ed25ee75c03aafe6c6f9d6`](https://github.com/DingJWuyu/Typelay/commit/5f096fc073f6fd9167ed25ee75c03aafe6c6f9d6)，tree `919f7472b22ac137bde0f8ca1fbec8f00056bdc8`。新 12 文件配置焦点/编辑确认/真实输入验证批已逐项核远端 blob/tree/parent/ref；Browser Editing 两端消费者、GIMP 导航校准等前序成果保持。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本次环境恢复时，未发布 17 文件的冻结 tree `05c838e0` 与独立 S09 候选再次不可读，远端旧 tree 查询为 404。当前重新 clone 精确 dd229750，按真实 CI575 失败重建必要修复；不把历史实现或验证摘要冒充原件，不继承丢失候选的通过。已成熟且审验的后继及时提交开发分支，再跟精确 CI，避免积累未保存源码；本地目录不承诺跨环境保留。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型；自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI578](https://github.com/DingJWuyu/Typelay/actions/runs/41395176608) 精确 head 5f096fc 已于 2026-11-02 06:08:27 UTC 终态 failure。范围、共享、双端 Index 通过，两主平台失败。两端终态日志、Windows contracts/完整包、Linux small desktop 原件已核大小/SHA；Linux 完整包 701,527,657 bytes 超过可读取上限，未取。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows contracts | App 实际执行 1446，1445 过、1 失败，22 未执行；Runtime 1556、Core 318、Protocol 28、Windows.Contracts 211 与独立 2 项通过 | 唯一红是 ApplicationSheet hide 后第二次打开：首 hide/清背景/文件不变通过，第二次 SendInput 返回 2，但 owner routed 右键 0/0、候选 0，未进入第二 dialog。保原 3s/单输入，窄观察原 HWND 消息边界，不先改生产 |
| LocalDraft / General / FirstWake | 两个 LocalDraft 原红与本头 General/FirstWake 均通过 | 不解释旧 foreground 事件早晚或旧时序根因；未知原生时基不相减，不忽略真实失权、不松 500ms/300ms/150ms |
| S09 / S08 静态 | CI576 known/unknown 两独立暂停图已核；同头四张 S08 指定图未见新可独立修的静态差距 | S08 end 是真实屏幕组合，quit 是无 owner 的 Content 图；模糊/饱和度、透明边缘、中帧与多 DPI 仍未闭 |
| Windows Browser | setup/自动预览与 Editing 确认真过，4 种 Keep、3 种 Replace 完成，原源不变/零 submit。后继 glossary 因按钮 stale 超时，后 5 场未跑 | 本次 target-bound fresh capture 的保守 observedAt 比 request 早 1ms，原 fresh 门正确拒绝。核独立读取因果证据，不改公用观测时刻、permit/lease/acceptedAt 或期限；IME/混版 GUI 仍未全验 |
| Linux Browser | 真实 DOM 96/96、native messaging/路由及正式 desktop Browser 流程通过 | More 同稿 echo 与无票确认的新正负回归已执行；这不代替所有 Windows/IME/rich 变更场景 |
| GTK3 目标搜索 | 完整 GTK3 cooperation 步骤通过，非 ASCII 查询已越过；同稿、目录、真实输入法与零源写邻近保持 | 只完成现有目标搜索/本地键路由切片，不冒完整方向头、交换与即时保存；Unknown 专项未实测仍单列 |
| GTK4 配置 | 新 nonmodal 配置已保持原 search 焦点并成功打开；首次 report 尚未 paint、尺寸 0×0 被测试直接与 264 比较而失败 | 在原同一次 wait 内补当前 map 的真实 painted-geometry 前置，保 264 与原焦点/IME/期限断言；新四生命周期场景未到达，不称全面通过 |
| Qt6 | F23 probe 与完整合作包成功；本头真实 relocated-bundle。Ctrl/held-release/Undo、expand/reverse/reduced/safety 与六主题场已越过 | 后继同 revision 的 translationActions 刷新误关 language 菜单，现 view-retired 原件明确。窄修仅原 menuOnly 合同保 language，More 与真实身份/权限变化照旧退休；中文 collapse-settled 尚未到达 |
| Linux tutorial | 首自动 lesson 的真实 Tab 接受与推进通过；新 24 字符原文已生成 7 字符候选，但 practice-continuity-lost 后无接受资格，原 wait 失败 | 原 describe→allowField 返回已耗 169ms，超过原 150ms 资格；新四点观察区分排队/处理/返回，不重绑或延期限 |
| GIMP | 本头正式包六场及断连清理通过 | 保原普通应用、接受/编辑/按需、兼容 Undo 与设置消费者边界，不冒全部普通应用覆盖 |
| Notepad / Continuous | Continuous 通过；Notepad 5 场通过，接受/Undo 两项可信物理输入依赖未测，无新增失败 scene | 不以软件注入代替可信输入；Notepad 套件总体未通过，不称整端通过 |

仍有效的邻近依据：Onboarding 退休 opening 的旧 Show 复活及 nested/inherited/属性策略/Close/异常已有真实 WPF 回归通过；owned Replace click/Enter/Space、空稿、held/repeat 后新动作与 Keep 稿/选区/Undo 已有 CI574 证据。Queue 后续通过未解释原超时根因。Settings 深色标题窄修已有有效九图与样式节点通过，不等于整页材质/动效 1:1。

## 当前候选与恢复动作

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| 发布与集成 | CI578 终态与原件已核；下一 19 文件（15 代码/测试、4 文档）已合并并完成适用检查 | 冻结已验证字节、核远端 5f096fc 后提交开发分支，跟精确后继 CI；不混未闭半链 |
| Browser glossary | 已证真实 fresh RPC 的保守时钟下界造成 1ms 拒绝；普通 snapshot 时刻还用于许可/lease，不可直接抬高 | 独立因果字段两端与唯一 Runtime 消费者已合并并静态审查；同源码 Runtime 1565、InputFeedback 216 与托管构建通过；真实 Windows 后继术语链待验 |
| GTK4 配置窗口 | tests-only 同一 wait 已合当前 popup/search 已 paint 前置，静态审查与 Python 格式/语法通过 | 保原 5s/600ms、264、焦点与四生命周期后置；真实运行待下一 CI |
| Qt 语言菜单 | 已合原 valid+menuOnly 下无关 More 动作刷新保 language 的窄修，公开正负 fixture 与静态审查完成 | 同 revision 语言可用、More 退休与异身份关闭的公开回归；不扩大输入权限 |
| Windows ApplicationSheet | 第二 hide opening 的公开 HwndSource 消息边界观察已合，格式/Release 编译通过，未运行 WPF | 原 3s/一次右键不变，区分消息到达但未提升与此 hook 未见；不猜最终激活返回 |
| Linux tutorial | 新句已译但原读取超过 150ms 后正确退休；首等待读取的 allowField 四点诊断已合本地，受控 Python、真实 pipe 与 Host stdio 通过 | 保完整源/IME/epoch/写回门，先实际因果后最小修；不增加等候预算 |
| Windows LocalDraft | 本头两原红已过，原事件早晚仍无证实因果 | 暂停弱诊断；真正离开即使返回仍失效，不靠重激活/延时/宽门求绿 |

Qt 目标搜索壳仍未恢复；GTK3 本批只补现有语言子层的目标搜索与本地键盘选择，不冒完整原型方向头/交换/即时保存。后继按实际原件补有价值缺口，不重启模型研究或为忙碌扩诊断。

## 验证环境与未闭产品边界

当前 19 文件已完成独立源码审查与共享文件三方合并核对。7 个 C# 文件 folder-only whitespace、7 个 Python 文件 Ruff/格式/AST 与 diff 检查通过；Runtime.Tests、LinuxHost、App.Tests、DesktopFixture 四个 Release 构建均为 0 warnings / 0 errors。正式 `dotnet test` 运行 Runtime 1565/1565，InputFeedback 216/216，真实 GLib pipe 1/1、LinuxHost stdio 1/1 通过，包含新因果字段及诊断开关/原策略响应边界。整组 86.81s，993 个已冻结输入无变更，检查进程已退出。

这些是当前源码的受控控制器、进程/协议与编译证据，不是 GTK/Qt/WPF/DOM 新行为实测。Core 未变，仍只引用 CI578 对应的 318 项通过，不虚报重跑；新 native 菜单保留、paint 前置、Windows 只读捕获及失败观察需要下一目标平台结果。

- 本地 SDK 10.0.401 已按官方元数据核完整 SHA512 恢复，Ruff 0.14.0/fontTools 4.61.1 按现锁安装；从已核正式包恢复 32 面字体并通过官方 verify，native DLL 核 SHA。工具恢复不是产品测试。本次 Runtime.Tests 与 LinuxHost 的 locked restore 已过，未改依赖或锁文件；NuGet 使用官方环境变量指定工作区缓存。
- 当前没有可用 DISPLAY/WAYLAND 或 GTK/Qt 开发 SDK、真实 WPF。本地可执行正式共享测试、受控 Python/stdio、folder 格式检查与托管编译；DOM、GTK/Qt 与 WPF 运行依赖目标平台。Linux 完整 artifact 超过可读取的大小上限，当前采用独立 desktop/probe 原件。
- 完整双端逐页/状态/真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭；S08 材质/中间组合像素、多 DPI 与跨页 Full 动效仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒原型完整呈现；下一真实结果决定后继修复。
