# 当前开发状态与安排

本页只记录当前项目状态；要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准。

## 当前身份与最近 CI

截至 **2026-11-01**，唯一开发发布分支为 `work/linux-usability`。现已发布后续成熟批 [`e55cdf0501e5930b01a242699e4a9de052d6eca0`](https://github.com/DingJWuyu/Typelay/commit/e55cdf0501e5930b01a242699e4a9de052d6eca0)，26 文件（22 代码/测试与 4 项目文档），tree `506a04cc7df3ffc1207ef67462d8c9b149c5193f`，parent 为前批 20 文件的 `5e981387`。26 blobs、tree 映射、commit parent 与分支已逐项回读，非 force，未混入 Browser 半链。恢复基线曾从 GitHub 回读并重新 clone 精确 [`8d3cd433faf5aa9f4897fdaf85271544a9522e65`](https://github.com/DingJWuyu/Typelay/commit/8d3cd433faf5aa9f4897fdaf85271544a9522e65)，已提交源码、原型和技能可读。`main` 仍为 `09fe613a606073876483b51446211030a66a9597`，`trunk` 仍为 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

用户已确认待发布 31 文件范围，但原工作区及未发布原件在本次恢复时不可读。旧本地 tree `8cac726a`、Browser 后继 `2289adb8`、Qt 后继 `61784ed9` 的源码、补丁与验证日志均未在当前可访问工作区找到；精确 8cac726a GitHub tree 查询为 404。不能从记忆重造原件或继承通过。当前从 8d3 重建同范围的成熟子集，使用新源码身份、新审查与新检查；前批 20 文件及后续 26 文件均已实际提交，后者包含 owned Replace、已发布菜单的 Qt 点击修复、Settings 标题配色及失败窄诊断；不是找回旧原件。后继 Browser 完整确认、Qt 搜索壳及 S09 命名修不混入本次原 31 范围。

实现范围不变：原型 I–III 的真实用户流程与界面；IV 的 N06–N11 仅真实禁用前端和[能力缺口](../design/implementation-gaps.md)；V 的模拟桌面和定时译文不做。既有同语、旧模型、源选择、撤销与全部输入安全条件保持。没有两小时截止，不以单批 CI 代替完整交付。

## 当前可核证据与工具

- [CI574](https://github.com/DingJWuyu/Typelay/actions/runs/41277142145) 精确 head 为 e55cdf05，已于 06:00 UTC 终态 failure；scope/shared 与双端 Index 通过。Windows 主链仅 Notepad 可信物理输入依赖未测，Linux 主链 GIMP 退出、GTK4 配置入口与 Qt 语言选择后继失败。下列 CI573 结果仅归父基线，不外推新源码。
- [CI573](https://github.com/DingJWuyu/Typelay/actions/runs/41273079858) 精确 head 为 5e981387，已终态 failure；scope/shared 与双端 Index 生命周期成功，Windows/Linux 主链失败。下一成熟批按本头实际失败取证，不继承父基线通过。
- [CI572](https://github.com/DingJWuyu/Typelay/actions/runs/41157421806) 精确对应 8d3，已终态 failure；scope/shared 与双端 Index 生命周期通过，Windows/Linux 主检查失败，Qt iteration 按范围跳过。这些统计只属于父基线，不外推 CI573。
- 已重新下载并核官方 SHA 后安全解压 CI572 Windows contracts/source 与 Linux UI/desktop 四份小 artifact；原件在 ignored `artifacts/recovery/ci572/`。Windows 完整 artifact 360361246 字节也已核 SHA 恢复。Linux 完整 artifact 为 702105114 字节，既有 512MiB 工具拒绝不重试或换路线。
- 当前没有可用 DISPLAY/WAYLAND_DISPLAY。系统 Python 可导入 GTK3/Gdk/Atk/cairo，但不等于真实桌面可执行；旧 Xorg/Chromium socket 与系统安装拒绝不因环境替换绕过。Qt/GTK4 SDK 与真实 WPF/Browser 行为仍需目标平台。
- 私有 .NET SDK 10.0.401 已按 global.json 从官方元数据指定 URL 下载并核 SHA512 后解压；Ruff 0.14.0/fontTools 4.61.1 私有环境已按锁恢复；32 面字体从该 CI 正式包提取后通过现役字体锁验证，真实 native DLL 32768 字节亦重新提取核 SHA `b9ce5e64`。这仅是构建输入，不是本机 native/WPF 通过。新检查须记录实际输入与退出结果。
- 未发布候选只保存本地；不承诺跨环境持久备份。当前不上传到额外存储目标。

## 当前真实平台依据

CI574 已取得 Windows contracts 阶段结果：App metadata total 1468，实际 1446/1446 通过、零失败；Core 318、Runtime 1535、Protocol 28、Windows.Contracts 211 与独立 2 项均通过。较 CI573 新增 16 项均逐名 Passed，其中 7 项真实 owned WPF 确认界面、1 项手势过期/重试与 8 项管道契约；覆盖 Replace click/Enter/Space、空稿、held/repeat 后新操作、Click Keep 稿/选区/Undo 等。CI573 两个 WPF 失败本轮未重现，诊断没有新的失败因果证据。Windows shipped Browser 原 8 场已逐场核实通过，原 Settings-hidden 未重现但未有新失败因果；Linux Qt cooperative probe 本轮成功、待逐项原件。Notepad 与 CI573 相同：5 个实际场景通过，接受/Undo 需可信物理输入未测、编辑回源/Undo 因依赖未测，suite 因此 failed，不是新产品回归。Linux 正式 Settings、Tutorial、desktop/Browser、GTK3、VS Code 与 Hy 步骤通过；GIMP、GTK4 与 Qt desktop 后继失败见下表，不用 contracts 绿代替整端验收。

CI573 Windows contracts ZIP 已核官方 SHA 并解析：App metadata total 1452，实际执行 1430、通过 1428、失败 2；Runtime 1520、Core 310、Protocol 28、Windows.Contracts 211、独立两项均通过。原两红是普通源确认 en/dark/no-animation 的 TaskCanceled 与应用 sheet hide 退休的 dialog-did-not-close，本轮 CI574 均通过但根因未证；不是已过的 Onboarding 新回归。它们只属于已发 5e981，不外推本地 owned/Browser 后继。CI572 原失败仍保留作因果对照，不用一次通过解释全部历史波动。

| 场景 | 已有事实与边界 | 当前动作 |
| --- | --- | --- |
| Onboarding | CI573 原旧 Show 复活回归与新增 nested/inherited/属性策略/Close/异常 8 项全部实际 Passed | 提交前取消退休 opening 的原生 show 已有真实 WPF 子项证据，不外推全部页面/动效完成 |
| Queue 两分支 | CI573 allowInjectedInput True/False 均 Passed；CI572 的 100ms/1460 未重现 | 失败晚读诊断因此没有新的归因证据，不称已修超时根因；原期限/输入门不变 |
| FirstWake | CI573 ordered True/False 均 Passed；CI572 的 157ms/150ms 未重现 | 一次绿色不证明时序根因已消失，不预热、延时或删身份门 |
| Appearance | CI573 第二选择已实际发生并 Passed；trace 从已画 0.627867 重定向，后收敛 0.52947→0.50147→0.5 | 关闭失效测试前置，保原后置/预算；不称已修产品动效性能根因 |
| Settings | CI572 实图 Ctrl+C 成为 Ctrl+Shift+C、九 client 图透明；CI573 正式 settings-ui 已通过，新 Ctrl+C 风险图无 Shift，九 compositor 图均有效并已逐张查看 | CI574 新九图和公开样式节点回归通过，逐图确认深色标题低对比已修、浅深中英标题与关闭按钮可读；不外推可信物理源输入或整页材质/动效 1:1 |
| GIMP | CI574 已真实执行并通过前 5 个功能场景；退出后的首次 Disconnect(cancel) 尚未点击即被 fullclip 拒绝。新增只读诊断证实 target 已 showing/focused/enabled，depth 6 VIEWPORT 的 SCREEN 原点为未知值；不是焦点失败或 1×1 | 已复用现首屏严格校准：两原函数迁同一 support，当前 GIMP 每次导航取 fresh 同 viewport 校准；55 项受控及 Ruff 全过、双轴独审后合本地，真实首次点击/取消/确认断连待下一 CI。仍不以截图代替 clip |
| GTK4 | CI574 后继语言配置场仍在 typelay-menu-trigger 的 current painted geometry missing 处失败；CI573 DTO 已观测 422×44 previewBounds，不能与 CI572 首 paint 失败合并归因 | 已核首 map 后 18ms 于 native layout 中 unmap、候选子控件尚无 paint；前一实例 critical 不归此实例。仅下一 owned 配置 fixture 开官方 size-request 与同 PID/address/代次有限 marker，保原期限/清理和有界字节，不主动 measure；尚未证明 min 拒绝分支 |
| Qt6 | QWidget receiver 与两菜单不穿透修正后，CI574 cooperative probe 已通过；正式 Qt desktop 本次实际运行，已有控制/编辑返回与主题动效原件，后在 typelay-source-auto 控件不可用处失败 | 已核 language 接线和点位，原 160 条有限事件预算漏了耗尽后的首次 trigger。新增同预算保留 trigger/menu 开合与 probe 下 fullview 退休元数据已独审合本地；只供下一次归因，未证原因、不松退休门 |

CI573 Linux Browser、GTK3、Settings 与 Tutorial 正式链通过。Windows Browser 的 setup、automatic-preview-tab、glossary-lifetime 通过，on-demand-settings 在隐藏菜单的 Settings 按钮 0×0 处失败，后四场未运行；CI574 原 8 场已全过，失败专用诊断未触发，尚不能解释 CI573 根因。Notepad 仍仅可信物理输入依赖的接受/Undo 与编辑回源/Undo 两场未运行，其余 preflight/settings/preview/on-demand/exit 通过。上述均不外推新的 Browser Editing 确认。

## 当前责任线与下一动作

所有 Agent 目标配置为 `gpt-6-astra` / `xhigh`；派发工具只确认任务标识，实际模型/强度未独立核实。作者只写各自 ignored 候选，集成唯一修改 live、现役文档和发布。

| 责任线 / owner | 当前动作 | 完成依据与停止条件 |
| --- | --- | --- |
| 集成 / resume_typelay_ui_delivery | 恢复原件/工具；编排同原 31 范围新候选、独立审查与适用并集；已发布新26并跟踪精确 CI574；Browser 新候选独立修审，不追加未闭消费者链 | 每份源码身份对应实际检查，验证 blob/tree/commit/ref 后跟精确 CI；拒绝即停该动作，不换路线 |
| Windows / 集成 | Onboarding 与 owned 新真实回归已有 CI574 通过；CI573 两个后继 WPF 失败本轮未重现，窄诊断无新失败证据。Notepad 两物理输入依赖场仍未测 | 不把一次通过称时序根因已解；可信实际输入不能由注入或受控工具替代 |
| owned Replace / 集成 | 已发布 e55，新 16 项在 CI574 Windows contracts 全过，包含 7 项真实 WPF UI、1 手势与 8 管道契约；真实 click/Enter/Space、空稿、held/repeat 拒后新操作与 Keep 稿/选区/Undo 有证据 | 原 down 起 500ms 不续，与接受/写回分离；完整视觉与生产包邻近仍按场景，不外推普通物理源输入 |
| Linux 消费者 / gimp_disconnect_resume | CI574 GIMP 前五场通过、末 Disconnect 被未知 viewport 原点拒绝；已按现有首屏校准语义最小接线，55/55 与七文件 Ruff 通过、双轴独审后合本地 | 不降 focus/fullclip，不用截图冒坐标身份，不新增泛化诊断框架 |
| Qt 菜单 / rebuild_qt_menus | 已发布菜单点击修在 CI574 cooperative probe 通过；正式 desktop 语言 source-auto 后继不可用已有窄只读诊断，保持 160 总预算并区分 click/show/hide/view-retired；当前未本机编译或 GUI | 仅在可观察故障边界最小修验；无 SDK/GUI 不称本机真实 Qt 通过 |
| 独立 Standards / rebuild_standards | 新 Browser Windows v7、Linux v3 与三方合并均按固定源独审，无剩余静态阻塞；实际检查身份已核 | 新修正另审差量，不继承旧候选或编译为 GUI 通过 |
| Browser Editing / 集成，作者 browser_confirm_shared_windows + browser_confirm_linux_dom | 新 24 文件已双轴独审并与 e55 owned/菜单诊断三方合入本地。实际并集 12 阶段全过：Runtime 1556/1556、Program stdio 6/6、Input 213/213、Linux Host 与 Windows App.Tests/Fixture Release、folder-only、Node/Ruff；89.07s、879 输入无漂、owned 空。未发布，原公开双异步间隙、实际新 revision 与陈旧 render editing 的 RED 均保留 | 下一成熟批真实 DOM/键鼠/IME、Windows safe-false→retry 与混版交互待平台；非 Editing 完整确认与 rich 已变正文的 bookmark 限制仍未齐，不称全 Browser 完成 |
| GTK 标题栏 / rebuild_linux_evidence | 以 CI573 真实深色低对比图和官方 GtkWindow 默认 CSD 节点为依据，仅配对自有 settings 标题栏前景/背景；两文件已发布 e55cdf05，CI574 正式九图/样式节点检查通过，九张原图逐一核看，标题及关闭按钮邻近可读；本机 parser 未执行的记录不改写 | 保全局主题/原窗口几何/按钮；复用现九状态下一正常 CI，parser/样式上下文不代替最终像素 |
| GTK4 / gtk4_geometry_triage | 已核 GTK 4.22.5 官方 size-request 输出；仅该配置子进程启用，有界首尾各 128KiB 与现 map/unmap marker，3 个受控 StreamReader/取消用例与 Ruff 通过，双轴独审后合本地 | 未编译 GTK C/未 GUI；下一一次日志须关联同 PID/nativeAddress/代次，截断或未命中仍未决，诊断场不作性能验收 |
| Root / Spec与advisor | 用户沟通、产品边界与新候选独立 Spec；持续核原型余项 | 真实用户收益、原门不放宽，非仅测试列表完成 |

新 Browser 本地 24 文件包含完整两端消费者与同一 DOM schema，不单独发布任一半链。三方合并保留 e55 的 owned Replace 生命周期与原菜单失败观察。当前并集已实际通过，源码冻结后才准备包含必要架构/缺口/状态文档的下一批；CI574 已终态，GIMP 七文件校准消费者修正与 Qt 两文件窄诊断已成熟合本地，GTK4 三测试文件官方尺寸观察也已独审合入，与 GIMP 共用 geometry 测试只合并 import/新增捕获用例；最终 39 文件批的窄 Python 组合 58/58 与八文件 Ruff check/format 均通过，0.823s、877 输入无漂、owned 空。Browser 24 文件逐字不变；原 879 输入只有已单独验证的 GIMP/Qt/GTK 测试及诊断差量改变，未重跑无关 C# 全套。真实平台和未测项仍逐项记录，不用本地受控/stdio/编译代替。

所有重 CPU/动态检查统一单 owner：READY 后 fresh GO 才 START，终态先 END 与 owned 清理再分析；记录命令、cwd、输入、session、期限和 exit。不确定结果先核，不盲重跑。

## 仍未关闭的产品边界

完整双端逐页/状态验收未完成。owned Replace 已发布，7 项真实 WPF UI 与手势/管道邻近已通过，完整呈现与生产包邻近仍按场景核验；Browser 全键盘确认、Qt/GTK4/GTK3 O04 搜索及实际配置链、S08 材质/中间组合像素、跨页 Full 动效仍有缺口。后继 Browser/Qt 旧实现原件当前缺失，只能按实际基线重建并重验，不能称仍已本地交付。

### 模型能力边界

Index 仍默认，不扩模型。固定 Index profile 的中英双向 Glossary 发布行已在已提交源码中；风格/换种说法先前有界裁决未达标准，仍是未完成产品能力，不归 N06–N11 前端例外，也不重启优化研究。旧模型原始证据缺失，不声称重新评审。

输入来源、IME、焦点身份、接受资格、写回和 Undo 不因界面重构放宽；原 down+500ms、FirstWake 150ms 等预算保持。字体只按锁定来源校验准备，产品离线；安全退役和明确材质回退不能冒充原型完整呈现。
