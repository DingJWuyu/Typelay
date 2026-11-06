# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-06**，最近已核平台验收对应开发分支 `work/linux-usability` 的提交 [`e36f34cceda21babac52494a4b7dd4052ae6b2a1`](https://github.com/DingJWuyu/Typelay/commit/e36f34cceda21babac52494a4b7dd4052ae6b2a1)。该提交已包含 Qt 目标搜索、GTK4 搜索 Enter 路由和 Index 断连诊断；[CI586](https://github.com/DingJWuyu/Typelay/actions/runs/41594820979) 于 2026-11-03 07:38:24 UTC 终态 failure。Windows 两 job 被选择器跳过，最近实测仍为 CI584。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本批待验证变更限定为九个现役文件：Qt 正式桌面 fixture 生命周期、两项 Unicode 搜索测试输入、GTK4 焦点拒绝诊断及相关回归、两条既有 artifact 路径与三份产品/工程文档。不增加产品功能，不实施 O02 hover 或 O04 几何、材质、动效候选。

实施范围保持原型 I-III；IV 的 N06-N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型。自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

以下 Linux 结果来自精确 head e36f34cc 的 [CI586](https://github.com/DingJWuyu/Typelay/actions/runs/41594820979)，通过、失败和未执行分开记录。Qt 完整 probe 失败后，正式桌面使用已校验的未封装构建收集独立交互证据，`distributionAcceptance: false`；不等于发行包通过，不能抵消 probe 失败。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| 共享 / Linux Browser | 共享检查、正式 DOM 100/100 与 Browser 七项通过 | 保留 pending echo、后来稿/ABA、中断与 focus 恢复重入等已有边界；不外推 Windows 可恢复拒绝重试、混版 GUI、非 Editing 确认或 rich 正文变更后 bookmark 体验 |
| Settings / 教程 / VS Code | CI586 三条链通过 | 各项实际交互证据不替代完整视觉、真实物理输入或全部应用验收 |
| GTK3 正式链 | 正式桌面 21 项完成 | 目标搜索、源选择与显式 Apply 保持既有行为；Unknown 专项仍未测，不概括所有焦点与 AT-SPI 生命周期均无错 |
| GTK4 配置 | 前七个独立场景通过，包括 Enter 保存、真实 pinyin、held repeat、跨 row 与释放后 fresh 保存。第八个 window-focus 在 Begin 前 readiness 失败：初始 focused/topActive=true，但 IME=null、cooperative=false；Begin/Save/Cancel 均为 0 | 原 Enter 失败已有本头真实回归证据；本次未进入配置壳，不能称配置中途失焦。后续场景未执行，完整外点/自然越界/Esc 矩阵与原 Composing 行点按接收目标仍未闭；只补原拒绝后的诊断，原期限/失权门不变 |
| Qt 受控搜索 | 真实 MOC/C++ 构建通过；前两次 opening 的同语保存、空结果、Up/Down、held 拒绝与 fresh 保存通过。第三次“韩语”查询在实际文本/队列观察处超时，当次状态与日志未进入既有小型 artifact | keymap 原因未证；两项 Unicode 查询改用一次真实剪贴板 Ctrl+V，保 MIME 恢复与外部 owner 更新，不称物理 Unicode 键盘输入验收。新增两条精确路径以保留下一次早期失败证据 |
| Qt 真实 Host 搜索 | 正式桌面已先通过按需 en 同语保存后恢复 zh、持久化、原源不变、选区/Undo/零写及 Automatic/Editing 禁用负控，随后才在 multiline CPU-candidate readiness 失败 | 搜索交互通过与后续失败分别保留，不把整条正式桌面写成通过或未执行。未封装构建不证明发行包搬迁验收；其余 GUI/IME、键盘遍历及新截图仍按实际结果关闭 |
| Qt fixture 生命周期 | 窗口约在启动后 59.927s 消失；最后一次 62 字符 capture 距消失仅约 201ms，短于原 300ms debounce | 固定 60s fixture 自退得到强支持，但尚非排他定因。仅正式 Qt caller 可停用 fixture 自退，保既有外层 240s 硬截止、进程退出观察与清理；等待同头目标平台验证 |
| Index Linux | 全部 12 阶段通过，用时 88.79s | 本头成功不解释 CI585 install-argos 断连原因；保留现役失败阶段/进程退出诊断，不新增模型或无假设重跑 |
| 发行附件 / GIMP / 正式 Hy | Qt probe 失败阻断发行附件与准入，GIMP 和正式 Hy 本头均为 SKIPPED，最近真实证据仍为 CI585 | 不沿用 CI585 通过作为本头结果；须在新头满足原发行准入后执行，不能用 Qt 未封装交互证据替代 |

Windows 在 CI586 未执行；以下仅保留 [CI584](https://github.com/DingJWuyu/Typelay/actions/runs/41582698454) 最近真实证据与仍有效的关闭条件。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows 合同与主链 | 完整构建通过；App 实际执行 1448，1447 通过、1 失败、22 未执行，Core 318/318、Runtime 1565/1565、Contracts 211/211、Protocol 28/28。Continuous、Hy ABI5、Settings 生命周期、LocalDraft、FirstWake、Child Generation 与 OwnedPracticeQueue 通过 | 唯一失败是 sheet Hide 后第二次 opening：首末候选窗 0，原 owner HwndSource hook 未见所筛选右键消息；第一 opening 的隐藏/背景退休/文件不变已过，第二 opening 旧 consent 后置未到。SendInput=2 不证明目标接收，零 hook 也不证明 OS 丢输入；尚无有证据支持的产品修法 |
| Source 确认 Keep / General 动效 | Keep 原失败及邻近通过；General 原四语言/主题、同步慢保存、已画完再 ACK 均通过。新轨迹从首个有效 Rendering 起使用原 300ms，已开始轨迹跨重建不重播 | 对应公开回归已过；旧 Select 234.824ms 不等于磁盘独耗，单轮通过不证明所有页面流畅或全部像素一致 |
| Windows Browser | setup + 8 场完整通过；Editing 确认 4 Keep / 3 Replace（click、Enter、Space），原源不变、submit=0；术语生命周期、按需/直接/断连与正向 submit 控制通过 | pending 精确 echo 有真实平台证据；上一头缺 render 身份，不能认定排他根因。physicalIme 未测，safe-false 后重试、混版及非 Editing 完整确认仍未闭 |
| Notepad / 物理包 | 预检、模型/预览、按需取消/直接翻译/Undo、退出通过。自动接受/Undo 为 trusted-source-input-required、attempted=false，编辑回源/Undo 因依赖不可用未测；原文件不变、cleanupErrors 为空。独立缺资格 smoke 生成 physical-Tab 包 | 两项可信物理输入仍未执行；发现所需 Tab 路径或软件注入不能替代实测，不记作产品写回回归或整端通过 |

最近完整 Qt 正式桌面 23 项及十张主题/编辑/暂停图证据仍来自 CI585，paused 浅深图与采色后置已通过；不继承为 CI586 整链通过。图片仍为中文，More 三点与长稿 Expand 箭头可见，英文 UI 和展开态 Collapse 图未齐。26px/15px 控件断言不代替逐像素 1:1；常驻 ×、hover、材质、完整动效与多 DPI 仍按[原型对照](../design/prototype-parity.md)逐项验收。“暂时隐藏”的既有受控单次 escape/旧 view 拒绝与正式 More 全部 12 项可见证据保留，完整 Host 隐藏及再次聚焦恢复用户链仍未闭。

## 当前工作与收束条件

本批围绕真实语言搜索与长文输入路径的已观察 CI 阻塞：查询输入超时、fixture 固定退出和 Begin 前资格未就绪。日常用户发生频率未知，尚不能据此判定产品键盘或焦点根因。窄测试修正与现有拒绝点诊断成本有限，收益是取得可归因的同头证据，保留搜索、输入与写回安全门。

| 责任 / 模块 | 当前工作与依赖 | 收束条件 |
| --- | --- | --- |
| Qt 测试 / fixture 生命周期 | 正式 Qt caller 可显式停用 fixture 的 60s 自退；其他 caller 保持默认。依赖目标平台复验 | 原外层 240s 及 cleanup 不变，进程已退出即报告，回归覆盖该观察边界；不增加产品 debounce、不补输入、不把候选写成已验证根因 |
| Qt 测试 / Unicode 查询 | 只替换“韩语”和“日本語”两项查询的输入方式，每项一次真实剪贴板 Ctrl+V；保原查询/队列断言、MIME 恢复与外部 owner 更新。依赖真实 Qt GUI | 实际查询文本、过滤和保存/取消结果在原期限内成立；粘贴证据与物理 Unicode 按键分开报告，不以 keymap 猜测关闭失败 |
| GTK4 合作组件 / 焦点诊断 | 仅在既有服务器焦点拒绝之后记录一条 TRACE，资格判定和返回值保持。依赖下一次同场景原件 | 诊断只记录定位拒绝所需的结构化状态，不含输入正文；不触发 retry、抢焦点、Begin 或放宽 IME/cooperative 门。若仍失败，依据新证据决定下一步 |
| Linux CI / 证据保留 | 小型 artifact 只补 `artifacts/linux/qt-cooperative/language-options-native-state.json` 和 `artifacts/linux/qt-cooperative/language-options-native.log` 两条既有路径，不改发布包准入 | 同头运行分别报告构建、受控搜索、真实 Host 搜索、后续桌面及发行消费者的结果。Qt probe 通过前不将 GIMP/Hy 记作已执行，不上传模型或扩大为整个 artifacts 目录 |

Qt 目标搜索沿用 config-only Begin→退休 ACK→配置保存链，原 source/target/交换保留；仅按需快捷键已启用的源态开放，自动/禁快捷键及编辑态禁用。Host 原 generation/owner 门仍在 StopSession 前拒绝陈旧请求，原有 stdio 回归不代替新 GUI 结果。GTK4 原次 Return 的 capture/IME/held claim 已有 CI586 实测，Unknown/Composing 不借后续 Clear 升级，同硬件 held 跨 row 不升级，release 后才可接受 fresh 保存。GTK3 保源选择与显式 Apply，不冒完整原型方向头/交换/即时保存。

## 验证环境与未闭边界

本批最终 Qt 候选观察回归 18/18 通过，含已单独重跑的 15 项既有场景与 3 项新增真实子进程观察回归；新增回归已有先失败后通过证据。三份受影响 Python 的固定 Ruff 0.14.0 lint/format、compileall/AST 与 diff 检查通过。GTK 补丁所用固定上游五份源码已校验，原/新补丁 dry-run、实际应用与反向检查均无 offset/fuzz；提取实际前后拒绝代码块的严格 C++ 编译及 3072 组受控组合通过，仅证明局部语法/控制流，不是 GTK 原生验证。工作流 YAML 解析与结构比对确认只新增两条 artifact 路径。

以上为本批重新执行的本地检查，与 CI586 平台结果分开。本批尚未执行真实 Qt/GTK 构建、GUI 粘贴或 fixture 自退实验，也未执行真实 Index/模型或 WPF。下一步核对本批同头 CI 的每条实际执行链及未执行原因，不能把本地受控通过写成目标平台完成。

- 完整双端逐页、真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭。
- S08 材质/中间组合像素、多 DPI、跨页 Full 动效及 O02/O03/O04 余项仍有差距；本批诊断修正不关闭这些产品缺口。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒完整原型呈现。
