# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

当前远端开发头为 [`3c89b55`](https://github.com/DingJWuyu/Typelay/commit/3c89b55d7bfb5b57f790fac895d95a1fa7ab0120)，[CI564](https://github.com/DingJWuyu/Typelay/actions/runs/41035604090) 已全部终态。main/trunk未改。双端Index生命周期通过，但不代表桌面或翻译语义质量认证。

Windows App1350执行/1348通过，仅两条新local-draft返回失败：Keep、同TextBox Undo及窄窗口前段已经实际通过，快Tab按下/释放后旧held资格拒绝；正在仅新local路径实现一次真实keyup兑现，原down+500ms不续期，旧普通/正常owned路径不动。确认宽动画同侧/同锚完整通过，Appearance与应用picker此前通过子项保持。正式格式、continuous、搬迁与physicalTab通过，本轮80分钟/60分钟后链准入恢复：Hy完整15场通过；Notepad五个可执行场景通过，两项仍为旧可信物理输入not-run。Browser自动预览Tab通过，术语公开按钮仍disabled，后续未运行，正在保门诊断首拒因。

Linux严格构建、tray/S07、隔离GTK合作、原始Qt完整probe及可选合作包搬迁通过，旧More首点与八项输入清单阻塞已越过。实际仍红：Browser90例89过（原控制键计划续显一例，本地同源一次诊断通过但未解释CI）；settings/tutorial准备后的折叠连接详情未展开；GIMP确认框焦点祖先拒绝；真实Firefox一次输入182ms后才处理，旧观察已过期而正确拒绝；GTK4配置首打开即关闭，尚未到保存；Qt动画中第二次点击落在移动按钮之外。各自保留原件，不用阶段名或本地通过替代实际链闭环。真实GTK3后链本轮通过。

### 成熟后继与验证边界

CP24成熟并集已于10:44:16 UTC实际聚合结束0：996源码/字体输入前后同SHA，App/Tests、真实NativeFixture Release、DesktopFixture、Practice、LinuxHost全部编译0；Runtime1497/Core310零跳过，174个Python格式与Ruff、32字体锁通过。15份C#实际folder扫描仅一份机械排版，Roslyn token及非布局trivia等价。仍待新精确远端头真实WPF/SDK验收，不把编译等同交互通过。

当前独立后继：Windows应用整体sheet五源已双轴审并在993输入独立树App/Tests编译通过（09:54:03终态0、两新源仅排版且token/trivia等价）；已精确合本地，8个新WPF场景及真实屏幕组合图尚待下轮。底层scrim与sheet同一现有ShowDialog HWND，未做180%饱和度；不拿Blur参数等同完整CSS材质。Linux完整托盘v7受控公开动作与四组320px主图通过；v10通过正常公开Settings.show重新入场后，完整邻近合同也通过，109输入无漂。五个生产源与已验panel逐字相同，六源已审合本地，并加入独立40秒panel CI消费者；真实远端和首帧截图完整性/材质仍待。旧139及中间失败原件保留，不混CI564。

CP23已发布以下独审有限修复：Qt生产者/打包者共用原8项精确fixture输入清单并补font-order二进制校验；GTK4首个More点只在当前完整render的自然layout/paint后固定；Linux两处旧公开Appearance名称消费者迁移；Windows owned测试真实扫描码与前一菜单测试keyup清理；确认宽动画自有窗口初始几何；应用picker截图前原3秒内正常绘制机会。没有降低真实输入、焦点、期限、尺寸或最终精确命中门。

Qt文件打包合同已取得真实匹配bundle RED→GREEN，全41项通过。GTK4纯几何合同76/76通过、953输入前后无漂；不能借纯合同称原生40例或O04配置16例通过。CP23的991输入独立聚合已于09:42:00 UTC实际结束0，前后身份一致：App/Tests、真实NativeFixture Release、DesktopFixture、Practice及LinuxHost编译通过，Runtime1497与Core310全部通过零跳过；4份差量C#实际folder扫描0/4变更，Python Ruff/32字体锁通过。真实WPF和原生SDK仍待下一精确头CI。当前没有以新完整tray或整体sheet未完成实现阻塞这些成熟修复。

已保留的产品子项：Windows Appearance公开交互20项、动效14项和应用picker11项实际通过；Linux Appearance单页Full×OSfalse显示层wrapper真实12场与单次slider邻近通过，原会改变viewport的margin方案未合。此前Hy完整15场景、Notepad所有可执行场景和正常退出实际通过；Notepad两项可信物理输入仍是重构前已明确的not-run边界，不用软件注入冒充。

仍未完成：owned Keep后的两条真实快Tab返回链，Replace尚未实现；S08整体确认sheet的原生模态、背景材质及完整入场；Linux完整T01托盘的精确远端验收与首帧/材质/入场；GTK4 O04原生完整交互/几何；其他页面Full动效与剩余25项原型差距。B02风格/另述最终有界评估未通过，属于未交付功能，不混入用户批准仅前端的第四类。B01仅固定Index中英双向Glossary按既定profile准入，不泛化组合/其他方向。

公开术语handoff重叠已取得5项及全Runtime1491 RED→GREEN；真实Browser后链尚待，不能说已证旧失败唯一根因。连续链公开cue已通过，但不修复译文可见到输入资格发布的间隙，原150ms门保留。

当前原件保留在 `artifacts/ui-rebuild/ci564/`，CI563原件继续保留于原目录；早期失败定位和候选RED/GREEN保留各自目录，不重写旧结果。实际GUI截图、原型逐页交互与剩余实现范围以[固定25项](../design/prototype-parity.md)为准。2026-10-30环境替换丢失过旧未提交原件，后续候选均重新取得源码身份与验证，不拿摘要冒恢复成功。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CI564终态已分流，准备CP24成熟并集；不等shader或新Qt方案，源码冻结后原聚合再发布 | 新源身份、独审、本地适用检查与精确head CI |
| Windows设置 | Appearance/Motion/picker已真实通过；整体Settings客户区模态sheet五源已审编译，下一实际八场与组合图；FXC仅非阻塞库存观察 | 公开操作、实际组合像素与同偏好页面图 |
| Linux设置 | T01完整panel与邻近full实际通过、六源已审合；新独立正常CI消费者待远端，后继仅修首帧截图就绪 | 真实Host/GTK/AT-SPI、实际字体与交互图 |
| Windows浮层 | Keep/Undo已真过，只有新local fastTab返回待release-only八源验证；旧普通/owned held不改，Replace继续等待 | fresh source/Core/Bind、原down500期限与零误写负控 |
| 合作浮层 | GTK合作/Qt原probe及搬迁通过；O04首次关闭待有限阶段诊断，Qt反向第二点击真实移动时机待明确方案 | 实际case、正反控与未改产品安全门 |
| root支持 | Browserhandoff首拒因诊断全Runtime1497与overlap5通过，prepared chooser公开展开小修已审；持续负责Windows实际原件与Spec | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

合作浮层 O04 后继限定于 GTK4：CI564实际首场已证begin发出及旧源退休，但配置壳随后关闭，16场尚未完成。两源有限32条opening/焦点阶段只读诊断已独审并合本地，仍待下一SDK实际分支证据，不能先放宽autohide或600ms握手。首实际图仍需核264宽、冻结trigger左−4及上下gap6/底缘8/列表232上限；配置壳三态300ms入场与完整材质仍未完成，不抓屏伪造模糊、不改宿主全局设置，不横铺GTK3/Qt配置协议。

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
