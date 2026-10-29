# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-29**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

本批发布前基线 [`2742dc0`](https://github.com/DingJWuyu/Typelay/commit/2742dc0691363eb233614e53c5f74f71920fd621) 已发布，其 [CI553](https://github.com/DingJWuyu/Typelay/actions/runs/40924464287) Linux 已终态失败。它只在 CP13 上修正 guardian 测试 PID 标记的原子发布，不夹带未验证的字体或计时改动；本地 20/20 与独立审查通过。main/trunk 未改。

CP13 的 CI552：共享与两平台 Index 生命周期成功。Linux 在 build 阶段旧 guardian fixture 读取未写完的 JSON 标记失败，后续原生/教程/Qt 阶段未执行，不能借同名旧失败归因。Windows App 实际 1178 执行、1177 通过、1 失败、22 未执行；Runtime 1436、Core 302、Protocol 28、WinContracts 210 全部通过。唯一 App 失败是 100ms 下载超时测试尚未写入 64-byte 前缀即取消，观察到其它 testhost 重叠；正准备两 case 单独进程的窄 runner，不改期限或断言。Windows 后继浏览器/Notepad已有步骤失败，最终run被下一同ref并发候选取消；未完成阶段不当通过，下一整体head须两端跑至终态。

CP13 已发布 ACK 菜单投影、浏览器胶囊、WPF More/S08/滚动条、Linux 四步教程及精确失败观测。CI552 S08 16 项、接入动作 2 项、滚动条 6 项实际通过；图片发现应用表加号/详情/停用点引用不存在颜色键，后继窄修待验。CI551 S07 22 项、O05 普通编辑态 8 项、O06 加载 6 项以及 T02/S10 有效证据保留。不能据行为合同通过宣称全页像素相同。

浏览器胶囊六项与四张原型几何对照通过；全90项曾89通过、1复合动效场景过期，改成预先独立场景后 focused 通过，不冒称完整90全绿。Linux教程真实保存/失败恢复/旧peer/迟到启动守卫通过；后继稳定五图已达到560×460，使用旧18字体且练习图为明确renderer输入，完整真实练习仍待永久链。接入当前请求焦点与公开ComboBox流程继续窄验证。

下一共同树App、Tests、Fixture、Linux构建与全Runtime1436/1436真实通过，947输入无漂移；完整Browser90实际87/90、3方法5子例失败，未以编译替Windows真实呈现。

32面最终离线字体候选已完成全部文件哈希/manifest和Pango五项；新共同树Browser两个focused合同实际通过，覆盖7个Latin角色、6个CJK区域与system/appearance，全部输入前后相同；尚未发布，不冒充WPF/Qt/GTK原生或完整90项通过。新包全量90实际87通过，动效两个子例、菜单外点两个子例及ko/claude一例失败；保原件，先固定观测区分失效，不重跑粉饰。T01真实模型准备/语言搜索、浮层按钮动效、GTK4几何dirty候选均按页面审查与运行，不阻塞成熟修复发布。

完整原型审查曾发现部分页面仅更换样式而缺少原型交互，属于范围执行和验收遗漏，不是细节。现按[固定25项页面/状态表](../design/prototype-parity.md)逐页收敛；等待外部CI时可推进下一独立切片，但未验证项不标完成。双端尚未达成完整一比一验收。

### 当前失败的原件定位

路径相对于被忽略的 `artifacts/ui-rebuild/`。以实际case、stage与结构化结果判断，不以步骤名、包名或日志推进推断通过。

| 原件 | 实际情况 | 下一动作 |
| --- | --- | --- |
| `ci553` Linux | guardian入口已跨过、build与21–29成功；30/33/42/43/46/47失败，45浏览器与49Code成功、48Qt未执行 | 本轮small diagnostics已取得，逐case核，不按旧step猜 |
| `ci552/contracts/` | App仅下载计时 fixture 前缀未入场；S08/滚动条当前合同通过 | 窄两阶段 runner 候选；不改100ms/3s/64或生产取消 |
| `ci550/windows-full/` browser | 第二轮glossary菜单投影隐藏 | CI552实际menu可见但buttonDisabled；SourceEpoch在begin-edit到ACK间变化，查移交而不删守卫 |
| `ci552/windows-full/` Notepad | 自动预览/按需直译通过，ordinary-exit确认查找deadline；自动接受及依赖编辑仍可信物理输入not-run | root限同PID owned窗口层级查找候选待真实CI，物理旧缺口不软件伪造 |
| CI550 Hy / CI552 | 旧cold-start-english等待Ready；552被取消且无result.json | 不声称已取得本轮cold诊断，保旧未知 |
| `ci553/qt-early/` | claude/ja在完整有效展示期始终真实选SC face，预期JP不符 | 查实际Qt多family更新，不放宽region字形表 |
| `ci553/linux-diagnostics/` settings/tutorial | ComboBox外层grab_focus失败；教程已跨四步后desktop_row局部作用域未定义 | 原生popup current/closed实际通过；外窗present未取得ACTIVE前置、未算通过；唯一desktop_row作用域窄修已通过静态检查，本批等待真实教程重走 |
| 同目录 GTK3 | 实际PNG中间帧已证，命令完成较晚 | CP13仅无损压缩减扰，原几何期限不改 |
| 同目录 GTK4 | 高度42→165→44，不是颜色失败 | 已审仅尺寸变更dirty候选，真实CI待验 |

Notepad可信物理输入条件不足是重构前已明确的缺口，保原not-run；不当作新问题阻止其他远端工作，也不以软件注入冒充可信来源。

## 当前负责人及下一动作

执行优先级：先收口完整页面和真实用户流程。独立字体校准、模型能力研究不得阻塞无依赖的界面实现与验收；已通过子项保留有效证据，最终精确提交的双端全回归不降门。B02本轮裁决后停止调参，不把未交付能力藏入第四类；本优先级也适用于任务恢复后的安排。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | guardian 窄修已发布并跟踪 CI553；CP13与后继字体/页面严格隔离；统一动态资源排程 | 实际step结论、包、结构化结果及独立审查 |
| Windows设置 | S08/滚动条实际通过；后继应用glyph、T01模型/语言及字体角色候选 | 公开操作、保存失败/取消/重入、实际几何及同偏好原型图 |
| Windows浮层 | O04 在 CI550 的 17 项与四图通过；O05 普通编辑态/O06 加载已有真实通过；后继 hover动效待编译 | 真实 UIA 与鼠标、旧 peer/IME/关闭负控；owned/source 确认键盘仍欠交付 |
| Linux设置 | S01/S06 有效已验子项保留，G03 状态与导航已验证；当前 N01 四步真实教程与迟到启动守卫、永久真实流程验收；CI551 接入 selector 已存在但 grab_focus 失败待定位 | 真实 GTK/AT-SPI、revision 冲突及旧交互负控，不以静态 lint 代替 |
| 合作浮层 | Idle/Paused 胶囊原型尺寸已实际 RED→GREEN 六项，图片正在对照；CI551 Qt 跨过旧空 runs 点后 claude/ja face 检查失败；GTK4 42→165→44几何待验证dirty修复 | 保原输入/租约/字形断言，真实端到端结果 |
| 共享能力 | B01 固定双向 Glossary 已发布；ACK菜单投影已发布；当前Windows定时测试窄runner；B02 最终裁决关闭、不再调参 | 固定 profile、既有完整性、资格正反控与稳定 Tick 不重复展示 |
| 字体 | SC600 真实 WPF 17 项已通过；Source Serif 16 角色候选/Pango 五项通过，各消费者接线前保现役锁 | 实际离线族/字重及消费者字形；独立 Chromium 比对被权限阻止，未产图 |
| 独立审查 | N01 迟到启动、共享投影及后继菜单/滚动条/诊断，审查只覆盖精确冻结源码 | 工程标准与原型行为两面核验 |

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current两行候选及切旧模型消费合同已独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。18面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
