# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-03**，开发分支 `work/linux-usability` 已核提交 [`a821d34f989819d649ac65714dcdbcda0fcf25e4`](https://github.com/DingJWuyu/Typelay/commit/a821d34f989819d649ac65714dcdbcda0fcf25e4)，tree `f5f513c96e9a3f250b0f48d418b7017486585328`。本头已保存 Qt More 暂时隐藏入口、GTK4 原独立场景重排、AT-SPI 已消失节点观察修正及当前产品状态；[CI585](https://github.com/DingJWuyu/Typelay/actions/runs/41587164424) 已于 06:31 UTC 终态 failure。CI585 按本批路径只选择 Linux，Windows 两 job 未执行，其最近实测仍为 CI584。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型。自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI585](https://github.com/DingJWuyu/Typelay/actions/runs/41587164424) 精确 head a821d34f 的共享通过，Linux 主链除 GTK4 配置 Enter 保存外均通过；独立 Index job 另有 install-argos 断连。GTK3 与 Qt 正式桌面已越过上一头 AT-SPI 已消失节点错误并完成整链，Qt paused 采色和十张主题/编辑/暂停图真实取得。GTK4 调整场景顺序后，交换与 Auto 不可交换两场通过，第三场 Enter 保存失败，后续未执行。Linux 小型诊断 artifact 的 275 文件、13,503,730 解压字节及 ZIP SHA 已核；不以局部通过宣称整端交付。

