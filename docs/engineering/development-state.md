# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-03**，远端开发分支仍为 `work/linux-usability`，已核提交 [`d7ec604289a367e7dc3505c1ea2e25a9b873135a`](https://github.com/DingJWuyu/Typelay/commit/d7ec604289a367e7dc3505c1ea2e25a9b873135a)，tree `7ee10dbd2cd495f9559c124f8c6e054bd3dd71cf`。新 11 文件桌面开始条件/配置录制与回源观察批已逐项核远端 blob/tree/parent/ref；Browser Editing 两端消费者、GIMP 导航校准等前序成果保持。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

2026-11-03 恢复时，未发布 11 文件批的 7 个代码/测试原件不可读；4 份已上传产品文档已按 Git blob 与 SHA256 精确恢复。当前从 d7ec6042 重建同范围修复，重新审查并验证，不把新候选称为旧冻结 tree `625c786c`，也不继承缺失原件的本地通过。本地目录不承诺跨环境保留，成熟后继及时保存开发分支并跟精确 CI。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型；自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI581](https://github.com/DingJWuyu/Typelay/actions/runs/41416674534) 精确 head d7ec6042 已终态 failure。范围、共享与 Linux Index 通过，Windows 主检查/Index 构建失败，Linux 五个步骤失败。两端终态日志、早 GTK/Qt 与 small desktop 原件已核大小/SHA。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows 构建 | NativeFixture 手列 KeyboardHook 时遗漏新增 ForegroundDelivery partial，三处 CS0103；这是本批集成漏项，不是平台波动 | 一行 Compile Link 候选已实测原 RED→GREEN；App.Tests/DesktopFixture 旧构建不覆盖该消费者。Windows 动态全部未执行 |
| Windows 待验 | CI580 FirstWake notice 158ms 超过原 150ms，LocalDraft 开始后退休；Browser 第三 Space 受理后 9ms 在 Return ContextLost，未到 fresh 重译 | 原期限定值不松。新 callback 开始回执与固定 Return 失败观察本头未运行；此前通过不能覆盖新头失败 |
| Linux Browser / Settings / 教程 | DOM、正式 Chromium/Firefox 与 rich 路径、设置及完整教程步骤通过；VS Code 正式入口通过 | 不冒物理输入/全部应用覆盖 |
| GTK4 glossary probe | 20 个 Entry 与 9 个 TextView 场景通过；下一场仍在准备源，capture 选区 0/0 不符原 UTF-16 4/9，尚未打开该次 glossary | 原单击返回早于实际 GTK 焦点事件；核原点击完成与单次 prepare 的先后，不当 expired 产品逻辑失败，不松精确选区 |
| GTK4 配置 | 真实内容盒 264、实际 Clear、Tab 行焦点、Shift+Tab 回 search、Pinyin 输入进入 Composing 均越过。后原行点按期间 search 实际提交“拟好”、Clear 为 true、save 为 0，Composing 后置失败 | 尚无 TypeLay 行实际收到该点击的证据，不能把 IME 候选窗可能命中当已证原因或产品越权。完整外点/自然越界/Escape 矩阵仍未跑 |
| Qt probe | unmodified reentry 已越过；ordered 场景在字体 policy 退休后等待新 editor 票回源超时 | 只读核现 release/焦点/票序列，不沿旧 F23 假设或放宽门 |
| GTK3 / Qt 正式链 | 新 settings helper 假设保存文件必有 AcceptShortcut，遇合法稀疏配置 KeyError。Qt 明确 admitted-build-input，非发行包验收；未到本次目标交互 | tests-only 补正式缺省 Tab/Esc，显式非法值仍拒；空/单键缺失公开回归，不报成 provider 故障 |
| GIMP / Hy / Notepad | 本头 GIMP 与 Linux Hy 因前置未执行；Windows 全部动态未执行 | CI580 GIMP 六场、Windows Hy 正式 15 场和 Continuous 全链为此前有效证据；Notepad 两项可信物理输入依赖仍未测，不能沿用为本头全绿 |

此前可用的视觉依据：CI580 Qt 正式 bundle 已过安全收展、语言、中文展开/settled 收起、浅深/选区/Undo/两段接受，8 张有效图可读但仍有文字展开/常驻关闭等差距。Settings 深色标题九图、S09 known/unknown 两图与指定 S08 静态图有真实原件，材质、中帧和多 DPI 仍未齐。Onboarding 退休 Show/nested/策略/Close 和 owned Replace 的真实 WPF 邻近证据保持，但不代替本头未运行。

## 当前候选与恢复动作

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| Windows 构建修复 | NativeFixture 增加现有 partial 的一行 Compile Link，独立审查通过；全仓显式 Link 消费者已核 | 原项目同三处编译 RED，候选零警告 GREEN。下一批恢复真实 Windows 验证，不改变任何业务或输入门 |
| 快捷键 helper | 在已保合法两步顺序上补正式缺省配置语义，空/单键缺失公开 RED→GREEN 已完成，最终 12/12 通过 | 原每项保存 ACK/风险确认与两次输入保持；显式非法值拒绝，真正互换无合法中间态仍提前拒绝 |
| Qt O02 图标 | 四文件独立候选已合入：26px 按钮、15px 原型展开/收起/More 矢量，中英无障碍与提示；原 NoFocus/长文显隐/动作/身份与主题更新保持 | QPainter 原依赖，无 QtSvg；本机仅静态检查，下一真实浅深/中英图与动效/Undo验证。hover 显隐和常驻 × 尚未改，不称 O02 完整 |
| GTK4 / Qt 未决场景 | 仅沿本头实际选区、IME 点按和新票回源原件定位 | 已足够解释则最小修；缺事实明确未决，不为旧失败持续扩探针，不拖成熟修复保存 |

Qt 目标搜索仍未恢复，现有 child 与配置退休链仅作只读比较。GTK3 当前目标搜索保源选择和显式 Apply，不冒完整原型方向头/交换/即时保存。后继按真实产品缺口推进，不重启模型研究。

## 验证环境与未闭产品边界

本次重新构建的 7 个代码/测试文件分别完成独立工程与需求审查。NativeFixture locked restore 已通过；原项目实际复现同三处 CS0103，增加现有 partial 的一行 Link 后 Release 构建通过，0 warnings / 0 errors（7.40s）。新 RED/GREEN 窗口 58.17s，984 个输入无漂移，检查进程已退出；只证明该项目可编译，不冒 Windows 动态或旧 Browser 故障已解决。

快捷键 helper 的同一份最终测试在原实现上复现三个缺字段 KeyError；两行 missing-only 默认修复后 12/12 通过（0.340s），含显式 null/非法值零输入、原文件不预写与原 10 项顺序/风险确认邻近。20 个受控输入前后 SHA 一致；使用现有 AT-SPI 端口替身，不是 GTK/Qt 实际投递。Qt 四文件的 Ruff/格式/AST/patch 检查已通过；两 Python 机械格式前后 AST 等价，两 C++ 字节未变。本机没有 Qt SDK，未编译或运行图形界面。

上述源码与恢复的产品文档已合并，最终四 Python 的 Ruff/格式/AST、12 项 helper 端口回归、项目 XML 和 diff 检查全部通过（1.09s，984 个输入无漂移，检查进程已退出）。代码/测试与各自新审验字节一致；本次不重跑无改动的共享套件，下一步冻结此 11 文件候选并保存开发分支，跟踪精确 CI。

前批 App.Tests/DesktopFixture 的本地构建并未覆盖手列 Link 的 NativeFixture，原局部通过保留但不能冒全工作区绿；新增加 partial 与配置 helper 后，必须按[验证清单](testing.md#修复交接检查清单)核所有实际消费者与稀疏配置。当前共享 C# / Linux 生产输入规则未改；CI581 共享通过不代替新 Qt 呈现与 Windows 动态链。

- 本地 SDK 10.0.401 已按官方元数据核完整 SHA512 恢复，Ruff 0.14.0/fontTools 4.61.1 按现锁安装；NuGet 使用官方环境变量指定工作区缓存，未改依赖或锁文件。本次只恢复 NativeFixture 所需托管依赖，未恢复旧本地字体/native DLL 或 GUI 环境；工具恢复不是产品测试。
- 当前没有可用 DISPLAY/WAYLAND 或 GTK/Qt 开发 SDK、真实 WPF。本地可执行正式共享测试、受控 Python/stdio、folder 格式检查与托管编译；DOM、GTK/Qt 与 WPF 运行依赖目标平台。Linux 完整 artifact 超过可读取的大小上限，当前采用独立 desktop/probe 原件。
- 完整双端逐页/状态/真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭；S08 材质/中间组合像素、多 DPI 与跨页 Full 动效仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒原型完整呈现；下一真实结果决定后继修复。
