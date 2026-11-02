# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-02**，远端开发分支仍为 `work/linux-usability`，已核提交 [`27a18d8cae413f477d620910eb279906723c536f`](https://github.com/DingJWuyu/Typelay/commit/27a18d8cae413f477d620910eb279906723c536f)，tree `3c61396e580cf80c8d0f99d0fa8b4a512fd769d6`。新 20 文件搜索/编辑菜单/验证批已逐项核远端 blob/tree/parent/ref；Browser Editing 两端消费者、GIMP 导航校准等前序成果保持。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本次环境恢复时，未发布 17 文件的冻结 tree `05c838e0` 与独立 S09 候选再次不可读，远端旧 tree 查询为 404。当前重新 clone 精确 dd229750，按真实 CI575 失败重建必要修复；不把历史实现或验证摘要冒充原件，不继承丢失候选的通过。已成熟且审验的后继及时提交开发分支，再跟精确 CI，避免积累未保存源码；本地目录不承诺跨环境保留。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型；自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI577](https://github.com/DingJWuyu/Typelay/actions/runs/41389340074) 精确 head 27a18d8 已于 2026-11-02 05:03:58 UTC 终态 failure。范围、共享、双端 Index 通过，两主平台失败。Windows contracts/完整包、Linux desktop/Qt probe 小原件及两端终态日志已取回并核大小/SHA；Linux 完整包超过可读取上限，未取。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows contracts | App 实际执行 1446，1444 过、2 失败，22 未执行；Runtime 1556、Core 318、Protocol 28、Windows.Contracts 211 与独立 2 项通过 | 两红为 LocalDraft current/unarmed-prefix。前者有效 keyup 后 16ms AnchorInvalid；后者未 arm 的 pair 后 anchor 退休，cancel 当时 foreground 为 overlay。该回调不含原 WinEvent 生成时间/原 HWND，不能由当前焦点猜旧事件，保 500ms/真实失权拒绝 |
| GeneralMode / FirstWake | 本头 General 四组及 FirstWake 两分支通过，General 失败专用时间观察未触发 | 不解释 CI576 采帧前 56.5164 或旧 FirstWake 根因；不松 55.99/300ms/150ms |
| S09 / S08 静态 | CI576 known/unknown 两独立暂停图已核，文件覆盖子项已闭；同头四张 S08 指定图未见新可独立修的静态差距 | S08 end 是真实屏幕组合，quit 是无 owner 的 Content 图，不混判 scrim；模糊/饱和度、透明边缘、中帧与多 DPI 仍未闭 |
| Windows Browser | setup/自动预览过；More 越过上轮隐藏阻塞，4 种 Keep 已真实完成。首 Replace 轮（计划 click，实际尚未 ClickChoice）在 wait-confirmation 原 90s 超时，稿精确/connected/editor focus 为真、confirmation 为假；后 6 场未跑 | 不沿旧“点击未送达/菜单隐藏”标签猜；核首次 Replace 提示为何未出现。完整键鼠/IME、明确失败后再试与混版 GUI 仍未验 |
| Linux Browser | 95 个 DOM tests 中原 94 通过；新 More test 的 8 个 subcase 均在 reload 后 capture described=false，尚未开菜单 | 已定位新增循环漏真实 #source.focus，单行前置修候选不改断言；新 More 正向/7 负向仍待执行，不能声称生产 guard 失败或已过 |
| GTK3 目标搜索 | 本头 C 构建成功；ASCII筛选/空态/导航与 repeat、非首项 Enter、文案切换、重开和同 opening 选区 refresh 越过；首“日语”查询未进入真实 Entry，仍 Japanese/有焦点/IME Clear/零源写 | 仅非 ASCII fixture 输入复用既有 GTK Unicode-entry 真按键路线，保同 5s 总界/精确 text 与 code。生产过滤不改，pinyin 后继/Unknown 未实测仍分开 |
| GTK4 配置 | 首 preview paint/触发继续越过；新配置 popup 收到自身 GDK focus-out 后取消，事件归属已实测 | 固定 GTK 的 autohide grab crossing 也会合成同 popup out/in，不能按该事件豁免真失焦。已合窄 nonmodal 配置生命周期候选，保原失权/IME/期限及外点/键盘语义 |
| Qt6 | F23 probe 实际 second press/release 的 Qt key/native symbol 均 0、scan 8，严格 F23 门正确失败；新显式 unbundled 资格真实通过，正式用户流程已运行 | 先修该隔离 fixture 的 F23 映射寿命，不改 provider/断言。后继用户流程的 F7 身份实际正常，但消费者 published=0 拒绝；核 held-return 尾部与 idle 观察，theme/settled 更晚场景尚未到达 |
| GIMP | 本头完整 package 因原 Qt probe 失败未成，未执行；最近 CI575 六场/断连清理通过 | 保完整包依赖，不将独立 Qt build-input 路由冒发行包或 GIMP 验收 |
| Notepad / Continuous | Continuous 通过；Notepad 5 场通过，接受/Undo 两项可信物理输入依赖未测，没有新增失败 scene | 不以软件注入代替可信输入；Notepad 套件总体未通过，不称整端通过 |