Windows 本头未执行；以下 Windows 项仅保留 [CI584](https://github.com/DingJWuyu/Typelay/actions/runs/41582698454) 最近真实证据和未闭边界。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows 合同与主链 | 完整构建通过；App 实际执行 1448，1447 通过、1 失败、22 未执行，Core 318/318、Runtime 1565/1565、Contracts 211/211、Protocol 28/28。Continuous、Hy ABI5 与 Settings 生命周期通过 | 唯一失败是 sheet Hide 后第二次 opening：首末候选窗 0、原 owner HwndSource hook 未见所筛选右键消息；第一 opening 的隐藏/背景退休/文件不变已过，第二 opening 旧 consent 后置未到。SendInput=2 不证明目标已接收，零 hook 观察也不证明 OS 丢输入；暂无有证据支持的产品修法。LocalDraft、FirstWake、Child Generation 与 OwnedPracticeQueue 本头通过 |
| Source 确认 Keep / General 动效 | Keep 原失败及邻近通过；General 原四语言/主题、同步慢保存、已画完再 ACK 均通过。新轨迹首有效 Rendering 起原 300ms，已开始轨迹跨重建不重播 | 关闭本次公开回归；整体 Select 的旧 234.824ms 不等于磁盘独耗，单轮通过不证明所有页面流畅或全部像素一致 |
| Windows Browser | setup + 8 场完整通过；Editing 确认 4 Keep / 3 Replace（click、Enter、Space），原源不变、submit=0；术语生命周期、按需/直接/断连与正向 submit 控制通过 | 新 pending 精确 echo 路径有实际平台证据；上一头缺 render 身份，仍不能据本头绿认定其排他根因。physicalIme 未测，safe-false 后重试、混版及非 Editing 完整确认仍未闭 |
| Notepad / 物理包 | 预检、模型/预览、按需取消/直接翻译/Undo、退出通过。自动接受/Undo 为 trusted-source-input-required、attempted=false，编辑回源/Undo 因依赖不可用未测；原文件不变、cleanupErrors 为空。独立缺资格 smoke 通过并生成 physical-Tab 包 | 包已发现所需物理 Tab 路径，但没有执行可信物理按键；软件注入不能替代这两项，不把未测写成产品写回回归或整端通过 |
| Linux 资源与发行 | 固定三树回收真实成功，Available 增 897896 KiB（约 877 MiB）；发行 finalize/distribution 与后继消费者通过，CI585 日志无 ENOSPC | 本轮回收前仍有约 74 GiB 可用，不证明回收单独解决旧满盘。成功树内部 CMake 日志随中间树回收，源码/运行时/包/失败构建与其它诊断保留 |
| Linux Browser | 正式 DOM 100/100 通过（206.368s），包含 pending echo、完整中断/ABA、后稿原 receipt 与 focus 恢复重入回归；正式 Wayland Chromium/Firefox 标准与 rich 七项原链通过 | 不外推 Windows 可恢复拒绝重试、混版 GUI、非 Editing 确认或 rich 正文变更后 bookmark 体验 |
| Settings / 教程 / GIMP | Settings 风险录制与九图、完整 owned 自动/按需教程、GIMP 六项真实流程、VS Code 和 Linux Hy 本头通过 | 保持各项实际边界；真实 GIMP 原生 Undo 能力按其报告单列，不以通过替代全部视觉验收 |
| GTK3 正式链 | 正式桌面 21 项完成，包含真实源输入/模型接受、Ctrl+Fn、编辑回源与独立接受、长稿动效、Entry/TextView 选择/Undo、pinyin、暂停和实际 Settings 恢复 | 本头已越过原 AT-SPI 风险确认观察失败；只跳过明确消失节点，不吞其它错误。目标搜索 Unknown 专项仍未测，不外推所有 AT-SPI 生命周期无错 |
| GTK4 配置 | 原独立 opening 重排后，swap 和 swap-auto 两场真实通过；第三场原搜索 Enter 未产生保存，报 real search Enter did not save | 当前核原输入、实例、IME 和保存观察，不放宽门。其余五个独立 opening 及原 language-options/Composing 链未到；上一头 Composing 点击的接收目标仍未知，不能把它当本头失败或已修复 |
| Qt 正式链 | 正式桌面 23 项全部完成：人工稿回源/独立接受、设置恢复、动效、源语言、More Copy、十张主题/编辑/paused 图、暂停恢复、选区/Undo、pinyin 和直接翻译均通过 | ddec 的隐藏 viewport 采样前置修正已有真实 paused 浅深两图与可见 editor 邻近证据；单次样本不等完整视觉 1:1。More 暂时隐藏在正式受控 Qt 场景单次 escape/旧 view 负控通过；正式 Host 只复用当前 More 快照核 12 项完整可见，不冒该入口的完整 Host 用户链 |

CI585 十张 Qt 主题/编辑/暂停图已取得，新增 paused 浅深图已目视核实，原八张对应状态保留。界面仍为中文，短稿 More 三点、长稿 Expand 对角箭头可见；没有英文 UI 或展开态 Collapse 实图。26px/15px 控件断言不代替逐像素 1:1。常驻 ×、hover 显隐、材质和完整动效仍未齐。既有 Settings 九图、S09 known/unknown 与 S08 静态图保留有效范围，多 DPI/中帧不能由静态图关闭。

## 当前工作与收束条件

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| Qt paused 采样 | 已发布 tests-only 修正只在 editor/viewport 均可见且参与采色时冻结其几何；隐藏内部布局不再否定独立 overlay 样本 | 同一对象/父子/窗口/可见性与 overlay 几何仍冻结，单次 grab/alpha255/全部颜色后置不变；CI585 真实 paused 浅深采样与可见 editor 邻近均通过，原空样本前置已修，不据此声称全像素/材质完成 |
| Windows 诊断原件 | CI584 已按 19 条固定路径上传小型 artifact，实际 18 文件、解压 718261 字节，ZIP 55709 字节；Notepad overlay trace 本轮未生成 | Browser/Notepad/Continuous 结果已直接读回。只含受控测试结果/合成输入与结构化元数据，原完整包、访问与 14 天保留不变；无新增采样、无未来大小保证，不作为物理输入证明 |
| AT-SPI 风险确认观察 | GTK3/Qt 共用 visible_named 的窄修正已发布：仅同次遍历跳过明确已消失节点 | 不吞其它 DBus/owner/歧义错误，不增加输入或等待；公开 stale→live 原 helper RED、新 14 项与 43 项既有消费者受控回归通过，静态审查通过；根/子枚举原错误保持，CI585 GTK3/Qt 两正式桌面完整通过 |
| GTK4 搜索 Enter | 后继四文件候选已合入本地、未发布：搜索原次 Return 改由 popup SAME_NATIVE capture 在 IME 前路由，复用原 held/claim；原 GtkText 默认 activate 会先吃事件，不能再依赖 bubble | Unknown/Composing 原事件交 GTK 一次，不在默认 activate 后借 Clear 执行；同硬件 held 跨 row repeat 不升级、release 清槽，普通源 Return 不扩监听。原 ko Enter 正场和期限不变，新增真实 pinyin/repeat/退休负控尚未执行；row 负控可能旧版也绿，不冒其增量 RED。需求与工程静态审查通过，SDK/GUI 未验，CI585 Enter 仍是未闭真实失败 |
| Qt More 隐藏入口 | 本头源码新增复用现役 escape 的“暂时隐藏”；原 × 在所有状态保持，术语 opening/form 鼠标出口不变 | 正式受控 Qt 已通过公开一次精确事件/旧 view 按下后释放负控；正式桌面原快照也已核全部 12 可见菜单项不裁切，无新增模型调用。无 ×、hover、完整 Host 入口链与再次聚焦恢复差异仍未闭 |
| Index 断连取证 | 后继两文件候选已合入本地，未发布：仅在原失败且 cleanup 前保存固定协议阶段、发送操作白名单与现有直接进程退出码 | 原失败、600s 与清理保持，null 仅代表尚未观察到退出；正式既有 lifecycle 套件 37 项及 3 项邻近受控回归通过，静态审查通过。未执行真实 Index 或模型，不归因 CI585；下一正常 CI 若复现再据原件区分发送/回复/接收与退出状态 |

Qt 目标搜索八文件候选已合入本地、未发布，接入已有 config-only Begin→退休 ACK→配置保存链，原 source/target/交换 child 保留；本阶段仅按需源状态开放，自动/禁快捷键及编辑态明确禁用并给出提示。native 设置只控制可用提示，Host 原 generation/owner 门仍在 StopSession 前拒绝陈旧请求。真实 Host stdio 回归已证明：Host 切到 Automatic 并建立未绑定练习后，旧 Qt generation 的 Begin/Save 被拒，练习进度和设置不变，没有 hide/clear/新许可或回焦；该场与三个邻近均通过，不等于 Qt GUI。搜索框承担本地 Up/Down 选择、Enter 与 Escape，Tab 自然越界取消；只搜索当前 UI label/nativeLabel/code，不扩 DTO/provider ABI、不恢复旧源许可。八文件组合静态审查与适用格式检查通过，真实 Qt 编译、IME 按次、焦点、同步重入和新增截图仍须下一 CI。GTK3 目标搜索保源选择和显式 Apply，不冒完整原型方向头/交换/即时保存。后继按真实产品缺口推进，不以一批补丁完成作为项目完成。

## 验证环境与未闭边界

已发布 a821 的适用本地检查和 CI585 真实目标平台结果分开保留。待发布并集为 14 个源码/测试文件与 4 个产品文档：Qt 目标搜索八文件、GTK4 搜索 Enter 四文件、Index 断连诊断两文件。需求与工程静态审查均通过；最终组合在 2 CPU 下通过八 Python 的 Ruff/format/AST、公共设计 header 的 C11 严格语法和 diff 检查（0.70s），984 个输入无漂，检查进程全部退出。逐字一致的 Input 218/218、Index 37+3 受控结果和真实 Host stdio 4/4 沿用，不重复运行；Host 已实际重新构建通过，未改 C# 生产。GTK4 原正向和新增 pinyin/held 负控未执行，row 负控也不作为该增量的 RED→GREEN 证明。没有 GTK/Qt SDK 或 GUI，未运行真实 Index/模型；下一步冻结本 18 文件并提交开发分支，跟踪同头目标平台构建、搜索/保存/取消与真实输入法结果。

SDK 10.0.401、Ruff 0.14.0/fontTools 4.61.1 仍按现锁；32 个字体从本头已校发行资源只读恢复，正式 verify_bundle 逐项 SHA 校验通过。工具与依赖恢复不计产品测试。当前没有可用 DISPLAY/WAYLAND、GTK/Qt 开发 SDK 或真实 WPF；本地只做适用托管编译/受控回归/格式，目标平台运行依赖 CI。

- 完整双端逐页、真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭。
- S08 材质/中间组合像素、多 DPI、跨页 Full 动效及 O02/O03/O04 余项仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒完整原型呈现。
