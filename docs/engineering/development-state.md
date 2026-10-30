# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

本批 CP25 的验证基线为 [`6160579`](https://github.com/DingJWuyu/Typelay/commit/61605791bd5a0b071c198b812597f5ed3adbd45f)，[CI565](https://github.com/DingJWuyu/Typelay/actions/runs/41043817261) 已双端终态失败；main/trunk未改。双端Index生命周期通过，但不代表桌面或翻译语义质量认证。

Windows App1384执行/1343通过/41失败：35项集中于新应用sheet的pressScale模板TargetName运行错误，优先修实际命名目标并保持按下缩放；另有Appearance retarget一项、owned返回三项、旧Queue读取一项和FirstWake过期一项。共享Runtime1497/Core310/Protocol28/WinContracts210及独立下载两例通过。本轮后链预算准入拒绝，不能沿用CI564的Hy15场、Notepad可执行五场通过作为本头证据。此前Browser真实术语按钮disabled仍未闭，本头仅增加首拒因诊断。

Linux新完整托盘首次确认Cancel原3秒has_focus失败，旧托盘合同不能代替新页验收。settings/tutorial已越过旧准备与chooser展开阶段，现失败是重新打开应用页后拿折叠详情里的旧控件做初始滚动校准；GIMP已越过旧ALERT焦点祖先门，卡在旧语言菜单名称而未进入新More。两类正在精确迁移公开fixture入口，原PID、角色、可见与完整裁剪/输入门保持。GTK4配置本轮begin=0、trigger有map无paint，不与上轮已begin后关闭混归因；隔离GTK4已真实点击术语按钮但未收到事件。Qt第二次尾段反转已越过，新的Reduce观察器可能绑定非当前expand实例，正在以同一可见实例闭合观察。所有原件保留，未用本地绿替代正常CI。API逐step确认普通Browser、Firefox扩展、真实desktop+Browser、真实GTK3、已安装VSCode与正式Hy生命周期本轮success；原始Qt probe亦success。不据此解释旧Firefox182ms晚到或把各阶段通过扩成全端验收。

### 成熟后继与验证边界

CP25成熟并集已于11:49:28 UTC实际聚合结束0：1001源码/字体输入前后同SHA，App/Tests、真实NativeFixture Release、DesktopFixture、Practice、LinuxHost全部编译0；Runtime1497/Core310零跳过，175个Python格式与Ruff、32字体锁通过。20份C#实际folder扫描仅三份机械排版，Roslyn token及非布局trivia等价，运行结束后才同步回工作树。主要修复是565的sheet模板共因与Linux托盘当前请求的原生呈现；owned返回、FirstWake、GTK4新诊断并未被宣称已修。全部仍需下一精确头真实WPF/SDK及正式solution格式验证。

后继当前事实：Windows N01真实bytes/total、未知进度与同库存Ready已完成七源独审编译，14个新WPF及旧fixture仍待下一正常CI；400ms显示层进度独立五源已编译并合本地、实际WPF未跑，不能用编译替代像素。Linux T02取得“无确认就退出”的真实RED后，完整确认/取消/默认焦点/两阶段草稿/旧prompt拒绝及原tray邻近已实际通过，已精确合本地，仍待远端；About两个禁用更新控件已独立真实RED→GREEN，四种中英浅深的禁用动作/零设置写与图证保留，124输入无漂；恢复原T02公开退出回调后最小合入，没有更新后台。Browser完整确认键盘的首真实RED为Keep未获默认焦点，后继仅设计确认专用身份，不白名单延旧编辑许可。Firefox182ms晚到只增加有限既有时点诊断，纯3+原Browser2通过不证明根因已修。

Windows应用整体sheet五源曾双轴审并在993输入独立树App/Tests编译通过（09:54:03终态0、两新源仅排版且token/trivia等价）；已精确合本地，565实际运行暴露35项同一模板命名错误，后继三源最小修/诊断已独审且996输入App/Tests编译0；真实修复仍待下轮WPF，不能用旧编译记录代替运行。底层scrim与sheet同一现有ShowDialog HWND，未做180%饱和度；不拿Blur参数等同完整CSS材质。Linux完整托盘v7受控公开动作与四组320px主图通过；v10通过正常公开Settings.show重新入场后，完整邻近合同也通过，109输入无漂。五个生产源与已验panel逐字相同，六源已审合本地，并加入独立40秒panel CI消费者；565远端首个Cancel焦点失败已单列；后继首帧底部完整截图已本地核，材质仍待。旧139及中间失败原件保留。

已有公开消费者、精确字体/fixture闭包与几何修复保留原回归；旧聚合与RED/GREEN记录在各checkpoint原件目录。每次新提交仍执行同一精确输入闭包的编译/格式/共享合同和正式双端CI，不将历史构建通过继承为新界面运行通过。

已保留的产品子项：Windows Appearance此前公开交互20项、动效14项与应用picker11项实际通过；565动效retarget一项回归仍红，其余子项不被整体失败抹去；Linux Appearance单页Full×OSfalse显示层wrapper真实12场与单次slider邻近通过，原会改变viewport的margin方案未合。此前Hy完整15场景、Notepad所有可执行场景和正常退出实际通过；Notepad两项可信物理输入仍是重构前已明确的not-run边界，不用软件注入冒充。

仍未完成：owned Keep后的真实返回链及565新增前置失败，Replace尚未实现；S08整体确认sheet的原生模态、背景材质及完整入场；Linux完整T01托盘的精确远端验收与首帧/材质/入场；GTK4 O04原生完整交互/几何；其他页面Full动效与剩余25项原型差距。B02风格/另述最终有界评估未通过，属于未交付功能，不混入用户批准仅前端的第四类。B01仅固定Index中英双向Glossary按既定profile准入，不泛化组合/其他方向。

公开术语handoff重叠已取得5项及全Runtime1491 RED→GREEN；真实Browser后链尚待，不能说已证旧失败唯一根因。连续链公开cue已通过，但不修复译文可见到输入资格发布的间隙，原150ms门保留。

当前原件保留在 `artifacts/ui-rebuild/ci565/`，CI564及更早原件继续保留于原目录；早期失败定位和候选RED/GREEN保留各自目录，不重写旧结果。实际GUI截图、原型逐页交互与剩余实现范围以[固定25项](../design/prototype-parity.md)为准。2026-10-30环境替换丢失过旧未提交原件，后续候选均重新取得源码身份与验证，不拿摘要冒恢复成功。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CP24已发布6160579、565终态；先收35共因及当前真实失败，再聚合成熟后继，不以新功能掩回归 | 新源身份、独审、本地适用检查与精确head CI |
| Windows设置 | 565应用sheet35项共因模板修、Appearance retarget一项；N01真实模型卡/400ms显示层已独审编译，待正常CI | 公开操作、实际组合像素与同偏好页面图 |
| Linux设置 | 565托盘确认默认Cancel原3秒native焦点红，当前同步用户modal呈现最小修；T02已本地完整GREEN并合live，真实结构化进度暂让位回归 | 真实Host/GTK/AT-SPI、实际字体与交互图 |
| Windows浮层 | 565 owned三项及Queue读取一项按实际阶段分开；快Tab已到claim/fence但后继仍红，不推进Replace实现 | fresh source/Core/Bind、原down500期限与零误写负控 |
| 合作浮层 | 565 O04首trigger未paint、隔离GTK4术语事件及Qt reduced观察对象三案；旧尾段反转已越过，不重修旧阶段 | 实际case、正反控与未改产品安全门 |
| 浏览器确认 | Keep默认焦点公开RED已证；aux/Keep/Esc seam设计暂存释放slot优先WO回归，无生产修改，恢复先闭双端proof | 先安全设计与真实RED，再最小纵向 |
| root支持 | Windows原件与FirstWake阶段诊断、LinuxAbout两禁用更新控件独立GREEN候选，所有新动态仍统一排程 | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

合作浮层 O04 后继限定于 GTK4：CI564曾在begin后关闭，565则首trigger只有map无paint、begin为零，16场尚未完成。现owned fixture日志未正确接到inside.log，后继仅恢复已有输出路由取得真实分支，不从缺日志猜没有hide，不先放宽autohide或600ms握手。首实际图仍需核264宽、冻结trigger左−4及上下gap6/底缘8/列表232上限；配置壳三态300ms入场与完整材质仍未完成，不抓屏伪造模糊、不改宿主全局设置，不横铺GTK3/Qt配置协议。

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。本批已将 Start-TypeLay.cmd 的仓库 blob 恢复为 .gitattributes 要求的规范 LF，工作树仍 CRLF；发布区分原工作树校验与按 path clean 后的 Git blob，不改命令或另开微批。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