仍有效的邻近依据：Onboarding 退休 opening 的旧 Show 复活及 nested/inherited/属性策略/Close/异常已有真实 WPF 回归通过；owned Replace click/Enter/Space、空稿、held/repeat 后新动作与 Keep 稿/选区/Undo 已有 CI574 证据。Queue 后续通过未解释原超时根因。Settings 深色标题窄修已有有效九图与样式节点通过，不等于整页材质/动效 1:1。

## 当前候选与恢复动作

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| 发布与集成 | 20 文件批已保存远端并核 CI577 终态；按本头原件收口后继必要修复 | 下一 12 文件已审验，冻结后保存并跟精确 CI，保持 `main`/`trunk` 不变 |
| Browser 编辑菜单 | reload 后公开 source.focus 前置与无票确认的精确同稿 echo 显示候选已合本地；保完整非 text 身份、draftRevision 防 ABA、原展示 renew 和真实 Replace 才起 500ms | 下一真实 DOM 执行新八分支，Windows 单独沿 actual stage 分析，不泛改通用身份 |
| GTK3 语言菜单 | 非 ASCII 查询改既有 Unicode-entry 真按键的 tests-only 候选已审验，ASCII/pinyin 原路不改 | 下一实际输入与精确 code 验证；不称原临时 keymap 是唯一原因 |
| GTK4 配置窗口 | 仅配置 child 的 nonmodal 候选已合本地并静态审查，重入与 controller 清理保持原生命周期 | 保原 top inactive/current/opening/IME/600ms；inside 不误关、outside 取消透传、Tab 越界与 Escape 不恢复旧源票，实际矩阵待验 |
| Qt 验证流程 | F23 仅本隔离子进程寿命内固定空 row 映射、退出后恢复的窄候选；新 route 已真实启动正式流程 | 下一真实 F23 pair 验证，保失败门；F7 原 paired-release 日志仅补事件身份，未作为 barrier，不改输入或等待；下一原件独立核 |
| Windows LocalDraft | 已定位 unarmed 分支的原生 foreground 撤销入口，但原事件早晚仍未决 | 无证据不忽略事件，真正离开即使返回仍失效；不靠重新激活、延时或宽门求绿 |

Qt 目标搜索壳仍未恢复；GTK3 本批只补现有语言子层的目标搜索与本地键盘选择，不冒完整原型方向头/交换/即时保存。后继按实际原件补有价值缺口，不重启模型研究或为忙碌扩诊断。

## 验证环境与未闭产品边界

当前后继 12 文件（8 代码/测试、4 文档）已完成源码审查与共享文件合并核对。最终组合 4 个 Python 文件 Ruff/格式/AST、Browser JS 语法与 diff 检查通过；`QtDesktopAdmissionTests` 10 项与 `FcitxTestManifestTests` 4 项均过，整组 1.31s，983 个输入文件未变，检查进程已退出。F7 日志补丁在固定上游 6 份完整源码上核 blob SHA，原/候选均以 `--fuzz=0` 实际应用通过；不作本批释放 barrier。

前序 20 文件批的完整 `test_build_bundle` 51/51 与 General App.Tests Release build 通过仍仅对应其已核字节，不冒充本批新增 native/DOM 验收。本批无 C# 改动，不重复该构建；GTK4 C、Qt 原生编译、F23 真实映射生命周期、Browser 新确认与 GTK3 Unicode 输入均待下一目标 CI。

- 本地 SDK 10.0.401 已按官方元数据核完整 SHA512 恢复，Ruff 0.14.0/fontTools 4.61.1 按现锁安装；从已核正式包恢复 32 面字体并通过官方 verify，native DLL 核 SHA。工具恢复不是产品测试。两项目 locked restore 已过；原 NuGet 默认只读缓存失败保留，改用官方环境变量指定工作区缓存。
- 当前没有可用 DISPLAY/WAYLAND 或 GTK/Qt 开发 SDK、真实 WPF。本地只执行 folder 格式检查和托管编译；DOM、GTK/Qt 与 WPF 运行依赖目标平台。Linux 完整 artifact 超过可读取的大小上限，当前采用独立 desktop/probe 原件。
- 完整双端逐页/状态/真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭；S08 材质/中间组合像素、多 DPI 与跨页 Full 动效仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒原型完整呈现；下一真实结果决定后继修复。
