# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-03**，开发分支 `work/linux-usability` 已核提交 [`afa94da7457fae14756195ba5f3f86efaac2e768`](https://github.com/DingJWuyu/Typelay/commit/afa94da7457fae14756195ba5f3f86efaac2e768)，tree `8f77b1112e43163e95efd6189893b1b930b68d96`。本头已保存 General 首帧起钟、Browser 精确 pending 回显、Keep 等待合同、Linux 中间构建回收和 GTK4 失败观察；[CI583](https://github.com/DingJWuyu/Typelay/actions/runs/41578245931) 已终态 failure，实际通过与剩余阻塞见下。前一头 Qt 图标与消费者修复继续保留，不冒充此前不可恢复的候选原件。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型。自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI583](https://github.com/DingJWuyu/Typelay/actions/runs/41578245931) 精确 head afa94da7 已于 2026-11-03 04:58 UTC 终态 **failure**。共享、两端 Index 均通过；Windows 唯一失败是 Notepad 可信物理输入覆盖未完成，Linux 剩 GTK4 和 Qt 正式桌面链失败。两端终态日志、Windows contracts/发行及 Linux small desktop 原件已核大小/SHA。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows 合同与主链 | 完整构建通过；App 实际 1448 通过、22 未执行、0 失败，Core 318/318、Runtime 1565/1565、Contracts 211/211、Protocol 28/28。Continuous、Hy ABI5 与 Settings 生命周期通过 | LocalDraft、FirstWake、旧 Child Generation 与 OwnedPracticeQueue 本头通过；后两项未改产品，不能称上一头失败独因已解，也不再按单次 exit1 推定 cmd 构造必然不兼容 |
| Source 确认 Keep / General 动效 | Keep 原失败及邻近通过；General 原四语言/主题、同步慢保存、已画完再 ACK 均通过。新轨迹首有效 Rendering 起原 300ms，已开始轨迹跨重建不重播 | 关闭本次公开回归；整体 Select 的旧 234.824ms 不等于磁盘独耗，单轮通过不证明所有页面流畅或全部像素一致 |
| Windows Browser | setup + 8 场完整通过；Editing 确认 4 Keep / 3 Replace（click、Enter、Space），原源不变、submit=0；术语生命周期、按需/直接/断连与正向 submit 控制通过 | 新 pending 精确 echo 路径有实际平台证据；上一头缺 render 身份，仍不能据本头绿认定其排他根因。physicalIme 未测，safe-false 后重试、混版及非 Editing 完整确认仍未闭 |
| Notepad / 物理包 | 预检、模型/预览、按需取消/直接翻译/Undo、退出通过。自动接受/Undo 为 trusted-source-input-required、attempted=false，编辑回源/Undo 因依赖不可用未测；原文件不变、cleanupErrors 为空。独立缺资格 smoke 通过并生成 physical-Tab 包 | 包已发现所需物理 Tab 路径，但没有执行可信物理按键；软件注入不能替代这两项，不把未测写成产品写回回归或整端通过 |
| Linux 资源与发行 | 固定三树回收真实成功，Available 增 897912 KiB（约 877 MiB）；发行 finalize/distribution 与后继消费者通过，本头日志无 ENOSPC | 本轮回收前仍有约 74 GiB 可用，不证明回收单独解决旧满盘。成功树内部 CMake 日志随中间树回收，源码/运行时/包/失败构建与其它诊断保留 |
| Linux Browser | 正式 DOM 100/100 通过，包含 pending echo、完整中断/ABA、后稿原 receipt 与 focus 恢复重入回归；正式 Wayland Chromium/Firefox 标准与 rich 原链通过 | 不外推 Windows 可恢复拒绝重试、混版 GUI、非 Editing 确认或 rich 正文变更后 bookmark 体验 |
| Settings / 教程 / GIMP / GTK3 | 本头全部正式步骤通过：Settings 风险录制与九图、完整 owned 自动/按需教程、GIMP 六项真实流程、GTK3 目标搜索/原源选择/Apply 与真实 Pinyin 邻近、VS Code 和 Linux Hy | 保持各项实际边界；GTK3 Unknown 专项未测，真实 GIMP 原生 Undo 能力按其报告单列，不以通过替代全部视觉验收 |
| GTK4 配置 | 内容 264、Clear、Tab/Shift+Tab 与 Pinyin Composing 越过；行点击后 Composing 后置仍失败，search 保焦、合成词已提交、save=0。两份已有 snapshot 同时间，pointer 祖先含该行，但没有 button 接收证据 | 本次窄观察已收束：hover 与后续 enter/leave 不能证明实际点击目标，IME 候选层遮挡仍是假设。保原失败与安全门，外点/自然越界/Escape 后继矩阵仍未跑；不再凭同样信息追加日志 |
| Qt 正式链 | 语言、中文稿、收展/安全隐藏、前八主题采样越过；第九次 system-paused-light 的 editor/overlay 样本仍空，paused palette 浅色、Resume 可见。随后 fixture 或私有 Fcitx 已退出 | 未保存 returncode；60s fixture timer 与清理后的 Fcitx SIGTERM 不足以证明崩溃根因。先核一次 grab 与隐藏 editor 几何校验的具体关系，不延预算、重采样或松像素断言 |

