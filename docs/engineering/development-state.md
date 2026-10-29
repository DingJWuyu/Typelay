# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-29**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

最近已完成的远端验证为 [`1d68702`](https://github.com/DingJWuyu/Typelay/commit/1d687024f252a180be8f79d15f2770266fdf6c96) 的 [CI549](https://github.com/DingJWuyu/Typelay/actions/runs/40889171329)：双平台主任务失败，两个Index生命周期及共享任务成功。

本批后继包含WPF快捷键、全页模型/保存状态、关于页、退役后配置专用语言菜单、术语范围模板与公开引导比较修复；Linux行级搜索与明确控件归属；Browser More行为、术语管理生命周期及原生失败只读诊断。61个源文件经分块审查和独立共同树验证：Ruff全164文件、字体锁、官方folder格式、App Build、App Tests Compile、DesktopFixture Compile、Linux Build和Runtime1425/1425通过。格式收敛只改变空白；过程中真实编译失败及修复记录保留。浏览器More完整86项真实回归通过。此处编译不等于实际WPF、GTK或最终整链验收，后继必须由精确提交的CI验证。

本批仅登记已审固定Index行为profile下中英双向Glossary两行资格。CI549验证时Current仍为空；新登记的实际UI消费尚待后继CI。EffectiveStyle为只读状态投影，不开放未证明的风格能力。

CI549两平台Index正式生命周期已成功：Windows12项全通过、4阶段固定profile附着且owned workers全部退出；Linux同样成功。此前Windows混斜线路径必拒已由真实正式安装验证修复。B01现在进入中英双向Glossary固定资格表和旧模型切换即时关闭的实现，不再以profile未过阻塞。Windows Runtime 1416/1416通过；App执行1073项、1068通过、5失败，另22未执行不计通过。字体OfflineFontRenderingTests精确17/17通过，四个旧600字重问题已由真实WPF闭环。package生命周期成功不等于语言质量或桌面验收。

本轮App五项失败分为两组：四偏好More菜单测试没有为真实手工编辑态投影CanOpenGlossary；术语取消后的计数在新容器布局前执行。两项fixture窄修保原生产门与断言。独立实际图另发现术语范围选中项显示对象名，局部ComboBox模板缺少ItemTemplateSelector，生产修与双语真实文本回归已审编译。S01搜索专项21项真实通过，但页面像素对照仍须单独确认。

完整原型审查曾发现部分页面仅更换样式而缺少原型交互，属于范围执行和验收遗漏，不是细节。现按[固定25项页面/状态表](../design/prototype-parity.md)逐页收敛；等待外部CI时可推进下一独立切片，但未验证项不标完成。双端尚未达成完整一比一验收。

### 当前失败的原件定位

路径相对于被忽略的 `artifacts/ui-rebuild/`。以实际case、stage与结构化结果判断，不以步骤名、包名或日志推进推断通过。

| 原件 | 实际情况 | 下一动作 |
| --- | --- | --- |
| `ci549/contracts/` App TRX | 4个More手工态能力投影错误，1个术语取消后尚未布局即计数 | 已审fixture窄修；保实际WPF后继验证 |
| 同目录 `glossary-edit-en-light-920.png` | 范围选中项显示对象名 | 局部模板selector修与双语真实文本回归已审编译 |
| `ci549/gtk-early/` | GTK4 glossary仍失败，包含本轮只读bubble/capture观测 | 实际点与GDK pick正确但两个控制器未收到crossing；加入原生事件派发只读诊断，不修改hover门 |
| `ci549/qt-early/` | Qt probe本轮仍失败 | 仅末轮已过display期限不足归因；新增原预算内first/last字形与字体就绪观测 |
| `ci549/linux-diagnostics/setup-settings/inside.log` | 接入方式selector角色断言失败，控件仍应为ComboBoxText | 本批按同PID/可见/ComboBox角色精确唯一匹配，不任取同名Label |
| `ci549/linux-diagnostics/setup-tutorial/inside.log` | 欢迎标签出现两个可见匹配，原唯一性断言失败 | 本批通过当前owned教程窗口内唯一稍后设置按钮定位内容分支，仍保持标签唯一性 |
| `ci549/linux-diagnostics/gtk-desktop/inside.log` | expand截图采样的前后height为252/365，原中间帧断言失败 | 核实际动画/截图时序，不延长原预算 |
| `ci549/linux-diagnostics/gtk4-desktop/inside.log` | ValidTranslation/light渲染样式/字号未观察到 | 核真实状态与目标样式，不泛化为环境 |
| `ci549/continuous/result.json` | Windows连续场景失败 | 刷新源全文曾真实匹配，随后可见译文未完成；本批只记录常量firstMatched/last状态，不增加capture或期限 |
| `ci549/windows-full/` browser-cooperation、hy-packages、ordinary-notepad结果 | 三者共同在await-public-onboarding-save抛InvalidOperationException，后续未跑 | 已定位raw legacy Motion与正式canonical保存的比较不一致，expected侧窄迁移修已独审，保actual全字段精确比较；不归为旧物理输入缺口 |

Notepad可信物理输入条件不足是重构前已明确的缺口，保原not-run；不当作新问题阻止其他远端工作，也不以软件注入冒充可信来源。

## 当前负责人及下一动作

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | 发布本批精确冻结树并跟踪其CI；后继静态切片与本批隔离；统一动态资源排程 | 实际step结论、包、结构化结果及独立审查 |
| Windows设置 | 语言与S01专项已过；S07/术语scope修、G03全页模型/保存状态、S10关于候选已独审，已通过共同树编译，待实际WPF；下一S08接入与管理名单兼容标记、托盘剩余块 | 公开操作、保存失败/取消/重入、实际几何及同偏好原型图 |
| Windows浮层 | O04配置专用语言壳、真实Broker/保存ACK和较新焦点负控已独审，已通过共同树编译，待实际WPF；随后O05确认与O06动效 | 真实UIA与鼠标正控、旧peer/IME/关闭负控，保Controller安全门 |
| Linux设置 | 语言真实AT-SPI、键盘、写盘失败与四偏好最终图已验证；S06术语inline/±/撤销/即时保存与旧peer/全空dirty、真实失败恢复的settings+tray完整合同已通过；20张中英浅深图已产生且发现dark字色/inline范围宽度差距，最后差量待实际原型宽度对照与再渲染；S01首切片真实搜索/旧query/Enter定位/Esc已通过，双语、保存失败恢复及关闭重开旧peer负控也已真实通过；随后G03真实状态、N01教程与接入/托盘剩余块 | 实际GTK/AT-SPI、revision冲突及旧交互负控，不以静态lint代替 |
| 合作浮层 | 浏览器O01加载/更新/失败Retry；GTK4 hover观测、Qt实际布局与剩余原生路径 | 保原输入/租约/字形断言，真实端到端结果 |
| 共享能力 | 两平台正式profile已通过，实施仅Index中英双向Glossary固定登记及实际模型切换闭门 | 固定profile、既有包完整性、能力正反资格与切旧模型立即关闭 |
| 字体 | SC600真实WPF17项已通过；Source Serif16角色独立候选已生成、Pango5项通过，Chromium原权限probe被EPERM阻止未产图；消费者一批切换前保现役字体锁 | 正确离线族/字重和真实消费者字形，不降低到接受500冒600 |
| 独立审查 | 新O03入场生命周期与各页面公开交互；已审本批不覆盖后续新写 | 工程标准与原型行为两面核验 |

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current两行候选及切旧模型消费合同已独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰，不自动无限调参。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。18面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
