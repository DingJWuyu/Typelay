# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-03**，开发分支 `work/linux-usability` 已核提交 [`ddec795bb8a2293f7192330729ea89ca0fca65e4`](https://github.com/DingJWuyu/Typelay/commit/ddec795bb8a2293f7192330729ea89ca0fca65e4)，tree `795440ccdbacbc45369cb83b87f9a0949a03a335`。本头已保存 Qt 暂停态采色前置、小型 Windows 桌面证据上传和当前产品状态；[CI584](https://github.com/DingJWuyu/Typelay/actions/runs/41582698454) 已终态 failure，实际通过与剩余阻塞见下。前一头 Qt 图标与消费者修复继续保留，不冒充此前不可恢复的候选原件。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型。自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI584](https://github.com/DingJWuyu/Typelay/actions/runs/41582698454) 精确 head ddec795b 已于 2026-11-03 05:42 UTC 终态 **failure**。共享、两端 Index 均通过；Windows 剩应用 sheet 的 Hide 后第二次 opening 与 Notepad 可信物理输入覆盖，Linux 剩 GTK3/Qt 同一 AT-SPI 观察错误及 GTK4 Composing 后置。两端终态日志、Windows contracts/小型桌面诊断与 Linux small desktop 原件已核大小/SHA。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows 合同与主链 | 完整构建通过；App 实际执行 1448，1447 通过、1 失败、22 未执行，Core 318/318、Runtime 1565/1565、Contracts 211/211、Protocol 28/28。Continuous、Hy ABI5 与 Settings 生命周期通过 | 唯一失败是 sheet Hide 后第二次 opening：首末候选窗 0、原 owner HwndSource hook 未见所筛选右键消息；第一 opening 的隐藏/背景退休/文件不变已过，第二 opening 旧 consent 后置未到。SendInput=2 不证明目标已接收，零 hook 观察也不证明 OS 丢输入；暂无有证据支持的产品修法。LocalDraft、FirstWake、Child Generation 与 OwnedPracticeQueue 本头通过 |
| Source 确认 Keep / General 动效 | Keep 原失败及邻近通过；General 原四语言/主题、同步慢保存、已画完再 ACK 均通过。新轨迹首有效 Rendering 起原 300ms，已开始轨迹跨重建不重播 | 关闭本次公开回归；整体 Select 的旧 234.824ms 不等于磁盘独耗，单轮通过不证明所有页面流畅或全部像素一致 |
| Windows Browser | setup + 8 场完整通过；Editing 确认 4 Keep / 3 Replace（click、Enter、Space），原源不变、submit=0；术语生命周期、按需/直接/断连与正向 submit 控制通过 | 新 pending 精确 echo 路径有实际平台证据；上一头缺 render 身份，仍不能据本头绿认定其排他根因。physicalIme 未测，safe-false 后重试、混版及非 Editing 完整确认仍未闭 |
| Notepad / 物理包 | 预检、模型/预览、按需取消/直接翻译/Undo、退出通过。自动接受/Undo 为 trusted-source-input-required、attempted=false，编辑回源/Undo 因依赖不可用未测；原文件不变、cleanupErrors 为空。独立缺资格 smoke 通过并生成 physical-Tab 包 | 包已发现所需物理 Tab 路径，但没有执行可信物理按键；软件注入不能替代这两项，不把未测写成产品写回回归或整端通过 |
| Linux 资源与发行 | 固定三树回收真实成功，Available 增 897896 KiB（约 877 MiB）；发行 finalize/distribution 与后继消费者通过，本头日志无 ENOSPC | 本轮回收前仍有约 74 GiB 可用，不证明回收单独解决旧满盘。成功树内部 CMake 日志随中间树回收，源码/运行时/包/失败构建与其它诊断保留 |
| Linux Browser | 正式 DOM 100/100 通过，包含 pending echo、完整中断/ABA、后稿原 receipt 与 focus 恢复重入回归；正式 Wayland Chromium/Firefox 标准与 rich 原链通过 | 不外推 Windows 可恢复拒绝重试、混版 GUI、非 Editing 确认或 rich 正文变更后 bookmark 体验 |
| Settings / 教程 / GIMP | Settings 风险录制与九图、完整 owned 自动/按需教程、GIMP 六项真实流程、VS Code 和 Linux Hy 本头通过 | 保持各项实际边界；真实 GIMP 原生 Undo 能力按其报告单列，不以通过替代全部视觉验收 |
| GTK3 正式链 | 既有语言搜索/选择、实际输入和接受越过；后继设置 Ctrl+F7/Ctrl+F8 时，风险确认搜索遍历已销毁 AT-SPI proxy，get_name 抛 object does not exist | 与 Qt 同一测试辅助观察缺陷。修复仅跳过明确消失节点，保完整 owner/role/可见性/歧义与原时间预算；本头后继未跑，不能沿 CI583 完整通过当本头通过，Unknown 专项仍未测 |
| GTK4 配置 | 内容 264、Clear、Tab/Shift+Tab 与 Pinyin Composing 越过；行点击后 Composing 后置仍失败，search 保焦、合成词已提交、save=0。两份已有 snapshot 同时间，pointer 祖先含该行，但没有 button 接收证据 | 本次窄观察已收束：hover 与后续 enter/leave 不能证明实际点击目标，IME 候选层遮挡仍是假设。保原失败与安全门，外点/自然越界/Escape 后继矩阵仍未跑；不再凭同样信息追加日志 |
| Qt 正式链 | Ctrl+F7 人工稿回源、第二次接受和 Undo 越过；恢复 Tab/Esc 的设置风险确认搜索遇到与 GTK3 相同的已销毁 AT-SPI 节点，整次观察异常退出 | 未到主题/paused 采色，ddec 已发布采样修正仍未实测。CI583 第九次空样本原件保留，不把本头更早阻塞说成采色继续失败或修复已通过 |

