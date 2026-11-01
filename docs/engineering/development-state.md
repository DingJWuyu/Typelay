# 当前开发状态与安排

本页只记录当前项目状态；要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准。

## 当前身份与范围

截至 **2026-11-01**，唯一开发发布分支为 `work/linux-usability`。现已发布新成熟重建子集 [`5e9813871969aef008a0dfeba81e51ba4a256a95`](https://github.com/DingJWuyu/Typelay/commit/5e9813871969aef008a0dfeba81e51ba4a256a95)，20 文件（16 代码/测试与 4 项目文档），tree `27db4eb2b0f009bf47769cdc2ca3a076c5985be4`。20 blobs、tree、commit parent 与分支已逐项回读，非 force，未混入后继半链。恢复基线曾从 GitHub 回读并重新 clone 精确 [`8d3cd433faf5aa9f4897fdaf85271544a9522e65`](https://github.com/DingJWuyu/Typelay/commit/8d3cd433faf5aa9f4897fdaf85271544a9522e65)，已提交源码、原型和技能可读。`main` 仍为 `09fe613a606073876483b51446211030a66a9597`，`trunk` 仍为 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

用户已确认待发布 31 文件范围，但原工作区及未发布原件在本次恢复时不可读。旧本地 tree `8cac726a`、Browser 后继 `2289adb8`、Qt 后继 `61784ed9` 的源码、补丁与验证日志均未在当前可访问工作区找到；精确 8cac726a GitHub tree 查询为 404。不能从记忆重造原件或继承通过。当前从 8d3 重建同范围的成熟子集，使用新源码身份、新审查与新检查；上述 20 文件现已实际提交，属于原批准范围的成熟子集，不是找回旧原件。后继 Browser 完整确认、Qt 搜索壳及 S09 命名修不混入本次原 31 范围。

实现范围不变：原型 I–III 的真实用户流程与界面；IV 的 N06–N11 仅真实禁用前端和[能力缺口](../design/implementation-gaps.md)；V 的模拟桌面和定时译文不做。既有同语、旧模型、源选择、撤销与全部输入安全条件保持。没有两小时截止，不以单批 CI 代替完整交付。

## 当前可核证据与工具

- 新 [CI573](https://github.com/DingJWuyu/Typelay/actions/runs/41273079858) 精确 head 为 5e981387，已终态 failure；scope/shared 与双端 Index 生命周期成功，Windows/Linux 主链失败。下一成熟批按本头实际失败取证，不继承父基线通过。
- [CI572](https://github.com/DingJWuyu/Typelay/actions/runs/41157421806) 精确对应 8d3，已终态 failure；scope/shared 与双端 Index 生命周期通过，Windows/Linux 主检查失败，Qt iteration 按范围跳过。这些统计只属于父基线，不外推 CI573。
- 已重新下载并核官方 SHA 后安全解压 CI572 Windows contracts/source 与 Linux UI/desktop 四份小 artifact；原件在 ignored `artifacts/recovery/ci572/`。Windows 完整 artifact 360361246 字节也已核 SHA 恢复。Linux 完整 artifact 为 702105114 字节，既有 512MiB 工具拒绝不重试或换路线。
- 当前没有可用 DISPLAY/WAYLAND_DISPLAY。系统 Python 可导入 GTK3/Gdk/Atk/cairo，但不等于真实桌面可执行；旧 Xorg/Chromium socket 与系统安装拒绝不因环境替换绕过。Qt/GTK4 SDK 与真实 WPF/Browser 行为仍需目标平台。
- 私有 .NET SDK 10.0.401 已按 global.json 从官方元数据指定 URL 下载并核 SHA512 后解压；Ruff 0.14.0/fontTools 4.61.1 私有环境已按锁恢复；32 面字体从该 CI 正式包提取后通过现役字体锁验证，真实 native DLL 32768 字节亦重新提取核 SHA `b9ce5e64`。这仅是构建输入，不是本机 native/WPF 通过。新检查须记录实际输入与退出结果。
- 未发布候选只保存本地；不承诺跨环境持久备份。当前不上传到额外存储目标。

## 当前真实平台依据

CI573 Windows contracts ZIP 已核官方 SHA 并解析：App metadata total 1452，实际执行 1430、通过 1428、失败 2；Runtime 1520、Core 310、Protocol 28、Windows.Contracts 211、独立两项均通过。现两红是普通源确认 en/dark/no-animation 的 TaskCanceled 与应用 sheet hide 退休的 dialog-did-not-close，正在按实际原件定位；不是已过的 Onboarding 新回归。它们只属于已发 5e981，不外推本地 owned/Browser 后继。CI572 原失败仍保留作因果对照，不用一次通过解释全部历史波动。

| 场景 | 已有事实与边界 | 当前动作 |
| --- | --- | --- |
| Onboarding | CI573 原旧 Show 复活回归与新增 nested/inherited/属性策略/Close/异常 8 项全部实际 Passed | 提交前取消退休 opening 的原生 show 已有真实 WPF 子项证据，不外推全部页面/动效完成 |
| Queue 两分支 | CI573 allowInjectedInput True/False 均 Passed；CI572 的 100ms/1460 未重现 | 失败晚读诊断因此没有新的归因证据，不称已修超时根因；原期限/输入门不变 |
| FirstWake | CI573 ordered True/False 均 Passed；CI572 的 157ms/150ms 未重现 | 一次绿色不证明时序根因已消失，不预热、延时或删身份门 |
| Appearance | CI573 第二选择已实际发生并 Passed；trace 从已画 0.627867 重定向，后收敛 0.52947→0.50147→0.5 | 关闭失效测试前置，保原后置/预算；不称已修产品动效性能根因 |
| Settings | CI572 实图 Ctrl+C 成为 Ctrl+Shift+C、九 client 图透明；CI573 正式 settings-ui 已通过，新 Ctrl+C 风险图无 Shift，九 compositor 图均有效并已逐张查看 | 该映射/采集子项已获真实控件证据；深色标题栏浅底白字低对比另做窄修，不外推可信物理源输入或整页 1:1 |
| GIMP | CI572 末尾 Disconnect 已可见，但 focus/fullclip 失败；CI573 因 Qt probe 失败、正式组合包前置未成而 skipped | 等正式包前置恢复后执行现诊断；本轮无新 GIMP 结论，不放宽门 |
| GTK4 | CI573 DTO 菜单场景已观测 422×44 previewBounds；后继配置链触发器缺当前 paint 几何，begin/cancel/save 均未发，伴随 g_object_ref critical | 与 CI572 首 paint 失败分开；尚无因果证据，不猜修、不额外 present/主动 measure |
| Qt6 | CI573 module/fixture 真实编译通过，cooperative 在 source glossary 激活失败：菜单内点击先被 outside filter 关闭，再落到底层，Qt desktop 因此前置失败而 skipped | 官方 Qt 分发链已核 QWindow 预分发；仅 QWidget receiver 外点判断与两菜单不穿透的修正已本地合入，静态双轴审过，真实下一 Qt 运行待验 |

CI573 Linux Browser、GTK3、Settings 与 Tutorial 正式链通过。Windows Browser 的 setup、automatic-preview-tab、glossary-lifetime 通过，on-demand-settings 在隐藏菜单的 Settings 按钮 0×0 处失败，后四场未运行；正在沿现役消费链定位，不继承 CI572 八场通过。Notepad 仍仅可信物理输入依赖的接受/Undo 与编辑回源/Undo 两场未运行，其余 preflight/settings/preview/on-demand/exit 通过。上述均不外推新的 Browser Editing 确认。

## 当前责任线与下一动作

所有 Agent 目标配置为 `gpt-6-astra` / `xhigh`；派发工具只确认任务标识，实际模型/强度未独立核实。作者只写各自 ignored 候选，集成唯一修改 live、现役文档和发布。

| 责任线 / owner | 当前动作 | 完成依据与停止条件 |
| --- | --- | --- |
| 集成 / resume_typelay_ui_delivery | 恢复原件/工具；编排同原 31 范围新候选、独立审查与适用并集；已取得 CI573 终态原件；准备 owned Replace、Qt 点击修、Settings 标题配色及两 WPF 失败窄诊断的下一批，Browser 半链隔离 | 每份源码身份对应实际检查，验证 blob/tree/commit/ref 后跟精确 CI；拒绝即停该动作，不换路线 |
| Windows / rebuild_onboarding | Onboarding 新旧 9 项目标现已真 WPF 通过；两个后继失败尚无产品根因；tests-only 候选仅增加真实输入 receipt/既有焦点与窗口阶段观察，原期限/断言不变，已独立双轴审；新托管 build 通过；原 folder 格式失败已保留，41 处纯布局修正可精确反构建已测源码，最终 folder 通过，已按 SHA 合入本地 | 按原 stack/截图/用户路径区分产品与测试观察问题，保原断言/期限，不因偶发重试求绿 |
| owned Replace / rebuild_owned_replace | 新 15 文件已合本地并双轴独审。公开 RED 分别证明绑定后未重译、旧 receipt 续期和空稿错拒；已最小修正，另修 Click Keep 误退与过期手势吞首次重试。当前 Runtime 475/Core 318、App.Tests/NativeFixture 托管构建通过；5e981+owned 的 App.Tests/DesktopFixture Release 与 folder 并集通过，870 输入无漂/owned 空。168 个共享相关源码/依赖输入逐字等于已测候选，不混入 CI573 已发源码 | 原操作起 500ms 不续，与接受/写回分离；合法未受理失败保稿，明确失权按 Keep 退休；真实 WPF 新负控待下一平台执行 |
| Linux 消费者 / rebuild_linux_evidence | 字母映射受控 11 组新 RED→GREEN，正式导入缺 Atspi 单列；透明/坏 PNG 新 RED 后截图 helper 10 项通过（真实解码、非桌面），原测试 GI 加载失败也保留；focus 诊断 RED 已取得，最终六套 56 tests 全过、输入无漂/owned 空；双轴独审通过 | 同公开 helper RED→GREEN、邻近检查；受控导入不能冒真实 GTK/键投递/截图 |
| Qt 菜单 / rebuild_qt_menus | 已发布 v2 本头真实编译通过；新实际菜单内点击误判 outside 已有日志和官方分发链因果，最小修与现 fixture 同菜单实例 press/release 负控已双轴审后合入本地；新 Qt 编译/GUI 未执行 | 沿 operation 更新真实 fixture/desktop 消费者；无 SDK/GUI时明确未编译/未验，不混入后继搜索协议 |
| 独立 Standards / rebuild_standards | 成熟 16 文件各固定源审查均无剩余阻塞；Onboarding 纯布局与 Qt v2 另核 delta，owned 后续独立接续 | 不继承丢失候选的审查结论 |
| Browser Editing / browser_confirm_shared_windows + browser_confirm_linux_dom | 从 5e981 实际源码重建共享/Windows 与 Linux/DOM 两消费者，单一关联结构已对齐；Runtime 公开 RED 已实证 ACK 越过 fresh、后来稿/ABA 与 Render 同步重入；新 v4 Runtime 1539/1539（新 19 项）及托管 fixture 构建通过，Linux Input 全集 195/195 受控通过。独立审又发现 Windows 可恢复拒绝后的实际 editor token 未交接缺陷已在隔离 v5 最小修正并独审/编译，Linux Input 新全套 198/198 通过；Program/真实 DOM 尚未验，候选仍隔离 | 真正 Editing 范围、fresh 当前原源、后来稿/ABA 优先、未派发与 unknown 分离；非 Editing/native 邻近保持，真实 DOM 未验不冒通过 |
| GTK 标题栏 / rebuild_linux_evidence | 以 CI573 真实深色低对比图和官方 GtkWindow 默认 CSD 节点为依据，仅配对自有 settings 标题栏前景/背景；两文件已静态双轴审、Ruff 通过并合入本地；Gtk 导入会初始化显示，parser 未执行 | 保全局主题/原窗口几何/按钮；复用现九状态下一正常 CI，parser/样式上下文不代替最终像素 |
| Root / Spec与advisor | 用户沟通、产品边界与新候选独立 Spec；持续核原型余项 | 真实用户收益、原门不放宽，非仅测试列表完成 |

已发布 20 文件的 managed 并集检查为 locked restore、App.Tests Release 与 folder-only 全部 exit 0，50.04s、870 非文档输入无漂、owned 空；对应真实平台结果见上表。当前下一成熟批中的 owned 15 文件已有共享/托管组合证据，Qt 点击修与 GTK 标题配色各 2 文件已有固定源独审与静态检查；两 WPF 失败诊断已收口。Windows Browser 更早 Settings 隐藏失败另有 1 文件 fixture-only 有界输入/菜单观察，不改产品、原单击或原错误。当前完整 26 文件（22 代码/测试、4 文档）并集已实际通过 App.Tests/DesktopFixture Release（均 0 警告/错误）、folder-only 18 个 C# 文件、三 Python 的 Ruff check/format，35.32s、870 输入无漂、owned 空。168 个共享输入仍逐字等于已通过的 owned Runtime 475/Core 318 候选，未无意义重跑。新 Qt/GTK/WPF/Browser 平台行为与失败诊断需下一正常 CI，不把未完成 Browser/Qt 搜索后继混入。

所有重 CPU/动态检查统一单 owner：READY 后 fresh GO 才 START，终态先 END 与 owned 清理再分析；记录命令、cwd、输入、session、期限和 exit。不确定结果先核，不盲重跑。

## 仍未关闭的产品边界

完整双端逐页/状态验收未完成。owned Replace 已本地实现，真实 WPF 全链待验；Browser 全键盘确认、Qt/GTK4/GTK3 O04 搜索及实际配置链、S08 材质/中间组合像素、跨页 Full 动效仍有缺口。后继 Browser/Qt 旧实现原件当前缺失，只能按实际基线重建并重验，不能称仍已本地交付。

Index 仍默认，不扩模型。固定 Index profile 的中英双向 Glossary 发布行已在已提交源码中；风格/换种说法先前有界裁决未达标准，仍是未完成产品能力，不归 N06–N11 前端例外，也不重启优化研究。旧模型原始证据缺失，不声称重新评审。

输入来源、IME、焦点身份、接受资格、写回和 Undo 不因界面重构放宽；原 down+500ms、FirstWake 150ms 等预算保持。字体只按锁定来源校验准备，产品离线；安全退役和明确材质回退不能冒充原型完整呈现。
