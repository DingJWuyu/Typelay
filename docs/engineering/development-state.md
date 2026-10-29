# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-29**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

最新候选 [`7ceebd8`](https://github.com/DingJWuyu/Typelay/commit/7ceebd8cc73349493f3c21e10b2e65aaf7ea9be3) 已核实发布到开发分支，[CI548](https://github.com/DingJWuyu/Typelay/actions/runs/40881281729) 运行中；共享检查已通过，Linux Index正式12阶段与6处profile附着均通过，Windows Index在activate-index阶段IOException，profile尚未附着；静态已定位Windows分段路径与混斜线字符串比较必拒，正窄修并待真实重验。40文件冻结树经过独立审查；隔离树 App Build、App Tests Compile、DesktopFixture Compile、Linux Build 与174项能力合同通过。Linux本机完整DesktopFixture Build在复制Windows原生DLL时失败，不能称Windows完整打包通过，实际生命周期与WPF执行交本次CI。

该候选包含WPF术语单行编辑/导航即时保存、Linux语言最终公开键盘/保存回归、14项固定原生来源的推理profile绑定、字体选择诊断及精确旧UI测试迁移。尚不包含继续开发中的全局搜索、Linux术语inline、WPF More菜单与浏览器加载态新切片。Current仍空，模型高级能力尚未开放。

上一轮CI547已终态失败。两平台Index生命周期成功，但未验证新profile附着；Windows App执行1017项，通过1012项，失败5项。原116项XAML模板错误已消失；4项SC600仍选择Medium，1项退休UIA peer合法禁用异常的测试适配已进入新候选。Windows语言15项中14项真实通过，已取得中英浅深图片；这不代表整页像素及字体验收完成。Linux tray焦点回归通过，其余浏览器、合作模块、设置与教程路径仍有失败。

完整原型审查曾发现部分页面仅更换样式而缺少原型交互，属于范围执行和验收遗漏，不是细节。现按[固定25项页面/状态表](../design/prototype-parity.md)逐页收敛；等待外部CI时可推进下一独立切片，但未验证项不标完成。双端尚未达成完整一比一验收。

### 当前失败的原件定位

路径相对于被忽略的 `artifacts/ui-rebuild/`。以实际case、stage与结构化结果判断，不以步骤名、包名或日志推进推断通过。

| 原件 | 实际情况 | 下一动作 |
| --- | --- | --- |
| `ci547/contracts/*12_32_07*.trx` | SC600实际选择Medium，4失败 | CI548已加入直接字形加载/族分组/legacy族三路只读诊断，保期望600 |
| 同TRX LanguagePicker退休peer | 导航后UIA拒绝禁用元素，1失败 | 新候选只适配已退休分支异常，保当前正控及不写盘断言 |
| `ci547/gtk-early/gtk4-glossary-result.json`及clicks/state | 21例后TextView ValidTranslation pointer-save hover期限失败；固定点和公开GDK祖先命中目标，但motion observer不含pointer | 核fixture事件观测，尚未修改hover门，不归为旧点位移 |
| `ci546-linux-job.log` Qt appearance | editor可见和字体属性已更新，实际glyphRuns仍空 | CI548同原等待预算加入真实非空glyphRuns就绪，最终字形hash仍严格 |
| `ci547/jobs-latest.json` | 浏览器、GTK/Qt、模型设置/教程、Windows browser/Hy失败 | 对照新候选精确结果；没有原件的原因不猜测 |
| `browser-o01-independent.log` | 4独立正常capture场景20态及Retry，2/2通过9.917s | 保原单capture超1500ms失败证据；新10图待对照，后继全量不省略 |
| `glossary-inline/glossary-inline-first-green.log` | 新编辑行已打开但child未显示 | 新实现局部show children，full-v3真实公开恢复已通过；旧peer/全空dirty负控收口中，原失败保留 |

Notepad可信物理输入条件不足是重构前已明确的缺口，保原not-run；不当作新问题阻止其他远端工作，也不以软件注入冒充可信来源。

## 当前负责人及下一动作

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | 跟踪CI548精确head；新静态切片与冻结树隔离；统一动态资源排程 | 实际step结论、包、结构化结果及独立审查 |
| Windows设置 | 新语言/术语等待实际CI；S01搜索定位/滚动候选已审与编译；CI548术语18失败归并为公开UIA provider/退休键事件构造，fixture-only修已审编译；下一S07统一录制与恢复默认实现中 | 公开操作、保存失败/取消/重入、实际几何及同偏好原型图 |
| Windows浮层 | O03 More顺序/四段/键盘及一次入场、真实IsOpen退休候选已审编译，晚补实际编辑态测试待总编译；下一O04内嵌语言菜单方案调查 | 真实UIA与鼠标正控、旧peer/IME/关闭负控，保Controller安全门 |
| Linux设置 | 语言真实AT-SPI、键盘、写盘失败与四偏好最终图已验证；S06术语inline/±/撤销/即时保存与旧peer/全空dirty、真实失败恢复的settings+tray完整合同已通过；列宽/中英浅深最终图待视觉差量 | 实际GTK/AT-SPI、revision冲突及旧交互负控，不以静态lint代替 |
| 合作浮层 | 浏览器O01加载/更新/失败Retry；GTK4 hover观测、Qt实际布局与剩余原生路径 | 保原输入/租约/字形断言，真实端到端结果 |
| 共享能力 | Linux正式profile已过，Windows激活失败待定位；两平台通过后仅Index中英双向Glossary固定登记候选 | 固定profile、既有包完整性、能力正反资格与切旧模型立即关闭 |
| 字体 | SC600真实WPF诊断；Source Serif500与光学尺寸仍未完成 | 正确离线族/字重和真实消费者字形，不降低到接受500冒600 |
| 独立审查 | 新O03入场生命周期与各页面公开交互；已审CP9不覆盖后续新写 | 工程标准与原型行为两面核验 |

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定通过前Current保持空。通过后只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰，不自动无限调参。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。18面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