CI583 已实看的八张 Qt 主题图均为中文 UI，短稿 More 三点、长稿 Expand 对角箭头可见；没有英文 UI 或展开态 Collapse 实图。26px/15px 控件断言不代替逐像素 1:1。常驻 ×、hover 显隐、材质和完整动效仍未齐。既有 Settings 九图、S09 known/unknown 与 S08 静态图保留有效范围，多 DPI/中帧不能由静态图关闭。

## 当前工作与收束条件

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| Qt paused 采样 | 已发布 tests-only 修正只在 editor/viewport 均可见且参与采色时冻结其几何；隐藏内部布局不再否定独立 overlay 样本 | 同一对象/父子/窗口/可见性与 overlay 几何仍冻结，单次 grab/alpha255/全部颜色后置不变；静态审查通过，Qt SDK 缺失，本头被更早 AT-SPI 前置阻挡，仍待真实 paused 与可见 editor 邻近验证 |
| Windows 诊断原件 | 本头已按 19 条固定路径上传小型 artifact，实际 18 文件、解压 718261 字节，ZIP 55709 字节；Notepad overlay trace 本轮未生成 | Browser/Notepad/Continuous 结果已直接读回。只含受控测试结果/合成输入与结构化元数据，原完整包、访问与 14 天保留不变；无新增采样、无未来大小保证，不作为物理输入证明 |
| AT-SPI 风险确认观察 | GTK3/Qt 共用 visible_named 的窄修正已合入未发布并集：仅同次遍历跳过明确已消失节点 | 不吞其它 DBus/owner/歧义错误，不增加输入或等待；公开 stale→live 原 helper RED、新 14 项与 43 项既有消费者受控回归通过，静态审查通过；根/子枚举原错误保持，真实桌面下轮验证 |
| GTK4 配置后继 | 本次既有快照观察已收束；独立 tests-only 候选将原 8 个独立配置 opening 提前到 language-options 之前，避免已知 Composing 失败遮住独立行为证据 | 每场新 fixture/peer/目录及原清理、文件名独立；原 240s、fail-fast 和 Composing 场景全部保留，不能保证总界内全部执行，未混入本头；不是 GTK4 产品修复 |
| Qt More 隐藏入口 | 后继源码新增复用现役 escape 的“暂时隐藏”；原 × 在所有状态保持，术语 opening/form 鼠标出口不变 | 已静态审查，公开一次精确事件/旧 view 按下后释放负控及中英文案待真实 Qt；正式桌面只复用原快照核全部 12 可见菜单项不裁切，无新增模型调用。无 ×、hover 与再次聚焦恢复差异仍未闭，未发布 |

Qt 目标搜索仍未完成，现有 child 与配置退休链只作只读比较。GTK3 目标搜索保源选择和显式 Apply，不冒完整原型方向头/交换/即时保存。后继按真实产品缺口推进，不以一批补丁完成作为项目完成。

## 验证环境与未闭边界

后继未发布并集为 7 个源码/测试文件与 3 个产品文档：Qt More 冗余隐藏入口、GTK4 原独立场景重排及 AT-SPI 已消失节点观察修正。需求与工程静态审查通过；AT-SPI 原 helper 单项 RED，新 14 项与 43 项既有消费者受控回归通过。最终并集已在 2 CPU 下完成五 Python 的 Ruff/format/AST、57 项受控回归、diff 和输入无漂检查（1.48s），检查进程均已退出。已有 ddec 的 Qt 单次采样修正逐字保留。没有 C# 或模型改动；C/Qt SDK 缺失，未本地编译/运行新 GUI。不把未发布候选算入 CI584 通过。

本头已发布批仅含 Qt 采样前置、小型 Windows artifact 工作流与本页等 3 个产品文档，共 5 文件；已完成需求及工程静态审查。工作流 19 路径/既有步骤逐字保留等 16 项结构核对通过，现役 CI selector 28/28 通过；Qt patch applicability 与 whitespace 通过，实际可见 editor 的全部原检查保留。最终并集 YAML/原工作流结构保持/diff/输入无漂检查通过；Qt SDK 缺失，未本地编译或运行 GUI，新 artifact 已在 CI584 真实上传并读回。两源码文件和文档按最终字节冻结，无 C#、生产 JS 或模型改动，不重跑未变化的托管套件。Qt 隐藏入口独立候选未合入此批。

SDK 10.0.401、Ruff 0.14.0/fontTools 4.61.1 仍按现锁；32 个字体从本头已校发行资源只读恢复，正式 verify_bundle 逐项 SHA 校验通过。工具与依赖恢复不计产品测试。当前没有可用 DISPLAY/WAYLAND、GTK/Qt 开发 SDK 或真实 WPF；本地只做适用托管编译/受控回归/格式，目标平台运行依赖 CI。

- 完整双端逐页、真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭。
- S08 材质/中间组合像素、多 DPI、跨页 Full 动效及 O02/O03/O04 余项仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒完整原型呈现。
