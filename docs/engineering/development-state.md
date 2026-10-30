# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

当前远端开发头是 [`5bc1679`](https://github.com/DingJWuyu/Typelay/commit/5bc167975afc1e950bc70255d4b34cd0a72ea2a2)，[CI557](https://github.com/DingJWuyu/Typelay/actions/runs/40998670319) 已全部终态。共享、双端 Index 包生命周期通过，Windows/Linux 主应用链仍失败；不是稳定体验包。`main`、`trunk` 未改。下一批只在精确候选审查、适用本地验证及远端读回后发布，不用微批取消正在执行的同头 CI。

| 当前实际证据 | 未闭合问题与下一动作 |
| --- | --- |
| Windows App1247执行、1245通过、2失败；Runtime1454/Core302/Protocol28/WindowsContracts210及独立计时2例通过 | 两个失败是新模型页公开控件消费者：关闭的editable Combo未展开就取选项，旧ModelRefresh仍找退役标题/布局。两fixture已独审、Tests Compile通过并本地合入，真实WPF复验仍待。22未执行不计通过。 |
| S09新16场景15通过；真实就绪/下载/失败/Hy800窗口的模型页图已查看，公开Hy下载按钮可达 | 不再只报告S09编译通过；同偏好全状态视觉仍按parity逐项验收。 |
| Windows formatting阶段真实失败 | 官方solution格式要求对象初始化换行，先前folder模式通过不足以证明同门。完整冻结树的正式solution formatter因BuildHost IPC权限失败且未改源；受控提升也在脚本执行前失败，停止该动作。按实际CI错误及.editorconfig做有限纯排版修正、审语法等价并正常编译；本地正式verify未完成，下一Windows仍执行原强门。原CI格式错误与本地执行限制分别记录。 |
| Windows Browser失败已有实际hit诊断 | 166×150源字段中心被180×452浮层遮住，没有新源focus/text，不能归为旧Glossary拒退休。按公开可达源区域和实际hit修单次点击，不隐藏浮层、强制focus或延时。 |
| Hy正式worker5场景通过；UI安装/选择/预览/补充语言等8场景通过 | cold ready在2173ms已到，后续835s超时是fixture等待已折叠旧语言标签。候选保持模型页，用公开PreparationStatus精确14方向集合校验，新增阶段区分；不改模型/预算或把count冒方向证明。 |
| Notepad所有可执行场景通过，包含普通退出；自动接受及依赖编辑仍两项not-run | 这是重构前已明确的可信物理输入缺口。不能用软件注入冒充，也不作为停止其它工作的理由。 |
| Linux Browser完整90/90、178.975秒及真实browser桌面链通过；双端Index生命周期均12阶段通过 | Linux Index本轮Argos安装通过，但没有改安装生产路径，不据此次绿解释旧一次失败；包生命周期与桌面/语义质量分别判定。 |
| Linux tray原3秒等待失败 | 原NextStep被新增长管理名单挤出首屏。已将同实例放在两摘要后、管理表前；24条真实名单压力及完整原tray期限/可见性合同通过，四源已审并本地合入。 |
| Linux settings/tutorial因同一个退役“应用连接：桌面普通输入框”入口失败 | 四处summary与两处Addpicker消费者已按新公开点击序列统一迁移，保唯一、完整可见与原15秒。真实模型desktop/Gimp整链仍须下一CI。 |
| GTK4 glossary前21例过，第22例真实motion坐标为float32相邻表示，原1e-6比较拒绝 | 仅callback采用一binary32 ULP表示区间，device/point/身份/时序门不变；独审及组合71纯合同通过，真实40例点击待CI。 |
| GTK3五个motion场景及collapse167实际通过 | 后继reentry Expand误用短文，实际按钮隐藏；改预注册短文隐藏负控→八行长文正控，保原800ms及一次真实点击，待下一native链。 |
| GTK4 desktop短文自然状态仍42/44尺寸差；Qt ja仍实际选SC字形 | 保原尺寸与glyph门。已审自然frame观察和独立Qt显示层有限实验等待正式SDK运行；不能以Ruff/import或其他平台字体通过代替。 |

### 当前本地后继与证据边界

已本地合入但尚未发布：S02通用页13源（双轴审查、App/Tests/Fixture编译通过，12项WPF/新图待跑）、普通source O05（19项Runtime、App/Tests编译通过，9项WPF待跑）、上述Windows/Linux消费者修复、GTK4独立More及GTK3术语/通用操作的真实IME与同opening守卫、有限native诊断。最终冻结966输入的正常App/Tests/Fixture/Practice编译均通过，全Runtime1474/1474；八份纯排版差量的Roslyn词法token与非布局trivia逐项相同。它们不代替受阻的本地正式solution格式检查或Windows实际运行。owned首纵向只有已独审编译的公开expected RED单例，尚未真实执行，也未实现新生产协议；下一Windows正常suite不skip或反转它，先确认失败确落用户路径再实现单个Keep→fresh Bind纵向。

Linux toast容器裁影已取得真实RED→修复证据：仅扩留白会吞底层点击，已拒绝该候选；最终局部绘制窗穿透、pill独立输入窗及按GTK实际核范围留白，真实halo/普通pill/Undo/hidden/旧peer和浅深四图均通过。pill距底28及原6秒不变；自然窗口边界裁切不是容器内部截影。这是本地真实UI证据，现CI没有自动执行新增toast专用入口。

2026-10-30环境替换后，旧未提交候选/图片/日志丢失；不能拿摘要或旧SHA冒充原件。当前源码从远端恢复，SDK10.0.401、锁定工具与32面字体已重新准备；旧session80867终态仍未知。新S08单页与默认nullable配置的完整邻近合同已实际通过，新S09按实际源重新审编译/取得CI图；不把它们说成丢失旧候选直接恢复通过。

当前CI合成原件在 `artifacts/ui-rebuild/ci557/`，已按GitHub digest核验：`contracts`、`windows-full`由root解析，`linux-diagnostics`、`gtk4-glossary`、`qt-early`及`index-linux`用于Linux定位；不执行下载包中的二进制。原失败保留，不靠重复运行或降低期限求绿。原型全量逐页状态与剩余实现缺口仍以[固定25项](../design/prototype-parity.md)为准，不把范围遗漏包装成只差测试。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CI557已终态；合入已审页面、公开消费者与native窄修，冻结下一完整编译/发布候选 | 新源身份、独审、本地适用检查与精确 head CI |
| Windows设置 | S09已取得真实图，修两个公开fixture及本批格式；S02通用页已审编译，后继共享选中背景动效仍待 | 官方编译、公开操作和真实同偏好页面图 |
| Linux设置 | S08名单布局/完整tray及toast四图已通过；推进S07完整快捷键页、页头统一恢复默认与真实录制消费者 | 真实 host/GTK/AT-SPI、完整新锁字体与交互图 |
| Windows浮层 | 普通source确认已审编译/19Runtime通过待WPF；owned公开expected RED已编译待首次真实执行，未先铺生产协议 | fresh source/Core/Bind、原期限与零误写负控 |
| 合作浮层 | CI557原件已定位；GTK3通用More补实际IME/opening守卫，GTK4坐标/natural帧与Qt有限实验按原门等待SDK验收 | 实际 case、正反控与未改产品安全门 |
| root支持 | 已核CI557 Windows原件与S09实际图；Hy冷启动就绪后的公开方向消费者窄修已审，待最终Fixture编译与下一CI | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