已实看的八张 Qt 主题图均为中文 UI，短稿 More 三点、长稿 Expand 对角箭头可见；没有英文 UI 或展开态 Collapse 实图。26px/15px 控件断言不代替逐像素 1:1。常驻 ×、hover 显隐、材质和完整动效仍未齐。既有 Settings 九图、S09 known/unknown 与 S08 静态图保留有效范围，多 DPI/中帧不能由静态图关闭。

## 当前工作与收束条件

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| Qt paused 采样 | tests-only 候选只在 editor/viewport 均可见且参与采色时冻结其几何；隐藏内部布局不再否定独立 overlay 样本 | 同一对象/父子/窗口/可见性与 overlay 几何仍冻结，单次 grab/alpha255/全部颜色后置不变；静态审查通过，Qt SDK 缺失，下一真实 paused 与可见 editor 邻近验证，不称已证本头独因 |
| Windows 诊断原件 | workflow 候选在原上传点单列 Browser/Notepad/Continuous 的 19 条既有固定结果与 trace；本头原件共 654891 字节 | 只含受控测试结果/合成输入与结构化元数据，原完整包、访问与 14 天保留不变；无新增采样，无未来字节大小保证，不作为物理输入证明；实际上传待 CI |
| GTK4 配置后继 | 本次两份既有快照不能区分真实行点击与候选层消费，停止扩展该观察 | 具体可达输入路径或现有原件形成可证依据后再修；保原 Composing 与零保存门，不按猜测改变产品 |
| Qt 隐藏入口 | 独立准备仅在 More 实际可达态新增现役 escape 的“暂时隐藏”并去 ×；术语 opening/form 及其它 More 不可达态保原鼠标出口 | 先核完整状态矩阵与同步重入，复用原身份/IME/退出与零写门；不扩协议或承诺再次聚焦恢复，hover 与完整 O02 仍未闭，候选未合入 |

Qt 目标搜索仍未完成，现有 child 与配置退休链只作只读比较。GTK3 目标搜索保源选择和显式 Apply，不冒完整原型方向头/交换/即时保存。后继按真实产品缺口推进，不以一批补丁完成作为项目完成。

## 验证环境与未闭边界

当前后继批仅含 Qt 采样前置、小型 Windows artifact 工作流与本页等 3 个产品文档，共 5 文件；已完成需求及工程静态审查。工作流 19 路径/既有步骤逐字保留等 16 项结构核对通过，现役 CI selector 28/28 通过；Qt patch applicability 与 whitespace 通过，实际可见 editor 的全部原检查保留。Qt SDK 缺失，未本地编译或运行 GUI；新 artifact 实际上传尚待下一 CI。两源码文件和文档按最终字节冻结，无 C#、生产 JS 或模型改动，不重跑未变化的托管套件。Qt 隐藏入口独立候选未合入此批。

本轮三文件候选在固定 984 输入下完成 folder whitespace 和 locked restore。首次 App.Tests 构建已产出托管 DLL，但 Linux 条件未把已恢复 NativeInput 复制到 Broker bin，命令在末端复制报 MSB3030；保留该失败。将本头已校发行包的同一 DLL 放入忽略的构建目录后，原命令增量通过，0 warnings / 0 errors（7.37s），输入无漂移、检查进程已退出。没有改 csproj 或平台条件，也未执行 Windows DLL。

本头已发布 7 个源码/测试/工作流文件与 4 个产品文档。两 Python 文件 Ruff/format/AST、生产 Browser JS 与 18 个新增公开 DOM 表达式语法、diff 检查均通过（0.55s）；执行输入无漂移，检查进程已退出。477 个托管构建输入逐字节与前述通过构建一致，未重新把静态 JS 检查称作 WPF 执行。Linux 回收脚本在独立合成树完成 10/10 正负路径，固定 workflow/脚本字节对应；没有删除本地真实产物，本头真实容量与后继结果已在上表列明。Browser 回归已执行并通过，GTK4 失败投影已执行但未证明实际 button 接收者。

SDK 10.0.401、Ruff 0.14.0/fontTools 4.61.1 仍按现锁；32 个字体从本头已校发行资源只读恢复，正式 verify_bundle 逐项 SHA 校验通过。工具与依赖恢复不计产品测试。当前没有可用 DISPLAY/WAYLAND、GTK/Qt 开发 SDK 或真实 WPF；本地只做适用托管编译/受控回归/格式，目标平台运行依赖 CI。

- 完整双端逐页、真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭。
- S08 材质/中间组合像素、多 DPI、跨页 Full 动效及 O02/O03/O04 余项仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒完整原型呈现。
