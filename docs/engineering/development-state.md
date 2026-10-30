# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

当前远端开发头是 [`eab94ce`](https://github.com/DingJWuyu/Typelay/commit/eab94ce96dcfc7e256e8f1156e2a9061741d5ec9)，[CI560](https://github.com/DingJWuyu/Typelay/actions/runs/41015770568) 已全部终态。57文件逐树、提交和远端读回一致，`main`、`trunk` 未改，不用后继微批取消本轮。CI560 Windows主链和Windows Index已在构建失败：NativeFixture显式链接遗漏新KeyboardHook.LocalDraft.cs，所有本轮WPF/桌面行为均未运行；先前本地App/Tests/DesktopFixture Compile是有限覆盖，未证明该显式消费者闭包。最小csproj一行链接修已实际NativeFixture Release Build通过、948输入无漂，下一正式Windows门待验；不以DesktopFixture Compile替代。Linux Index原件12项通过/cleanup空/owned全退；主Linux失败：GTK4已越旧高度断言，真实More截图的首帧节点缺失使fixture退出，待自然帧就绪窄修；settings已越标题发现，现卡GTK FRAME与niri尺寸严格比较，已加不放宽门的有限诊断。Qt严格probe编译转换错误待后继修。下表其余仍为CI559已终态证据，不冒充新head。下表区分本轮实证与仍沿用的先前结果，不能以阶段推进代替通过；整体不是稳定体验包。

| 当前实际证据 | 未闭合问题与下一动作 |
| --- | --- |
| CI559 Windows App1289执行、1288通过、仅owned首纵向1失败；Runtime1474/Core302/Protocol28/WindowsContracts210及独立计时2例通过 | 上轮其余17项普通source与S02回归已实际消除。owned有限事件证实More夺焦后浮层隐藏、稿与Undo清空；已独审新Keep纵向Core310/Runtime1486及托管编译通过，仍待同条真实WPF后半链。Replace未实现。 |
| Windows Hy完整15场景通过 | 冷启动公开14方向、两向实际预览、切回/移除/正常退出均通过，旧隐藏标签等待已关闭。不是其他模型所有语义质量认证。 |
| CI559 Windows正式Formatting/Python步骤通过 | 本地folder实际扫描/token证明仅为有限验证，不能替代已实际通过的本轮正式solution门；后继新源码仍须同门。 |
| CI559 Windows Browser仍在公开术语入口失败；连续链使用公开cue实际通过 | held-capture重叠已取得公开RED→窄修GREEN：5项及全Runtime1491项通过、零跳过。只在原500ms内保留尚未发起的一次复核，未新增授权；真实Browser后链仍待新CI，不称已证明旧失败唯一根因。连续链cue不修preview到输入资格发布间隙，原150ms保留。 |
| Notepad所有可执行场景通过，含普通退出；两项可信物理输入仍not-run | 重构前已明确的物理边界，不能用软件注入冒充，也不阻停止其他工作。 |
| Linux主构建、IPC/unknownIME、完整tray、Browser公开适配/路由及真实Browser链通过；Index12阶段通过且cleanup空、owned全退 | 长名单NextStep可见的tray修已进入远端通过；Index生命周期不是桌面/语义质量评审。 |
| CI560 Linux教程、真实GTK3/Browser链、S07正式包公开九图通过 | settings已找到唯一真实窗口，但FRAME与niri尺寸不等；原件无双方尺寸，后继只补原读取值诊断，不猜CSD或放宽比较。 |
| CI560 GTK4已越44→42旧断言，首More截图节点缺失导致fixture退出 | 截图自然帧就绪与子进程退出归因候选独立进行，原800ms/几何/真实输入不变；不是完整40例通过。 |
| CI560 Qt严格probe编译拒绝qsizetype转int | 加长度上界与显式转换后待真实SDK编译/地区字形验收；其余源/票据/revision门未改。 |

### 当前本地后继与证据边界

前一批已合入：Windows owned Keep首纵向、完整Appearance页、术语编辑handoff重叠窄修；Linux精确Esc键名与clipboard标题消费、GTK4局部scrollbar几何过渡修、Qt地区显示层候选和S07正式CI入口。各来源均按独审后的精确文件身份合并，本批独立986输入聚合已完成：App/Tests/Fixture/Practice编译通过、Runtime1491与Core310全部通过且零跳过、全Python Ruff通过；40份C#真实folder扫描仅两份机械排版且token/非布局trivia等价，输入前后无漂。新head双端真实验收仍待，不能以本机托管通过替WPF或原生SDK运行。

本次成熟后继独立988输入聚合已在07:12:04 UTC结束，全部输入前后无漂且与合入源相同：App、Tests、NativeFixture实际Release Build、DesktopFixture与Practice均零警告/错误；Runtime1491/1491、Core310/310零跳过，全Python Ruff与字体锁校验通过。10份C# folder实际扫描无排版变化。包含NativeFixture显式链接修、Windows选环与应用页tokens、Linux完整Appearance页、Qt转换窄修及settings有限诊断；不混仍在开发的GTK4语言壳/截图修。正式Windows运行与solution格式、Linux原生SDK门仍由下一同head CI判定。

owned候选已通过Core310/Runtime1486及App/Tests/Fixture/Practice托管编译；后来术语窄修在相同共享候选上公开5项与全Runtime1491项通过。Keep保同TextBox本地稿和Undo，真实Tab重新取得源/Core/Bind；旧owned授权永久退休。同壳340px确认sheet的尺寸/字号/按钮与窄字段呈现候选已独审并随本批托管编译通过，仍待Windows实际图/交互；中心modal/backdrop、真实模糊及完整sheet动画未完成。真实WPF首链尚待，Replace未实现。WPF完整Appearance页已编译，实时Slider保存、三态动效选择与20项真实UI合同仍待Windows运行。Linux完整Appearance后继已真实通过：六组字体preview330宽/正文ink、三态偏好×OS矩阵、即时拖动按住16→19与原0.4秒held ACK、最终磁盘19、保存失败/恢复/旧peer以及15张图。原失败保留：GtkRange观察坐标错误、1ms轮询饿死GDK低优先级motion，以及关闭数值标签后round_digits被重置的离散化缺口分别窄修；没有延时或更换期望求绿。实跑59个runtime输入无漂，仍须保当前正式包入口合并。Full×OS减少时真实局部动画、thumb端点包络未完，不称整页全像素1:1。


S07专用公开合同和九图已在CI560正式发行包/既有niri scope实际通过，模块来源与原预算保持；受控输入不是物理输入验证。Qt候选保持真实document revision与旧票退休，同policy不重复格式化。CI560更严格probe目标在两个qsizetype转int以-Werror失败，尚未执行字形；已独审长度上界与显式转换窄修，本机无Qt SDK，下一正式门验证，不禁warning。GTK4局部CSS同样待正式原生验证，不放宽44px尺寸门。GTK4语言配置Host/bridge各3项独立通过，native纯配置壳仍在实现，不混成熟批。

continuous固定stock Edit场景的既有公开快捷键就绪cue已在CI559通过；仍唯一按键、原150ms与原截止，不修复译文可见到源控制资格发布的160ms间隙。正式solution格式门本轮通过；后继本地folder必须记录实际扫描数，不能替代下轮正式门。

Linux toast容器裁影已取得真实RED→修复证据：仅扩留白会吞底层点击，已拒绝该候选；最终局部绘制窗穿透、pill独立输入窗及按GTK实际核范围留白，真实halo/普通pill/Undo/hidden/旧peer和浅深四图均通过。pill距底28及原6秒不变；自然窗口边界裁切不是容器内部截影。这是本地真实UI证据，现CI没有自动执行新增toast专用入口。

2026-10-30环境替换后，旧未提交候选/图片/日志丢失；不能拿摘要或旧SHA冒充原件。当前源码从远端恢复，SDK10.0.401、锁定工具与32面字体已重新准备；旧session80867终态仍未知。新S08单页与默认nullable配置的完整邻近合同已实际通过，新S09按实际源重新审编译/取得CI图；不把它们说成丢失旧候选直接恢复通过。

当前CI合成原件在 `artifacts/ui-rebuild/ci559/`，已按GitHub digest核验：`contracts`由root解析，完整旧场景原件另保`ci558/windows-full`，`linux-diagnostics`、`gtk4-glossary`、`qt-early`及`index-linux`用于Linux定位；不执行下载包中的二进制。原失败保留，不靠重复运行或降低期限求绿。原型全量逐页状态与剩余实现缺口仍以[固定25项](../design/prototype-parity.md)为准，不把范围遗漏包装成只差测试。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CI560精确eab94ce已发布并启动；跟踪双端终态。后继L Appearance、W选环、O04与Replace独立推进，不混运行头 | 新源身份、独审、本地适用检查与精确 head CI |
| Windows设置 | S09已有真实图；S02页面/300ms动效23项CI559通过；完整S03在CI560待实际WPF，200ms选环后继独立编译已通过并合未发布源，13项实际WPF仍待 | 官方编译、公开操作和真实同偏好页面图 |
| Linux设置 | S08名单布局/完整tray及toast四图已通过；S07 v5完整合同与九图通过，pulse/shake仍未完；S03三态首纵向已绿，完整页面与局部Full动效继续 | 真实 host/GTK/AT-SPI、完整新锁字体与交互图 |
| Windows浮层 | 普通source九项CI559通过；owned已据真实失焦清稿RED实现Keep→Tab→fresh Bind并托管编译，等待真实同条转绿；确认sheet呈现独立补齐 | fresh source/Core/Bind、原期限与零误写负控 |
| 合作浮层 | GTK4几何/Qt实验生命周期与Browser资格按558原件收敛；GTK4语言搜索Host首公开RED已成立，纯配置退休ACK单纵向在独立候选中，不改三端通用框架 | 实际 case、正反控与未改产品安全门 |
| root支持 | 已核CI559 Windows终态、Hy15真通过；公开held-capture窄修5项与全Runtime1491通过，支持下轮Browser实际验证 | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
